namespace PCPerfSuite.Core.Cache;

public sealed class CacheCategory
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }

    /// <summary>Chemins candidats (variables d'environnement non résolues). Le premier qui existe sert de dossier "ouvrir".</summary>
    public required IReadOnlyList<string> PathTemplates { get; init; }

    /// <summary>Si vrai, on vide le contenu du dossier mais on garde le dossier lui-même (certains pilotes s'attendent à ce qu'il existe).</summary>
    public bool KeepFolder { get; init; } = true;

    /// <summary>Si renseigné, ne supprime que les fichiers dont le nom correspond (ex: "thumbcache_*.db") plutôt que tout le dossier.</summary>
    public string? FileNamePattern { get; init; }

    public bool RequiresAdmin { get; init; }
}

public sealed class CacheCategoryResult
{
    public required CacheCategory Category { get; init; }
    public required IReadOnlyList<string> ExistingPaths { get; init; }
    public long SizeBytes { get; init; }
    public bool Exists => ExistingPaths.Count > 0;
}

public sealed class CacheCleanResult
{
    public required string CategoryId { get; init; }
    public long BytesFreed { get; init; }
    public int FilesDeleted { get; init; }
    public int FilesSkipped { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
}

/// <summary>Catalogue des caches/fichiers temporaires connus sur un PC Windows gaming.</summary>
public static class KnownCaches
{
    public static IReadOnlyList<CacheCategory> All { get; } = new List<CacheCategory>
    {
        new()
        {
            Id = "temp-user",
            Name = "Fichiers temporaires (%TEMP%)",
            Description = "Le dossier temporaire de ton compte Windows (WIN+R puis %temp%). S'accumule avec les installeurs, mises à jour, fichiers d'app oubliés.",
            PathTemplates = new[] { "%TEMP%" },
        },
        new()
        {
            Id = "temp-system",
            Name = "Fichiers temporaires système",
            Description = "Dossier temporaire partagé par tous les comptes et les services Windows.",
            PathTemplates = new[] { "%SystemRoot%\\Temp" },
            RequiresAdmin = true,
        },
        new()
        {
            Id = "nvidia-shader-cache",
            Name = "Cache shaders NVIDIA",
            Description = "Cache de compilation de shaders DirectX/OpenGL du pilote NVIDIA. Se régénère automatiquement (léger ralentissement au premier lancement d'un jeu après nettoyage).",
            PathTemplates = new[]
            {
                "%LOCALAPPDATA%\\NVIDIA\\DXCache",
                "%LOCALAPPDATA%\\NVIDIA\\GLCache",
                "%LOCALAPPDATA%\\NVIDIA Corporation\\NV_Cache",
                "%PROGRAMDATA%\\NVIDIA Corporation\\NV_Cache",
            },
        },
        new()
        {
            Id = "amd-shader-cache",
            Name = "Cache shaders AMD",
            Description = "Cache de compilation de shaders du pilote AMD (DXC/DXVK/OpenGL/Vulkan).",
            PathTemplates = new[]
            {
                "%LOCALAPPDATA%\\AMD\\DxCache",
                "%LOCALAPPDATA%\\AMD\\DxcCache",
                "%LOCALAPPDATA%\\AMD\\GLCache",
                "%LOCALAPPDATA%\\AMD\\VkCache",
            },
        },
        new()
        {
            Id = "intel-shader-cache",
            Name = "Cache shaders Intel",
            Description = "Cache de compilation de shaders du pilote graphique Intel (iGPU ou Arc).",
            PathTemplates = new[] { "%LOCALAPPDATA%\\Intel\\ShaderCache" },
        },
        new()
        {
            Id = "directx-shader-cache",
            Name = "Cache shaders DirectX (Windows)",
            Description = "Cache de pipeline PSO géré par Windows lui-même (D3DSCache), partagé par de nombreux jeux/moteurs.",
            PathTemplates = new[] { "%LOCALAPPDATA%\\D3DSCache" },
        },
        new()
        {
            Id = "steam-shader-cache",
            Name = "Cache shaders Steam",
            Description = "Pré-compilation de shaders gérée par Steam pour accélérer le premier lancement des jeux.",
            PathTemplates = new[]
            {
                "%ProgramFiles(x86)%\\Steam\\steamapps\\shadercache",
                "C:\\Steam\\steamapps\\shadercache",
            },
        },
        new()
        {
            Id = "windows-update-cache",
            Name = "Cache Windows Update",
            Description = "Fichiers d'installation déjà téléchargés/appliqués par Windows Update.",
            PathTemplates = new[] { "%SystemRoot%\\SoftwareDistribution\\Download" },
            RequiresAdmin = true,
        },
        new()
        {
            Id = "prefetch",
            Name = "Prefetch Windows",
            Description = "Données de préchargement utilisées par Windows pour accélérer le démarrage des programmes. Se régénère automatiquement.",
            PathTemplates = new[] { "%SystemRoot%\\Prefetch" },
            RequiresAdmin = true,
        },
        new()
        {
            Id = "wer",
            Name = "Rapports d'erreurs Windows (WER)",
            Description = "Rapports de plantage et dumps mémoire générés par Windows Error Reporting.",
            PathTemplates = new[]
            {
                "%LOCALAPPDATA%\\Microsoft\\Windows\\WER",
                "%PROGRAMDATA%\\Microsoft\\Windows\\WER",
            },
        },
        new()
        {
            Id = "thumbnail-cache",
            Name = "Cache des miniatures",
            Description = "Miniatures d'images/vidéos générées par l'Explorateur Windows.",
            PathTemplates = new[] { "%LOCALAPPDATA%\\Microsoft\\Windows\\Explorer" },
            FileNamePattern = "thumbcache_*.db",
        },
        new()
        {
            Id = "delivery-optimization",
            Name = "Cache Delivery Optimization",
            Description = "Fragments de mises à jour Windows partagés en P2P avec d'autres PC. Certains fichiers protégés peuvent être ignorés, c'est normal.",
            PathTemplates = new[] { "%PROGRAMDATA%\\Microsoft\\Network\\Downloader" },
            RequiresAdmin = true,
        },
    };
}
