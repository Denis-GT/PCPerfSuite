namespace PCPerfSuite.Core.Hardware;

public sealed class GpuFanInfo
{
    public required int CoolerId { get; init; }
    public int CurrentLevelPercent { get; init; }
    public int CurrentRpm { get; init; }
    public int MinLevelPercent { get; init; }
    public int MaxLevelPercent { get; init; }
}

public sealed class GpuControlSnapshot
{
    public required string Name { get; init; }

    public float PowerLimitPercent { get; init; }
    public float PowerLimitMinPercent { get; init; } = 50;
    public float PowerLimitMaxPercent { get; init; } = 100;
    public float PowerLimitDefaultPercent { get; init; } = 100;

    public IReadOnlyList<GpuFanInfo> Fans { get; init; } = Array.Empty<GpuFanInfo>();
}

/// <summary>
/// État d'overclocking lu via NVAPI : décalages d'horloge (P-States 2.0), limite de température
/// (thermal policies) et surtension cœur. Chaque bloc a son propre indicateur "supporté" : selon la
/// génération de GPU et le pilote, une partie seulement des trois est disponible, et ce qui ne l'est
/// pas est masqué dans l'interface plutôt que de faire échouer tout le reste.
/// </summary>
public sealed class GpuOverclockSnapshot
{
    public bool ClockOffsetsSupported { get; init; }
    public int CoreOffsetMhz { get; init; }
    public int CoreOffsetMinMhz { get; init; }
    public int CoreOffsetMaxMhz { get; init; }
    public int MemoryOffsetMhz { get; init; }
    public int MemoryOffsetMinMhz { get; init; }
    public int MemoryOffsetMaxMhz { get; init; }

    public bool TemperatureLimitSupported { get; init; }
    public int TemperatureLimitC { get; init; }
    public int TemperatureLimitMinC { get; init; }
    public int TemperatureLimitMaxC { get; init; }
    public int TemperatureLimitDefaultC { get; init; }

    /// <summary>Surtension cœur : API NVAPI réservée aux GPU Pascal (GTX 10xx), d'où le test à
    /// l'exécution plutôt qu'une hypothèse sur le modèle.</summary>
    public bool VoltageBoostSupported { get; init; }

    public int VoltageBoostPercent { get; init; }
}

/// <summary>Ce qui bride la carte à l'instant T (plusieurs raisons peuvent se cumuler) — l'équivalent
/// de la ligne "Perf cap reason" de GPU-Z ou d'Afterburner.</summary>
[Flags]
public enum GpuPerformanceLimit
{
    None = 0,
    Power = 1,
    Temperature = 2,
    Voltage = 4,

    /// <summary>La carte n'a simplement pas assez de travail pour monter en fréquence.</summary>
    NoLoad = 8,

    Other = 16,
}

/// <summary>Jeu de réglages d'overclocking enregistré sous un nom, façon profils d'Afterburner.
/// Une valeur nulle signifie "ne pas toucher à ce réglage en appliquant le profil".</summary>
public sealed class GpuOverclockProfile
{
    public string Name { get; set; } = "Profil";
    public int CoreClockOffsetMhz { get; set; }
    public int MemoryClockOffsetMhz { get; set; }
    public float? PowerLimitPercent { get; set; }
    public int? TemperatureLimitC { get; set; }
    public int? VoltageBoostPercent { get; set; }
}
