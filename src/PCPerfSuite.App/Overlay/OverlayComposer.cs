using System.Text;
using System.Windows.Media;
using PCPerfSuite.App.Metrics;

namespace PCPerfSuite.App.Overlay;

/// <summary>Morceau coloré d'une ligne d'overlay (le libellé et la valeur n'ont pas la même couleur).</summary>
public sealed class OverlaySegment
{
    public required string Text { get; init; }
    public required string ColorHex { get; init; }

    /// <summary>Pinceau figé prêt pour le rendu WPF (aperçu et fenêtre d'overlay).</summary>
    public Brush Brush => _brush ??= OverlayPalette.ToBrush(ColorHex);

    private Brush? _brush;
}

public sealed class OverlayLine
{
    public required IReadOnlyList<OverlaySegment> Segments { get; init; }
}

/// <summary>Couleurs à appliquer à une ligne d'overlay.</summary>
public sealed class OverlayColorScheme
{
    /// <summary>Couleur du libellé d'une catégorie (CPU, RAM, NET...).</summary>
    public required Func<MetricCategory, string> CategoryColor { get; init; }

    public required string ValueColor { get; init; }
}

/// <summary>
/// Met en forme les métriques sélectionnées en lignes colorées, puis, pour RTSS, en texte balisé.
/// Une seule source de vérité : l'aperçu dans l'app, la fenêtre d'overlay et l'OSD de RTSS affichent
/// exactement la même chose.
/// </summary>
public static class OverlayComposer
{
    public static List<OverlayLine> Compose(
        MetricSample sample,
        IReadOnlyList<MetricDefinition> metrics,
        bool oneLinePerMetric,
        OverlayColorScheme colors)
    {
        var lines = new List<OverlayLine>();

        if (oneLinePerMetric)
        {
            foreach (MetricDefinition metric in metrics)
            {
                lines.Add(new OverlayLine
                {
                    Segments = new[]
                    {
                        new OverlaySegment
                        {
                            Text = $"{metric.Category.OsdLabel} {metric.OsdLabel}",
                            ColorHex = colors.CategoryColor(metric.Category),
                        },
                        new OverlaySegment
                        {
                            Text = $"  {metric.Read(sample).Text}",
                            ColorHex = colors.ValueColor,
                        },
                    },
                });
            }

            return lines;
        }

        // Façon Afterburner : une ligne par catégorie, ex. "GPU  45%  62°C  180 W".
        foreach (IGrouping<MetricCategory, MetricDefinition> group in metrics.GroupBy(m => m.Category))
        {
            string values = string.Join("  ", group.Select(m => m.Read(sample).Text));
            lines.Add(new OverlayLine
            {
                Segments = new[]
                {
                    new OverlaySegment { Text = group.Key.OsdLabel, ColorHex = colors.CategoryColor(group.Key) },
                    new OverlaySegment { Text = $"  {values}", ColorHex = colors.ValueColor },
                },
            });
        }

        return lines;
    }

    /// <summary>
    /// Sérialise les lignes pour l'OSD de RTSS. RTSS interprète des balises de mise en forme dans le
    /// texte partagé : &lt;C=AARRGGBB&gt;…&lt;C&gt; pour la couleur et &lt;S=nnn&gt;…&lt;S&gt; pour la
    /// taille (en % de la police configurée dans RTSS). Chaque balise est refermée pour ne pas
    /// déteindre sur le texte des autres applications qui partagent l'OSD.
    /// </summary>
    public static string ToRtssText(IReadOnlyList<OverlayLine> lines, bool withColors, int sizePercent)
    {
        var text = new StringBuilder();
        bool withSize = sizePercent is > 0 and not 100;

        if (withSize) text.Append($"<S={sizePercent}>");

        for (int i = 0; i < lines.Count; i++)
        {
            if (i > 0) text.Append('\n');

            foreach (OverlaySegment segment in lines[i].Segments)
            {
                if (withColors)
                {
                    text.Append($"<C={OverlayPalette.ToRtssColor(segment.ColorHex)}>{segment.Text}<C>");
                }
                else
                {
                    text.Append(segment.Text);
                }
            }
        }

        if (withSize) text.Append("<S>");

        return text.ToString();
    }
}
