using System.Windows;
using System.Windows.Media;

namespace PCPerfSuite.App.Controls;

/// <summary>
/// Mini-graphique d'historique (comme les petites courbes du Gestionnaire des tâches, en plus fin).
/// Alimenté en poussant des valeurs 0-100 ; garde une fenêtre glissante en interne.
/// Dessin manuel (pas de lib de charts externe) pour rester léger et fiable au premier build.
/// </summary>
public sealed class Sparkline : FrameworkElement
{
    private readonly Queue<double> _samples = new();

    public static readonly DependencyProperty MaxSamplesProperty = DependencyProperty.Register(
        nameof(MaxSamples), typeof(int), typeof(Sparkline), new PropertyMetadata(90));

    public static readonly DependencyProperty LineBrushProperty = DependencyProperty.Register(
        nameof(LineBrush), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF)), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillBrushProperty = DependencyProperty.Register(
        nameof(FillBrush), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromArgb(0x30, 0x0A, 0x84, 0xFF)), FrameworkPropertyMetadataOptions.AffectsRender));

    public int MaxSamples
    {
        get => (int)GetValue(MaxSamplesProperty);
        set => SetValue(MaxSamplesProperty, value);
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

    public void Push(double percent0To100)
    {
        _samples.Enqueue(Math.Clamp(percent0To100, 0, 100));
        while (_samples.Count > MaxSamples) _samples.Dequeue();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth;
        double h = ActualHeight;
        if (w <= 0 || h <= 0 || _samples.Count < 2) return;

        double[] values = _samples.ToArray();
        int n = values.Length;
        double stepX = w / Math.Max(1, MaxSamples - 1);
        // Aligne à droite : les échantillons les plus récents finissent sur le bord droit.
        double startX = w - (n - 1) * stepX;

        var geometry = new StreamGeometry();
        using (StreamGeometryContext ctx = geometry.Open())
        {
            Point P(int i) => new(startX + i * stepX, h - (values[i] / 100.0) * h);

            ctx.BeginFigure(new Point(P(0).X, h), isFilled: true, isClosed: true);
            ctx.LineTo(P(0), true, false);
            for (int i = 1; i < n; i++) ctx.LineTo(P(i), true, false);
            ctx.LineTo(new Point(P(n - 1).X, h), true, false);
        }
        geometry.Freeze();
        dc.DrawGeometry(FillBrush, null, geometry);

        var lineGeometry = new StreamGeometry();
        using (StreamGeometryContext ctx = lineGeometry.Open())
        {
            Point P(int i) => new(startX + i * stepX, h - (values[i] / 100.0) * h);
            ctx.BeginFigure(P(0), false, false);
            for (int i = 1; i < n; i++) ctx.LineTo(P(i), true, false);
        }
        lineGeometry.Freeze();
        dc.DrawGeometry(null, new Pen(LineBrush, 1.75), lineGeometry);
    }
}
