namespace PCPerfSuite.Core.Hardware.Cpu.CoreParking;

/// <summary>Un réglage du parking des cœurs, dans le plan d'alimentation Windows.</summary>
public sealed class CoreParkingSetting
{
    public required string Id { get; init; }
    public required Guid Guid { get; init; }

    /// <summary>Nom court de Windows (CPMINCORES…), celui que montre <c>powercfg /qh</c> : repris dans le diagnostic.</summary>
    public required string Alias { get; init; }

    public required string Label { get; init; }

    /// <summary>Libellé sur un processeur hybride, où le réglage sans suffixe ne vise que les cœurs efficaces.</summary>
    public string? HybridLabel { get; init; }
    public required string Description { get; init; }

    /// <summary>Valeurs possibles d'un réglage à liste, null pour un pourcentage.</summary>
    public IReadOnlyList<CpuPowerChoice>? Choices { get; init; }

    public uint Min { get; init; }
    public uint Max { get; init; } = 100;

    /// <summary>Réglage d'ordonnancement des processeurs hybrides, rangé sous « Avancé ».</summary>
    public bool IsAdvanced { get; init; }

    /// <summary>Nombre de cœurs actifs (CPMINCORES, CPMAXCORES et leurs variantes) : ce que changent les préréglages.</summary>
    public bool IsCoreCount { get; init; }

    /// <summary>Plancher (CPMINCORES) plutôt que plafond (CPMAXCORES).</summary>
    public bool IsMinimum { get; init; }

    /// <summary>Le libellé qui convient à ce processeur.</summary>
    public string LabelFor(bool isHybrid) => isHybrid && HybridLabel is { } hybrid ? hybrid : Label;

    /// <summary>La valeur en clair : « 100 % », ou le libellé du choix.</summary>
    public string Format(uint value)
        => Choices is { } choices ? choices.FirstOrDefault(c => c.Value == value)?.Label ?? $"valeur {value}" : $"{value} %";

    /// <summary>Ramène une valeur dans ce que le réglage accepte, null pour un choix absent de la liste.</summary>
    public uint? Sanitize(uint value)
        => Choices is { } choices ? (choices.Any(c => c.Value == value) ? value : null) : Math.Clamp(value, Min, Max);
}

/// <summary>
/// Réglages du parking des cœurs du sous-groupe « Gestion de l'alimentation du processeur » (GUID confirmés par
/// <c>powercfg /qh</c> sur Windows 11 22631). Sur un processeur hybride, les réglages sans suffixe visent la classe
/// d'efficacité 0 (les cœurs efficaces), ceux suffixés « 1 » la classe 1 (les cœurs performants).
///
/// Indépendants des profils processeur (décision de Denis du 01/10/2026) : ils ne sont pas dans le catalogue de
/// <see cref="CpuPowerTuningService"/>, donc ni dans CpuProfile ni dans les groupes de profils.
/// </summary>
public static class CoreParkingCatalog
{
    public static readonly Guid SubGroup = new("54533251-82be-4824-96c1-47b60b740d00");

    private static readonly CpuPowerChoice[] SchedulingChoices =
    [
        new(0, "Tous les cœurs"),
        new(1, "Cœurs performants"),
        new(2, "Préférer les cœurs performants"),
        new(3, "Cœurs efficaces"),
        new(4, "Préférer les cœurs efficaces"),
        new(5, "Automatique"),
    ];

    public static CoreParkingSetting MinCores { get; } = new()
    {
        Id = "min-cores",
        Guid = new Guid("0cc5b647-c1df-4637-891a-dec35c318583"),
        Alias = "CPMINCORES",
        Label = "Cœurs toujours actifs",
        HybridLabel = "Cœurs efficaces toujours actifs",
        Description = "Part des cœurs que Windows ne parque jamais. 100 % désactive le parking : tous les cœurs restent disponibles, au prix d'une consommation et d'une chauffe plus élevées au repos.",
        IsCoreCount = true,
        IsMinimum = true,
    };

    public static CoreParkingSetting MaxCores { get; } = new()
    {
        Id = "max-cores",
        Guid = new Guid("ea062031-0e34-4ff1-9b6d-eb1059334028"),
        Alias = "CPMAXCORES",
        Label = "Cœurs actifs au maximum",
        HybridLabel = "Cœurs efficaces actifs au maximum",
        Description = "Part des cœurs que Windows peut garder actifs en même temps. Sous 100 %, les autres restent parqués même en pleine charge : moins de chauffe et de consommation, moins de performances en multicœur.",
        IsCoreCount = true,
    };

    public static CoreParkingSetting MinCoresPerformance { get; } = new()
    {
        Id = "min-cores-perf",
        Guid = new Guid("0cc5b647-c1df-4637-891a-dec35c318584"),
        Alias = "CPMINCORES1",
        Label = "Cœurs performants toujours actifs",
        Description = "Même plancher, pour les seuls cœurs performants (P-cores). Le réglage précédent vise alors les cœurs efficaces : écrire seulement celui-là laisse les cœurs performants parqués.",
        IsCoreCount = true,
        IsMinimum = true,
    };

    public static CoreParkingSetting MaxCoresPerformance { get; } = new()
    {
        Id = "max-cores-perf",
        Guid = new Guid("ea062031-0e34-4ff1-9b6d-eb1059334029"),
        Alias = "CPMAXCORES1",
        Label = "Cœurs performants actifs au maximum",
        Description = "Même plafond, pour les seuls cœurs performants.",
        IsCoreCount = true,
    };

    public static CoreParkingSetting SchedulingPolicy { get; } = new()
    {
        Id = "sched-policy",
        Guid = new Guid("93b8b6dc-0698-4d1c-9ee4-0644e900c85d"),
        Alias = "SCHEDPOLICY",
        Label = "Placement des tâches longues",
        Description = "Sur quels cœurs Windows place les tâches qui durent. « Automatique » laisse décider Windows (et le Thread Director sur Intel).",
        Choices = SchedulingChoices,
        Min = 0,
        Max = 5,
        IsAdvanced = true,
    };

    public static CoreParkingSetting ShortSchedulingPolicy { get; } = new()
    {
        Id = "short-sched-policy",
        Guid = new Guid("bae08b81-2d5e-4688-ad6a-13243356654b"),
        Alias = "SHORTSCHEDPOLICY",
        Label = "Placement des tâches courtes",
        Description = "Même choix, pour les tâches brèves.",
        Choices = SchedulingChoices,
        Min = 0,
        Max = 5,
        IsAdvanced = true,
    };

    public static CoreParkingSetting HeteroPolicy { get; } = new()
    {
        Id = "hetero-policy",
        Guid = new Guid("7f2f5cfa-f10c-4823-b5e1-e93ae85f46b5"),
        Alias = "HETEROPOLICY",
        Label = "Stratégie hybride en vigueur",
        Description = "Jeu de règles hybrides que suit Windows, numéroté de 0 à 4 sans autre documentation. Pour les essais : garde la valeur d'origine sans raison précise.",
        Choices = [new(0, "Stratégie 0"), new(1, "Stratégie 1"), new(2, "Stratégie 2"), new(3, "Stratégie 3"), new(4, "Stratégie 4")],
        Min = 0,
        Max = 4,
        IsAdvanced = true,
    };

    /// <summary>Tout le catalogue, y compris ce qui ne vaut que sur un processeur hybride : la restauration le parcourt
    /// en entier.</summary>
    public static IReadOnlyList<CoreParkingSetting> All { get; } =
        [MinCores, MaxCores, MinCoresPerformance, MaxCoresPerformance, SchedulingPolicy, ShortSchedulingPolicy, HeteroPolicy];

    /// <summary>Les réglages qui ont un sens sur ce processeur : les variantes des cœurs performants et l'ordonnancement
    /// hybride seulement s'il y a au moins deux classes de cœurs.</summary>
    public static IReadOnlyList<CoreParkingSetting> For(bool isHybrid) => isHybrid ? All : [MinCores, MaxCores];

    /// <summary>Clé de l'origine dans <c>AppSettings.OriginalPowerValues</c>, au format du tweak d'Optimisation Windows
    /// (« sous-groupe/réglage »), suivie de « /ac » ou « /dc ».</summary>
    public static string OriginKey(CoreParkingSetting setting) => $"{SubGroup:D}/{setting.Guid:D}";
}
