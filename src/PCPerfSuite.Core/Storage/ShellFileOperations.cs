using System.ComponentModel;
using System.Runtime.InteropServices;

namespace PCPerfSuite.Core.Storage;

public sealed class RecycleResult
{
    /// <summary>Code renvoyé par SHFileOperation (0 = succès). Ce ne sont pas tous des codes Win32 standard.</summary>
    public int ErrorCode { get; init; }

    /// <summary>Vrai si l'utilisateur a annulé dans un dialogue du shell (ex: avertissement de suppression définitive).</summary>
    public bool Aborted { get; init; }
}

/// <summary>Actions de l'Explorateur sur un fichier/dossier : envoi à la Corbeille et fenêtre Propriétés natives.</summary>
public static class ShellFileOperations
{
    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_WANTNUKEWARNING = 0x4000;

    private const uint SEE_MASK_INVOKEIDLIST = 0x0000000C;
    private const int SW_SHOW = 5;

    /// <summary>
    /// Envoie l'élément dans la Corbeille, jamais de suppression définitive silencieuse : si Windows ne peut
    /// pas le recycler (disque sans Corbeille, élément trop volumineux), FOF_WANTNUKEWARNING lui fait d'abord
    /// demander confirmation — c'est le rôle documenté de ce drapeau dans shellapi.h.
    ///
    /// L'appel part sur un thread dédié pour ne pas figer l'interface pendant toute l'opération. Ce thread est
    /// mis en STA parce que SHFileOperation affiche des dialogues du shell (progression, fichier en cours
    /// d'utilisation) : la doc de la fonction n'énonce aucune exigence d'apartment, c'est le modèle attendu du
    /// shell en général — non vérifié en MTA, et il n'y a aucune raison d'aller le tester.
    /// </summary>
    public static Task<RecycleResult> SendToRecycleBinAsync(string path, IntPtr ownerHwnd)
    {
        var completion = new TaskCompletionSource<RecycleResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { completion.SetResult(SendToRecycleBin(path, ownerHwnd)); }
            catch (Exception ex) { completion.SetException(ex); }
        })
        {
            Name = "PCPerfSuite - Corbeille",
            // Thread d'arrière-plan : un thread d'avant-plan empêche le CLR de terminer le processus tant qu'il
            // tourne. Une suppression longue, ou un dialogue du shell resté ouvert, laisserait sinon PCPerfSuite
            // en processus fantôme après la fermeture de la fenêtre.
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static RecycleResult SendToRecycleBin(string path, IntPtr ownerHwnd)
    {
        var operation = new SHFILEOPSTRUCT
        {
            hwnd = ownerHwnd,
            wFunc = FO_DELETE,
            // pFrom est une liste terminée par deux caractères nuls : le marshaling LPWStr ajoute le second.
            pFrom = path + '\0',
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_WANTNUKEWARNING,
        };

        int result = SHFileOperation(ref operation);
        return new RecycleResult { ErrorCode = result, Aborted = operation.fAnyOperationsAborted };
    }

    /// <summary>Ouvre la fenêtre Propriétés de l'Explorateur pour ce chemin. À appeler depuis le thread UI : la
    /// feuille de propriétés a besoin d'un thread STA qui pompe les messages et qui reste en vie tant qu'elle est
    /// affichée. La doc de ShellExecuteEx ne dit pas si l'appel rend la main immédiatement avec le verbe
    /// « properties » — à confirmer en exécutant l'app ; si l'interface se fige, ajouter SEE_MASK_ASYNCOK.</summary>
    public static void ShowProperties(string path, IntPtr ownerHwnd)
    {
        var info = new SHELLEXECUTEINFO
        {
            cbSize = (uint)Marshal.SizeOf<SHELLEXECUTEINFO>(),
            fMask = SEE_MASK_INVOKEIDLIST,
            hwnd = ownerHwnd,
            lpVerb = "properties",
            lpFile = path,
            nShow = SW_SHOW,
        };

        if (!ShellExecuteEx(ref info))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    // Disposition séquentielle par défaut : shellapi.h (SDK 10.0.26100) encadre ses structures par
    // « #if !defined(_WIN64) / #include <pshpack1.h> » ligne 56 et le poppack correspondant ligne 1678, donc
    // l'alignement sur 1 octet ne vaut qu'en 32 bits ; en 64 bits elles suivent l'alignement naturel
    // (SHFILEOPSTRUCTW = 56 octets, SHELLEXECUTEINFOW = 112). Le projet est en AnyCPU et tourne donc en 64 bits
    // sur un Windows 64 bits : ne pas le forcer en x86 sans repasser sur Pack = 1.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHELLEXECUTEINFO
    {
        public uint cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public string? lpVerb;
        public string? lpFile;
        public string? lpParameters;
        public string? lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        public string? lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIconOrMonitor;
        public IntPtr hProcess;
    }

    [DllImport("shell32.dll", EntryPoint = "SHFileOperationW", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

    [DllImport("shell32.dll", EntryPoint = "ShellExecuteExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO lpExecInfo);
}
