using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PCPerfSuite.App.Interop;

/// <summary>
/// Rend une fenêtre totalement "traversante" pour la souris et invisible pour Alt+Tab : indispensable
/// pour un overlay, sinon la fenêtre volerait les clics destinés au jeu situé dessous.
///
/// - WS_EX_TRANSPARENT : les clics passent au travers (WPF IsHitTestVisible ne joue qu'à l'intérieur
///   de l'app, pas au niveau de Windows).
/// - WS_EX_NOACTIVATE : la fenêtre ne prend jamais le focus (le jeu ne se minimise pas).
/// - WS_EX_TOOLWINDOW : pas d'entrée dans Alt+Tab ni dans la barre des tâches.
/// </summary>
internal static class ClickThroughWindow
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    public static void Apply(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero) return;

                int style = GetWindowLong(hwnd, GWL_EXSTYLE);
                SetWindowLong(hwnd, GWL_EXSTYLE, style | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
            }
            catch
            {
                // Sans ces styles l'overlay reste affiché, il capterait juste les clics : on n'annule rien.
            }
        };
    }
}
