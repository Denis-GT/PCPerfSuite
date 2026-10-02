namespace PCPerfSuite.Core.Benchmark;

/// <summary>
/// Numéros de version du bench. Un score ne se compare qu'à un score de la même version <see cref="Bench"/> : changer un
/// noyau, une durée de passe ou une taille de tampon oblige à l'incrémenter. La version du runtime .NET est relevée à
/// part dans chaque résultat, car l'app est framework-dependent : le JIT peut changer sous nos pieds.
/// </summary>
public static class BenchVersion
{
    /// <summary>Version des noyaux et du protocole de mesure.</summary>
    public const int Bench = 1;

    /// <summary>Version des messages échangés sur le tube entre l'app et son worker (<see cref="Protocol.BenchMessage"/>).</summary>
    public const int Protocol = 1;

    /// <summary>Composant du journal de session et identifiant de demandeur (bail de réglage, bail de cadence).</summary>
    public const string Requester = "bench";
}

/// <summary>Les tests du bench, choisis un par un dans la page.</summary>
public enum BenchTestKind
{
    CpuMono,
    CpuMulti,
    RamBandwidth,
    RamLatency,
    Disk,
}

public static class BenchTestKinds
{
    public static readonly IReadOnlyList<BenchTestKind> All =
        [BenchTestKind.CpuMono, BenchTestKind.CpuMulti, BenchTestKind.RamBandwidth, BenchTestKind.RamLatency, BenchTestKind.Disk];

    /// <summary>Clé stable (fichiers de résultats, journal de session, messages du tube).</summary>
    public static string Key(BenchTestKind kind) => kind switch
    {
        BenchTestKind.CpuMono => "cpu-mono",
        BenchTestKind.CpuMulti => "cpu-multi",
        BenchTestKind.RamBandwidth => "ram-debit",
        BenchTestKind.RamLatency => "ram-latence",
        BenchTestKind.Disk => "disque",
        _ => "inconnu",
    };

    public static BenchTestKind? Parse(string? key) => key switch
    {
        "cpu-mono" => BenchTestKind.CpuMono,
        "cpu-multi" => BenchTestKind.CpuMulti,
        "ram-debit" => BenchTestKind.RamBandwidth,
        "ram-latence" => BenchTestKind.RamLatency,
        "disque" => BenchTestKind.Disk,
        _ => null,
    };

    public static string Title(BenchTestKind kind) => kind switch
    {
        BenchTestKind.CpuMono => "Processeur, un cœur",
        BenchTestKind.CpuMulti => "Processeur, tous les cœurs",
        BenchTestKind.RamBandwidth => "Mémoire : débit",
        BenchTestKind.RamLatency => "Mémoire : latence",
        BenchTestKind.Disk => "Disque",
        _ => "Test inconnu",
    };
}
