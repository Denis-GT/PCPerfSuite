using System.Windows;
using System.Windows.Media;
using PCPerfSuite.App.Styles;

namespace PCPerfSuite.App.Controls;

/// <summary>
/// Un fil d'exécution (processeur logique) dans le visuel par cœur : une jauge verticale remplie du bas selon la charge,
/// couverte de hachures d'autant plus marquées que le fil a passé de temps parqué sur les derniers relevés. Dessinée à la
/// main, sans Effect, comme <see cref="MeterBar"/> : 20 à 64 jauges se redessinent chaque seconde.
/// </summary>
public sealed class CoreThreadBar : FrameworkElement
{
    public static readonly DependencyProperty LoadProperty = DependencyProperty.Register(
        nameof(Load), typeof(double), typeof(CoreThreadBar),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Part du temps parqué, de 0 à 1 ; NaN quand l'état parqué n'est pas lu (pas de hachures).</summary>
    public static readonly DependencyProperty ParkedShareProperty = DependencyProperty.Register(
        nameof(ParkedShare), typeof(double), typeof(CoreThreadBar),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Faux tant que rien n'a été relevé : la jauge reste vide et grisée, jamais un faux zéro.</summary>
    public static readonly DependencyProperty HasValueProperty = DependencyProperty.Register(
        nameof(HasValue), typeof(bool), typeof(CoreThreadBar),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush Track = ThemeColors.FrozenBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
    private static readonly Pen Outline = FrozenPen(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF), 1);

    private static Brush? _fill;
    private static Brush? _hatch;

    public double Load
    {
        get => (double)GetValue(LoadProperty);
        set => SetValue(LoadProperty, value);
    }

    public double ParkedShare
    {
        get => (double)GetValue(ParkedShareProperty);
        set => SetValue(ParkedShareProperty, value);
    }

    public bool HasValue
    {
        get => (bool)GetValue(HasValueProperty);
        set => SetValue(HasValueProperty, value);
    }

    static CoreThreadBar()
    {
        WidthProperty.OverrideMetadata(typeof(CoreThreadBar), new FrameworkPropertyMetadata(16.0));
        HeightProperty.OverrideMetadata(typeof(CoreThreadBar), new FrameworkPropertyMetadata(46.0));
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        const double radius = 4;
        var full = new Rect(0, 0, w, h);
        dc.DrawRoundedRectangle(Track, null, full, radius, radius);

        dc.PushClip(new RectangleGeometry(full, radius, radius));
        if (HasValue)
        {
            double load = double.IsNaN(Load) ? 0 : Math.Clamp(Load, 0, 100) / 100.0;
            double fillHeight = h * load;
            if (fillHeight > 0) dc.DrawRectangle(Fill(), null, new Rect(0, h - fillHeight, w, fillHeight));

            if (!double.IsNaN(ParkedShare) && ParkedShare > 0)
            {
                // L'opacité suit la part parquée : un fil parqué une seconde sur cinq se devine, un fil parqué tout le
                // temps est franchement hachuré.
                dc.PushOpacity(0.25 + 0.75 * Math.Clamp(ParkedShare, 0, 1));
                dc.DrawRectangle(Hatch(), null, full);
                dc.Pop();
            }
        }

        dc.Pop();
        dc.DrawRoundedRectangle(null, Outline, new Rect(0.5, 0.5, Math.Max(0, w - 1), Math.Max(0, h - 1)), radius, radius);
    }

    private static Brush Fill()
    {
        if (_fill is not null) return _fill;

        Color accent = ThemeColors.Accent;
        var brush = new LinearGradientBrush(
            new GradientStopCollection
            {
                new(ThemeColors.WithAlpha(accent, 0xFF), 0),
                new(ThemeColors.WithAlpha(accent, 0xB0), 1),
            },
            new Point(0, 0), new Point(0, 1));
        brush.Freeze();
        return _fill = brush;
    }

    /// <summary>Hachures diagonales, en tuile répétée : un seul pinceau gelé pour toutes les jauges.</summary>
    private static Brush Hatch()
    {
        if (_hatch is not null) return _hatch;

        var pen = FrozenPen(ThemeColors.WithAlpha(ThemeColors.TextSecondary, 0xC0), 1.6);
        var drawing = new GeometryDrawing(null, pen, new GeometryGroup
        {
            Children =
            {
                new LineGeometry(new Point(0, 6), new Point(6, 0)),
                new LineGeometry(new Point(-1, 1), new Point(1, -1)),
                new LineGeometry(new Point(5, 7), new Point(7, 5)),
            },
        });

        var brush = new DrawingBrush(drawing)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 6, 6),
            ViewportUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, 6, 6),
            ViewboxUnits = BrushMappingMode.Absolute,
        };
        brush.Freeze();
        return _hatch = brush;
    }

    private static Pen FrozenPen(Color color, double thickness)
    {
        var pen = new Pen(ThemeColors.FrozenBrush(color), thickness);
        pen.Freeze();
        return pen;
    }
}
