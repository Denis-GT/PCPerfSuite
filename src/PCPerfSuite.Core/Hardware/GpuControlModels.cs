namespace PCPerfSuite.Core.Hardware;

/// <summary>Marque du GPU piloté, et donc de l'API constructeur utilisée : NVAPI (NVIDIA), ADLX (AMD)
/// ou IGCL (Intel).</summary>
public enum GpuVendor
{
    Nvidia,
    Amd,
    Intel,
}

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
    public GpuVendor Vendor { get; init; }

    /// <summary>Faux quand le pilote n'expose pas la limite de puissance pour cette carte.</summary>
    public bool PowerLimitSupported { get; init; } = true;

    public float PowerLimitPercent { get; init; }
    public float PowerLimitMinPercent { get; init; } = 50;
    public float PowerLimitMaxPercent { get; init; } = 100;
    public float PowerLimitDefaultPercent { get; init; } = 100;

    public IReadOnlyList<GpuFanInfo> Fans { get; init; } = Array.Empty<GpuFanInfo>();
}

/// <summary>Unité du réglage de tension, qui dépend de la marque : NVIDIA (Pascal) raisonne en % de la
/// surtension autorisée, AMD et Intel en millivolts (ou en % sur certaines Arc récentes).</summary>
public enum GpuVoltageUnit
{
    Percent,
    Millivolts,
}

/// <summary>
/// État d'overclocking lu via l'API du constructeur : décalages d'horloge cœur/mémoire, limite de
/// température et tension. Chaque bloc a son propre indicateur "supporté" : selon la marque, la
/// génération de GPU et le pilote, une partie seulement est disponible, et ce qui ne l'est pas est
/// masqué (avec une explication) dans l'interface plutôt que de faire échouer tout le reste.
/// </summary>
public sealed class GpuOverclockSnapshot
{
    public bool CoreOffsetSupported { get; init; }
    public bool MemoryOffsetSupported { get; init; }
    public bool ClockOffsetsSupported => CoreOffsetSupported || MemoryOffsetSupported;

    public int CoreOffsetMhz { get; init; }
    public int CoreOffsetMinMhz { get; init; }
    public int CoreOffsetMaxMhz { get; init; }
    public int MemoryOffsetMhz { get; init; }
    public int MemoryOffsetMinMhz { get; init; }
    public int MemoryOffsetMaxMhz { get; init; }

    /// <summary>Unité affichée pour le décalage mémoire : "MHz" en général, "MT/s" ou "Mbps" chez Intel,
    /// dont le pilote exprime la vitesse mémoire en débit plutôt qu'en fréquence.</summary>
    public string MemoryOffsetUnit { get; init; } = "MHz";

    public bool TemperatureLimitSupported { get; init; }
    public int TemperatureLimitC { get; init; }
    public int TemperatureLimitMinC { get; init; }
    public int TemperatureLimitMaxC { get; init; }
    public int TemperatureLimitDefaultC { get; init; }

    /// <summary>Réglage de tension : surtension NVAPI réservée aux GPU Pascal, tension ou décalage de
    /// tension chez AMD/Intel — d'où le test à l'exécution plutôt qu'une hypothèse sur le modèle.</summary>
    public bool VoltageSupported { get; init; }

    public int Voltage { get; init; }
    public int VoltageMin { get; init; }
    public int VoltageMax { get; init; } = 100;
    public int VoltageDefault { get; init; }
    public GpuVoltageUnit VoltageUnit { get; init; } = GpuVoltageUnit.Percent;

    /// <summary>Vrai quand la valeur est un décalage autour de 0 (affiché signé), faux quand c'est une
    /// tension absolue (Radeon RDNA 1 à 3, où l'on règle directement la tension max en mV).</summary>
    public bool VoltageIsOffset { get; init; }
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

    /// <summary>Ancien champ (surtension NVIDIA en %), conservé pour relire les profils existants :
    /// <see cref="GetVoltage"/> s'en sert quand <see cref="VoltageValue"/> est vide.</summary>
    public int? VoltageBoostPercent { get; set; }

    /// <summary>Tension enregistrée, dans l'unité <see cref="VoltageUnit"/> — un profil n'applique sa
    /// tension que sur une carte qui raisonne dans la même unité (50 % ≠ 50 mV).</summary>
    public int? VoltageValue { get; set; }

    public GpuVoltageUnit? VoltageUnit { get; set; }

    public (int Value, GpuVoltageUnit Unit)? GetVoltage()
    {
        if (VoltageValue is { } value) return (value, VoltageUnit ?? GpuVoltageUnit.Percent);
        if (VoltageBoostPercent is { } legacy) return (legacy, GpuVoltageUnit.Percent);
        return null;
    }
}
