using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PCPerfSuite.Core.Installations;

/// <summary>Comment un outil a été lancé.</summary>
public enum UnelevatedLaunchResult
{
    /// <summary>Lancé par le shell du bureau, avec les droits de la personne connectée : l'outil demande lui-même
    /// l'autorisation de Windows (UAC) s'il a besoin d'être administrateur.</summary>
    Launched,

    /// <summary>Le shell n'a pas répondu : l'Explorateur est ouvert sur le fichier, à lancer d'un double-clic.</summary>
    ShownInExplorer,

    Failed,
}

/// <summary>
/// Lance un programme SANS les droits administrateur de PCPerfSuite. L'app tourne élevée : un Process.Start direct
/// transmettrait ce jeton à l'outil, sans invite. Ici, c'est le shell du bureau (explorer.exe, qui tourne déjà sous le
/// compte de la personne connectée) qui fait le ShellExecute, par sa vue du bureau (IShellWindows → IShellBrowser →
/// IShellView → IShellDispatch2.ShellExecute) : la méthode que Microsoft donne pour lancer un processus non élevé depuis
/// un processus élevé. Un outil qui a besoin d'être administrateur affiche alors sa propre invite UAC.
///
/// Si le shell ne répond pas (Explorateur arrêté, bureau à distance particulier), rien n'est lancé en administrateur
/// à la place : l'Explorateur est ouvert sur le fichier (explorer.exe /select, comme Processus › Ouvrir l'emplacement),
/// et l'utilisateur le lance d'un double-clic.
///
/// Best-effort (règle 2) : ne lève jamais.
/// </summary>
public static class UnelevatedLauncher
{
    private const int CsidlDesktop = 0;
    private const int SwcDesktop = 8;
    private const int SwfoNeedDispatch = 1;
    private const uint SvgioBackground = 0;
    private const int SwShowNormal = 1;

    private static readonly Guid ShellWindowsClsid = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    private static readonly Guid TopLevelBrowserService = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    private static readonly Guid ShellBrowserIid = new("000214E2-0000-0000-C000-000000000046");
    private static readonly Guid DispatchIid = new("00020400-0000-0000-C000-000000000046");

    /// <summary><paramref name="showInExplorerOnFailure"/> : si le shell ne répond pas, ouvrir l'Explorateur sur le
    /// fichier plutôt que d'échouer (faux pour une commande comme winget, qui ne se lance pas d'un double-clic).</summary>
    public static UnelevatedLaunchResult Launch(string path, string arguments, out string? error, bool showInExplorerOnFailure = true)
    {
        if (!File.Exists(path))
        {
            error = $"Fichier introuvable : {path}";
            return UnelevatedLaunchResult.Failed;
        }

        try
        {
            ShellExecuteFromDesktop(path, arguments, Path.GetDirectoryName(path) ?? "");
            error = null;
            return UnelevatedLaunchResult.Launched;
        }
        catch (Exception shellError)
        {
            error = null;
            if (showInExplorerOnFailure && TryShowInExplorer(path, out error)) return UnelevatedLaunchResult.ShownInExplorer;
            error = $"Lancement impossible ({shellError.Message}). {error}".TrimEnd();
            return UnelevatedLaunchResult.Failed;
        }
    }

    /// <summary>Ouvre l'Explorateur sur le fichier, sélectionné. L'Explorateur tourne sous le compte de la personne
    /// connectée : rien n'hérite du jeton de PCPerfSuite.</summary>
    public static bool TryShowInExplorer(string path, out string? error)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
                $"/select,\"{path}\"") { UseShellExecute = true })?.Dispose();
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = $"Impossible d'ouvrir l'Explorateur sur « {path} » ({ex.Message}).";
            return false;
        }
    }

    private static void ShellExecuteFromDesktop(string path, string arguments, string directory)
    {
        Type type = Type.GetTypeFromCLSID(ShellWindowsClsid, throwOnError: true)!;
        object? shellWindowsObject = null;
        object? desktop = null;
        IShellBrowser? browser = null;
        IShellView? view = null;
        object? folderView = null;
        try
        {
            shellWindowsObject = Activator.CreateInstance(type)!;
            var shellWindows = (IShellWindows)shellWindowsObject;

            object location = CsidlDesktop;
            object? empty = null;
            desktop = shellWindows.FindWindowSW(ref location, ref empty, SwcDesktop, out _, SwfoNeedDispatch)
                      ?? throw new InvalidOperationException("bureau introuvable");

            Guid service = TopLevelBrowserService;
            Guid browserIid = ShellBrowserIid;
            ((IComServiceProvider)desktop).QueryService(ref service, ref browserIid, out object browserObject);
            browser = (IShellBrowser)browserObject;

            browser.QueryActiveShellView(out view);
            Guid dispatchIid = DispatchIid;
            view.GetItemObject(SvgioBackground, ref dispatchIid, out folderView);

            // IShellFolderViewDual.Application → IShellDispatch2, appelé par IDispatch : ShellExecute s'exécute dans le
            // processus de l'Explorateur, avec son jeton.
            dynamic shellFolderView = folderView;
            dynamic application = shellFolderView.Application;
            application.ShellExecute(path, arguments, directory, "open", SwShowNormal);
        }
        finally
        {
            Release(folderView);
            Release(view);
            Release(browser);
            Release(desktop);
            Release(shellWindowsObject);
        }
    }

    private static void Release(object? comObject)
    {
        try
        {
            if (comObject is not null && Marshal.IsComObject(comObject)) Marshal.ReleaseComObject(comObject);
        }
        catch { /* best-effort */ }
    }

    // Interfaces du shell déclarées dans l'ordre exact de leur table virtuelle (exdisp.idl, shobjidl.idl) : seules les
    // méthodes appelées ont une signature utile, les autres tiennent leur place.

    [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IShellWindows
    {
        int Count { get; }
        [return: MarshalAs(UnmanagedType.IDispatch)] object Item([In, Optional] object index);
        [return: MarshalAs(UnmanagedType.IUnknown)] object NewEnum();
        void Register([MarshalAs(UnmanagedType.IDispatch)] object dispatch, int hwnd, int shellWindowClass, out int cookie);
        void RegisterPending(int threadId, [In] ref object location, [In] ref object? locationRoot, int shellWindowClass, out int cookie);
        void Revoke(int cookie);
        void OnNavigate(int cookie, [In] ref object location);
        void OnActivated(int cookie, [MarshalAs(UnmanagedType.VariantBool)] bool active);

        [return: MarshalAs(UnmanagedType.IDispatch)]
        object? FindWindowSW([In] ref object location, [In] ref object? locationRoot, int shellWindowClass, out int hwnd, int options);
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IComServiceProvider
    {
        void QueryService(ref Guid service, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object result);
    }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        void GetWindow(out IntPtr window);
        void ContextSensitiveHelp([MarshalAs(UnmanagedType.Bool)] bool enterMode);
        void InsertMenusSB(IntPtr shared, IntPtr menuWidths);
        void SetMenuSB(IntPtr shared, IntPtr oleMenu, IntPtr activeObject);
        void RemoveMenusSB(IntPtr shared);
        void SetStatusTextSB(IntPtr statusText);
        void EnableModelessSB([MarshalAs(UnmanagedType.Bool)] bool enable);
        void TranslateAcceleratorSB(IntPtr message, ushort id);
        void BrowseObject(IntPtr pidl, uint flags);
        void GetViewStateStream(uint mode, out IntPtr stream);
        void GetControlWindow(uint id, out IntPtr window);
        void SendControlMsg(uint id, uint message, IntPtr wParam, IntPtr lParam, out IntPtr result);
        void QueryActiveShellView(out IShellView view);
    }

    [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellView
    {
        void GetWindow(out IntPtr window);
        void ContextSensitiveHelp([MarshalAs(UnmanagedType.Bool)] bool enterMode);
        void TranslateAccelerator(IntPtr message);
        void EnableModeless([MarshalAs(UnmanagedType.Bool)] bool enable);
        void UIActivate(uint state);
        void Refresh();
        void CreateViewWindow(IntPtr previous, IntPtr settings, IntPtr browser, IntPtr rect, out IntPtr window);
        void DestroyViewWindow();
        void GetCurrentInfo(IntPtr settings);
        void AddPropertySheetPages(uint reserved, IntPtr callback, IntPtr lParam);
        void SaveViewState();
        void SelectItem(IntPtr pidl, uint flags);
        void GetItemObject(uint item, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object result);
    }
}
