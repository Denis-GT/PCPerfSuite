namespace PCPerfSuite.Core.Storage;

/// <summary>
/// Modifications en place de l'arborescence déjà scannée (rescan d'un dossier, retrait d'un élément
/// envoyé à la Corbeille). On garde les mêmes instances de nœuds le long du chemin modifié : la treemap
/// retrouve ainsi ses blocs dépliés sans avoir à tout rescanner.
/// </summary>
public static class FolderTree
{
    /// <summary>Chaîne racine → cible (les deux incluses), ou null si la cible ne fait pas (ou plus) partie de l'arbre.</summary>
    public static List<FolderNode>? FindAncestry(FolderNode root, FolderNode target)
    {
        var chain = new List<FolderNode>();
        return Walk(root) ? chain : null;

        bool Walk(FolderNode node)
        {
            chain.Add(node);
            if (ReferenceEquals(node, target)) return true;

            foreach (FolderNode child in node.Children)
            {
                if (MayContain(child, target) && Walk(child)) return true;
            }

            chain.RemoveAt(chain.Count - 1);
            return false;
        }
    }

    public static bool Contains(FolderNode subtreeRoot, FolderNode target) => FindAncestry(subtreeRoot, target) is not null;

    /// <summary>Remplace le contenu du dernier nœud de la chaîne par celui d'un nouveau scan du même dossier,
    /// et répercute l'écart de taille sur tous ses ancêtres. Renvoie cet écart.</summary>
    public static long ApplyRescan(IReadOnlyList<FolderNode> ancestry, FolderNode fresh)
    {
        FolderNode node = ancestry[^1];
        long delta = fresh.SizeBytes - node.SizeBytes;

        node.Children.Clear();
        node.Children.AddRange(fresh.Children);
        node.SizeBytes = fresh.SizeBytes;

        for (int i = 0; i < ancestry.Count - 1; i++)
        {
            ancestry[i].SizeBytes += delta;
        }

        return delta;
    }

    /// <summary>Retire le dernier nœud de la chaîne de son parent et soustrait sa taille de tous ses ancêtres.</summary>
    public static void Remove(IReadOnlyList<FolderNode> ancestry)
    {
        if (ancestry.Count < 2) throw new ArgumentException("La racine de l'arborescence ne peut pas être retirée.", nameof(ancestry));

        FolderNode node = ancestry[^1];
        ancestry[^2].Children.Remove(node);

        for (int i = 0; i < ancestry.Count - 1; i++)
        {
            ancestry[i].SizeBytes -= node.SizeBytes;
        }
    }

    // Élague la recherche : un dossier ne peut contenir la cible que si son chemin préfixe celui de la cible.
    // Sans chemin (racine multi-disques, cible "Autres éléments"), on ne peut pas élaguer et on explore.
    private static bool MayContain(FolderNode candidate, FolderNode target)
    {
        if (ReferenceEquals(candidate, target)) return true;
        if (candidate.IsFile || candidate.Children.Count == 0) return false;
        if (candidate.FullPath.Length == 0 || target.FullPath.Length == 0) return true;

        string prefix = Path.EndsInDirectorySeparator(candidate.FullPath)
            ? candidate.FullPath
            : candidate.FullPath + Path.DirectorySeparatorChar;
        return target.FullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
