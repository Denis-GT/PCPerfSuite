using System.Collections.Specialized;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using PCPerfSuite.App.Styles;
using PCPerfSuite.Core.Hardware;

namespace PCPerfSuite.App.Controls;

/// <summary>
/// Éditeur de courbe température → % ventilateur, dessiné à la main (comme Sparkline/MeterBar/
/// TreemapControl), façon éditeur de courbe de MSI Afterburner/FanControl : on glisse un point dans
/// les deux axes (sans pouvoir doubler son voisin), on en ajoute un au double-clic et on en retire un
/// au clic droit.
/// </summary>
public sealed class FanCurveEditor : FrameworkElement
{
    public static readonly RoutedEvent EditingCompletedEvent = EventManager.RegisterRoutedEvent(
        nameof(EditingCompleted), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(FanCurveEditor));

    public event RoutedEventHandler EditingCompleted
    {
        add => AddHandler(EditingCompletedEvent, value);
        remove => RemoveHandler(EditingCompletedEvent, value);
    }

    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(
        nameof(Points), typeof(System.Collections.ObjectModel.ObservableCollection<FanCurvePoint>), typeof(FanCurveEditor),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnPointsChanged));

    public static readonly DependencyProperty CurrentTempCProperty = DependencyProperty.Register(
        nameof(CurrentTempC), typeof(double?), typeof(FanCurveEditor),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MinTempCProperty = DependencyProperty.Register(
        nameof(MinTempC), typeof(double), typeof(FanCurveEditor),
        new FrameworkPropertyMetadata(20.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaxTempCProperty = DependencyProperty.Register(
        nameof(MaxTempC), typeof(double), typeof(FanCurveEditor),
        new FrameworkPropertyMetadata(85.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public System.Collections.ObjectModel.ObservableCollection<FanCurvePoint>? Points
    {
        get => (System.Collections.ObjectModel.ObservableCollection<FanCurvePoint>?)GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    public double? CurrentTempC
    {
        get => (double?)GetValue(CurrentTempCProperty);
        set => SetValue(CurrentTempCProperty, value);
    }

    public double MinTempC
    {
        get => (double)GetValue(MinTempCProperty);
        set => SetValue(MinTempCProperty, value);
    }

    public double MaxTempC
    {
        get => (double)GetValue(MaxTempCProperty);
        set => SetValue(MaxTempCProperty, value);
    }

    private static readonly SolidColorBrush GridBrush = Freeze(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush AxisTextBrush = Freeze(ThemeColors.TextSecondary);
    private static readonly SolidColorBrush CurveBrush = Freeze(ThemeColors.Accent);
    private static readonly SolidColorBrush FillBrush = Freeze(ThemeColors.WithAlpha(ThemeColors.Accent, 0x30));
    private static readonly SolidColorBrush MarkerBrush = Freeze(ThemeColors.Warn);

    private const double TopPad = 8, BottomPad = 20, SidePad = 14;
    private const double HandleRadius = 6;

    /// <summary>Écart minimal entre deux points, pour qu'un point ne puisse pas en croiser un autre
    /// (la courbe resterait dessinable, mais deviendrait impossible à rattraper à la souris).</summary>
    private const double MinTempGap = 2;

    private const int MinPoints = 2;
    private const int MaxPoints = 12;

    private int _dragIndex = -1;
    private double _dragMinTemp;
    private double _dragMaxTemp;
    private readonly Typeface _typeface = new("Segoe UI Variable, Segoe UI");

    private static SolidColorBrush Freeze(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public FanCurveEditor()
    {
        Height = 150;
        Cursor = Cursors.Hand;
    }

    private static void OnPointsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (FanCurveEditor)d;
        if (e.OldValue is INotifyCollectionChanged oldCol) oldCol.CollectionChanged -= control.OnCollectionChanged;
        if (e.NewValue is INotifyCollectionChanged newCol) newCol.CollectionChanged += control.OnCollectionChanged;
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

    private Rect PlotArea => new(
        SidePad, TopPad,
        Math.Max(0, ActualWidth - 2 * SidePad),
        Math.Max(0, ActualHeight - TopPad - BottomPad));

    private Point ToScreen(Rect plot, FanCurvePoint p)
    {
        double tSpan = Math.Max(0.01, MaxTempC - MinTempC);
        double x = plot.X + (p.TempC - MinTempC) / tSpan * plot.Width;
        double y = plot.Y + (1 - Math.Clamp(p.Percent, 0, 100) / 100.0) * plot.Height;
        return new Point(x, y);
    }

    private (float TempC, float Percent) FromScreen(Rect plot, Point p)
    {
        double tSpan = Math.Max(0.01, MaxTempC - MinTempC);
        double temp = MinTempC + (p.X - plot.X) / Math.Max(1, plot.Width) * tSpan;
        double percent = (1 - (p.Y - plot.Y) / Math.Max(1, plot.Height)) * 100.0;

        return ((float)Math.Round(Math.Clamp(temp, MinTempC, MaxTempC)),
                (float)Math.Round(Math.Clamp(percent, 0, 100)));
    }

    protected override void OnRender(DrawingContext dc)
    {
        List<FanCurvePoint>? points = Points?.OrderBy(p => p.TempC).ToList();
        Rect plot = PlotArea;
        if (points is not { Count: > 0 } || plot.Width <= 0 || plot.Height <= 0) return;

        for (int pct = 0; pct <= 100; pct += 25)
        {
            double y = plot.Y + (1 - pct / 100.0) * plot.Height;
            dc.DrawLine(new Pen(GridBrush, 1), new Point(plot.X, y), new Point(plot.Right, y));
        }

        var fillGeometry = new StreamGeometry();
        using (StreamGeometryContext ctx = fillGeometry.Open())
        {
            Point p0 = ToScreen(plot, points[0]);
            ctx.BeginFigure(new Point(p0.X, plot.Bottom), true, true);
            ctx.LineTo(p0, true, false);
            for (int i = 1; i < points.Count; i++) ctx.LineTo(ToScreen(plot, points[i]), true, false);
            ctx.LineTo(new Point(ToScreen(plot, points[^1]).X, plot.Bottom), true, false);
        }
        fillGeometry.Freeze();
        dc.DrawGeometry(FillBrush, null, fillGeometry);

        var lineGeometry = new StreamGeometry();
        using (StreamGeometryContext ctx = lineGeometry.Open())
        {
            ctx.BeginFigure(ToScreen(plot, points[0]), false, false);
            for (int i = 1; i < points.Count; i++) ctx.LineTo(ToScreen(plot, points[i]), true, false);
        }
        lineGeometry.Freeze();
        dc.DrawGeometry(null, new Pen(CurveBrush, 2), lineGeometry);

        if (CurrentTempC is { } temp && temp >= MinTempC && temp <= MaxTempC)
        {
            double x = plot.X + (temp - MinTempC) / Math.Max(0.01, MaxTempC - MinTempC) * plot.Width;
            dc.DrawLine(new Pen(MarkerBrush, 1.5) { DashStyle = DashStyles.Dash }, new Point(x, plot.Y), new Point(x, plot.Bottom));

            float target = FanCurveMath.Evaluate(points, (float)temp);
            double y = plot.Y + (1 - target / 100.0) * plot.Height;
            dc.DrawEllipse(MarkerBrush, null, new Point(x, y), 4, 4);
        }

        FanCurvePoint? dragged = _dragIndex >= 0 && Points is { } all && _dragIndex < all.Count ? all[_dragIndex] : null;

        foreach (FanCurvePoint p in points)
        {
            Point screen = ToScreen(plot, p);
            dc.DrawEllipse(Brushes.White, new Pen(CurveBrush, 2), screen, HandleRadius, HandleRadius);

            var label = new FormattedText(
                $"{p.TempC:0}°", System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                _typeface, 10.5, AxisTextBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(label, new Point(screen.X - label.Width / 2, plot.Bottom + 4));

            // Le point qu'on déplace affiche sa valeur complète, les autres resteraient illisibles.
            if (!ReferenceEquals(p, dragged)) continue;

            var value = new FormattedText(
                $"{p.TempC:0}° / {p.Percent:0}%", System.Globalization.CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, _typeface, 11, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(value, new Point(
                Math.Clamp(screen.X - value.Width / 2, plot.X, plot.Right - value.Width),
                Math.Max(plot.Y, screen.Y - HandleRadius - value.Height - 3)));
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var points = Points;
        if (points is null) return;

        Rect plot = PlotArea;
        Point p = e.GetPosition(this);
        int closest = FindClosest(points, plot, p, out double bestDist);

        if (closest >= 0 && bestDist <= HandleRadius * 3)
        {
            _dragIndex = closest;
            ComputeDragBounds(points, closest);
            CaptureMouse();
            UpdateDrag(p, plot);
            return;
        }

        if (e.ClickCount == 2) AddPoint(points, plot, p);
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        var points = Points;
        if (points is null || points.Count <= MinPoints) return;

        int closest = FindClosest(points, PlotArea, e.GetPosition(this), out double bestDist);
        if (closest < 0 || bestDist > HandleRadius * 3) return;

        points.RemoveAt(closest);
        InvalidateVisual();
        RaiseEvent(new RoutedEventArgs(EditingCompletedEvent, this));
    }

    private int FindClosest(IList<FanCurvePoint> points, Rect plot, Point p, out double distance)
    {
        int closest = -1;
        distance = double.MaxValue;

        for (int i = 0; i < points.Count; i++)
        {
            double dist = (ToScreen(plot, points[i]) - p).Length;
            if (dist >= distance) continue;

            distance = dist;
            closest = i;
        }

        return closest;
    }

    private void AddPoint(System.Collections.ObjectModel.ObservableCollection<FanCurvePoint> points, Rect plot, Point p)
    {
        if (points.Count >= MaxPoints || plot.Width <= 0 || plot.Height <= 0) return;

        (float tempC, float percent) = FromScreen(plot, p);
        if (points.Any(existing => Math.Abs(existing.TempC - tempC) < MinTempGap)) return;

        points.Add(new FanCurvePoint { TempC = tempC, Percent = percent });
        InvalidateVisual();
        RaiseEvent(new RoutedEventArgs(EditingCompletedEvent, this));
    }

    /// <summary>Bornes de température du point en cours de déplacement, figées au début du glisser :
    /// les recalculer en continu ferait basculer l'ordre des points sous la souris.</summary>
    private void ComputeDragBounds(IList<FanCurvePoint> points, int index)
    {
        double current = points[index].TempC;
        double min = MinTempC;
        double max = MaxTempC;

        for (int i = 0; i < points.Count; i++)
        {
            if (i == index) continue;

            double temp = points[i].TempC;
            if (temp <= current) min = Math.Max(min, temp + MinTempGap);
            else max = Math.Min(max, temp - MinTempGap);
        }

        _dragMinTemp = min;
        _dragMaxTemp = Math.Max(min, max);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragIndex < 0) return;
        UpdateDrag(e.GetPosition(this), PlotArea);
    }

    private void UpdateDrag(Point p, Rect plot)
    {
        var points = Points;
        if (_dragIndex < 0 || points is null || _dragIndex >= points.Count || plot.Height <= 0) return;

        (float tempC, float percent) = FromScreen(plot, p);
        FanCurvePoint point = points[_dragIndex];
        point.TempC = (float)Math.Clamp(tempC, _dragMinTemp, _dragMaxTemp);
        point.Percent = percent;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_dragIndex < 0) return;

        _dragIndex = -1;
        ReleaseMouseCapture();
        RaiseEvent(new RoutedEventArgs(EditingCompletedEvent, this));
    }
}
