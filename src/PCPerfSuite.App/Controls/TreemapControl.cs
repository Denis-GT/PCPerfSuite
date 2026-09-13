using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using PCPerfSuite.App.Utils;
using PCPerfSuite.Core.Storage;

namespace PCPerfSuite.App.Controls;

/// <summary>
/// Carte d'occupation disque façon SpaceSniffer : treemap "squarifié" (Bruls/Huizing/van Wijk) dont les
/// dossiers se dévoilent au clic. Un dossier fermé est une simple case ; un clic l'ouvre sur place — un
/// bandeau de titre apparaît et ses sous-dossiers/fichiers se déplient à l'intérieur de sa case, sans que
/// les autres cases changent de taille. Un clic sur le bandeau le referme, un double-clic zoome dedans, un
/// clic droit (ou le bouton "précédent" de la souris) remonte.
/// </summary>
public sealed class TreemapControl : FrameworkElement
{
    public static readonly DependencyProperty RootProperty = DependencyProperty.Register(
        nameof(Root), typeof(FolderNode), typeof(TreemapControl),
        new FrameworkPropertyMetadata(null, (d, _) => ((TreemapControl)d).OnRootChanged()));

    public static readonly DependencyProperty HoveredNodeProperty = DependencyProperty.Register(
        nameof(HoveredNode), typeof(FolderNode), typeof(TreemapControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty DrillCommandProperty = DependencyProperty.Register(
        nameof(DrillCommand), typeof(ICommand), typeof(TreemapControl));

    public static readonly DependencyProperty ZoomOutCommandProperty = DependencyProperty.Register(
        nameof(ZoomOutCommand), typeof(ICommand), typeof(TreemapControl));

    /// <summary>Dossier dont le contenu remplit la vue.</summary>
    public FolderNode? Root
    {
        get => (FolderNode?)GetValue(RootProperty);
        set => SetValue(RootProperty, value);
    }

    public FolderNode? HoveredNode
    {
        get => (FolderNode?)GetValue(HoveredNodeProperty);
        set => SetValue(HoveredNodeProperty, value);
    }

    /// <summary>Exécutée avec le dossier dans lequel zoomer (double-clic).</summary>
    public ICommand? DrillCommand
    {
        get => (ICommand?)GetValue(DrillCommandProperty);
        set => SetValue(DrillCommandProperty, value);
    }

    public ICommand? ZoomOutCommand
    {
        get => (ICommand?)GetValue(ZoomOutCommandProperty);
        set => SetValue(ZoomOutCommandProperty, value);
    }

    private const double HeaderHeight = 18;
    private const int MaxDepth = 8;
    private const int MaxCells = 6000;
    private const double ZoomAnimationMs = 320;
    private const double RevealAnimationMs = 260;

    private static readonly Color[] Palette =
    {
        Color.FromRgb(0x0A, 0x84, 0xFF),
        Color.FromRgb(0x30, 0xD1, 0x58),
        Color.FromRgb(0xFF, 0x9F, 0x0A),
        Color.FromRgb(0xBF, 0x5A, 0xF2),
        Color.FromRgb(0x64, 0xD2, 0xFF),
        Color.FromRgb(0xFF, 0x37, 0x5F),
        Color.FromRgb(0xFF, 0xD6, 0x0A),
        Color.FromRgb(0x5E, 0x5C, 0xE6),
        Color.FromRgb(0x66, 0xD4, 0xCF),
        Color.FromRgb(0xAC, 0x8E, 0x68),
    };

    private static readonly Color AggregateColor = Color.FromRgb(0x5A, 0x60, 0x6E);
    private static readonly Color BackdropColor = Color.FromRgb(0x0B, 0x0D, 0x14);

    private static readonly Typeface LabelFace = new(
        new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    private static readonly Brush DarkText = Frozen(new SolidColorBrush(Color.FromRgb(0x12, 0x14, 0x1A)));
    private static readonly Brush DarkTextSecondary = Frozen(new SolidColorBrush(Color.FromArgb(0xB0, 0x12, 0x14, 0x1A)));
    private static readonly Brush LightTextSecondary = Frozen(new SolidColorBrush(Color.FromArgb(0xC8, 0xFF, 0xFF, 0xFF)));
    private static readonly Pen SheenPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF))), 1));
    private static readonly Brush FoldBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush HoverFill = Frozen(new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)));
    private static readonly Pen HoverPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF))), 1.5));
    private static readonly Pen LightChevronPen = MakeChevronPen(Color.FromArgb(0xD8, 0xFF, 0xFF, 0xFF));
    private static readonly Pen DarkChevronPen = MakeChevronPen(Color.FromArgb(0xC0, 0x12, 0x14, 0x1A));

    private sealed class Cell
    {
        public required FolderNode Node { get; init; }
        public required Rect Rect { get; init; }
        public required int Parent { get; init; }
        public required double Radius { get; init; }

        /// <summary>Dossier dévoilé : bandeau de titre + son contenu dessiné à l'intérieur.</summary>
        public required bool IsOpen { get; init; }

        /// <summary>Dossier assez grand à cet endroit pour être dévoilé sur place.</summary>
        public required bool CanOpen { get; init; }
    }

    /// <summary>Carte calculée pour une racine et un ensemble de dossiers dévoilés, dessinée une fois dans un
    /// Drawing figé : pendant les animations on ne fait que la redessiner (transformée, découpée), sans recalcul.</summary>
    private sealed class Layout
    {
        public required FolderNode Root { get; init; }
        public required List<Cell> Cells { get; init; }
        public required Dictionary<FolderNode, int> IndexByNode { get; init; }
        public required Drawing Drawing { get; init; }

        /// <summary>Case du nœud, ou de son plus proche ancêtre affiché s'il est trop petit pour avoir la sienne.</summary>
        public Rect? RectOf(FolderNode node)
        {
            for (FolderNode? n = node; n is not null; n = n.Parent)
            {
                if (IndexByNode.TryGetValue(n, out int index)) return Cells[index].Rect;
            }
            return null;
        }
    }

    private enum Transition { ZoomIn, ZoomOut, Reveal, Fold }

    /// <summary>Dossiers dévoilés. Conservés en zoomant/dézoomant, vidés à chaque nouvelle analyse.</summary>
    private readonly HashSet<FolderNode> _open = new();

    private Layout? _layout;
    private Layout? _outgoing;
    private Transition _transition;
    private Rect _transitionRect;
    private double _durationMs;
    private bool _animating;
    private readonly Stopwatch _clock = new();
    private int _hoverIndex = -1;

    /// <summary>Dossier visé par le premier clic d'un double-clic : ce premier clic a déjà pu dévoiler le
    /// dossier, et donc changer ce qui se trouve sous le curseur au moment du second.</summary>
    private FolderNode? _lastClicked;

    public TreemapControl()
    {
        ClipToBounds = true;
        RenderOptions.SetClearTypeHint(this, ClearTypeHint.Enabled);
        Unloaded += (_, _) => StopAnimation();
    }

    // ----- Cycle de vie -----

    private void OnRootChanged()
    {
        Layout? previous = _layout;
        StopAnimation();

        FolderNode? root = Root;
        if (root is null || previous is null || !ReferenceEquals(TopOf(root), TopOf(previous.Root)))
        {
            _open.Clear();
        }
        else if (IsDescendant(previous.Root, root))
        {
            // En remontant, le chemin dont on vient reste dévoilé pour garder le contexte sous les yeux.
            for (FolderNode? n = previous.Root; n is not null && !ReferenceEquals(n, root); n = n.Parent)
            {
                _open.Add(n);
            }
        }

        _layout = BuildLayout(root);
        ResetHover();
        if (previous is not null && _layout is not null) TryStartZoomTransition(previous, _layout);
        InvalidateVisual();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        StopAnimation();
        _layout = BuildLayout(Root);
        ResetHover();
        InvalidateVisual();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        _layout = BuildLayout(Root);
        InvalidateVisual();
    }

    // ----- Dévoiler / replier -----

    private void SetOpen(FolderNode node, bool open, Rect rect)
    {
        if (open ? !_open.Add(node) : !_open.Remove(node)) return;

        Layout? previous = _layout;
        _layout = BuildLayout(Root);

        if (open && !(_layout is not null && _layout.IndexByNode.TryGetValue(node, out int index) && _layout.Cells[index].IsOpen))
        {
            // Limite de cases atteinte : impossible de le dévoiler sur place, on zoome dedans à la place.
            _open.Remove(node);
            _layout = previous;
            Drill(node);
            return;
        }

        StartTransition(previous, open ? Transition.Reveal : Transition.Fold, rect, RevealAnimationMs);
        RefreshHover();
        InvalidateVisual();
    }

    private void Drill(FolderNode node)
    {
        if (DrillCommand?.CanExecute(node) == true) DrillCommand.Execute(node);
    }

    // ----- Animations -----

    private void TryStartZoomTransition(Layout from, Layout to)
    {
        Rect? rect;
        Transition kind;
        if (IsDescendant(to.Root, from.Root))
        {
            rect = from.RectOf(to.Root);
            kind = Transition.ZoomIn;
        }
        else if (IsDescendant(from.Root, to.Root))
        {
            rect = to.RectOf(from.Root);
            kind = Transition.ZoomOut;
        }
        else
        {
            return;
        }

        if (rect is { } target) StartTransition(from, kind, target, ZoomAnimationMs);
    }

    private void StartTransition(Layout? from, Transition kind, Rect rect, double durationMs)
    {
        StopAnimation();
        if (from is null || _layout is null || rect.Width <= 1 || rect.Height <= 1) return;

        _outgoing = from;
        _transition = kind;
        _transitionRect = rect;
        _durationMs = durationMs;
        _animating = true;
        _clock.Restart();
        CompositionTarget.Rendering += OnRenderingFrame;
    }

    private void OnRenderingFrame(object? sender, EventArgs e)
    {
        if (_clock.Elapsed.TotalMilliseconds >= _durationMs)
        {
            StopAnimation();
            RefreshHover();
        }
        InvalidateVisual();
    }

    private void StopAnimation()
    {
        if (!_animating) return;
        _animating = false;
        _outgoing = null;
        _clock.Reset();
        CompositionTarget.Rendering -= OnRenderingFrame;
    }

    private void FinishAnimation()
    {
        if (!_animating) return;
        StopAnimation();
        InvalidateVisual();
    }

    private static bool IsDescendant(FolderNode node, FolderNode ancestor)
    {
        for (FolderNode? n = node.Parent; n is not null; n = n.Parent)
        {
            if (ReferenceEquals(n, ancestor)) return true;
        }
        return false;
    }

    private static FolderNode TopOf(FolderNode node)
    {
        FolderNode top = node;
        while (top.Parent is not null) top = top.Parent;
        return top;
    }

    // ----- Construction de la carte -----

    private Layout? BuildLayout(FolderNode? root)
    {
        if (root is null || ActualWidth < 4 || ActualHeight < 4) return null;

        var cells = new List<Cell>();
        var drawing = new DrawingGroup();
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        using (DrawingContext dc = drawing.Open())
        {
            LayoutLevel(dc, root, new Rect(0, 0, ActualWidth, ActualHeight), 0, -1, null, cells, pixelsPerDip);
        }
        drawing.Freeze();

        var index = new Dictionary<FolderNode, int>(cells.Count);
        for (int i = 0; i < cells.Count; i++) index[cells[i].Node] = i;

        return new Layout { Root = root, Cells = cells, IndexByNode = index, Drawing = drawing };
    }

    private void LayoutLevel(DrawingContext dc, FolderNode parent, Rect area, int depth, int parentIndex,
        Color? branchColor, List<Cell> cells, double pixelsPerDip)
    {
        List<FolderNode> items = parent.Children.Where(c => c.SizeBytes > 0).OrderByDescending(c => c.SizeBytes).ToList();
        if (items.Count == 0 || area.Width < 2 || area.Height < 2) return;

        foreach ((FolderNode node, Rect slot) in Squarify(items, area))
        {
            Rect rect = Inset(slot, 1);
            if (rect.Width < 1 || rect.Height < 1) continue;

            Color color = ColorFor(node, branchColor, depth);
            double radius = Math.Min(depth == 0 ? 6 : 4, Math.Min(rect.Width, rect.Height) / 4);
            bool canOpen = node.CanDrillInto && depth < MaxDepth && rect.Width >= 56 && rect.Height >= HeaderHeight + 24;
            bool isOpen = canOpen && _open.Contains(node) && cells.Count < MaxCells;

            int index = cells.Count;
            cells.Add(new Cell { Node = node, Rect = rect, Parent = parentIndex, Radius = radius, IsOpen = isOpen, CanOpen = canOpen });

            if (isOpen)
            {
                DrawContainer(dc, node, rect, radius, color, pixelsPerDip);
                var body = new Rect(rect.X + 3, rect.Y + HeaderHeight, rect.Width - 6, rect.Height - HeaderHeight - 3);
                LayoutLevel(dc, node, body, depth + 1, index, branchColor ?? color, cells, pixelsPerDip);
            }
            else
            {
                DrawTile(dc, node, rect, radius, color, pixelsPerDip);
            }
        }
    }

    /// <summary>Dossier dévoilé : cadre coloré + bandeau titre (avec chevron "replier"), intérieur assombri.</summary>
    private static void DrawContainer(DrawingContext dc, FolderNode node, Rect rect, double radius, Color color, double pixelsPerDip)
    {
        dc.DrawRoundedRectangle(Frozen(new SolidColorBrush(color)), null, rect, radius, radius);

        var body = new Rect(rect.X + 2, rect.Y + HeaderHeight - 1, rect.Width - 4, rect.Height - HeaderHeight - 1);
        double innerRadius = Math.Max(0, radius - 2);
        dc.DrawRoundedRectangle(Frozen(new SolidColorBrush(Mix(color, BackdropColor, 0.74))), null, body, innerRadius, innerRadius);

        bool darkText = Luminance(color) > 0.62;
        bool showChevron = rect.Width >= 80;
        var header = new Rect(rect.X + 6, rect.Y, Math.Max(1, rect.Width - 12 - (showChevron ? 14 : 0)), HeaderHeight - 1);

        string size = ByteFormatter.Format(node.SizeBytes);
        FormattedText text = MakeText($"{node.Name}  {size}", 11, FontWeights.Normal, darkText ? DarkText : Brushes.White, header.Width, pixelsPerDip);
        text.SetFontWeight(FontWeights.SemiBold, 0, node.Name.Length);
        text.SetForegroundBrush(darkText ? DarkTextSecondary : LightTextSecondary, node.Name.Length + 2, size.Length);

        dc.PushClip(new RectangleGeometry(header));
        dc.DrawText(text, new Point(header.X, header.Y + (header.Height - text.Height) / 2));
        dc.Pop();

        if (!showChevron) return;

        double cx = rect.Right - 12;
        double cy = rect.Y + (HeaderHeight - 1) / 2;
        var chevron = new StreamGeometry();
        using (StreamGeometryContext ctx = chevron.Open())
        {
            ctx.BeginFigure(new Point(cx - 3.5, cy + 1.75), false, false);
            ctx.LineTo(new Point(cx, cy - 1.75), true, true);
            ctx.LineTo(new Point(cx + 3.5, cy + 1.75), true, true);
        }
        chevron.Freeze();
        dc.DrawGeometry(null, darkText ? DarkChevronPen : LightChevronPen, chevron);
    }

    /// <summary>Fichier, bloc "Autres", ou dossier fermé (coin corné = un clic le dévoile).</summary>
    private static void DrawTile(DrawingContext dc, FolderNode node, Rect rect, double radius, Color color, double pixelsPerDip)
    {
        var fill = new LinearGradientBrush(Mix(color, Colors.White, 0.14), Mix(color, Colors.Black, 0.16), 90);
        fill.Freeze();
        dc.DrawRoundedRectangle(fill, null, rect, radius, radius);

        if (rect.Width > 8 && rect.Height > 6)
        {
            // Liseré de lumière en haut de la case, pour l'effet verre.
            dc.DrawLine(SheenPen, new Point(rect.X + radius, rect.Y + 0.5), new Point(rect.Right - radius, rect.Y + 0.5));
        }

        if (node.CanDrillInto && rect.Width >= 16 && rect.Height >= 16)
        {
            double s = Math.Min(9, Math.Min(rect.Width, rect.Height) / 3);
            double inset = radius * 0.3;
            var fold = new StreamGeometry();
            using (StreamGeometryContext ctx = fold.Open())
            {
                ctx.BeginFigure(new Point(rect.Right - inset - s, rect.Y + inset), true, true);
                ctx.LineTo(new Point(rect.Right - inset, rect.Y + inset), false, false);
                ctx.LineTo(new Point(rect.Right - inset, rect.Y + inset + s), false, false);
            }
            fold.Freeze();
            dc.DrawGeometry(FoldBrush, null, fold);
        }

        if (rect.Width < 38 || rect.Height < 15) return;

        bool darkText = Luminance(color) > 0.62;
        Brush primary = darkText ? DarkText : Brushes.White;
        var area = new Rect(rect.X + 5, rect.Y + 3, rect.Width - 10, rect.Height - 6);

        dc.PushClip(new RectangleGeometry(rect));
        if (rect.Height >= 32)
        {
            FormattedText name = MakeText(node.Name, 11.5, FontWeights.SemiBold, primary, area.Width, pixelsPerDip);
            FormattedText size = MakeText(ByteFormatter.Format(node.SizeBytes), 10.5, FontWeights.Normal,
                darkText ? DarkTextSecondary : LightTextSecondary, area.Width, pixelsPerDip);
            dc.DrawText(name, area.TopLeft);
            dc.DrawText(size, new Point(area.X, area.Y + name.Height));
        }
        else
        {
            FormattedText name = MakeText(node.Name, 11, FontWeights.SemiBold, primary, area.Width, pixelsPerDip);
            dc.DrawText(name, new Point(area.X, rect.Y + (rect.Height - name.Height) / 2));
        }
        dc.Pop();
    }

    private static FormattedText MakeText(string content, double fontSize, FontWeight weight, Brush brush, double maxWidth, double pixelsPerDip)
    {
        var text = new FormattedText(content, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, LabelFace,
            fontSize, brush, new NumberSubstitution(), TextFormattingMode.Display, pixelsPerDip)
        {
            MaxTextWidth = Math.Max(1, maxWidth),
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        text.SetFontWeight(weight);
        return text;
    }

    // ----- Algorithme squarifié -----

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

        if (row.Count > 0) LayoutRow(row, items, scale, ref rect, result);
        return result;
    }

    /// <summary>Pire ratio largeur/hauteur qu'obtiendrait un des blocs de cette rangée posée sur un côté de
    /// longueur w — formule fermée du papier de Bruls/Huizing/van Wijk.</summary>
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
            double length = (items[i].SizeBytes * scale / rowTotal) * (horizontal ? rect.Width : rect.Height);

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

    // ----- Couleurs -----

    /// <summary>Chaque branche de premier niveau a sa teinte, que tout son contenu reprend (repère visuel de la
    /// branche, comme SpaceSniffer), avec une légère variation par élément pour distinguer les voisins.</summary>
    private static Color ColorFor(FolderNode node, Color? branchColor, int depth)
    {
        if (node.IsAggregate) return AggregateColor;

        uint hash = StableHash(node.Name);
        if (branchColor is not { } branch) return Palette[hash % (uint)Palette.Length];

        double shift = ((int)(hash % 7) - 3) * 0.06;
        Color color = shift >= 0 ? Mix(branch, Colors.White, shift) : Mix(branch, Colors.Black, -shift);
        color = Mix(color, Colors.Black, Math.Min(0.2, depth * 0.035));

        if (!node.IsFile) return color;

        byte gray = (byte)(Luminance(color) * 255);
        return Mix(Mix(color, Color.FromRgb(gray, gray, gray), 0.25), Colors.Black, 0.1);
    }

    /// <summary>Hash FNV-1a : string.GetHashCode est aléatoire à chaque lancement en .NET, les couleurs changeraient.</summary>
    private static uint StableHash(string value)
    {
        uint hash = 2166136261;
        foreach (char c in value)
        {
            hash ^= c;
            hash *= 16777619;
        }
        return hash;
    }

    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t),
        (byte)(a.G + (b.G - a.G) * t),
        (byte)(a.B + (b.B - a.B) * t));

    private static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255;

    // ----- Rendu -----

    protected override void OnRender(DrawingContext dc)
    {
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        dc.DrawRectangle(Brushes.Transparent, null, bounds);
        if (_layout is null) return;

        if (!_animating || _outgoing is null)
        {
            dc.DrawDrawing(_layout.Drawing);
            DrawHover(dc);
            return;
        }

        double p = Math.Clamp(_clock.Elapsed.TotalMilliseconds / _durationMs, 0, 1);
        double t = 1 - Math.Pow(1 - p, 3);

        switch (_transition)
        {
            case Transition.Reveal:
                // Le dossier se déplie : bandeau d'abord, puis son contenu se révèle de haut en bas dans sa case.
                dc.DrawDrawing(_outgoing.Drawing);
                DrawUnfolding(dc, _layout, _transitionRect, t);
                break;

            case Transition.Fold:
                dc.DrawDrawing(_layout.Drawing);
                DrawUnfolding(dc, _outgoing, _transitionRect, 1 - t);
                break;

            case Transition.ZoomIn:
            {
                // La case grandit jusqu'à remplir la vue ; son contenu apparaît dedans pendant que l'ancienne carte s'efface.
                Rect target = Lerp(_transitionRect, bounds, t);
                DrawLayer(dc, _outgoing, MapRect(_transitionRect, target), 1 - t);
                DrawLayer(dc, _layout, MapRect(bounds, target), Math.Min(1, t * 1.8));
                break;
            }

            case Transition.ZoomOut:
            {
                // Inverse : on recule, et le dossier qu'on quitte se replie dans sa case de la carte parente.
                Rect viewport = Lerp(_transitionRect, bounds, t);
                Matrix parentTransform = MapRect(viewport, bounds);
                DrawLayer(dc, _layout, parentTransform, 1);
                Rect folded = Rect.Transform(_transitionRect, parentTransform);
                DrawLayer(dc, _outgoing, MapRect(bounds, folded), 1 - t);
                break;
            }
        }
    }

    private static void DrawUnfolding(DrawingContext dc, Layout layout, Rect cell, double amount)
    {
        if (amount <= 0.01) return;

        double height = Math.Min(cell.Height, HeaderHeight + (cell.Height - HeaderHeight) * amount);
        dc.PushClip(new RectangleGeometry(new Rect(cell.X, cell.Y, cell.Width, height)));
        dc.PushOpacity(Math.Min(1, 0.3 + amount));
        dc.DrawDrawing(layout.Drawing);
        dc.Pop();
        dc.Pop();
    }

    private void DrawHover(DrawingContext dc)
    {
        if (_layout is null || _hoverIndex < 0 || _hoverIndex >= _layout.Cells.Count) return;
        Cell cell = _layout.Cells[_hoverIndex];
        dc.DrawRoundedRectangle(HoverFill, HoverPen, cell.Rect, cell.Radius, cell.Radius);
    }

    private static void DrawLayer(DrawingContext dc, Layout layout, Matrix transform, double opacity)
    {
        if (opacity <= 0.01) return;
        dc.PushTransform(new MatrixTransform(transform));
        dc.PushOpacity(opacity);
        dc.DrawDrawing(layout.Drawing);
        dc.Pop();
        dc.Pop();
    }

    private static Matrix MapRect(Rect from, Rect to)
    {
        Matrix m = Matrix.Identity;
        m.Translate(-from.X, -from.Y);
        m.Scale(to.Width / from.Width, to.Height / from.Height);
        m.Translate(to.X, to.Y);
        return m;
    }

    private static Rect Lerp(Rect a, Rect b, double t) => new(
        a.X + (b.X - a.X) * t,
        a.Y + (b.Y - a.Y) * t,
        a.Width + (b.Width - a.Width) * t,
        a.Height + (b.Height - a.Height) * t);

    private static Rect Inset(Rect r, double d)
        => r.Width > 2 * d && r.Height > 2 * d ? new Rect(r.X + d, r.Y + d, r.Width - 2 * d, r.Height - 2 * d) : r;

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    private static Pen MakeChevronPen(Color color) => Frozen(new Pen(Frozen(new SolidColorBrush(color)), 1.4)
    {
        StartLineCap = PenLineCap.Round,
        EndLineCap = PenLineCap.Round,
        LineJoin = PenLineJoin.Round,
    });

    // ----- Interaction -----

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_animating) UpdateHover(e.GetPosition(this));
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverIndex < 0) return;
        ResetHover();
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_layout is null) return;
        e.Handled = true;

        if (e.ClickCount >= 2)
        {
            FolderNode? target = _lastClicked;
            _lastClicked = null;
            if (target is { CanDrillInto: true }) Drill(target);
            return;
        }

        FinishAnimation();
        _lastClicked = null;

        Point point = e.GetPosition(this);
        int index = HitTest(point);
        if (index < 0) return;

        Cell cell = _layout.Cells[index];
        if (cell.IsOpen)
        {
            // Seul le bandeau replie : un clic dans un interstice entre deux cases ne doit rien fermer.
            _lastClicked = cell.Node;
            if (point.Y <= cell.Rect.Y + HeaderHeight) SetOpen(cell.Node, false, cell.Rect);
            return;
        }

        if (!cell.Node.CanDrillInto) return;
        _lastClicked = cell.Node;

        if (cell.CanOpen) SetOpen(cell.Node, true, cell.Rect);
        else Drill(cell.Node); // trop petit ici pour être dévoilé sur place : on zoome dedans
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        ZoomOut();
        e.Handled = true;
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.ChangedButton != MouseButton.XButton1) return;
        ZoomOut();
        e.Handled = true;
    }

    private void ZoomOut()
    {
        FinishAnimation();
        if (ZoomOutCommand?.CanExecute(null) == true) ZoomOutCommand.Execute(null);
    }

    private void UpdateHover(Point point)
    {
        if (_layout is null) return;

        int index = HitTest(point);
        Cursor = IsClickable(index, point) ? Cursors.Hand : Cursors.Arrow;
        if (index == _hoverIndex) return;

        _hoverIndex = index;
        SetCurrentValue(HoveredNodeProperty, index >= 0 ? _layout.Cells[index].Node : null);
        InvalidateVisual();
    }

    private void RefreshHover()
    {
        ResetHover();
        if (IsMouseOver) UpdateHover(Mouse.GetPosition(this));
    }

    private void ResetHover()
    {
        _hoverIndex = -1;
        SetCurrentValue(HoveredNodeProperty, null);
    }

    private bool IsClickable(int index, Point point)
    {
        if (_layout is null || index < 0) return false;
        Cell cell = _layout.Cells[index];
        return cell.IsOpen ? point.Y <= cell.Rect.Y + HeaderHeight : cell.Node.CanDrillInto;
    }

    /// <summary>Case la plus profonde sous le curseur (les enfants sont ajoutés après leur dossier et dessinés dedans).</summary>
    private int HitTest(Point point)
    {
        if (_layout is null) return -1;
        for (int i = _layout.Cells.Count - 1; i >= 0; i--)
        {
            if (_layout.Cells[i].Rect.Contains(point)) return i;
        }
        return -1;
    }
}
