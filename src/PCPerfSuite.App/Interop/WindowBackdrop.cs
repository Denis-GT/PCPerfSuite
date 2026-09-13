using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace PCPerfSuite.App.Interop;

/// <summary>
/// Active le fond translucide natif de Windows 11 (Mica/Acrylic, la même techno que les effets
/// "verre" de l'Explorateur/Paramètres) derrière la fenêtre, pour un rendu façon verre dépoli.
/// Pur bonus visuel : sur un Windows plus ancien ou si l'appel échoue, on ne fait rien et la
/// fenêtre garde le dégradé opaque défini dans Theme.xaml (BgBrush), qui reste lisible seul.
/// </summary>
internal static class WindowBackdrop
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

    private enum BackdropType
    {
        Mica = 2,
        Acrylic = 3,
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);

    public static void Apply(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero) return;

                int darkMode = 1;
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));

                int acrylic = (int)BackdropType.Acrylic;
                int hr = DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref acrylic, sizeof(int));
                if (hr != 0)
                {
                    int mica = (int)BackdropType.Mica;
                    hr = DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref mica, sizeof(int));
                }

                if (hr == 0)
                {
                    // Ne rendre le fond transparent qu'après confirmation que Windows a bien pris
                    // en charge le fond translucide, sinon la fenêtre se retrouverait vide.
                    window.Background = Brushes.Transparent;
                }
            }
            catch
            {
                // Effet cosmétique seulement (Windows 11 22H2+) : on garde le dégradé XAML sinon.
            }
        };
    }
}
