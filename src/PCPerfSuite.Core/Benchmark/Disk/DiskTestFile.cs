using PCPerfSuite.Core.Compatibility;
using PCPerfSuite.Core.Installations;

namespace PCPerfSuite.Core.Benchmark.Disk;

/// <summary>Où le fichier de test ira, ou pourquoi il ne peut pas aller sur ce volume.</summary>
public sealed record DiskTestFilePlacement(string? Path, string? Folder, IReadOnlyList<string> Notes, Unavailable? Problem)
{
    public bool IsReady => Path is not null;
}

/// <summary>
/// Le fichier du test disque : un seul, au nom fixe, hors Documents et Bureau (accès contrôlé aux dossiers, EDR qui
/// voit des gigaoctets aléatoires) : sur le volume système dans <c>%ProgramData%\PCPerfSuite\Bench</c> (dossier sécurisé
/// de #7) ; sur un autre volume à sa racine, <c>X:\PCPerfSuite.Bench</c>, avec la même liste d'accès quand le système
/// de fichiers en a (NTFS, ReFS), sans liste sur exFAT, jonctions refusées dans les deux cas (revérifiées par le worker
/// juste avant la création, et par la reprise). Créé en <c>CreateNew</c> + <c>DeleteOnClose</c> par le worker : Windows
/// le supprime à la fermeture du handle, worker tué compris ; la reprise au lancement le supprime s'il reste d'une panne
/// de Windows.
/// </summary>
public static class DiskTestFile
{
    public const string FileName = "test-disque.bin";
    public const string SecondaryVolumeFolderName = "PCPerfSuite.Bench";
    public const long Mebibyte = 1L << 20;
    public const long DefaultFileBytes = 1024 * Mebibyte;
    public const long MinimumFileBytes = 256 * Mebibyte;
    public const long MaximumFileBytes = 8192 * Mebibyte;
    public const long FileStepBytes = 256 * Mebibyte;
    public const double FreeSpaceFactor = 1.1;
    public const long FreeSpaceMarginBytes = 512 * Mebibyte;

    /// <summary>Espace libre exigé : la taille et 10 %, plus 512 Mo pour ne pas remplir le volume.</summary>
    public static long RequiredFreeBytes(long fileBytes) => (long)(fileBytes * FreeSpaceFactor) + FreeSpaceMarginBytes;

    public static string FolderOnVolume(string driveLetter) => System.IO.Path.Combine(driveLetter + System.IO.Path.DirectorySeparatorChar, SecondaryVolumeFolderName);

    /// <summary>Prépare l'emplacement : dossier sécurisé, espace libre relu à l'instant (l'inventaire des volumes date de
    /// l'ouverture de la page), fichier d'un test précédent supprimé. Ne lève jamais ; un refus est expliqué.</summary>
    /// <param name="readFreeBytes">Espace libre actuel d'une lettre, null s'il n'est pas lisible (on garde alors celui de
    /// l'inventaire) ; par défaut <see cref="ReadFreeBytes"/>.</param>
    public static DiskTestFilePlacement Prepare(BenchVolume volume, long fileBytes, Func<string, long?>? readFreeBytes = null)
    {
        var notes = new List<string>();
        if (volume.Unavailable is { } unavailable) return new DiskTestFilePlacement(null, null, notes, unavailable);

        long required = RequiredFreeBytes(fileBytes);
        long free = (readFreeBytes ?? ReadFreeBytes)(volume.DriveLetter) ?? volume.FreeBytes;
        if (free < required)
        {
            return new DiskTestFilePlacement(null, null, notes, new Unavailable(UnavailableCause.HardwareOrDriver,
                $"pas assez d'espace libre sur {volume.DriveLetter} : il faut {required / Mebibyte} Mo ({free / Mebibyte} Mo libres)"));
        }

        string? folder = ResolveFolder(volume, notes, out Unavailable? problem);
        if (folder is null) return new DiskTestFilePlacement(null, null, notes, problem);

        string path = System.IO.Path.Combine(folder, FileName);
        if (!TryDeleteStale(path, out string? error))
        {
            return new DiskTestFilePlacement(null, folder, notes, new Unavailable(UnavailableCause.HardwareOrDriver, $"un fichier de test précédent ne peut pas être supprimé : {error}"));
        }

        return new DiskTestFilePlacement(path, folder, notes, null);
    }

    private static string? ResolveFolder(BenchVolume volume, List<string> notes, out Unavailable? problem)
    {
        SecureFolderResult result;
        if (volume.IsSystem)
        {
            result = ProgramDataFolder.TryEnsure(ProgramDataFolder.BenchFolderName);
            notes.Add("dossier : %ProgramData%\\PCPerfSuite\\Bench (liste d'accès réservée aux administrateurs)");
        }
        else if (BenchVolumeRules.SupportsAcl(volume.Format))
        {
            result = ProgramDataFolder.TryEnsure(FolderOnVolume(volume.DriveLetter), Array.Empty<string>());
            notes.Add($"dossier : {FolderOnVolume(volume.DriveLetter)} (liste d'accès réservée aux administrateurs)");
        }
        else
        {
            result = EnsurePlainFolder(FolderOnVolume(volume.DriveLetter));
            notes.Add($"dossier : {FolderOnVolume(volume.DriveLetter)} sans liste d'accès ({volume.Format} n'en gère pas)");
        }

        if (result.IsReady)
        {
            problem = null;
            return result.Path;
        }

        string error = result.Error ?? "dossier refusé";
        problem = new Unavailable(error.Contains("administrateur", StringComparison.OrdinalIgnoreCase) ? UnavailableCause.MissingRights : UnavailableCause.HardwareOrDriver,
            $"dossier de test refusé : {error}");
        return null;
    }

    /// <summary>exFAT : pas de liste d'accès possible ; on refuse seulement un lien et on crée le dossier.</summary>
    private static SecureFolderResult EnsurePlainFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) return new SecureFolderResult(null, $"{folder} est un lien vers un autre emplacement");
            }
            else
            {
                Directory.CreateDirectory(folder);
            }
            return new SecureFolderResult(folder, null);
        }
        catch (Exception ex)
        {
            return new SecureFolderResult(null, $"création impossible ({ex.Message})");
        }
    }

    /// <summary>Supprime un fichier de test laissé par un test interrompu ; un lien à sa place est refusé, jamais suivi. Un
    /// volume absent (clé débranchée) n'est pas un fichier supprimé : il le reste à vérifier.</summary>
    public static bool TryDeleteStale(string path, out string? error)
    {
        try
        {
            if (System.IO.Path.GetPathRoot(path) is { Length: > 0 } root && !Directory.Exists(root))
            {
                error = "volume absent (débranché ?) : fichier non vérifié";
                return false;
            }
            if (!File.Exists(path))
            {
                error = null;
                return true;
            }
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                error = "c'est un lien, pas un fichier";
                return false;
            }
            File.Delete(path);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Pour la reprise au lancement : le fichier de test d'un volume, supprimé s'il existe. Sans chemin dans le
    /// retour (il irait au journal).</summary>
    public static bool TryDeleteOnVolume(string driveLetter, bool isSystem, out string? error)
    {
        string? folder = isSystem ? ProgramDataFolder.TryGetExisting(ProgramDataFolder.BenchFolderName) : FolderOnVolume(driveLetter);
        if (folder is null)
        {
            error = null;
            return true;
        }
        // Sur un autre volume, le dossier n'a pas de parent sûr : remplacé par une jonction depuis le dernier test, il
        // ferait supprimer en administrateur un fichier choisi par un autre. Jamais suivi.
        if (FolderIsLink(folder))
        {
            error = "le dossier de test est devenu un lien vers un autre emplacement : rien n'est supprimé";
            return false;
        }
        return TryDeleteStale(System.IO.Path.Combine(folder, FileName), out error);
    }

    /// <summary>Vrai si le dossier existe et est un lien (jonction, lien symbolique) ; vrai aussi s'il est illisible :
    /// dans le doute, on ne s'en sert pas.</summary>
    public static bool FolderIsLink(string folder)
    {
        try
        {
            return Directory.Exists(folder) && (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>Espace libre d'un volume à l'instant, null s'il n'est pas lisible.</summary>
    public static long? ReadFreeBytes(string driveLetter)
    {
        try
        {
            var drive = new DriveInfo(driveLetter);
            return drive.IsReady ? drive.AvailableFreeSpace : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Taille réglée par +/− : multiple de 256 Mo dans [256 Mo, 8 Go].</summary>
    public static long ClampFileBytes(long bytes)
    {
        long stepped = Math.Max(FileStepBytes, bytes - bytes % FileStepBytes);
        return Math.Clamp(stepped, MinimumFileBytes, MaximumFileBytes);
    }
}
