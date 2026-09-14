using System.Collections.Specialized;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using PCPerfSuite.Core.Hardware;

namespace PCPerfSuite.App.Controls;

/// <summary>
/// Éditeur de courbe température → % ventilateur, dessiné à la main (comme Sparkline/MeterBar/
/// TreemapControl) : la température de chaque point est fixe, seul le % se règle en glissant le
/// point verticalement à la souris — façon éditeur de courbe de MSI Afterburner/FanControl.
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
    private static readonly SolidColorBrush AxisTextBrush = Freeze(Color.FromRgb(0xAC, 0xB4, 0xC8));
    private static readonly SolidColorBrush CurveBrush = Freeze(Color.FromRgb(0x0A, 0x84, 0xFF));
    private static readonly SolidColorBrush FillBrush = Freeze(Color.FromArgb(0x30, 0x0A, 0x84, 0xFF));
    private static readonly SolidColorBrush MarkerBrush = Freeze(Color.FromRgb(0xFF, 0x9F, 0x0A));

    private const double TopPad = 8, BottomPad = 20, SidePad = 14;
    private const double HandleRadius = 6;

    private int _dragIndex = -1;
    private readonly Typeface _typeface = new("Segoe UI");

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

        foreach (FanCurvePoint p in points)
        {
            Point screen = ToScreen(plot, p);
            dc.DrawEllipse(Brushes.White, new Pen(CurveBrush, 2), screen, HandleRadius, HandleRadius);

            var label = new FormattedText(
                $"{p.TempC:0}°", System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                _typeface, 10.5, AxisTextBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(label, new Point(screen.X - label.Width / 2, plot.Bottom + 4));
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var points = Points;
        if (points is not { Count: > 0 }) return;

        Rect plot = PlotArea;
        Point p = e.GetPosition(this);

        int closest = -1;
        double bestDist = double.MaxValue;
        for (int i = 0; i < points.Count; i++)
        {
            double dist = (ToScreen(plot, points[i]) - p).Length;
            if (dist < bestDist) { bestDist = dist; closest = i; }
        }

        if (closest < 0 || bestDist > HandleRadius * 3) return;

        _dragIndex = closest;
        CaptureMouse();
        UpdateDrag(p, plot);
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

        double percent = (1 - (p.Y - plot.Y) / plot.Height) * 100.0;
        points[_dragIndex].Percent = (float)Math.Round(Math.Clamp(percent, 0, 100));
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
