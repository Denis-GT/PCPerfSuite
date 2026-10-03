using System.Text.Json;
using System.Text.Json.Serialization;

namespace PCPerfSuite.Core.Benchmark.Protocol;

/// <summary>Demande de test envoyée au worker : le test (<see cref="BenchTestKinds.Key"/>) et ses paramètres. Tout ce
/// qui touche à la machine (quel cœur, quel fichier, quelles tailles) est décidé par l'app, pas par le worker.</summary>
public sealed class BenchJobRequest
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Clé du test (« cpu-mono », « cpu-multi », « ram-debit », « ram-latence », « disque »).</summary>
    public string Kind { get; set; } = "";

    public CpuJobParameters? Cpu { get; set; }

    public RamJobParameters? Ram { get; set; }

    public DiskJobParameters? Disk { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>Un processeur logique visé par un thread de charge : son groupe et son index Windows, et son identifiant de
/// CPU set (0 si Windows ne l'a pas donné : on se rabat alors sur l'affinité seule).</summary>
public sealed class LogicalProcessorTarget
{
    public int Group { get; set; }

    public int Index { get; set; }

    public uint CpuSetId { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>Paramètres des tests processeur. Les durées sont « expérimental » tant qu'elles ne sont pas calibrées sur de
/// vraies machines (règle 6).</summary>
public sealed class CpuJobParameters
{
    /// <summary>Préchauffe non comptée (JIT par paliers, Dynamic PGO, montée en fréquence).</summary>
    public double WarmupSeconds { get; set; } = 5;

    /// <summary>Durée de mesure de chaque noyau dans une passe.</summary>
    public double PassSeconds { get; set; } = 2;

    /// <summary>Nombre de passes ; médiane et coefficient de variation en sortie.</summary>
    public int Passes { get; set; } = 3;

    /// <summary>Charge continue jusqu'à ce point (au-delà du Tau Intel de 28 ou 56 s), puis des passes « soutenu ».
    /// 0 : pas de phase soutenue.</summary>
    public double SustainedSeconds { get; set; } = 180;

    /// <summary>Un thread par entrée, épinglé dessus ; null ou vide : <see cref="ThreadCount"/> threads non épinglés.</summary>
    public List<LogicalProcessorTarget>? Threads { get; set; }

    public int ThreadCount { get; set; } = 1;

    /// <summary>Noyaux à passer (clés de <see cref="Kernels.CpuKernelCatalog"/>) ; null : tous.</summary>
    public List<string>? Kernels { get; set; }

    /// <summary>Durée maximale d'une charge FMA d'affilée (profil « power virus ») : au-delà, on alterne les noyaux.</summary>
    public double MaxFloatStretchSeconds { get; set; } = 20;

    /// <summary>Cycles charge / repos pendant la charge continue (pour #15 et #16 : les transitions de boost). Null :
    /// charge continue.</summary>
    public int? DutyLoadMs { get; set; }

    public int? DutyRestMs { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>Paramètres des tests mémoire : tailles décidées par l'app (8 × L3 au moins, 25 % de la RAM libre au plus).</summary>
public sealed class RamJobParameters
{
    /// <summary>Tampon du test de débit (lecture, écriture, copie).</summary>
    public long BandwidthBytes { get; set; }

    /// <summary>Tampon du test de latence (pointer chasing), de 256 Mo à 1 Go.</summary>
    public long LatencyBytes { get; set; }

    public int Passes { get; set; } = 3;

    /// <summary>Durée minimale d'une passe de débit (balayages entiers du tampon jusqu'à l'atteindre).</summary>
    public double PassSeconds { get; set; } = 1;

    public int ThreadCount { get; set; } = 1;

    /// <summary>Un thread par entrée, épinglé dessus (cœurs de la plus haute classe : sur un hybride, un thread posé sur
    /// un cœur E mesurerait une autre latence) ; null ou vide : <see cref="ThreadCount"/> threads non épinglés.</summary>
    public List<LogicalProcessorTarget>? Threads { get; set; }

    /// <summary>Pas de pointer chasing par passe de latence.</summary>
    public long LatencySteps { get; set; } = 20_000_000;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>Paramètres du test disque : le fichier est créé, rempli, mesuré et supprimé par le worker ; l'app a vérifié
/// le dossier et l'espace libre avant.</summary>
public sealed class DiskJobParameters
{
    /// <summary>Chemin complet du fichier de test (dossier sécurisé, voir DiskTestFile).</summary>
    public string Path { get; set; } = "";

    public long FileSizeBytes { get; set; }

    /// <summary>Taille de secteur physique : offsets et tailles d'E/S en sont des multiples.</summary>
    public int SectorBytes { get; set; } = 4096;

    public double PhaseSeconds { get; set; } = 5;

    /// <summary>Disque à plateaux : profils raccourcis (file de 1 seulement).</summary>
    public bool IsRotational { get; set; }

    /// <summary>Volume écrit au plus pendant tout le test, préremplissage compris.</summary>
    public long WriteBudgetBytes { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}
