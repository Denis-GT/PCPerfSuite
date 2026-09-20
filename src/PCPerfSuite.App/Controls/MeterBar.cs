using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using PCPerfSuite.App.Styles;

namespace PCPerfSuite.App.Controls;

/// <summary>
/// Barre de charge, dessinée à la main (pas de ControlTemplate) pour éviter toute dépendance aux
/// mécanismes internes de ProgressBar. Value attendu entre 0 et 100.
///
/// Le relief vient du dessin et jamais d'un Effect : piste légèrement creusée, remplissage en dégradé
/// avec un reflet sur la moitié haute. Un Effect forcerait un rendu bitmap intermédiaire qui
/// désactiverait le ClearType du texte voisin.
/// </summary>
public sealed class MeterBar : FrameworkElement
{
    /// <summary>En dessous de cet écart (en points de pourcentage), la valeur est posée sans animation :
    /// animer un mouvement d'un demi-pixel ne se voit pas et coûte une passe de rendu par image.</summary>
    private const double AnimationFloor = 1.0;

    private static readonly Duration AnimationDuration = new(TimeSpan.FromMilliseconds(250));

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(MeterBar),
        new FrameworkPropertyMetadata(0.0, OnValueChanged));

    /// <summary>Valeur réellement dessinée : elle rejoint <see cref="Value"/> en douceur. Séparer les deux
    /// évite qu'une animation en cours ne soit relue comme la valeur métier.</summary>
    private static readonly DependencyProperty RenderValueProperty = DependencyProperty.Register(
        nameof(RenderValue), typeof(double), typeof(MeterBar),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(MeterBar),
        new FrameworkPropertyMetadata(ThemeColors.FrozenBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IndicatorBrushProperty = DependencyProperty.Register(
        nameof(IndicatorBrush), typeof(Brush), typeof(MeterBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>La couleur suit la valeur (accent, puis avertissement, puis danger) au lieu de rester
    /// fixe. À réserver aux mesures où « haut » veut dire « ça chauffe » : pour une santé de batterie,
    /// c'est le bas qui est mauvais, et la couleur dirait l'inverse de ce qu'il faut comprendre.</summary>
    public static readonly DependencyProperty UseValueColorProperty = DependencyProperty.Register(
        nameof(UseValueColor), typeof(bool), typeof(MeterBar),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty WarnThresholdProperty = DependencyProperty.Register(
        nameof(WarnThreshold), typeof(double), typeof(MeterBar),
        new FrameworkPropertyMetadata(75.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DangerThresholdProperty = DependencyProperty.Register(
        nameof(DangerThreshold), typeof(double), typeof(MeterBar),
        new FrameworkPropertyMetadata(90.0, FrameworkPropertyMetadataOptions.AffectsRender));

    // Dégradé mis en cache : sans cela on reconstruirait deux pinceaux à chaque image d'animation.
    private Color _cachedColor;
    private LinearGradientBrush? _cachedFill;

    private static readonly LinearGradientBrush Sheen = BuildFrozen(
        new GradientStop(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF), 0),
        new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1),
        vertical: true);

    private static readonly LinearGradientBrush TrackShade = BuildFrozen(
        new GradientStop(Color.FromArgb(0x1C, 0x00, 0x00, 0x00), 0),
        new GradientStop(Color.FromArgb(0x00, 0x00, 0x00, 0x00), 1),
        vertical: true);

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    private double RenderValue
    {
        get => (double)GetValue(RenderValueProperty);
        set => SetValue(RenderValueProperty, value);
    }

    public Brush TrackBrush
    {
        get => (Brush)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    /// <summary>Couleur du remplissage. Null (défaut) laisse la barre choisir : l'accent, ou la couleur
    /// de la valeur si <see cref="UseValueColor"/> est vrai.</summary>
    public Brush? IndicatorBrush
    {
        get => (Brush?)GetValue(IndicatorBrushProperty);
        set => SetValue(IndicatorBrushProperty, value);
    }

    public bool UseValueColor
    {
        get => (bool)GetValue(UseValueColorProperty);
        set => SetValue(UseValueColorProperty, value);
    }

    public double WarnThreshold
    {
        get => (double)GetValue(WarnThresholdProperty);
        set => SetValue(WarnThresholdProperty, value);
    }

    public double DangerThreshold
    {
        get => (double)GetValue(DangerThresholdProperty);
        set => SetValue(DangerThresholdProperty, value);
    }

    static MeterBar()
    {
        // Valeurs par défaut du type, et non valeurs locales posées dans le constructeur : une valeur locale
        // l'emporterait sur un Height écrit dans un DataTemplate, qui serait alors ignoré sans bruit.
        HeightProperty.OverrideMetadata(typeof(MeterBar), new FrameworkPropertyMetadata(8.0));
        MinWidthProperty.OverrideMetadata(typeof(MeterBar), new FrameworkPropertyMetadata(40.0));
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var bar = (MeterBar)d;
        double target = Clamp(e.NewValue as double? ?? 0);
        double current = bar.RenderValue;

        // Premier relevé, écart infime, ou barre pas encore à l'écran : on pose la valeur directement.
        // Une barre qui partirait de zéro à chaque apparition donnerait une impression de rechargement.
        if (!bar.IsLoaded || Math.Abs(target - current) < AnimationFloor || double.IsNaN(current))
        {
            bar.BeginAnimation(RenderValueProperty, null);
            bar.RenderValue = target;
            return;
        }

        bar.BeginAnimation(RenderValueProperty, new DoubleAnimation(target, AnimationDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd,
        });
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = Math.Max(0, ActualWidth);
        double h = Math.Max(0, ActualHeight);
        if (w <= 0 || h <= 0) return;

        double radius = h / 2;
        var full = new Rect(0, 0, w, h);
        dc.DrawRoundedRectangle(TrackBrush, null, full, radius, radius);

        // Creux de la piste : un voile sombre sur la moitié haute, découpé à la forme de la piste.
        dc.PushClip(new RectangleGeometry(full, radius, radius));
        dc.DrawRectangle(TrackShade, null, new Rect(0, 0, w, h / 2));
        dc.Pop();

        double percent = Clamp(RenderValue) / 100.0;
        if (percent <= 0) return;

        // Sous une largeur d'un « bout arrondi », la barre ne dessinerait qu'un trait coupé : on garde
        // un rond minimum pour qu'une valeur non nulle ne paraisse jamais vide.
        double indicatorWidth = Math.Min(w, Math.Max(w * percent, h));
        var indicator = new Rect(0, 0, indicatorWidth, h);

        dc.DrawRoundedRectangle(ResolveFill(), null, indicator, radius, radius);

        dc.PushClip(new RectangleGeometry(indicator, radius, radius));
        dc.DrawRectangle(Sheen, null, new Rect(0, 0, indicatorWidth, h / 2));
        dc.Pop();
    }

    /// <summary>Pinceau du remplissage : celui qu'on nous a donné, sinon un dégradé bâti sur la couleur
    /// qui convient à la valeur.</summary>
    private Brush ResolveFill()
    {
        if (IndicatorBrush is { } explicitBrush) return explicitBrush;

        Color color = ThemeColors.Accent;
        if (UseValueColor)
        {
            double value = Clamp(RenderValue);
            if (value >= DangerThreshold) color = ThemeColors.Danger;
            else if (value >= WarnThreshold) color = ThemeColors.Warn;
        }

        if (_cachedFill is not null && _cachedColor == color) return _cachedFill;

        _cachedColor = color;
        _cachedFill = BuildFrozen(
            new GradientStop(color, 0),
            new GradientStop(Lighten(color, 0.15), 1),
            vertical: false);
        return _cachedFill;
    }

    private static LinearGradientBrush BuildFrozen(GradientStop from, GradientStop to, bool vertical)
    {
        var brush = new LinearGradientBrush(new GradientStopCollection { from, to })
        {
            StartPoint = new Point(0, 0),
            EndPoint = vertical ? new Point(0, 1) : new Point(1, 0),
        };
        brush.Freeze();
        return brush;
    }

    private static Color Lighten(Color color, double amount) => Color.FromArgb(
        color.A,
        (byte)Math.Round(color.R + (255 - color.R) * amount),
        (byte)Math.Round(color.G + (255 - color.G) * amount),
        (byte)Math.Round(color.B + (255 - color.B) * amount));

    private static double Clamp(double value) => double.IsNaN(value) ? 0 : Math.Clamp(value, 0, 100);
}
