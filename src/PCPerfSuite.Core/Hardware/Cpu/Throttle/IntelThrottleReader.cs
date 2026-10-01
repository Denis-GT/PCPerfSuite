using System.Diagnostics;
using System.Runtime.Intrinsics.X86;
using PCPerfSuite.Core.Compatibility;

namespace PCPerfSuite.Core.Hardware.Cpu.Throttle;

/// <summary>
/// Bridage d'un processeur Intel, lu dans ses registres MSR par le module IntelMSR de PawnIO (sa propre instance :
/// le relevé ne partage pas celle des réglages de puissance). En lecture seule : TjMax, fréquences annoncées, état
/// thermique du paquet à chaque relevé, raisons fines si le module les autorise, et, pendant une mesure seulement,
/// un balayage des cœurs. Ne lève jamais (règle 2) ; à appeler depuis le seul thread du relevé.
/// </summary>
internal sealed class IntelThrottleReader : IDisposable
{
    private const string ReadFunction = "ioctl_read_msr";

    /// <summary>Accès refusé par le module (registre hors de sa liste blanche).</summary>
    private const uint AccessDenied = 0xD0000022;

    private readonly PawnIoModule? _msr;
    private readonly IntelThermalFeatures _features;
    private readonly double? _timeUnitSeconds;
    private readonly Dictionary<(ushort, byte), (ulong Aperf, ulong Mperf)> _lastCounters = new();

    private (ulong Value, long Timestamp)? _lastPerfStatus;
    private Unavailable? _fineReasonsUnavailable;

    private IntelThrottleReader(PawnIoModule? msr, IntelThermalFeatures features, Unavailable? unavailable)
    {
        _msr = msr;
        _features = features;
        Unavailable = unavailable;
        if (msr is null) return;

        (TjMaxC, TccOffsetC) = TryRead(IntelThrottleDecoder.TemperatureTargetMsr, out ulong target)
            ? IntelThrottleDecoder.DecodeTemperatureTarget(target)
            : (null, null);
        BaseMhz = TryRead(IntelThrottleDecoder.PlatformInfoMsr, out ulong platform) ? IntelThrottleDecoder.DecodeBaseMhz(platform) : null;
        MaxTurboMhz = TryRead(IntelThrottleDecoder.TurboRatioLimitMsr, out ulong turbo) ? IntelThrottleDecoder.DecodeMaxTurboMhz(turbo) : null;
        _timeUnitSeconds = TryRead(IntelThrottleDecoder.PowerUnitMsr, out ulong units) ? IntelThrottleDecoder.DecodeTimeUnitSeconds(units) : null;
    }

    public int? TjMaxC { get; }
    public int? TccOffsetC { get; }
    public double? BaseMhz { get; }
    public double? MaxTurboMhz { get; }

    /// <summary>Pourquoi les MSR ne sont pas lus, null s'ils le sont.</summary>
    public Unavailable? Unavailable { get; }

    public static IntelThrottleReader Create()
    {
        IntelThermalFeatures features = ReadFeatures();
        try
        {
            PawnIoModule? module = PawnIoDriver.TryLoadModule("IntelMSR", out string? error, ReadFunction);
            if (module is null || !module.Supports(ReadFunction))
            {
                module?.Dispose();
                return new IntelThrottleReader(null, features, new Unavailable(UnavailableCause.MissingRights,
                    $"registres MSR illisibles ({error ?? "module IntelMSR de PawnIO sans lecture"})"));
            }
            return new IntelThrottleReader(module, features, null);
        }
        catch (Exception ex)
        {
            return new IntelThrottleReader(null, features, new Unavailable(UnavailableCause.MissingRights,
                $"registres MSR illisibles ({ex.GetType().Name})"));
        }
    }

    /// <summary>Relevé du bridage. <paramref name="includeCores"/> : balayer aussi chaque processeur logique (mesure en
    /// cours seulement ; l'appelant en limite le rythme).</summary>
    public CpuThrottleReading Read(bool includeCores)
    {
        if (_msr is null)
        {
            return new CpuThrottleReading { Source = CpuThrottleSource.WindowsCounters, Unavailable = Unavailable };
        }

        PackageThermalStatus package = TryRead(IntelThrottleDecoder.PackageThermStatusMsr, out ulong raw)
            ? IntelThrottleDecoder.DecodePackage(raw, _features)
            : new PackageThermalStatus(null, null, null);

        CoreThrottleSweep? cores = includeCores ? SweepCores() : null;

        return new CpuThrottleReading
        {
            Source = CpuThrottleSource.IntelMsr,
            Thermal = package.Thermal ?? (cores is { } c1 ? c1.Thermal > 0 : null),
            Prochot = package.Prochot ?? (cores is { } c2 ? c2.Prochot > 0 : null),
            PowerLimit = package.PowerLimit ?? (cores is { } c3 && _features.PowerLimitNotification ? c3.PowerLimit > 0 : null),
            CurrentLimit = cores?.CurrentLimit is { } current ? current > 0 : null,
            CrossDomain = cores?.CrossDomain is { } cross ? cross > 0 : null,
            TjMaxC = TjMaxC,
            TccOffsetC = TccOffsetC,
            BaseMhz = BaseMhz,
            MaxTurboMhz = MaxTurboMhz,
            PowerThrottledPercent = ReadPowerThrottled(),
            FineReasons = ReadFineReasons(),
            FineReasonsUnavailable = _fineReasonsUnavailable,
            Cores = cores,
        };
    }

    private double? ReadPowerThrottled()
    {
        if (_timeUnitSeconds is not { } unit || !TryRead(IntelThrottleDecoder.PackagePerfStatusMsr, out ulong value)) return null;

        long now = Stopwatch.GetTimestamp();
        double? percent = _lastPerfStatus is { } last
            ? IntelThrottleDecoder.ThrottledPercent(last.Value, value, unit, Stopwatch.GetElapsedTime(last.Timestamp, now))
            : null;
        _lastPerfStatus = (value, now);
        return percent;
    }

    /// <summary>0x64F, tenté une seule fois s'il est refusé : le module IntelMSR 0.2.11 ne l'autorise pas.</summary>
    private IntelPerfLimitReasons? ReadFineReasons()
    {
        if (_fineReasonsUnavailable is not null || _msr is null) return null;

        if (_msr.TryExecute(ReadFunction, [IntelThrottleDecoder.PerfLimitReasonsMsr], 1, out ulong[] output) && output.Length > 0)
        {
            return IntelThrottleDecoder.DecodePerfLimitReasons(output[0]);
        }

        string version = _msr.Info.Version is { Length: > 0 } v ? $" {v}" : "";
        _fineReasonsUnavailable = (uint)_msr.LastError == AccessDenied
            ? new Unavailable(UnavailableCause.HardwareOrDriver,
                $"raisons fines (MSR 0x64F) non autorisées par le module PawnIO IntelMSR{version}")
            : new Unavailable(UnavailableCause.HardwareOrDriver,
                $"raisons fines (MSR 0x64F) illisibles : {PawnIoModule.DescribeError(_msr.LastError)}");
        return null;
    }

    private CoreThrottleSweep? SweepCores()
    {
        using ThreadPinning? pinning = ThreadPinning.Begin();
        if (pinning is null) return null;

        int read = 0, thermal = 0, prochot = 0, power = 0, current = 0, cross = 0;
        var frequencies = new List<double>();

        foreach ((ushort group, byte number) in ThreadPinning.LogicalProcessors())
        {
            if (!pinning.PinTo(group, number) || !TryRead(IntelThrottleDecoder.ThermStatusMsr, out ulong raw)) continue;

            read++;
            CoreThermalStatus status = IntelThrottleDecoder.DecodeCore(raw, _features);
            if (status.Thermal) thermal++;
            if (status.Prochot) prochot++;
            if (status.PowerLimit == true) power++;
            if (status.CurrentLimit == true) current++;
            if (status.CrossDomain == true) cross++;

            if (_features.AperfMperf && BaseMhz is { } baseMhz
                && TryRead(IntelThrottleDecoder.MperfMsr, out ulong mperf) && TryRead(IntelThrottleDecoder.AperfMsr, out ulong aperf))
            {
                if (_lastCounters.TryGetValue((group, number), out var last)
                    && IntelThrottleDecoder.EffectiveMhz(last.Aperf, aperf, last.Mperf, mperf, baseMhz) is { } mhz)
                {
                    frequencies.Add(mhz);
                }
                _lastCounters[(group, number)] = (aperf, mperf);
            }
        }

        if (read == 0) return null;
        return new CoreThrottleSweep(read, thermal, prochot, power,
            _features.CurrentAndCrossDomainLimits ? current : null,
            _features.CurrentAndCrossDomainLimits ? cross : null,
            frequencies.Count > 0 ? frequencies.Average() : null,
            frequencies.Count > 0 ? frequencies.Max() : null);
    }

    private bool TryRead(uint msr, out ulong value)
    {
        value = 0;
        if (_msr is null) return false;
        if (!_msr.TryExecute(ReadFunction, [msr], 1, out ulong[] output) || output.Length == 0) return false;
        value = output[0];
        return true;
    }

    private static IntelThermalFeatures ReadFeatures()
    {
        try
        {
            if (!X86Base.IsSupported) return IntelThermalFeatures.None;
            (int eax, _, int ecx, _) = X86Base.CpuId(6, 0);
            return IntelThermalFeatures.FromCpuId(eax, ecx);
        }
        catch (Exception)
        {
            return IntelThermalFeatures.None;
        }
    }

    public void Dispose() => _msr?.Dispose();
}
