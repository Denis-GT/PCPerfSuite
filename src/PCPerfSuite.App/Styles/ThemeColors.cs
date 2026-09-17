using System.Windows;
using System.Windows.Media;

namespace PCPerfSuite.App.Styles;

/// <summary>
/// Couleurs du thème (Styles/Theme.xaml) pour les contrôles dessinés en code, qui n'ont pas accès aux
/// StaticResource. Lues dans les ressources de l'application, avec la valeur du thème en secours
/// (concepteur XAML, ou appel avant le chargement des ressources).
/// </summary>
internal static class ThemeColors
{
    public static Color Accent => Get("AccentColor", 0xFF0A84FF);
    public static Color Accent2 => Get("AccentColor2", 0xFF5FE0C7);
    public static Color Warn => Get("WarnColor", 0xFFFF9F0A);
    public static Color TextPrimary => Get("TextPrimaryColor", 0xFFF2F4FA);
    public static Color TextSecondary => Get("TextSecondaryColor", 0xFFACB4C8);

    /// <summary>La même couleur avec une autre opacité.</summary>
    public static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    public static SolidColorBrush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Color Get(string key, uint fallbackArgb)
        => Application.Current?.TryFindResource(key) is Color color
            ? color
            : Color.FromArgb((byte)(fallbackArgb >> 24), (byte)(fallbackArgb >> 16), (byte)(fallbackArgb >> 8), (byte)fallbackArgb);
}
