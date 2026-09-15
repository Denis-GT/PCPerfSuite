using System.Globalization;
using System.Windows.Media;

namespace PCPerfSuite.App.Overlay;

/// <summary>Palette proposée pour colorer l'overlay : des teintes vives et lisibles sur une image de
/// jeu, plutôt qu'un sélecteur de couleur complet (un clic suffit à changer une ligne).</summary>
public static class OverlayPalette
{
    /// <summary>Contient toutes les couleurs par défaut du catalogue de métriques, pour que la
    /// couleur active d'une catégorie soit toujours l'une des pastilles affichées.</summary>
    public static IReadOnlyList<string> Colors { get; } = new[]
    {
        "#FFFFFF", "#B7C0D8", "#4CC2FF", "#00E5FF", "#4DD9C0", "#7BE38B",
        "#C6F432", "#FFD166", "#FFB74D", "#FF7A9C", "#FF5A52", "#C08CFF",
    };

    /// <summary>Convertit "#RRGGBB" en pinceau figé (Freeze : partagé entre l'aperçu et la fenêtre
    /// d'overlay sans recréer un pinceau à chaque relevé). Retombe sur blanc si la chaîne est
    /// invalide — un settings.json édité à la main ne doit pas faire planter l'app.</summary>
    public static Brush ToBrush(string? hex)
    {
        var brush = new SolidColorBrush(ToColor(hex));
        brush.Freeze();
        return brush;
    }

    public static Color ToColor(string? hex)
    {
        if (TryParse(hex, out Color color)) return color;
        return FallbackColor;
    }

    /// <summary>Format attendu par les balises de mise en forme de RTSS : AARRGGBB (l'alpha ne doit
    /// jamais valoir zéro, sinon RTSS considère la couleur comme absente).</summary>
    public static string ToRtssColor(string? hex)
    {
        Color c = ToColor(hex);
        return $"FF{c.R:X2}{c.G:X2}{c.B:X2}";
    }

    private static readonly Color FallbackColor = Color.FromRgb(0xFF, 0xFF, 0xFF);

    private static bool TryParse(string? hex, out Color color)
    {
        color = FallbackColor;
        if (string.IsNullOrWhiteSpace(hex)) return false;

        string value = hex.Trim().TrimStart('#');
        if (value.Length == 8) value = value[2..]; // AARRGGBB : on ignore l'alpha
        if (value.Length != 6) return false;

        if (!byte.TryParse(value.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte r) ||
            !byte.TryParse(value.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte g) ||
            !byte.TryParse(value.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
        {
            return false;
        }

        color = Color.FromRgb(r, g, b);
        return true;
    }
}
