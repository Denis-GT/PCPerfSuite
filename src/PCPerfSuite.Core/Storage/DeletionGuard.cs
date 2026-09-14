namespace PCPerfSuite.Core.Storage;

/// <summary>
/// Garde-fous appliqués avant d'envoyer un élément de la carte à la Corbeille. L'app tourne en
/// administrateur : sans ces règles, un clic malheureux pourrait retirer un dossier dont Windows ou un
/// profil utilisateur a besoin pour démarrer.
///
/// Deux familles de règles, volontairement redondantes :
/// — les chemins absolus du disque système, lus dans Environment.SpecialFolder ;
/// — les mêmes interdits appliqués relativement à la racine de CHAQUE lecteur, parce que l'onglet Stockage
///   analyse aussi les disques secondaires et les lecteurs réseau : un ancien disque système ou une image
///   de sauvegarde y porte un Windows et des profils que les chemins absolus ne décrivent pas.
/// </summary>
public static class DeletionGuard
{
    private static readonly string[] SystemFileNames = { "pagefile.sys", "hiberfil.sys", "swapfile.sys" };
    private static readonly string[] ReservedFolderNames = { "$Recycle.Bin", "System Volume Information" };

    /// <summary>Chemins relatifs à la racine d'un lecteur dont ni le dossier ni le contenu ne sont supprimables :
    /// des arborescences entièrement réservées à Windows, où il n'y a rien à récupérer comme espace.</summary>
    private static readonly string[] ProtectedTrees =
    {
        "Windows",
        "Recovery",                        // environnement de récupération (WinRE) : sans lui, plus de réparation ni de réinitialisation
        "Boot",                            // magasin BCD des machines démarrant en BIOS hérité
        "EFI",                             // partition système EFI, quand elle est montée dans un dossier
        "$WinREAgent",                     // espace de travail des mises à jour de fonctionnalités
        @"Program Files\WindowsApps",      // applications du Store, verrouillées par TrustedInstaller
        @"Program Files (x86)\WindowsApps",
    };

    /// <summary>Chemins relatifs à la racine d'un lecteur qu'on refuse de supprimer eux-mêmes, ou via un de leurs
    /// dossiers parents, mais dont le contenu reste supprimable : y faire le ménage est justement le but de
    /// l'onglet Stockage.</summary>
    private static readonly string[] ProtectedFolders =
    {
        "Users",
        "Documents and Settings",
        "Program Files",
        "Program Files (x86)",
        "ProgramData",
        @"ProgramData\Microsoft",          // Defender, Windows Update, cryptographie : un cran plus bas que ProgramData
    };

    /// <summary>Raison du refus, prête à afficher, ou null si l'élément peut être proposé à la suppression.</summary>
    public static string? GetRefusalReason(FolderNode node)
    {
        try
        {
            return Evaluate(node);
        }
        catch (Exception ex)
        {
            // On échoue du côté sûr : un garde-fou qui n'a pas pu se prononcer refuse. Sans ce filet, l'exception
            // remonterait jusqu'à la commande asynchrone du menu, qui la relaie sur le thread UI.
            return $"Impossible de vérifier que « {node.FullPath} » est supprimable sans risque " +
                   $"({ex.GetType().Name} : {ex.Message}). Suppression refusée.";
        }
    }

    private static string? Evaluate(FolderNode node)
    {
        if (node.IsAggregate || node.FullPath.Length == 0)
        {
            return "Ce bloc regroupe plusieurs éléments et ne correspond à aucun fichier ni dossier réel : il ne peut pas être supprimé.";
        }

        // Un chemin non pleinement qualifié (« C: », « Dossier\x ») serait résolu contre le répertoire courant du
        // processus par notre Normalize, mais potentiellement autrement par le shell : on vérifierait alors un
        // chemin et on en supprimerait un autre. Le scanner n'en produit pas ; on refuse plutôt que de supposer.
        if (!Path.IsPathFullyQualified(node.FullPath))
        {
            return $"« {node.FullPath} » n'est pas un chemin complet : suppression refusée.";
        }

        string path;
        try
        {
            path = Normalize(node.FullPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return $"Chemin illisible, suppression refusée : {node.FullPath}";
        }

        string? root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root) || string.Equals(path, Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase))
        {
            return $"« {path} » est la racine d'un disque : PCPerfSuite ne supprime jamais un disque entier.";
        }

        string name = Path.GetFileName(path);
        if (SystemFileNames.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return $"« {name} » est un fichier système de Windows (mémoire virtuelle ou veille prolongée) : il se règle dans les paramètres système, pas en le supprimant.";
        }

        string? reserved = path.Split(Path.DirectorySeparatorChar)
            .FirstOrDefault(segment => ReservedFolderNames.Contains(segment, StringComparer.OrdinalIgnoreCase));
        if (reserved is not null)
        {
            return $"« {reserved} » est un dossier réservé à Windows : ni lui ni son contenu ne peuvent être supprimés d'ici.";
        }

        // Dossier Windows : tout son contenu est intouchable (System32, SysWOW64, WinSxS, pilotes…).
        foreach (string systemTree in Existing(Environment.SpecialFolder.Windows, Environment.SpecialFolder.System, Environment.SpecialFolder.SystemX86))
        {
            if (IsSameOrInside(path, systemTree))
            {
                return $"« {path} » fait partie du dossier système « {systemTree} » : suppression refusée, Windows pourrait ne plus démarrer.";
            }
        }

        // Emplacements critiques qu'on refuse de supprimer eux-mêmes, ou via un de leurs dossiers parents
        // (ex: C:\Users emporterait le profil). Leurs sous-dossiers restent supprimables, comme les restes
        // d'un programme désinstallé dans Program Files.
        foreach (string location in CriticalLocations())
        {
            if (IsSameOrInside(location, path))
            {
                return string.Equals(location, path, StringComparison.OrdinalIgnoreCase)
                    ? $"« {path} » est un emplacement critique de Windows : suppression refusée."
                    : $"« {path} » contient l'emplacement critique « {location} » : suppression refusée.";
            }
        }

        // Les mêmes interdits, relatifs à la racine du lecteur analysé : les règles ci-dessus ne connaissent
        // que le disque système, or on peut tout aussi bien viser D:\Windows ou un profil sur un disque de
        // sauvegarde.
        foreach (string tree in RelativeTo(root, ProtectedTrees))
        {
            if (IsSameOrInside(path, tree))
            {
                return $"« {path} » fait partie de « {tree} », une arborescence réservée à Windows : suppression refusée.";
            }
        }

        foreach (string folder in RelativeTo(root, ProtectedFolders))
        {
            if (IsSameOrInside(folder, path))
            {
                return string.Equals(folder, path, StringComparison.OrdinalIgnoreCase)
                    ? $"« {path} » est un emplacement critique de Windows : suppression refusée."
                    : $"« {path} » contient l'emplacement critique « {folder} » : suppression refusée.";
            }
        }

        // Le dossier d'où PCPerfSuite est lancé : l'app s'effacerait elle-même en cours d'exécution, laissant
        // des fichiers verrouillés à moitié déplacés.
        if (ApplicationDirectory() is { Length: > 0 } appDirectory && IsSameOrInside(path, appDirectory))
        {
            return $"« {path} » fait partie du dossier d'où PCPerfSuite est lancé : suppression refusée.";
        }

        return null;
    }

    private static IEnumerable<string> RelativeTo(string driveRoot, string[] relativePaths)
        => relativePaths.Select(relative => Path.TrimEndingDirectorySeparator(Path.Combine(driveRoot, relative)));

    private static string ApplicationDirectory()
    {
        // ProcessPath désigne l'exécutable réellement lancé ; BaseDirectory sert de secours (hôte dotnet).
        string? directory = Environment.ProcessPath is { Length: > 0 } processPath
            ? Path.GetDirectoryName(processPath)
            : AppContext.BaseDirectory;

        return string.IsNullOrEmpty(directory) ? "" : Normalize(directory);
    }

    private static IEnumerable<string> CriticalLocations()
    {
        foreach (string location in Existing(
                     Environment.SpecialFolder.Windows,
                     Environment.SpecialFolder.System,
                     Environment.SpecialFolder.ProgramFiles,
                     Environment.SpecialFolder.ProgramFilesX86,
                     Environment.SpecialFolder.CommonApplicationData,
                     Environment.SpecialFolder.UserProfile))
        {
            yield return location;
        }

        // Tous les profils (pas seulement celui du compte courant) : lancée en administrateur via un autre
        // compte, l'app a pour UserProfile celui de l'admin, pas celui de la personne devant l'écran.
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string? profilesDirectory = userProfile.Length > 0 ? Path.GetDirectoryName(Normalize(userProfile)) : null;
        if (string.IsNullOrEmpty(profilesDirectory) || !Directory.Exists(profilesDirectory)) yield break;

        IEnumerable<string> profiles;
        try { profiles = Directory.GetDirectories(profilesDirectory); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { yield break; }

        foreach (string profile in profiles)
        {
            yield return Normalize(profile);
        }
    }

    private static IEnumerable<string> Existing(params Environment.SpecialFolder[] folders)
        => folders.Select(Environment.GetFolderPath).Where(p => p.Length > 0).Select(Normalize);

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool IsSameOrInside(string path, string location)
        => string.Equals(path, location, StringComparison.OrdinalIgnoreCase)
           || path.StartsWith(location + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
