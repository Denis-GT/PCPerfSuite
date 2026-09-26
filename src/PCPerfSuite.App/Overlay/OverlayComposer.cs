using System.Text;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using PCPerfSuite.App.Metrics;

namespace PCPerfSuite.App.Overlay;

/// <summary>Valeur d'une métrique dans une ligne d'overlay. Mise à jour en place à chaque relevé : seul le texte
/// qui change réellement est redessiné, et la mise en page ne bouge pas.</summary>
public sealed partial class OverlayCell : ObservableObject
{
    public OverlayCell(MetricDefinition metric, int column, string? prefix = null)
    {
        Metric = metric;
        Prefix = prefix;
        ValueGroup = $"value{column}";
        UnitGroup = $"unit{column}";
    }

    public MetricDefinition Metric { get; }

    /// <summary>Sous-libellé affiché avant la valeur (« VRAM », « RAM » sur la ligne MEM) ; null pour la plupart des cellules.</summary>
    public string? Prefix { get; }

    /// <summary>Noms des colonnes partagées d'une ligne à l'autre (voir StickyWidth).</summary>
    public string ValueGroup { get; }
    public string UnitGroup { get; }

    [ObservableProperty] private string value = "--";
    [ObservableProperty] private string unit = "";

    public void Apply(MetricSample sample)
    {
        MetricReading reading = Metric.Read(sample);
        Value = reading.Value;
        // Comme MetricReading.Text : "%" et "°C" collés au nombre, les autres unités séparées d'une espace.
        Unit = reading.Unit is "" or "%" or "°C" ? reading.Unit : " " + reading.Unit;
    }
}

public sealed class OverlayLine
{
    /// <summary>Clé stable de la ligne dans l'ordre enregistré : clé de catégorie (« ram » pour la ligne MEM) ou
    /// identifiant de métrique en mode une ligne par métrique.</summary>
    public required string Key { get; init; }

    /// <summary>Nom lisible de la ligne, pour la liste qui règle leur ordre.</summary>
    public required string Title { get; init; }

    public required string Label { get; init; }
    public required string LabelColorHex { get; init; }
    public required string ValueColorHex { get; init; }
    public required IReadOnlyList<OverlayCell> Cells { get; init; }

    /// <summary>Pinceaux figés prêts pour le rendu WPF (aperçu et fenêtre d'overlay).</summary>
    public Brush LabelBrush => _labelBrush ??= OverlayPalette.ToBrush(LabelColorHex);
    public Brush ValueBrush => _valueBrush ??= OverlayPalette.ToBrush(ValueColorHex);

    private Brush? _labelBrush;
    private Brush? _valueBrush;
}

/// <summary>Couleurs à appliquer à une ligne d'overlay.</summary>
public sealed class OverlayColorScheme
{
    /// <summary>Couleur du libellé d'une catégorie (CPU, RAM, NET...).</summary>
    public required Func<MetricCategory, string> CategoryColor { get; init; }

    public required string ValueColor { get; init; }
}

/// <summary>
/// Met en forme les métriques sélectionnées en lignes à colonnes fixes, puis, pour RTSS, en texte balisé.
/// Une seule source de vérité : l'aperçu dans l'app, la fenêtre d'overlay et l'OSD de RTSS affichent
/// exactement la même chose.
///
/// La structure (lignes et cellules) n'est construite qu'au changement de réglage ; chaque relevé ne fait que
/// mettre à jour les valeurs (<see cref="Update"/>).
/// </summary>
public static class OverlayComposer
{
    /// <summary>Libellé de la ligne qui regroupe la mémoire du GPU et la RAM.</summary>
    private const string MemoryLabel = "MEM";

    /// <summary>Nom de la ligne MEM dans la liste d'ordre des lignes.</summary>
    private const string MemoryTitle = "Mémoire (GPU et RAM)";

    /// <param name="lineOrder">Ordre des lignes : clés de catégorie, ou identifiants de métrique en mode une ligne
    /// par métrique (voir <see cref="OverlayLineOrder"/>). Null : l'ordre du catalogue.</param>
    public static List<OverlayLine> Build(
        IReadOnlyList<MetricDefinition> metrics,
        bool oneLinePerMetric,
        OverlayColorScheme colors,
        bool memorySubLabels = true,
        IReadOnlyList<string>? lineOrder = null)
    {
        if (oneLinePerMetric)
        {
            List<OverlayLine> perMetric = metrics
                .Select(metric => new OverlayLine
                {
                    Key = metric.Id,
                    Title = metric.Label,
                    Label = $"{metric.Category.OsdLabel} {metric.OsdLabel}",
                    LabelColorHex = colors.CategoryColor(metric.Category),
                    ValueColorHex = colors.ValueColor,
                    Cells = new[] { new OverlayCell(metric, 0) },
                })
                .ToList();

            return lineOrder is null ? perMetric : OverlayLineOrder.Sort(perMetric, line => line.Key, lineOrder);
        }

        // Façon Afterburner : une ligne par catégorie, ex. "GPU  45%  62°C  180 W". La mémoire du GPU quitte la
        // ligne GPU pour rejoindre la RAM sur une ligne MEM, qui porte la clé de la RAM : elle suit donc la
        // catégorie RAM dans l'ordre, sous GPU par défaut.
        List<OverlayLine> perCategory = metrics
            .GroupBy(LineCategory)
            .Select(group => group.Key == MetricCatalog.Ram
                ? BuildMemoryLine(group, colors, memorySubLabels)
                : new OverlayLine
                {
                    Key = group.Key.Key,
                    Title = group.Key.Name,
                    Label = group.Key.OsdLabel,
                    LabelColorHex = colors.CategoryColor(group.Key),
                    ValueColorHex = colors.ValueColor,
                    Cells = group.Select((metric, column) => new OverlayCell(metric, column)).ToArray(),
                })
            .ToList();

        return OverlayLineOrder.Sort(perCategory, line => line.Key, lineOrder ?? CatalogCategoryOrder);
    }

    /// <summary>Ordre par défaut des lignes par catégorie : celui du catalogue.</summary>
    public static IReadOnlyList<string> CatalogCategoryOrder { get; } = MetricCatalog.Categories.Select(c => c.Key).ToArray();

    /// <summary>Catégorie de la ligne d'une métrique : la mémoire du GPU est rangée avec la RAM.</summary>
    private static MetricCategory LineCategory(MetricDefinition metric)
        => MetricCatalog.GpuMemoryIds.Contains(metric.Id) ? MetricCatalog.Ram : metric.Category;

    /// <summary>Ligne MEM : d'abord la mémoire du GPU, puis la RAM, chaque groupe précédé de son sous-libellé si demandé.</summary>
    private static OverlayLine BuildMemoryLine(IEnumerable<MetricDefinition> metrics, OverlayColorScheme colors, bool subLabels)
    {
        MetricDefinition[] ordered = metrics
            .OrderBy(m => MetricCatalog.GpuMemoryIds.Contains(m.Id) ? 0 : 1)
            .ToArray();

        MetricDefinition? firstVram = ordered.FirstOrDefault(m => MetricCatalog.GpuMemoryIds.Contains(m.Id));
        MetricDefinition? firstRam = ordered.FirstOrDefault(m => !MetricCatalog.GpuMemoryIds.Contains(m.Id));

        string? PrefixFor(MetricDefinition metric)
        {
            if (!subLabels) return null;
            if (metric == firstVram) return "VRAM";
            return metric == firstRam ? "RAM" : null;
        }

        return new OverlayLine
        {
            Key = MetricCatalog.Ram.Key,
            Title = MemoryTitle,
            Label = MemoryLabel,
            LabelColorHex = colors.CategoryColor(MetricCatalog.Ram),
            ValueColorHex = colors.ValueColor,
            Cells = ordered.Select((metric, column) => new OverlayCell(metric, column, PrefixFor(metric))).ToArray(),
        };
    }

    public static void Update(IEnumerable<OverlayLine> lines, MetricSample sample)
    {
        foreach (OverlayLine line in lines)
        {
            foreach (OverlayCell cell in line.Cells) cell.Apply(sample);
        }
    }

    /// <summary>
    /// Sérialise les lignes pour l'OSD de RTSS. RTSS interprète des balises de mise en forme dans le
    /// texte partagé : &lt;C=AARRGGBB&gt;…&lt;C&gt; pour la couleur, &lt;S=nnn&gt;…&lt;S&gt; pour la
    /// taille (en % de la police configurée dans RTSS) et &lt;A=n&gt;…&lt;A&gt; pour caler un texte dans un
    /// champ de n caractères (signe de n : sens de l'alignement). Chaque balise est refermée pour ne pas
    /// déteindre sur le texte des autres applications qui partagent l'OSD.
    ///
    /// Les largeurs de champ viennent de <paramref name="widths"/>, qui ne font que grandir : comme dans la
    /// fenêtre, une valeur qui change de longueur ne décale plus la suite de la ligne.
    /// </summary>
    public static string ToRtssText(IReadOnlyList<OverlayLine> lines, bool withColors, int sizePercent, RtssColumnWidths widths)
    {
        var text = new StringBuilder();
        bool withSize = sizePercent is > 0 and not 100;

        if (withSize) text.Append($"<S={sizePercent}>");

        int labelWidth = widths.Grow("label", lines.Select(l => l.Label.Length).DefaultIfEmpty(0).Max());

        for (int i = 0; i < lines.Count; i++)
        {
            OverlayLine line = lines[i];
            if (i > 0) text.Append('\n');

            // Libellé calé à gauche, valeur à droite (collée à son unité), unité à gauche.
            AppendColored(text, Field(line.Label, labelWidth, rightAligned: false), line.LabelColorHex, withColors);

            foreach (OverlayCell cell in line.Cells)
            {
                if (!string.IsNullOrEmpty(cell.Prefix)) AppendColored(text, "  " + cell.Prefix, line.LabelColorHex, withColors);

                int valueWidth = widths.Grow(cell.ValueGroup, cell.Value.Length);
                int unitWidth = widths.Grow(cell.UnitGroup, cell.Unit.Length);

                string field = "  " + Field(cell.Value, valueWidth, rightAligned: true);
                if (unitWidth > 0) field += Field(cell.Unit, unitWidth, rightAligned: false);

                AppendColored(text, field, line.ValueColorHex, withColors);
            }
        }

        if (withSize) text.Append("<S>");

        return text.ToString();
    }

    private static string Field(string value, int width, bool rightAligned)
        => width <= 0 ? value : $"<A={(rightAligned ? -width : width)}>{value}<A>";

    private static void AppendColored(StringBuilder text, string content, string colorHex, bool withColors)
    {
        if (withColors) text.Append($"<C={OverlayPalette.ToRtssColor(colorHex)}>{content}<C>");
        else text.Append(content);
    }
}

/// <summary>Largeur maximale vue par colonne de l'OSD RTSS, en caractères. Remise à zéro avec la mise en page.</summary>
public sealed class RtssColumnWidths
{
    private readonly Dictionary<string, int> _widths = new();

    public int Grow(string column, int length)
    {
        int width = _widths.TryGetValue(column, out int current) ? Math.Max(current, length) : length;
        _widths[column] = width;
        return width;
    }

    public void Reset() => _widths.Clear();
}
