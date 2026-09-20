using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace PCPerfSuite.Core.SystemInfo;

/// <summary>
/// Sous quel compte PCPerfSuite tourne, et sous quel compte la personne devant l'écran est connectée.
///
/// L'app est manifestée requireAdministrator. Lancée depuis un compte standard par « Exécuter en tant
/// qu'administrateur », Windows l'exécute sous le compte administrateur dont on a saisi le mot de passe :
/// %TEMP%, %LOCALAPPDATA% et %APPDATA% désignent alors le profil de cet administrateur, pas celui de la
/// personne devant l'écran. Le nettoyage de caches annonce donc « 0 octet » pour des dossiers qui, eux,
/// sont pleins — sans que rien n'explique pourquoi (règle de compatibilité 3).
///
/// Le compte de la session est déduit du propriétaire d'explorer.exe : c'est le shell de la personne
/// connectée, il tourne forcément sous son compte et dans la même session que nous.
/// </summary>
public static class SessionUser
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenUser = 1;

    private static bool _resolved;
    private static string? _interactiveAccount;

    /// <summary>Compte sous lequel ce processus tourne, ex. « MON-PC\Admin ».</summary>
    public static string ProcessAccount { get; } = ReadProcessAccount();

    /// <summary>Compte de la personne connectée à cette session, ou null s'il n'a pas pu être déterminé
    /// (explorer.exe arrêté, jeton illisible) : on ne suppose rien dans ce cas.</summary>
    public static string? InteractiveAccount
    {
        get
        {
            if (!_resolved)
            {
                _resolved = true;
                _interactiveAccount = ReadInteractiveAccount();
            }

            return _interactiveAccount;
        }
    }

    /// <summary>Vrai quand l'app tourne sous un autre compte que la personne devant l'écran : tout ce qui
    /// passe par %TEMP%, %LOCALAPPDATA% ou %APPDATA% porte alors sur le mauvais profil.</summary>
    public static bool IsOtherProfile
        => InteractiveAccount is { } interactive
           && !string.Equals(interactive, ProcessAccount, StringComparison.OrdinalIgnoreCase);

    /// <summary>Explication prête à afficher, ou null quand les deux comptes coïncident (le cas courant).</summary>
    public static string? OtherProfileMessage => IsOtherProfile
        ? $"PCPerfSuite tourne sous le compte {ProcessAccount}, pas sous {InteractiveAccount}. Tout ce qui " +
          "dépend du profil utilisateur (fichiers temporaires, caches de shaders, miniatures) porte donc sur " +
          $"le profil de {ProcessAccount}. Pour agir sur celui de {InteractiveAccount}, lance PCPerfSuite " +
          "depuis ce compte-là."
        : null;

    private static string ReadProcessAccount()
    {
        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            return identity.Name;
        }
        catch
        {
            return System.Environment.UserName;
        }
    }

    private static string? ReadInteractiveAccount()
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
            int session = self.SessionId;

            foreach (Process shell in shells)
            {
                if (shell.SessionId != session) continue;
                if (TryGetOwner(shell.Id) is { } owner) return owner;
            }
        }
        catch
        {
            // Aucune information vaut mieux qu'une information fausse : on renvoie null, et l'appelant
            // s'abstient alors d'afficher quoi que ce soit.
        }
        finally
        {
            foreach (Process shell in shells) shell.Dispose();
        }

        return null;
    }

    private static string? TryGetOwner(int processId)
    {
        IntPtr process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process == IntPtr.Zero) return null;

        IntPtr token = IntPtr.Zero;
        IntPtr buffer = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(process, TokenQuery, out token)) return null;

            GetTokenInformation(token, TokenUser, IntPtr.Zero, 0, out uint needed);
            if (needed == 0) return null;

            buffer = Marshal.AllocHGlobal((int)needed);
            if (!GetTokenInformation(token, TokenUser, buffer, needed, out _)) return null;

            // TOKEN_USER ne contient qu'un SID_AND_ATTRIBUTES, dont le premier champ est le pointeur de SID.
            var sid = new SecurityIdentifier(Marshal.ReadIntPtr(buffer));
            return sid.Translate(typeof(NTAccount)).Value;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            if (token != IntPtr.Zero) CloseHandle(token);
            CloseHandle(process);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(IntPtr token, int informationClass, IntPtr information,
        uint informationLength, out uint returnLength);
}
