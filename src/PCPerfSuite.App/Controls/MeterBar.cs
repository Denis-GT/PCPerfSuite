using System.Windows;
using System.Windows.Media;

namespace PCPerfSuite.App.Controls;

/// <summary>
/// Barre de charge simple, dessinée à la main (pas de ControlTemplate) pour éviter toute
/// dépendance aux mécanismes internes de ProgressBar. Value attendu entre 0 et 100.
/// </summary>
public sealed class MeterBar : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(MeterBar),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(MeterBar),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IndicatorBrushProperty = DependencyProperty.Register(
        nameof(IndicatorBrush), typeof(Brush), typeof(MeterBar),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF)), FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public Brush TrackBrush
    {
        get => (Brush)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public Brush IndicatorBrush
    {
        get => (Brush)GetValue(IndicatorBrushProperty);
        set => SetValue(IndicatorBrushProperty, value);
    }

    public MeterBar()
    {
        Height = 8;
        MinWidth = 40;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = Math.Max(0, ActualWidth);
        double h = Math.Max(0, ActualHeight);
        if (w <= 0 || h <= 0) return;

        double radius = h / 2;
        dc.DrawRoundedRectangle(TrackBrush, null, new Rect(0, 0, w, h), radius, radius);

        double percent = Math.Clamp(Value, 0, 100) / 100.0;
        double indicatorWidth = w * percent;
        if (indicatorWidth > 0.5)
        {
            dc.DrawRoundedRectangle(IndicatorBrush, null, new Rect(0, 0, indicatorWidth, h), radius, radius);
        }
    }
}
