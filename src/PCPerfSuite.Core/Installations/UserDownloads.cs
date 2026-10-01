using System.Runtime.InteropServices;
using PCPerfSuite.Core.SystemInfo;

namespace PCPerfSuite.Core.Installations;

/// <summary>Dossier où remettre un fichier à l'utilisateur, et s'il s'agit des Téléchargements publics (partagés par
/// tous les comptes) plutôt que des siens.</summary>
public sealed record DownloadsFolder(string Path, bool IsPublic);

/// <summary>
/// Le dossier Téléchargements de la personne devant l'écran, et ce qu'il faut pour y déposer un fichier proprement.
///
/// L'app élevée tourne parfois sous un autre compte que cette personne (<see cref="SessionUser.IsOtherProfile"/>) : ses
/// Téléchargements à elle ne sont alors pas ceux de l'app, et l'Explorateur de la personne ne pourrait pas ouvrir ceux
/// de l'administrateur. Dans ce cas, le fichier va dans les Téléchargements publics (C:\Users\Public\Downloads), que
/// tous les comptes voient, et l'app le dit.
/// </summary>
public static class UserDownloads
{
    private static readonly Guid DownloadsFolderId = new("374DE290-123F-4565-9164-39C4925E467B");
    private static readonly Guid PublicDownloadsFolderId = new("3D644C9B-1FB8-4F30-9B45-F670235F79C0");

    /// <summary>Dossier choisi, ou null s'il est introuvable (profil sans dossier Téléchargements). Ne lève jamais.</summary>
    public static DownloadsFolder? Choose()
    {
        bool otherProfile = SessionUser.IsOtherProfile;
        string? path = KnownFolder(otherProfile ? PublicDownloadsFolderId : DownloadsFolderId);
        if (path is null && !otherProfile)
        {
            string fallback = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            path = Directory.Exists(fallback) ? fallback : null;
        }

        return path is null ? null : new DownloadsFolder(path, otherProfile);
    }

    /// <summary>Chemin libre pour <paramref name="fileName"/> dans <paramref name="folder"/> : « outil.zip », sinon
    /// « outil (2).zip », « outil (3).zip »… comme le fait un navigateur. Jamais un fichier existant.</summary>
    public static string UniquePath(string folder, string fileName)
    {
        string candidate = System.IO.Path.Combine(folder, fileName);
        if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;

        string stem = System.IO.Path.GetFileNameWithoutExtension(fileName);
        string extension = System.IO.Path.GetExtension(fileName);
        for (int i = 2; i < 1000; i++)
        {
            candidate = System.IO.Path.Combine(folder, $"{stem} ({i}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }

        return System.IO.Path.Combine(folder, $"{stem} ({Guid.NewGuid():N}){extension}");
    }

    /// <summary>Marque le fichier comme venu d'Internet (flux « Zone.Identifier », comme le fait un navigateur) : Windows
    /// garde ainsi ses protections habituelles (SmartScreen, mode protégé) quand l'utilisateur l'ouvre lui-même.
    /// Best-effort : un disque sans flux de données (FAT32, exFAT) n'en garde pas.</summary>
    public static void MarkFromInternet(string path, Uri source)
    {
        try
        {
            string host = $"{source.Scheme}://{source.Host}/";
            File.WriteAllText(path + ":Zone.Identifier", $"[ZoneTransfer]\r\nZoneId=3\r\nReferrerUrl={host}\r\nHostUrl={source.AbsoluteUri}\r\n");
        }
        catch
        {
            // best-effort : voir le résumé ci-dessus.
        }
    }

    private static string? KnownFolder(Guid id)
    {
        IntPtr pointer = IntPtr.Zero;
        try
        {
            int result = SHGetKnownFolderPath(id, 0, IntPtr.Zero, out pointer);
            return result == 0 ? Marshal.PtrToStringUni(pointer) : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (pointer != IntPtr.Zero) Marshal.FreeCoTaskMem(pointer);
        }
    }

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid folderId, uint flags, IntPtr token, out IntPtr path);
}
