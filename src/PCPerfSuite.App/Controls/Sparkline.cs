using System.Windows;
using System.Windows.Media;
using PCPerfSuite.App.Utils;

namespace PCPerfSuite.App.Controls;

/// <summary>
/// Mini-graphique d'historique (comme les petites courbes du Gestionnaire des tâches, en plus fin).
/// Dessine un <see cref="SampleHistory"/> tenu par le ViewModel, plus une éventuelle seconde série en
/// simple ligne (lecture/écriture, réception/envoi). Échelle fixe de 0 à <see cref="Maximum"/>, ou calée
/// sur le pic visible avec <see cref="AutoScale"/> pour les valeurs sans borne naturelle (débits, watts, RPM).
/// Dessin manuel (pas de lib de charts externe) pour rester léger et fiable.
/// </summary>
public sealed class Sparkline : FrameworkElement
{
    private SampleHistory? _attachedSeries;
    private SampleHistory? _attachedSecondarySeries;

    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(
        nameof(Series), typeof(SampleHistory), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnSeriesChanged));

    public static readonly DependencyProperty SecondarySeriesProperty = DependencyProperty.Register(
        nameof(SecondarySeries), typeof(SampleHistory), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnSeriesChanged));

    public static readonly DependencyProperty LineBrushProperty = DependencyProperty.Register(
        nameof(LineBrush), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF)), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillBrushProperty = DependencyProperty.Register(
        nameof(FillBrush), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromArgb(0x30, 0x0A, 0x84, 0xFF)), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SecondaryLineBrushProperty = DependencyProperty.Register(
        nameof(SecondaryLineBrush), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(0x5F, 0xE0, 0xC7)), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(Sparkline),
        new FrameworkPropertyMetadata(100d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AutoScaleProperty = DependencyProperty.Register(
        nameof(AutoScale), typeof(bool), typeof(Sparkline),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MinimumScaleProperty = DependencyProperty.Register(
        nameof(MinimumScale), typeof(double), typeof(Sparkline),
        new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender));

    public SampleHistory? Series
    {
        get => (SampleHistory?)GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    public SampleHistory? SecondarySeries
    {
        get => (SampleHistory?)GetValue(SecondarySeriesProperty);
        set => SetValue(SecondarySeriesProperty, value);
    }

    public Brush LineBrush
    {
        get => (Brush)GetValue(LineBrushProperty);
        set => SetValue(LineBrushProperty, value);
    }

    public Brush FillBrush
    {
        get => (Brush)GetValue(FillBrushProperty);
        set => SetValue(FillBrushProperty, value);
    }

    public Brush SecondaryLineBrush
    {
        get => (Brush)GetValue(SecondaryLineBrushProperty);
        set => SetValue(SecondaryLineBrushProperty, value);
    }

    /// <summary>Haut de l'échelle fixe (100 : pourcentages, et températures en °C).</summary>
    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public bool AutoScale
    {
        get => (bool)GetValue(AutoScaleProperty);
        set => SetValue(AutoScaleProperty, value);
    }

    /// <summary>Plancher de l'échelle automatique, pour qu'une valeur quasi nulle ne remplisse pas tout le graphique.</summary>
    public double MinimumScale
    {
        get => (double)GetValue(MinimumScaleProperty);
        set => SetValue(MinimumScaleProperty, value);
    }

    public Sparkline()
    {
        // Abonnement limité à la présence dans l'arbre visuel : l'historique vit dans le ViewModel, plus
        // longtemps que le graphique d'une ligne de disque ou de ventilateur, et ne doit pas le retenir.
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
    }

    private static void OnSeriesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var sparkline = (Sparkline)d;
        if (sparkline.IsLoaded) sparkline.Attach();
    }

    private void Attach()
    {
        Detach();
        _attachedSeries = Series;
        _attachedSecondarySeries = SecondarySeries;
        if (_attachedSeries is not null) _attachedSeries.Changed += InvalidateVisual;
        if (_attachedSecondarySeries is not null) _attachedSecondarySeries.Changed += InvalidateVisual;
        InvalidateVisual();
    }

    private void Detach()
    {
        if (_attachedSeries is not null) _attachedSeries.Changed -= InvalidateVisual;
        if (_attachedSecondarySeries is not null) _attachedSecondarySeries.Changed -= InvalidateVisual;
        _attachedSeries = null;
        _attachedSecondarySeries = null;
    }

    protected override void OnRender(DrawingContext dc)
    {
        SampleHistory? series = Series;
        double w = ActualWidth;
        double h = ActualHeight;
        if (series is null || w <= 0 || h <= 0) return;

        SampleHistory? secondary = SecondarySeries;
        // 15 % de marge au-dessus du pic pour que la courbe ne colle pas au bord supérieur.
        double top = AutoScale
            ? Math.Max(MinimumScale, Math.Max(series.Max(), secondary?.Max() ?? 0) * 1.15)
            : Maximum;
        if (top <= 0) return;

        double stepX = w / Math.Max(1, series.Capacity - 1);
        if (secondary is not null) DrawSeries(dc, secondary, null, new Pen(SecondaryLineBrush, 1.5), w, h, stepX, top);
        DrawSeries(dc, series, FillBrush, new Pen(LineBrush, 1.75), w, h, stepX, top);
    }

    /// <summary>Trace une série alignée à droite (le relevé le plus récent sur le bord droit), en coupant
    /// la courbe là où la valeur manque plutôt que de la faire plonger à zéro.</summary>
    private static void DrawSeries(DrawingContext dc, SampleHistory series, Brush? fill, Pen pen,
                                   double w, double h, double stepX, double top)
    {
        int n = series.Count;
        if (n < 2) return;

        double startX = w - (n - 1) * stepX;
        Point At(int i) => new(startX + i * stepX, h - Math.Clamp(series[i] / top, 0, 1) * h);

        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (StreamGeometryContext lineCtx = line.Open())
        using (StreamGeometryContext areaCtx = area.Open())
        {
            bool inSegment = false;
            for (int i = 0; i <= n; i++)
            {
                bool present = i < n && !double.IsNaN(series[i]);
                if (present)
                {
                    Point p = At(i);
                    if (!inSegment)
                    {
                        lineCtx.BeginFigure(p, isFilled: false, isClosed: false);
                        areaCtx.BeginFigure(new Point(p.X, h), isFilled: true, isClosed: true);
                        inSegment = true;
                    }
                    else
                    {
                        lineCtx.LineTo(p, true, false);
                    }
                    areaCtx.LineTo(p, true, false);
                }
                else if (inSegment)
                {
                    areaCtx.LineTo(new Point(At(i - 1).X, h), true, false);
                    inSegment = false;
                }
            }
        }
        line.Freeze();
        area.Freeze();

        if (fill is not null) dc.DrawGeometry(fill, null, area);
        dc.DrawGeometry(null, pen, line);
    }
}
