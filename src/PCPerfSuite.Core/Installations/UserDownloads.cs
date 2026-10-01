using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace PCPerfSuite.Core.Installations;

/// <summary>Fichier remis à l'utilisateur : son chemin, et le dossier Téléchargements où il est.</summary>
public sealed record DepositResult(string Path, string Folder);

/// <summary>
/// Remet un fichier vérifié dans le dossier Téléchargements de la personne devant l'écran.
///
/// La copie se fait avec le jeton du shell de la session (explorer.exe, celui de la personne connectée), jamais avec les
/// droits administrateur de l'app. L'emplacement des Téléchargements se règle dans le profil, sans droits : un programme
/// non élevé pourrait le faire pointer vers C:\Windows, par un nom court, un chemin \\?\, un partage ou une jonction posée
/// en cours de téléchargement, pour faire écrire l'app administrateur dans un dossier protégé. Avec le jeton du shell,
/// c'est Windows qui juge : le fichier ne va que là où l'utilisateur peut écrire lui-même.
///
/// Le dossier est celui du compte connecté, même quand l'app tourne sous un autre compte administrateur
/// (<see cref="SystemInfo.SessionUser.IsOtherProfile"/>) : c'est lui qui va ouvrir le fichier. Sans shell dans la session
/// (Explorateur arrêté), rien n'est déposé et le message le dit.
///
/// Best-effort (règle 2) : ne lève jamais.
/// </summary>
public static class UserDownloads
{
    private static readonly Guid DownloadsFolderId = new("374DE290-123F-4565-9164-39C4925E467B");

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const uint TokenDuplicate = 0x0002;
    private const uint TokenImpersonate = 0x0004;
    private const uint MaximumAllowed = 0x02000000;
    private const int SecurityImpersonation = 2;
    private const int TokenImpersonationType = 2;

    /// <summary>Copie <paramref name="sourcePath"/> dans les Téléchargements de la personne connectée, sous
    /// <paramref name="fileName"/> (« outil (2).zip » si le nom est pris), marqué comme venu de <paramref name="origin"/>.
    /// Disque et processus : hors du thread d'interface.</summary>
    public static DepositResult? TryDeposit(string sourcePath, string fileName, Uri origin, out string? error)
    {
        using SafeAccessTokenHandle? token = ShellUserToken();
        if (token is null)
        {
            error = "l'Explorateur de Windows ne tourne pas dans cette session : rien n'a été déposé";
            return null;
        }

        string? folder = KnownFolder(DownloadsFolderId, token);
        if (folder is null)
        {
            error = "le dossier Téléchargements de ton compte est introuvable";
            return null;
        }

        return DepositAs(token, sourcePath, folder, fileName, origin, out error);
    }

    /// <summary>La copie elle-même, sous le jeton donné : isolée pour être testée dans un dossier de test.</summary>
    internal static DepositResult? DepositAs(SafeAccessTokenHandle token, string sourcePath, string folder, string fileName, Uri origin,
        out string? error)
    {
        try
        {
            string target = WindowsIdentity.RunImpersonated(token, () =>
            {
                string path = UniquePath(folder, fileName);
                File.Copy(sourcePath, path, overwrite: false);
                MarkFromInternet(path, origin);
                return path;
            });

            error = null;
            return new DepositResult(target, folder);
        }
        catch (UnauthorizedAccessException)
        {
            error = $"ton compte ne peut pas écrire dans {folder}";
            return null;
        }
        catch (Exception ex)
        {
            error = $"le fichier n'a pas pu être déposé dans {folder} ({ex.Message})";
            return null;
        }
    }

    /// <summary>Jeton du processus courant, dupliqué pour l'emprunt : sert aux tests, qui n'ont pas d'autre compte.</summary>
    internal static SafeAccessTokenHandle? CurrentToken()
    {
        using Process self = Process.GetCurrentProcess();
        return DuplicateFor(self.Id);
    }

    /// <summary>Chemin libre pour <paramref name="fileName"/> dans <paramref name="folder"/> : « outil.zip », sinon
    /// « outil (2).zip », « outil (3).zip »… comme le fait un navigateur. Jamais un fichier existant.</summary>
    public static string UniquePath(string folder, string fileName)
    {
        string candidate = Path.Combine(folder, fileName);
        if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;

        string stem = Path.GetFileNameWithoutExtension(fileName);
        string extension = Path.GetExtension(fileName);
        for (int i = 2; i < 1000; i++)
        {
            candidate = Path.Combine(folder, $"{stem} ({i}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }

        return Path.Combine(folder, $"{stem} ({Guid.NewGuid():N}){extension}");
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

    /// <summary>Jeton d'emprunt du shell de cette session (explorer.exe), null s'il n'y en a pas ou s'il est illisible.</summary>
    private static SafeAccessTokenHandle? ShellUserToken()
    {
        Process[] shells;
        try
        {
            shells = Process.GetProcessesByName("explorer");
        }
        catch
        {
            return null;
        }

        try
        {
            using Process self = Process.GetCurrentProcess();
            foreach (Process shell in shells)
            {
                if (shell.SessionId == self.SessionId && DuplicateFor(shell.Id) is { } token) return token;
            }

            return null;
        }
        catch
        {
            return null;
        }
        finally
        {
            foreach (Process shell in shells) shell.Dispose();
        }
    }

    private static SafeAccessTokenHandle? DuplicateFor(int processId)
    {
        IntPtr process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process == IntPtr.Zero) return null;

        IntPtr token = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(process, TokenQuery | TokenDuplicate | TokenImpersonate, out token)) return null;
            if (!DuplicateTokenEx(token, MaximumAllowed, IntPtr.Zero, SecurityImpersonation, TokenImpersonationType, out IntPtr duplicate))
            {
                return null;
            }

            return new SafeAccessTokenHandle(duplicate);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (token != IntPtr.Zero) CloseHandle(token);
            CloseHandle(process);
        }
    }

    private static string? KnownFolder(Guid id, SafeAccessTokenHandle token)
    {
        IntPtr pointer = IntPtr.Zero;
        try
        {
            int result = SHGetKnownFolderPath(id, 0, token.DangerousGetHandle(), out pointer);
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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateTokenEx(IntPtr existingToken, uint desiredAccess, IntPtr tokenAttributes, int impersonationLevel,
        int tokenType, out IntPtr newToken);
}
