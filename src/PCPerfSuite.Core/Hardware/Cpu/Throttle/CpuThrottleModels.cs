using PCPerfSuite.Core.Compatibility;

namespace PCPerfSuite.Core.Hardware.Cpu.Throttle;

/// <summary>D'où vient la lecture du bridage CPU.</summary>
public enum CpuThrottleSource
{
    /// <summary>Registres MSR d'Intel, par le module IntelMSR de PawnIO (bits d'état thermique, PROCHOT, puissance).</summary>
    IntelMsr,

    /// <summary>PM table du SMU d'AMD, par le module RyzenSMU de PawnIO (PPT, TDC, EDC, THM et leurs limites).</summary>
    AmdPmTable,

    /// <summary>Compteurs de performances de Windows seulement, sans pilote : un indice, pas une raison.</summary>
    WindowsCounters,
}

/// <summary>
/// Raisons fines du bridage Intel (MSR_CORE_PERF_LIMIT_REASONS, 0x64F), bits d'état seulement. Le module IntelMSR de
/// PawnIO 0.2.11 refuse ce registre : ces raisons ne sont lues que si une version future du module l'autorise.
/// Expérimental : la signification des bits varie selon les générations.
/// </summary>
public sealed record IntelPerfLimitReasons(
    bool Prochot,
    bool Thermal,
    bool ResidencyStateRegulation,
    bool RunningAverageThermal,
    bool VoltageRegulatorThermal,
    bool VoltageRegulatorCurrent,
    bool ElectricalDesign,
    bool PackagePl1,
    bool PackagePl2,
    bool MaxTurboLimit,
    bool TurboTransitionAttenuation)
{
    public bool Any => Prochot || Thermal || ResidencyStateRegulation || RunningAverageThermal || VoltageRegulatorThermal
        || VoltageRegulatorCurrent || ElectricalDesign || PackagePl1 || PackagePl2 || MaxTurboLimit || TurboTransitionAttenuation;
}

/// <summary>
/// Balayage des processeurs logiques (MSR par cœur, réservé aux mesures sous bail de cadence) : combien de processeurs
/// lus, combien bridés par chaque raison au moment du relevé, et leur fréquence effective (APERF/MPERF) pendant qu'ils
/// travaillaient. La vue cœur par cœur est l'affaire de l'onglet Processeur (#5).
/// </summary>
public sealed record CoreThrottleSweep(
    int LogicalProcessorsRead,
    int Thermal,
    int Prochot,
    int PowerLimit,
    int? CurrentLimit,
    int? CrossDomain,
    double? EffectiveMhzAverage,
    double? EffectiveMhzMax);

/// <summary>
/// Limites du processeur AMD d'après la PM table du SMU : valeur du moment et limite, pour la puissance du socket
/// (PPT, W), le courant soutenu (TDC, A) et de pointe (EDC, A) du régulateur, et la température (THM, °C).
/// Expérimental : les positions dans la table ne sont pas documentées par AMD et changent avec sa version.
/// </summary>
public sealed record AmdPowerLimits(
    uint TableVersion,
    float? PptWatts, float? PptLimitWatts,
    float? TdcAmps, float? TdcLimitAmps,
    float? EdcAmps, float? EdcLimitAmps,
    float? ThmC, float? ThmLimitC)
{
    /// <summary>Part de la limite au-delà de laquelle une valeur est dite « à sa limite ».</summary>
    public const float AtLimitRatio = 0.97f;

    public static bool IsAtLimit(float? value, float? limit) => value is { } v && limit is { } l && l > 0 && v >= l * AtLimitRatio;

    public bool PptAtLimit => IsAtLimit(PptWatts, PptLimitWatts);
    public bool TdcAtLimit => IsAtLimit(TdcAmps, TdcLimitAmps);
    public bool EdcAtLimit => IsAtLimit(EdcAmps, EdcLimitAmps);
    public bool ThmAtLimit => IsAtLimit(ThmC, ThmLimitC);
}

/// <summary>
/// Ce qui bride le processeur au moment du relevé, et d'où vient l'information. Un drapeau null veut dire « non lu sur
/// ce PC » (voir <see cref="Unavailable"/>), jamais « non bridé ». Rien n'est jamais écrit dans le processeur : les
/// bits « log » collants d'Intel ne sont ni lus comme un état ni effacés.
/// </summary>
public sealed record CpuThrottleReading
{
    public required CpuThrottleSource Source { get; init; }

    /// <summary>Au seuil thermique (Intel : bit d'état du paquet ; AMD : THM à sa limite).</summary>
    public bool? Thermal { get; init; }

    /// <summary>PROCHOT : un signal externe (souvent le régulateur ou le contrôleur embarqué d'un portable) bride le
    /// processeur.</summary>
    public bool? Prochot { get; init; }

    /// <summary>À sa limite de puissance (Intel : notification de limite du paquet ; AMD : PPT à sa limite).</summary>
    public bool? PowerLimit { get; init; }

    /// <summary>À sa limite de courant (Intel : balayage par cœur ; AMD : TDC ou EDC à sa limite).</summary>
    public bool? CurrentLimit { get; init; }

    /// <summary>Intel : bridé à cause d'un autre domaine (GPU intégré, anneau).</summary>
    public bool? CrossDomain { get; init; }

    /// <summary>Température maximale de jonction (Intel : 0x1A2 ; AMD : limite THM de la PM table) et décalage TCC
    /// réglé par le BIOS (Intel).</summary>
    public int? TjMaxC { get; init; }
    public int? TccOffsetC { get; init; }

    /// <summary>Température à laquelle le processeur commence à se brider : TjMax moins le décalage TCC.</summary>
    public int? ThrottleTemperatureC => TjMaxC is { } tjMax ? tjMax - (TccOffsetC ?? 0) : null;

    /// <summary>Fréquence de base et turbo maximal annoncés par le processeur (Intel, 0xCE et 0x1AD).</summary>
    public double? BaseMhz { get; init; }
    public double? MaxTurboMhz { get; init; }

    /// <summary>Compteurs Windows (sans pilote) : performance en % de la fréquence nominale (plus de 100 % en turbo),
    /// fréquence effective qui en découle, performance garantie en % de la nominale (100 % sans limite ; en dessous, la
    /// politique d'alimentation, un budget de puissance ou la chaleur retient le processeur) et drapeaux de limite (non
    /// documentés : un indice).</summary>
    public double? PerformancePercent { get; init; }
    public double? EffectiveMhz { get; init; }
    public double? PerformanceLimitPercent { get; init; }
    public uint? PerformanceLimitFlags { get; init; }

    /// <summary>Intel : part du temps bridée par la limite de puissance (0x613) depuis le relevé précédent.
    /// Expérimental : ce compteur reste à zéro sur une partie des processeurs grand public.</summary>
    public double? PowerThrottledPercent { get; init; }

    /// <summary>Intel : raisons fines (0x64F), ou pourquoi elles manquent.</summary>
    public IntelPerfLimitReasons? FineReasons { get; init; }
    public Unavailable? FineReasonsUnavailable { get; init; }

    /// <summary>Intel : balayage par cœur, seulement pendant une mesure (bail de cadence).</summary>
    public CoreThrottleSweep? Cores { get; init; }

    /// <summary>AMD : valeurs et limites de la PM table.</summary>
    public AmdPowerLimits? Amd { get; init; }

    /// <summary>Pourquoi la source bas niveau (MSR ou PM table) manque, avec sa cause (règle 3) ; null si elle est lue.</summary>
    public Unavailable? Unavailable { get; init; }

    /// <summary>Au moins une raison de bridage lue et active.</summary>
    public bool IsThrottled => Thermal == true || Prochot == true || PowerLimit == true || CurrentLimit == true || CrossDomain == true;
}
