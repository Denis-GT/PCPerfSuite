namespace PCPerfSuite.Core.Hardware.Cpu.Throttle;

/// <summary>Ce que le processeur annonce savoir signaler (CPUID, feuille 6) : un bit non annoncé n'est jamais lu comme
/// « non bridé », il reste inconnu.</summary>
public sealed record IntelThermalFeatures(
    bool PackageThermal,
    bool PowerLimitNotification,
    bool CurrentAndCrossDomainLimits,
    bool AperfMperf)
{
    public static IntelThermalFeatures None { get; } = new(false, false, false, false);

    /// <summary>D'après CPUID.06H : EAX[6] gestion thermique du paquet (0x1B1), EAX[4] notification de limite de
    /// puissance (bit 10), EAX[7] limites de courant et d'autre domaine (bits 12 et 14 de 0x19C), ECX[0] compteurs
    /// APERF/MPERF. Numéros de bits à vérifier dans le manuel Intel (SDM vol. 4) sur une vraie machine.</summary>
    public static IntelThermalFeatures FromCpuId(int eax, int ecx) => new(
        PackageThermal: (eax & (1 << 6)) != 0,
        PowerLimitNotification: (eax & (1 << 4)) != 0,
        CurrentAndCrossDomainLimits: (eax & (1 << 7)) != 0,
        AperfMperf: (ecx & 1) != 0);
}

/// <summary>État du paquet (IA32_PACKAGE_THERM_STATUS, 0x1B1), bits d'état seulement.</summary>
public sealed record PackageThermalStatus(bool? Thermal, bool? Prochot, bool? PowerLimit);

/// <summary>État d'un cœur (IA32_THERM_STATUS, 0x19C), bits d'état seulement.</summary>
public sealed record CoreThermalStatus(bool Thermal, bool Prochot, bool? PowerLimit, bool? CurrentLimit, bool? CrossDomain);

/// <summary>
/// Décodage pur des registres MSR de bridage d'Intel, sans matériel : testable bit à bit. Seuls les bits d'état sont
/// lus ; les bits « log » (collants, qu'il faudrait effacer par une écriture) sont ignorés, et rien n'est jamais écrit.
/// </summary>
public static class IntelThrottleDecoder
{
    public const uint TemperatureTargetMsr = 0x1A2;
    public const uint PlatformInfoMsr = 0xCE;
    public const uint TurboRatioLimitMsr = 0x1AD;
    public const uint PackageThermStatusMsr = 0x1B1;
    public const uint ThermStatusMsr = 0x19C;
    public const uint PowerUnitMsr = 0x606;
    public const uint PackagePerfStatusMsr = 0x613;
    public const uint PerfLimitReasonsMsr = 0x64F;
    public const uint MperfMsr = 0xE7;
    public const uint AperfMsr = 0xE8;

    /// <summary>Horloge de référence des ratios Intel : 100 MHz depuis Sandy Bridge.</summary>
    public const double BusClockMhz = 100;

    public static PackageThermalStatus DecodePackage(ulong raw, IntelThermalFeatures features)
    {
        if (!features.PackageThermal) return new PackageThermalStatus(null, null, null);
        return new PackageThermalStatus(
            Thermal: Bit(raw, 0),
            Prochot: Bit(raw, 2),
            PowerLimit: features.PowerLimitNotification ? Bit(raw, 10) : null);
    }

    public static CoreThermalStatus DecodeCore(ulong raw, IntelThermalFeatures features) => new(
        Thermal: Bit(raw, 0),
        Prochot: Bit(raw, 2),
        PowerLimit: features.PowerLimitNotification ? Bit(raw, 10) : null,
        CurrentLimit: features.CurrentAndCrossDomainLimits ? Bit(raw, 12) : null,
        CrossDomain: features.CurrentAndCrossDomainLimits ? Bit(raw, 14) : null);

    /// <summary>TjMax (bits 23:16 de 0x1A2) et décalage TCC (bits 29:24 ; seuls 27:24 existent sur les anciens
    /// processeurs, les bits au-dessus y valent 0). Un TjMax hors de 60-130 °C n'est pas plausible : null.</summary>
    public static (int? TjMaxC, int? TccOffsetC) DecodeTemperatureTarget(ulong raw)
    {
        int tjMax = (int)((raw >> 16) & 0xFF);
        if (tjMax is < 60 or > 130) return (null, null);

        int offset = (int)((raw >> 24) & 0x3F);
        return (tjMax, offset < tjMax ? offset : null);
    }

    /// <summary>Ratio maximal hors turbo (bits 15:8 de 0xCE), donc fréquence de base en MHz ; null si nul.</summary>
    public static double? DecodeBaseMhz(ulong raw)
    {
        int ratio = (int)((raw >> 8) & 0xFF);
        return ratio > 0 ? ratio * BusClockMhz : null;
    }

    /// <summary>Turbo maximal sur un cœur (bits 7:0 de 0x1AD). Expérimental sur les hybrides, où ce registre ne décrit
    /// peut-être que les cœurs P.</summary>
    public static double? DecodeMaxTurboMhz(ulong raw)
    {
        int ratio = (int)(raw & 0xFF);
        return ratio > 0 ? ratio * BusClockMhz : null;
    }

    /// <summary>Unité de temps de RAPL (bits 19:16 de 0x606) : 1 / 2^n seconde.</summary>
    public static double DecodeTimeUnitSeconds(ulong raw606) => 1.0 / (1UL << (int)((raw606 >> 16) & 0xF));

    /// <summary>Part du temps bridée par RAPL (0x613, compteur 32 bits en unités de temps RAPL) entre deux relevés, en %.
    /// Null si l'écart de temps est nul ou si le résultat n'est pas plausible.</summary>
    public static double? ThrottledPercent(ulong previous, ulong current, double timeUnitSeconds, TimeSpan elapsed)
    {
        if (elapsed <= TimeSpan.Zero) return null;
        ulong delta = ((current & 0xFFFFFFFF) - (previous & 0xFFFFFFFF)) & 0xFFFFFFFF;
        double percent = delta * timeUnitSeconds / elapsed.TotalSeconds * 100;
        return percent is >= 0 and <= 100.5 ? Math.Min(percent, 100) : null;
    }

    /// <summary>Raisons fines de 0x64F (processeurs grand public), bits d'état 0 à 13.</summary>
    public static IntelPerfLimitReasons DecodePerfLimitReasons(ulong raw) => new(
        Prochot: Bit(raw, 0),
        Thermal: Bit(raw, 1),
        ResidencyStateRegulation: Bit(raw, 4),
        RunningAverageThermal: Bit(raw, 5),
        VoltageRegulatorThermal: Bit(raw, 6),
        VoltageRegulatorCurrent: Bit(raw, 7),
        ElectricalDesign: Bit(raw, 8),
        PackagePl1: Bit(raw, 10),
        PackagePl2: Bit(raw, 11),
        MaxTurboLimit: Bit(raw, 12),
        TurboTransitionAttenuation: Bit(raw, 13));

    /// <summary>
    /// Fréquence effective d'un processeur logique entre deux lectures : fréquence de base × ΔAPERF / ΔMPERF (les deux
    /// compteurs n'avancent que pendant que le processeur travaille). Compteurs 64 bits : un débordement est absorbé
    /// par la soustraction non signée. Null si MPERF n'a pas avancé (processeur resté au repos) ou si le résultat est
    /// hors de toute plausibilité.
    /// </summary>
    public static double? EffectiveMhz(ulong aperfBefore, ulong aperfAfter, ulong mperfBefore, ulong mperfAfter, double baseMhz)
    {
        ulong aperf = unchecked(aperfAfter - aperfBefore);
        ulong mperf = unchecked(mperfAfter - mperfBefore);
        if (mperf == 0 || baseMhz <= 0) return null;

        double mhz = baseMhz * aperf / mperf;
        return mhz is > 100 and < 10_000 ? mhz : null;
    }

    private static bool Bit(ulong raw, int bit) => (raw & (1UL << bit)) != 0;
}
