using System.Windows;
using System.Windows.Media;
using PCPerfSuite.App.Utils;
using PCPerfSuite.Core.Storage;

namespace PCPerfSuite.App.Controls;

/// <summary>
/// Treemap "squarifié" (algorithme de Bruls/Huizing/van Wijk) façon SpaceSniffer/WinDirStat : découpe
/// un rectangle en blocs proportionnels à la taille de chaque nœud, en essayant de garder des blocs
/// aussi carrés que possible plutôt que de fines lamelles illisibles. Dessin manuel (pas de lib externe)
/// pour rester cohérent avec MeterBar/Sparkline.
///
/// Interaction façon SpaceSniffer : cliquer un bloc le "dévoile" en subdivisant son propre rectangle
/// pour montrer ses enfants, sans jamais quitter la vue d'ensemble. Un second clic sur son bandeau
/// d'en-tête le referme. Chaque bloc garde son propre état déplié/replié (pas de pile de navigation).
/// </summary>
public sealed class TreemapControl : FrameworkElement
{
    public static readonly DependencyProperty NodesProperty = DependencyProperty.Register(
        nameof(Nodes), typeof(IReadOnlyList<FolderNode>), typeof(TreemapControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnNodesChanged));

    public static readonly DependencyProperty HoveredNodeProperty = DependencyProperty.Register(
        nameof(HoveredNode), typeof(FolderNode), typeof(TreemapControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SelectedNodeProperty = DependencyProperty.Register(
        nameof(SelectedNode), typeof(FolderNode), typeof(TreemapControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public IReadOnlyList<FolderNode>? Nodes
    {
        get => (IReadOnlyList<FolderNode>?)GetValue(NodesProperty);
        set => SetValue(NodesProperty, value);
    }

    public FolderNode? HoveredNode
    {
        get => (FolderNode?)GetValue(HoveredNodeProperty);
        set => SetValue(HoveredNodeProperty, value);
    }

    public FolderNode? SelectedNode
    {
        get => (FolderNode?)GetValue(SelectedNodeProperty);
        set => SetValue(SelectedNodeProperty, value);
    }

    private static readonly Color[] Palette =
    {
        Color.FromRgb(0x0A, 0x84, 0xFF),
        Color.FromRgb(0x5F, 0xE0, 0xC7),
        Color.FromRgb(0xFF, 0x9F, 0x0A),
        Color.FromRgb(0xBF, 0x5A, 0xF2),
        Color.FromRgb(0x64, 0xD2, 0xFF),
        Color.FromRgb(0xFF, 0x6B, 0x9D),
        Color.FromRgb(0x6F, 0xE0, 0x8F),
        Color.FromRgb(0xFF, 0xD6, 0x0A),
    };

    private static readonly Color AggregateColor = Color.FromRgb(0x4A, 0x50, 0x60);
    private static readonly Color GapColor = Color.FromRgb(0x0B, 0x0D, 0x14);

    private const double HeaderHeight = 16;
    private const double InnerPadding = 2;
    private const int MaxExpandDepth = 24;

    private readonly List<(FolderNode Node, Rect Rect, Color Color)> _layout = new();
    private readonly HashSet<FolderNode> _expanded = new();
    private readonly Typeface _typeface = new("Segoe UI");

    public TreemapControl()
    {
        ClipToBounds = true;
        Focusable = false;
    }

    private static void OnNodesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (TreemapControl)d;
        control._expanded.Clear();
        control.RecomputeLayout();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        RecomputeLayout();
    }

    private void RecomputeLayout()
    {
        _layout.Clear();

        List<FolderNode>? items = Nodes?.Where(n => n.SizeBytes > 0).OrderByDescending(n => n.SizeBytes).ToList();
        if (items is { Count: > 0 } && ActualWidth > 0 && ActualHeight > 0)
        {
            BuildLayout(items, new Rect(0, 0, ActualWidth, ActualHeight), 0);
        }

        InvalidateVisual();
    }

    /// <summary>Calcule le layout d'un niveau, puis se rappelle récursivement pour chaque bloc déplié
    /// afin de subdiviser son rectangle avec ses propres enfants (vue imbriquée façon SpaceSniffer).</summary>
    private void BuildLayout(List<FolderNode> items, Rect bounds, int depth)
    {
        foreach ((FolderNode node, Rect rect) in Squarify(items, bounds))
        {
            if (rect.Width <= 0.5 || rect.Height <= 0.5) continue;

            _layout.Add((node, rect, ColorFor(node)));

            if (depth >= MaxExpandDepth || !node.CanDrillInto || !_expanded.Contains(node)) continue;

            var inner = new Rect(
                rect.X + InnerPadding,
                rect.Y + HeaderHeight,
                Math.Max(0, rect.Width - 2 * InnerPadding),
                Math.Max(0, rect.Height - HeaderHeight - InnerPadding));

            if (inner.Width <= 4 || inner.Height <= 4) continue;

            List<FolderNode> children = node.Children.Where(c => c.SizeBytes > 0).OrderByDescending(c => c.SizeBytes).ToList();
            if (children.Count > 0)
            {
                BuildLayout(children, inner, depth + 1);
            }
        }
    }

    // ----- Layout (algorithme squarifié) -----

    private static List<(FolderNode, Rect)> Squarify(List<FolderNode> items, Rect bounds)
    {
        var result = new List<(FolderNode, Rect)>();
        double total = items.Sum(i => (double)i.SizeBytes);
        if (total <= 0 || bounds.Width <= 0 || bounds.Height <= 0) return result;

        double scale = (bounds.Width * bounds.Height) / total;
        double Area(int i) => items[i].SizeBytes * scale;

        var row = new List<int>();
        Rect rect = bounds;
        int index = 0;

        while (index < items.Count)
        {
            double w = Math.Min(rect.Width, rect.Height);

            if (row.Count == 0)
            {
                row.Add(index);
                index++;
                continue;
            }

            double worstBefore = WorstAspectRatio(row.Select(Area).ToList(), w);
            var candidate = new List<int>(row) { index };
            double worstAfter = WorstAspectRatio(candidate.Select(Area).ToList(), w);

            if (worstAfter <= worstBefore)
            {
                row.Add(index);
                index++;
            }
            else
            {
                LayoutRow(row, items, scale, ref rect, result);
                row.Clear();
            }
        }

        if (row.Count > 0)
        {
            LayoutRow(row, items, scale, ref rect, result);
        }

        return result;
    }

    /// <summary>Pire ratio largeur/hauteur qu'obtiendrait un des blocs de cette rangée si on la posait
    /// maintenant sur un côté de longueur w — formule fermée du papier de Bruls/Huizing/van Wijk.</summary>
    private static double WorstAspectRatio(List<double> areas, double w)
    {
        if (areas.Count == 0 || w <= 0) return double.MaxValue;

        double total = areas.Sum();
        if (total <= 0) return double.MaxValue;

        double s2 = total * total;
        double w2 = w * w;
        double rmax = areas.Max();
        double rmin = Math.Max(areas.Min(), 0.0001);

        return Math.Max(w2 * rmax / s2, s2 / (w2 * rmin));
    }

    private static void LayoutRow(List<int> rowIndices, List<FolderNode> items, double scale, ref Rect rect, List<(FolderNode, Rect)> result)
    {
        double rowTotal = rowIndices.Sum(i => items[i].SizeBytes * scale);
        if (rowTotal <= 0 || rect.Width <= 0 || rect.Height <= 0) return;

        bool horizontal = rect.Width <= rect.Height;
        double thickness = horizontal ? rowTotal / rect.Width : rowTotal / rect.Height;
        thickness = Math.Min(thickness, horizontal ? rect.Height : rect.Width);

        double offset = 0;
        foreach (int i in rowIndices)
        {
            double itemArea = items[i].SizeBytes * scale;
            double length = (itemArea / rowTotal) * (horizontal ? rect.Width : rect.Height);

            Rect itemRect = horizontal
                ? new Rect(rect.X + offset, rect.Y, length, thickness)
                : new Rect(rect.X, rect.Y + offset, thickness, length);

            result.Add((items[i], itemRect));
            offset += length;
        }

        rect = horizontal
            ? new Rect(rect.X, rect.Y + thickness, rect.Width, Math.Max(0, rect.Height - thickness))
            : new Rect(rect.X + thickness, rect.Y, Math.Max(0, rect.Width - thickness), rect.Height);
    }

    private static Color ColorFor(FolderNode node)
    {
        if (node.IsAggregate) return AggregateColor;

        string key = node.FullPath.Length > 0 ? node.FullPath : node.Name;
        int index = (key.GetHashCode() & int.MaxValue) % Palette.Length;
        Color baseColor = Palette[index];

        return node.IsFile ? Color.FromRgb((byte)(baseColor.R * 0.62), (byte)(baseColor.G * 0.62), (byte)(baseColor.B * 0.62)) : baseColor;
    }

    // ----- Rendu -----

    protected override void OnRender(DrawingContext dc)
    {
        if (_layout.Count == 0) return;

        foreach ((FolderNode node, Rect rect, Color color) in _layout)
        {
            if (rect.Width <= 0.5 || rect.Height <= 0.5) continue;

            bool isHovered = ReferenceEquals(node, HoveredNode);
            bool isExpanded = node.CanDrillInto && _expanded.Contains(node);

            var fill = new SolidColorBrush(color) { Opacity = isHovered ? 1.0 : 0.85 };
            dc.DrawRectangle(fill, new Pen(new SolidColorBrush(GapColor), 1), rect);

            if (isHovered)
            {
                dc.DrawRectangle(null, new Pen(Brushes.White, 1.5), rect);
            }

            if (isExpanded)
            {
                DrawExpandedHeader(dc, node, rect);
            }
            else if (rect.Width > 46 && rect.Height > 18)
            {
                DrawLabel(dc, node, rect);
            }
        }
    }

    private void DrawLabel(DrawingContext dc, FolderNode node, Rect rect)
    {
        string text = $"{node.Name}\n{ByteFormatter.Format(node.SizeBytes)}";
        var formatted = new FormattedText(
            text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            _typeface, 11.5, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            MaxTextWidth = Math.Max(1, rect.Width - 8),
            MaxTextHeight = Math.Max(1, rect.Height - 6),
            Trimming = TextTrimming.CharacterEllipsis,
        };

        dc.PushClip(new RectangleGeometry(rect));
        dc.DrawText(formatted, new Point(rect.X + 4, rect.Y + 3));
        dc.Pop();
    }

    /// <summary>Bandeau d'en-tête d'un bloc déplié : reste visible tout autour/au-dessus de ses enfants,
    /// c'est lui qu'on reclique pour replier le bloc.</summary>
    private void DrawExpandedHeader(DrawingContext dc, FolderNode node, Rect rect)
    {
        double headerHeight = Math.Min(HeaderHeight, rect.Height);
        var header = new Rect(rect.X, rect.Y, rect.Width, headerHeight);

        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0xA0, 0, 0, 0)), null, header);

        if (header.Width <= 24) return;

        string text = $"{node.Name} — {ByteFormatter.Format(node.SizeBytes)}";
        var formatted = new FormattedText(
            text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            _typeface, 10.5, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            MaxTextWidth = Math.Max(1, header.Width - 8),
            MaxTextHeight = Math.Max(1, header.Height),
            Trimming = TextTrimming.CharacterEllipsis,
        };

        dc.PushClip(new RectangleGeometry(header));
        dc.DrawText(formatted, new Point(header.X + 4, header.Y + 1));
        dc.Pop();
    }

    // ----- Interaction -----

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Point p = e.GetPosition(this);
        FolderNode? hit = HitTest(p);
        if (!ReferenceEquals(hit, HoveredNode))
        {
            SetCurrentValue(HoveredNodeProperty, hit);
        }
    }

    protected override void OnMouseLeave(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        SetCurrentValue(HoveredNodeProperty, null);
    }

    protected override void OnMouseLeftButtonUp(System.Windows.Input.MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        Point p = e.GetPosition(this);
        FolderNode? hit = HitTest(p);
        if (hit is null) return;

        SetCurrentValue(SelectedNodeProperty, hit);

        if (!hit.CanDrillInto) return;

        if (!_expanded.Add(hit))
        {
            // Déjà déplié : un second clic sur son bandeau referme le bloc (et tout ce qui était
            // déplié dessous, pour repartir d'un état propre la prochaine fois qu'on le rouvre).
            Collapse(hit);
        }

        RecomputeLayout();
    }

    private void Collapse(FolderNode node)
    {
        _expanded.Remove(node);
        foreach (FolderNode child in node.Children)
        {
            Collapse(child);
        }
    }

    private FolderNode? HitTest(Point p)
    {
        // Les enfants d'un bloc déplié sont ajoutés après lui dans _layout et sont dessinés par-dessus :
        // on parcourt donc à l'envers pour retomber sur le bloc le plus profond sous le curseur.
        for (int i = _layout.Count - 1; i >= 0; i--)
        {
            if (_layout[i].Rect.Contains(p)) return _layout[i].Node;
        }
        return null;
    }
}
