using System.Diagnostics;
using System.Globalization;
using PCPerfSuite.Core.Compatibility;

namespace PCPerfSuite.Core.Hardware.Cpu.Throttle;

/// <summary>
/// Bridage d'un processeur AMD d'après la PM table du SMU (module RyzenSMU de PawnIO, sa propre instance) : PPT, TDC,
/// EDC et THM, valeur du moment et limite, pour les versions de table cartographiées (<see cref="PmTableLayouts"/>).
/// Lu une fois par seconde au plus, sous le mutex partagé du SMU, sans jamais l'attendre plus de 50 ms : s'il est tenu
/// ailleurs, la dernière valeur est gardée. Ne lève jamais (règle 2) ; à appeler depuis le seul thread du relevé.
/// </summary>
internal sealed class AmdThrottleReader : IDisposable
{
    private static readonly TimeSpan GuardTimeout = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(1);

    private readonly PawnIoModule? _smu;
    private PmTableLayout? _layout;
    private uint? _version;
    private CpuThrottleReading? _last;
    private long _lastReadTimestamp;

    private AmdThrottleReader(PawnIoModule? smu, Unavailable? unavailable)
    {
        _smu = smu;
        Unavailable = unavailable;
    }

    /// <summary>Pourquoi la PM table n'est pas lue, null tant qu'elle l'est.</summary>
    public Unavailable? Unavailable { get; private set; }

    /// <summary>Version de la PM table lue, pour le diagnostic (null tant qu'elle n'a pas été lue).</summary>
    public uint? TableVersion => _version;

    public static AmdThrottleReader Create()
    {
        try
        {
            PawnIoModule? smu = PawnIoDriver.TryLoadModule("RyzenSMU", out string? error);
            return smu is null
                ? new AmdThrottleReader(null, new Unavailable(UnavailableCause.MissingRights,
                    $"PM table illisible ({error ?? "module RyzenSMU de PawnIO indisponible"})"))
                : new AmdThrottleReader(smu, null);
        }
        catch (Exception ex)
        {
            return new AmdThrottleReader(null, new Unavailable(UnavailableCause.MissingRights, $"PM table illisible ({ex.GetType().Name})"));
        }
    }

    public CpuThrottleReading Read()
    {
        if (_smu is null || (_version is not null && _layout is null))
        {
            return new CpuThrottleReading { Source = CpuThrottleSource.WindowsCounters, Unavailable = Unavailable };
        }

        if (_last is not null && Stopwatch.GetElapsedTime(_lastReadTimestamp) < MinInterval) return _last;

        CpuThrottleReading? reading = TryReadTable();
        if (reading is null) return _last ?? new CpuThrottleReading { Source = CpuThrottleSource.WindowsCounters, Unavailable = Unavailable };

        _last = reading;
        _lastReadTimestamp = Stopwatch.GetTimestamp();
        return reading;
    }

    private CpuThrottleReading? TryReadTable()
    {
        using var guard = new PciBusGuard(GuardTimeout);
        if (!guard.IsHeld && !guard.IsUnavailable) return null; // un autre outil parle au SMU : on garde la valeur d'avant

        if (_version is null)
        {
            // Sortie 0 : version de la table, sortie 1 : son adresse (RyzenSMU.p).
            if (!_smu!.TryExecute("ioctl_resolve_pm_table", [], 2, out ulong[] resolved, out int count) || count < 1)
            {
                Unavailable = new Unavailable(UnavailableCause.HardwareOrDriver,
                    $"PM table introuvable ({PawnIoModule.DescribeError(_smu.LastError)})");
                return null;
            }

            _version = (uint)resolved[0];
            _layout = PmTableLayouts.For(_version.Value);
            if (_layout is null)
            {
                Unavailable = new Unavailable(UnavailableCause.UnsupportedModel,
                    $"PM table version 0x{_version.Value.ToString("X6", CultureInfo.InvariantCulture)} pas encore prise en charge");
                return null;
            }
        }

        if (_layout is not { } layout) return null;

        if (!_smu!.TryExecute("ioctl_update_pm_table", [], 0, out _)
            || !_smu.TryExecute("ioctl_read_pm_table", [], layout.QwordsNeeded, out ulong[] table, out int returned))
        {
            Unavailable = new Unavailable(UnavailableCause.HardwareOrDriver,
                $"PM table illisible ({PawnIoModule.DescribeError(_smu.LastError)})");
            return null;
        }

        if (PmTableLayouts.Decode(layout, PmTableLayouts.ToFloats(table, returned)) is not { } limits)
        {
            Unavailable = new Unavailable(UnavailableCause.UnsupportedModel,
                $"PM table version 0x{layout.Version.ToString("X6", CultureInfo.InvariantCulture)} : valeurs non plausibles, lecture écartée");
            return null;
        }

        Unavailable = null;
        return new CpuThrottleReading
        {
            Source = CpuThrottleSource.AmdPmTable,
            Thermal = limits.ThmAtLimit,
            PowerLimit = limits.PptAtLimit,
            CurrentLimit = limits.TdcAtLimit || limits.EdcAtLimit,
            TjMaxC = limits.ThmLimitC is { } thm ? (int)Math.Round(thm) : null,
            Amd = limits,
        };
    }

    public void Dispose() => _smu?.Dispose();
}
