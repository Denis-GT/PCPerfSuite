using PCPerfSuite.Core.Hardware;
using PCPerfSuite.Core.Hardware.Fans;
using PCPerfSuite.Core.Safety;

namespace PCPerfSuite.Core.Benchmark.Session;

/// <summary>Pourquoi le bench s'est arrêté de lui-même.</summary>
public enum BenchStopReason
{
    /// <summary>Processeur au seuil pendant tout le délai.</summary>
    Thermal,

    /// <summary>Le ventilateur du processeur, identifié, est resté à 0 tr/min sous charge.</summary>
    FanStopped,

    /// <summary>Batterie sous le seuil.</summary>
    BatteryLow,

    /// <summary>Plus aucun relevé de capteurs : on ne voit plus rien.</summary>
    SensorsLost,
}

/// <summary>Un relevé réduit à ce que la sécurité regarde.</summary>
public sealed record BenchSafetySample(DateTimeOffset At, float? CpuTempC, float? CpuFanRpm, bool CpuFanIdentified, double? BatteryPercent, bool OnBattery)
{
    /// <summary>Extrait d'un relevé : température paquet (sinon cœur max), ventilateur CPU identifié le plus rapide,
    /// batterie en pourcentage de sa pleine charge.</summary>
    public static BenchSafetySample From(HardwareSnapshot snapshot, DateTimeOffset at)
    {
        List<FanReading> cpuFans = snapshot.Fans.Where(f => f.Category == FanCategory.Cpu).ToList();
        float? rpm = cpuFans.Count == 0 ? null : cpuFans.Max(f => f.Rpm);
        return new BenchSafetySample(at, snapshot.Cpu.PackageTempC ?? snapshot.Cpu.MaxCoreTempC, rpm, cpuFans.Count > 0,
            BatteryPercentOf(snapshot.Battery), snapshot.Battery is { PowerOnline: false });
    }

    public static double? BatteryPercentOf(BatterySnapshot? battery)
    {
        if (battery is not { RemainingMWh: { } remaining, FullChargeMWh: { } full } || full <= 0) return null;
        return Math.Clamp(remaining / full * 100, 0, 100);
    }
}

/// <summary>Verdict d'un relevé : rien, un échauffement en cours, ou un arrêt avec sa raison et un texte.</summary>
public sealed record BenchSafetyVerdict(BenchStopReason? Stop, string? Detail, bool Heating = false);

/// <summary>
/// Arrêts de sécurité du bench, en logique pure : thermique (garde de #2 au seuil de <see cref="BenchThermalPolicy"/>),
/// ventilateur du processeur identifié à 0 tr/min pendant <see cref="FanStoppedDelay"/> sous charge, batterie sous
/// <see cref="BenchPreconditions.BatteryStopPercent"/>, perte de tout relevé pendant le délai de la garde. Un capteur
/// absent ne déclenche rien (règle 2 : on ne refuse pas un PC qu'on ne sait pas lire, on le dit).
/// </summary>
public sealed class BenchSafetyMonitor
{
    public static readonly TimeSpan FanStoppedDelay = TimeSpan.FromSeconds(10);

    private readonly ThermalGuard _guard;
    private readonly BenchThermalLimits _limits;
    private DateTimeOffset? _fanStoppedSince;
    private DateTimeOffset? _lastSampleAt;

    public BenchSafetyMonitor(BenchThermalLimits limits)
    {
        _limits = limits;
        _guard = limits.CreateGuard();
    }

    public BenchThermalLimits Limits => _limits;

    public float? MaxCpuTempC { get; private set; }

    /// <summary>Un relevé pendant un test (<paramref name="underLoad"/> vrai) ou entre deux (faux : le ventilateur peut
    /// s'arrêter légitimement).</summary>
    public BenchSafetyVerdict Note(BenchSafetySample sample, bool underLoad = true)
    {
        _lastSampleAt = sample.At;
        if (sample.CpuTempC is { } temp && (MaxCpuTempC is null || temp > MaxCpuTempC)) MaxCpuTempC = temp;

        ThermalVerdict thermal = _guard.Note(sample.At, [sample.CpuTempC]);
        if (thermal.State == ThermalState.Tripped)
        {
            return new BenchSafetyVerdict(BenchStopReason.Thermal, $"processeur à {thermal.TemperatureC:0} °C pendant {_limits.Delay.TotalSeconds:0} s (seuil {_limits.CpuThresholdC:0} °C)");
        }
        if (thermal.State == ThermalState.Lost)
        {
            return new BenchSafetyVerdict(BenchStopReason.SensorsLost, $"plus aucune température lue depuis {_limits.LossDelay.TotalSeconds:0} s");
        }

        if (sample.OnBattery && sample.BatteryPercent is { } percent && percent < BenchPreconditions.BatteryStopPercent)
        {
            return new BenchSafetyVerdict(BenchStopReason.BatteryLow, $"batterie à {percent:0} %");
        }

        if (underLoad && sample.CpuFanIdentified && sample.CpuFanRpm is { } rpm && rpm <= 0)
        {
            _fanStoppedSince ??= sample.At;
            if (sample.At - _fanStoppedSince >= FanStoppedDelay)
            {
                _fanStoppedSince = null;
                return new BenchSafetyVerdict(BenchStopReason.FanStopped, $"ventilateur du processeur à 0 tr/min depuis {FanStoppedDelay.TotalSeconds:0} s");
            }
        }
        else
        {
            _fanStoppedSince = null;
        }

        return new BenchSafetyVerdict(null, thermal.State == ThermalState.Heating ? $"processeur à {thermal.TemperatureC:0} °C, au seuil" : null, thermal.State == ThermalState.Heating);
    }

    /// <summary>Quand aucun relevé n'arrive plus : perte passé le délai, à condition d'avoir déjà lu quelque chose.</summary>
    public BenchSafetyVerdict NoteNoReading(DateTimeOffset now)
    {
        if (_lastSampleAt is not { } last) return new BenchSafetyVerdict(null, null);
        if (now - last >= _limits.LossDelay)
        {
            _guard.Reset();
            _lastSampleAt = null;
            return new BenchSafetyVerdict(BenchStopReason.SensorsLost, $"aucun relevé de capteurs depuis {_limits.LossDelay.TotalSeconds:0} s");
        }
        return new BenchSafetyVerdict(null, null);
    }

    public void Reset()
    {
        _guard.Reset();
        _fanStoppedSince = null;
        _lastSampleAt = null;
    }

    public static string Label(BenchStopReason reason) => reason switch
    {
        BenchStopReason.Thermal => "arrêt de sécurité thermique",
        BenchStopReason.FanStopped => "ventilateur du processeur arrêté",
        BenchStopReason.BatteryLow => "batterie trop faible",
        BenchStopReason.SensorsLost => "capteurs muets",
        _ => "arrêt de sécurité",
    };
}
