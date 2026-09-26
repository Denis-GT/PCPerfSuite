using System.Text;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using PCPerfSuite.App.Metrics;

namespace PCPerfSuite.App.Overlay;

/// <summary>Valeur d'une métrique dans une ligne d'overlay. Mise à jour en place à chaque relevé : seul le texte
/// qui change réellement est redessiné, et la mise en page ne bouge pas.</summary>
public sealed partial class OverlayCell : ObservableObject
{
    public OverlayCell(MetricDefinition metric, string? prefix = null, string gap = OverlayComposer.WideGap, bool showUnit = true)
    {
        Metric = metric;
        Prefix = prefix;
        Gap = gap;
        ShowUnit = showUnit;
        ValueGroup = $"value.{metric.Id}";
        UnitGroup = $"unit.{metric.Id}";
    }

    public MetricDefinition Metric { get; }

    /// <summary>Sous-libellé affiché avant la valeur (« MOY », « 1% » sur la ligne JEU) ; null pour la plupart des
    /// cellules.</summary>
    public string? Prefix { get; }

    /// <summary>Faux quand le libellé de la cellule tient lieu d'unité (« MOY 138 » et non « MOY 138 FPS »).</summary>
    public bool ShowUnit { get; }

    /// <summary>Espaces entre ce qui précède (libellé de ligne, cellule ou sous-libellé) et la valeur. Choisis par
    /// <see cref="OverlayComposer"/> : resserrés dans une même ligne, sauf autour des débits disque et réseau.</summary>
    public string Gap { get; }

    /// <summary>Noms des colonnes de la valeur et de l'unité (voir StickyWidth et <see cref="RtssColumnWidths"/>) :
    /// propres à la métrique, donc à sa ligne. Partagées d'une ligne à l'autre, elles alignaient les valeurs, mais
    /// chacune prenait la largeur de la plus large de sa colonne sur toutes les lignes (un débit réseau sous une
    /// charge CPU) : l'écart entre deux valeurs d'une ligne en dépendait bien plus que des espaces.</summary>
    public string ValueGroup { get; }
    public string UnitGroup { get; }

    [ObservableProperty] private string value = "--";
    [ObservableProperty] private string unit = "";

    public void Apply(MetricSample sample)
    {
        MetricReading reading = Metric.Read(sample);
        Value = reading.Value;
        // Comme MetricReading.Text : "%" et "°C" collés au nombre, les autres unités séparées d'une espace.
        Unit = !ShowUnit ? "" : reading.Unit is "" or "%" or "°C" ? reading.Unit : " " + reading.Unit;
    }
}

public sealed class OverlayLine
{
    /// <summary>Clé stable de la ligne dans l'ordre enregistré : clé de catégorie (« vram » pour la ligne de la
    /// mémoire du GPU) ou identifiant de métrique en mode une ligne par métrique.</summary>
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
    /// <summary>Clé de la ligne de la mémoire du GPU dans l'ordre des lignes : ce n'est pas une catégorie du catalogue
    /// (ses métriques sont rangées sous GPU), mais elle a sa propre ligne, sous GPU par défaut.</summary>
    public const string VramKey = "vram";

    /// <summary>Libellé de la ligne de la mémoire du GPU.</summary>
    private const string VramLabel = "VRAM";

    /// <summary>Nom de la ligne VRAM dans la liste d'ordre des lignes.</summary>
    private const string VramTitle = "Mémoire du GPU (VRAM)";

    /// <summary>Deux espaces : après le libellé de la ligne, entre deux groupes de valeurs, et de chaque côté d'un
    /// débit, dont la largeur et l'unité changent d'un relevé à l'autre.</summary>
    public const string WideGap = "  ";

    /// <summary>Une espace : entre deux valeurs d'un même groupe, et entre un sous-libellé et sa valeur.</summary>
    public const string TightGap = " ";

    /// <param name="lineOrder">Ordre des lignes : clés de catégorie, ou identifiants de métrique en mode une ligne
    /// par métrique (voir <see cref="OverlayLineOrder"/>). Null : l'ordre du catalogue.</param>
    /// <param name="unavailableMemoryIds">Métriques des lignes VRAM et RAM que ce PC ne fournit pas (voir
    /// <see cref="UnavailableMemoryIds"/>) : une de ces lignes qui n'a plus que celles-là disparaît, libellé
    /// compris. Null : rien n'est retiré.</param>
    public static List<OverlayLine> Build(
        IReadOnlyList<MetricDefinition> metrics,
        bool oneLinePerMetric,
        OverlayColorScheme colors,
        IReadOnlyList<string>? lineOrder = null,
        IReadOnlySet<string>? unavailableMemoryIds = null)
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
                    Cells = new[] { new OverlayCell(metric) },
                })
                .ToList();

            return lineOrder is null ? perMetric : OverlayLineOrder.Sort(perMetric, line => line.Key, lineOrder);
        }

        // Façon Afterburner : une ligne par catégorie, ex. "GPU  45%  62°C  180 W". La mémoire du GPU quitte la
        // ligne GPU pour sa propre ligne VRAM, rangée sous GPU par défaut.
        IReadOnlySet<string> unavailable = unavailableMemoryIds ?? new HashSet<string>();
        List<OverlayLine> perCategory = metrics
            .GroupBy(LineKey)
            .Select(group => BuildCategoryLine(group.Key, group.ToArray(), colors, unavailable))
            .OfType<OverlayLine>()
            .ToList();

        return OverlayLineOrder.Sort(perCategory, line => line.Key, lineOrder ?? CatalogCategoryOrder);
    }

    /// <summary>Ordre par défaut des lignes par catégorie : celui du catalogue, avec la ligne VRAM juste sous GPU.</summary>
    public static IReadOnlyList<string> CatalogCategoryOrder { get; } = MetricCatalog.Categories
        .SelectMany(c => c == MetricCatalog.Gpu ? new[] { c.Key, VramKey } : new[] { c.Key })
        .ToArray();

    /// <summary>Clé de la ligne d'une métrique : sa catégorie, sauf la mémoire du GPU, qui a sa ligne VRAM.</summary>
    private static string LineKey(MetricDefinition metric)
        => MetricCatalog.GpuMemoryIds.Contains(metric.Id) ? VramKey : metric.Category.Key;

    private static bool IsMemoryLine(string key) => key == VramKey || key == MetricCatalog.Ram.Key;

    /// <summary>Métriques des lignes VRAM et RAM que ce PC ne fournit pas d'après ce relevé. Sert à retirer une ligne
    /// qui n'aurait rien à montrer ; « -- » (pas encore lu) n'en fait pas partie.</summary>
    public static HashSet<string> UnavailableMemoryIds(IEnumerable<MetricDefinition> metrics, MetricSample sample)
    {
        var ids = new HashSet<string>();
        foreach (MetricDefinition metric in metrics)
        {
            if (IsMemoryLine(LineKey(metric)) && metric.Read(sample).IsUnavailable) ids.Add(metric.Id);
        }
        return ids;
    }

    /// <summary>Ligne d'une catégorie, ou la ligne VRAM. Une ligne VRAM ou RAM dont ce PC ne fournit aucune valeur (pas
    /// de GPU dédié, par exemple) n'a rien à montrer, ni ses « N/D » ni son libellé : elle disparaît — d'où null. Une
    /// ligne qui a au moins une valeur garde les autres, « N/D » compris : on ne laisse pas de trou sans explication.
    /// Seules les métriques de ces deux lignes figurent dans <paramref name="unavailable"/>.</summary>
    private static OverlayLine? BuildCategoryLine(
        string key, MetricDefinition[] metrics, OverlayColorScheme colors, IReadOnlySet<string> unavailable)
    {
        if (metrics.All(m => unavailable.Contains(m.Id))) return null;

        bool isVram = key == VramKey;
        MetricCategory category = metrics[0].Category;
        return new OverlayLine
        {
            Key = key,
            Title = isVram ? VramTitle : category.Name,
            Label = isVram ? VramLabel : category.OsdLabel,
            LabelColorHex = colors.CategoryColor(category),
            ValueColorHex = colors.ValueColor,
            Cells = BuildCategoryCells(metrics),
        };
    }

    /// <summary>Cellules d'une ligne, chacune avec son espacement (voir <see cref="GapBefore"/>). Sur la ligne JEU, où
    /// chaque FPS porte son libellé (« FPS 144  MOY 138  1% 95  0.1% 80  6.9 ms »), chaque valeur forme son propre
    /// groupe : le temps de frame, qui n'en a pas, reste ainsi séparé du dernier libellé plutôt que de s'y coller. Une
    /// valeur dont le libellé (<see cref="MetricDefinition.LineLabel"/>) tient lieu d'unité n'affiche pas cette unité.</summary>
    private static OverlayCell[] BuildCategoryCells(MetricDefinition[] metrics)
    {
        bool labelled = metrics.Any(m => m.LineLabel is not null);
        var cells = new OverlayCell[metrics.Length];
        for (int i = 0; i < cells.Length; i++)
        {
            MetricDefinition metric = metrics[i];
            string? prefix = metric.LineLabel;
            cells[i] = new OverlayCell(
                metric,
                prefix,
                GapBefore(metric, i == 0 ? null : metrics[i - 1], opensGroup: labelled, hasPrefix: prefix is not null),
                showUnit: prefix is null);
        }
        return cells;
    }

    /// <summary>Espaces avant la valeur d'une cellule. Une espace entre valeurs d'un même groupe ; deux après le
    /// libellé de la ligne, avant un nouveau groupe, et de chaque côté d'un débit (disque, réseau), dont la largeur
    /// varie : c'est ce qui les garde lisibles. Après un sous-libellé, une espace : c'est lui qui apporte les deux
    /// espaces de séparation.</summary>
    /// <param name="previous">La métrique de la cellule précédente, null pour la première de la ligne.</param>
    private static string GapBefore(MetricDefinition metric, MetricDefinition? previous, bool opensGroup, bool hasPrefix)
    {
        if (hasPrefix) return TightGap;
        return previous is null || opensGroup || metric.IsRate || previous.IsRate ? WideGap : TightGap;
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
    /// fenêtre, une valeur qui change de longueur ne décale plus la suite de la ligne. Seul le libellé partage sa
    /// largeur avec les autres lignes ; chaque valeur a la sienne (<see cref="OverlayCell.ValueGroup"/>).
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

                string field = cell.Gap + Field(cell.Value, valueWidth, rightAligned: true);
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
