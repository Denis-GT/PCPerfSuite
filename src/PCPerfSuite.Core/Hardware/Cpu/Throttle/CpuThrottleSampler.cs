using System.Diagnostics;
using PCPerfSuite.Core.Compatibility;

namespace PCPerfSuite.Core.Hardware.Cpu.Throttle;

/// <summary>
/// Relevé du bridage CPU pour le relevé des capteurs : la source bas niveau du processeur (MSR Intel ou PM table AMD),
/// complétée des compteurs de Windows, toujours lus. Le balayage des cœurs n'a lieu que pendant une mesure (bail de
/// cadence sur le CPU), deux fois par seconde au plus : en permanence, il réveillerait les cœurs parqués et alourdirait
/// la lecture du CPU dont dépendent les courbes de ventilation. Créé à la première lecture, sur le thread du relevé.
/// </summary>
internal sealed class CpuThrottleSampler : IDisposable
{
    private static readonly TimeSpan CoreSweepInterval = TimeSpan.FromMilliseconds(500);

    private readonly IntelThrottleReader? _intel;
    private readonly AmdThrottleReader? _amd;
    private readonly ProcessorPerformanceCounters _counters = new();
    private readonly Unavailable? _platformUnavailable;
    private long _lastSweepTimestamp;

    public CpuThrottleSampler(CpuPlatform platform)
    {
        if (!platform.IsX64)
        {
            _platformUnavailable = new Unavailable(UnavailableCause.HardwareOrDriver,
                "le pilote PawnIO n'existe que pour les processeurs x64 : compteurs Windows seulement");
        }
        else if (platform.Vendor == CpuVendor.Intel)
        {
            _intel = IntelThrottleReader.Create();
        }
        else if (platform.Vendor == CpuVendor.Amd)
        {
            _amd = AmdThrottleReader.Create();
        }
        else
        {
            _platformUnavailable = new Unavailable(UnavailableCause.UnsupportedModel,
                $"processeur {platform.VendorLabel} : pas de lecture bas niveau du bridage, compteurs Windows seulement");
        }
    }

    /// <param name="measuring">Une mesure est en cours (bail de cadence sur le CPU) : balayer aussi les cœurs.</param>
    public CpuThrottleReading Read(bool measuring)
    {
        ProcessorPerformanceSample counters = _counters.Sample();

        CpuThrottleReading reading;
        if (_intel is not null)
        {
            bool sweep = measuring && Stopwatch.GetElapsedTime(_lastSweepTimestamp) >= CoreSweepInterval;
            if (sweep) _lastSweepTimestamp = Stopwatch.GetTimestamp();
            reading = _intel.Read(sweep);
        }
        else if (_amd is not null)
        {
            reading = _amd.Read();
        }
        else
        {
            reading = new CpuThrottleReading { Source = CpuThrottleSource.WindowsCounters, Unavailable = _platformUnavailable };
        }

        return reading with
        {
            PerformancePercent = counters.PerformancePercent,
            EffectiveMhz = counters.EffectiveMhz,
            PerformanceLimitPercent = counters.PerformanceLimitPercent,
            PerformanceLimitFlags = counters.PerformanceLimitFlags,
        };
    }

    public void Dispose()
    {
        _intel?.Dispose();
        _amd?.Dispose();
        _counters.Dispose();
    }
}
