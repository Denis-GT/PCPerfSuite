using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PCPerfSuite.App.Interop;

/// <summary>
/// Ramène réellement une fenêtre devant. Activate() seul ne suffit pas pour une fenêtre ré-affichée
/// après un Hide() : le clic vient de l'Explorateur et non de notre processus, donc Windows refuse
/// souvent de nous donner le premier plan et se contente de faire clignoter la barre des tâches.
///
/// Volontairement sans l'astuce Topmost=true/false : l'app possède déjà une fenêtre d'overlay
/// toujours au-dessus (voir <see cref="TopmostKeeper"/>), et la fenêtre principale passerait devant.
/// </summary>
internal static class WindowActivator
{
    private const int SW_SHOW = 5;
    private const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    public static void Restore(Window window)
    {
        window.Activate();

        try
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            ShowWindow(hwnd, SW_SHOW);
            ShowWindow(hwnd, SW_RESTORE);
            SetForegroundWindow(hwnd);
        }
        catch
        {
            // La fenêtre est affichée de toute façon : au pire elle reste derrière une autre.
        }
    }

    /// <summary>Passe un HWND au premier plan. Nécessaire avant d'ouvrir un menu depuis la zone de
    /// notification : sans fenêtre active à lui, Windows ne referme pas le menu au clic à côté.</summary>
    public static void SetForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        try
        {
            SetForegroundWindow(hwnd);
        }
        catch
        {
            // Au pire, le menu reste ouvert un instant de trop.
        }
    }
}
