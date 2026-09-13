namespace PCPerfSuite.Core.Storage;

/// <summary>Nœud de l'arborescence d'occupation disque (dossier, fichier, ou bloc agrégé "Autres...").</summary>
public sealed class FolderNode
{
    public required string Name { get; init; }

    /// <summary>Vide pour les nœuds synthétiques (racine multi-disques, bloc "Autres éléments").</summary>
    public string FullPath { get; init; } = "";

    public long SizeBytes { get; set; }
    public bool IsFile { get; init; }
    public bool IsAggregate { get; init; }

    /// <summary>Dossier contenant, null pour la racine d'une analyse — sert au fil d'Ariane et au zoom arrière.</summary>
    public FolderNode? Parent { get; set; }

    public List<FolderNode> Children { get; init; } = new();

    public bool CanDrillInto => !IsFile && !IsAggregate && Children.Count > 0;
}

public sealed class ScanProgress
{
    public required string CurrentPath { get; init; }
    public long BytesScanned { get; init; }
}

/// <summary>
/// Scanne récursivement un disque/dossier pour construire l'arborescence de tailles utilisée par la
/// vue "Stockage" (façon SpaceSniffer). Tourne entièrement en tâche de fond, annulable.
/// </summary>
public sealed class DiskSpaceScanner
{
    /// <summary>Au-delà de ce nombre d'enfants directs, on garde les plus gros et on agrège le reste
    /// dans un bloc "Autres éléments" — un dossier à 50 000 petits fichiers resterait sinon illisible
    /// (et très coûteux) à afficher en treemap.</summary>
    private const int MaxChildrenPerNode = 40;

    private sealed class ScanCounters
    {
        public long BytesScanned;
        public DateTime LastReport = DateTime.MinValue;
    }

    public Task<FolderNode> ScanAsync(string rootPath, string displayName, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var counters = new ScanCounters();
        return Task.Run(() => ScanDirectory(rootPath, displayName, progress, counters, ct), ct);
    }

    private FolderNode ScanDirectory(string path, string displayName, IProgress<ScanProgress>? progress, ScanCounters counters, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var children = new List<FolderNode>();
        long totalSize = 0;

        IEnumerable<string> subDirs = Array.Empty<string>();
        try { subDirs = Directory.EnumerateDirectories(path); }
        catch { /* dossier racine inaccessible : on renvoie un nœud vide plutôt que de planter */ }

        foreach (string dir in subDirs)
        {
            ct.ThrowIfCancellationRequested();
            string name = Path.GetFileName(dir);
            if (name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase)) continue;

            FolderNode child;
            try
            {
                var info = new DirectoryInfo(dir);
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    // Ne suit pas les jonctions/liens symboliques : évite les boucles infinies et le
                    // double comptage d'un même contenu physique monté à deux endroits.
                    continue;
                }

                child = ScanDirectory(dir, name, progress, counters, ct);
            }
            catch (UnauthorizedAccessException)
            {
                child = new FolderNode { Name = name, FullPath = dir };
            }
            catch (IOException)
            {
                continue;
            }

            children.Add(child);
            totalSize += child.SizeBytes;
        }

        IEnumerable<FileInfo> files = Array.Empty<FileInfo>();
        try { files = new DirectoryInfo(path).EnumerateFiles(); }
        catch { /* accès refusé à ce niveau : on garde ce qu'on a déjà collecté */ }

        foreach (FileInfo file in files)
        {
            ct.ThrowIfCancellationRequested();

            long size;
            try { size = file.Length; }
            catch { continue; }

            children.Add(new FolderNode { Name = file.Name, FullPath = file.FullName, SizeBytes = size, IsFile = true });
            totalSize += size;
            counters.BytesScanned += size;
        }

        if ((DateTime.UtcNow - counters.LastReport).TotalMilliseconds > 100)
        {
            counters.LastReport = DateTime.UtcNow;
            progress?.Report(new ScanProgress { CurrentPath = path, BytesScanned = counters.BytesScanned });
        }

        var node = new FolderNode { Name = displayName, FullPath = path, SizeBytes = totalSize };
        foreach (FolderNode child in Condense(children))
        {
            child.Parent = node;
            node.Children.Add(child);
        }
        return node;
    }

    private static List<FolderNode> Condense(List<FolderNode> children)
    {
        List<FolderNode> sorted = children.OrderByDescending(c => c.SizeBytes).ToList();
        if (sorted.Count <= MaxChildrenPerNode) return sorted;

        List<FolderNode> kept = sorted.Take(MaxChildrenPerNode - 1).ToList();
        List<FolderNode> rest = sorted.Skip(MaxChildrenPerNode - 1).ToList();
        long restSize = rest.Sum(c => c.SizeBytes);

        kept.Add(new FolderNode
        {
            Name = $"Autres éléments ({rest.Count})",
            SizeBytes = restSize,
            IsAggregate = true,
        });

        return kept;
    }
}
