using System.Globalization;
using System.Windows.Media;
using PCPerfSuite.App.Metrics;

namespace PCPerfSuite.App.Overlay;

/// <summary>Palette proposée pour colorer l'overlay : des teintes vives et lisibles sur une image de
/// jeu, plutôt qu'un sélecteur de couleur complet (un clic suffit à changer une ligne).</summary>
public static class OverlayPalette
{
    /// <summary>Teintes vives, puis les couleurs par défaut des catégories du catalogue de métriques (plus
    /// foncées). Ces dernières y sont reprises du catalogue, pour que la couleur active d'une catégorie
    /// non personnalisée soit toujours l'une des pastilles affichées, quoi qu'on change à ses défauts.</summary>
    public static IReadOnlyList<string> Colors { get; } = new[]
        {
            "#FFFFFF", "#B7C0D8", "#4CC2FF", "#00E5FF", "#4DD9C0", "#7BE38B",
            "#C6F432", "#FFD166", "#FFB74D", "#FF7A9C", "#FF5A52", "#C08CFF",
        }
        .Concat(MetricCatalog.Categories.Select(category => category.OverlayColor))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

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

    /// <summary>« #RRGGBB » en majuscules : le format enregistré dans settings.json.</summary>
    public static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    /// <summary>Teinte (0-360), saturation et luminosité (0-1) d'une couleur, pour le sélecteur de couleur précis.</summary>
    public static (double Hue, double Saturation, double Value) ToHsv(Color color)
    {
        double r = color.R / 255d, g = color.G / 255d, b = color.B / 255d;
        double max = Math.Max(r, Math.Max(g, b));
        double delta = max - Math.Min(r, Math.Min(g, b));

        double hue = delta == 0 ? 0
            : max == r ? 60 * (((g - b) / delta) % 6)
            : max == g ? 60 * (((b - r) / delta) + 2)
            : 60 * (((r - g) / delta) + 4);
        if (hue < 0) hue += 360;

        return (hue, max == 0 ? 0 : delta / max, max);
    }

    public static Color FromHsv(double hue, double saturation, double value)
    {
        hue = ((hue % 360) + 360) % 360;
        saturation = Math.Clamp(saturation, 0, 1);
        value = Math.Clamp(value, 0, 1);

        double chroma = value * saturation;
        double x = chroma * (1 - Math.Abs((hue / 60) % 2 - 1));
        double m = value - chroma;

        (double r, double g, double b) = hue switch
        {
            < 60 => (chroma, x, 0d),
            < 120 => (x, chroma, 0d),
            < 180 => (0d, chroma, x),
            < 240 => (0d, x, chroma),
            < 300 => (x, 0d, chroma),
            _ => (chroma, 0d, x),
        };

        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    private static readonly Color FallbackColor = Color.FromRgb(0xFF, 0xFF, 0xFF);

    /// <summary>Lit « #RRGGBB », « RRGGBB » ou « AARRGGBB » (l'alpha est ignoré). Faux si la chaîne n'est pas une couleur.</summary>
    public static bool TryParse(string? hex, out Color color)
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
