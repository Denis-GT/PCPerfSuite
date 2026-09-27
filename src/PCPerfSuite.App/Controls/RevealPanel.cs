using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace PCPerfSuite.App.Controls;

/// <summary>
/// Panneau qui se déroule vers le bas depuis son bord haut quand <see cref="IsOpen"/> passe à vrai, et se replie
/// quand il repasse à faux. Le contenu garde sa hauteur réelle et se découvre (il n'est pas écrasé), et ce qui se
/// trouve dessous suit le mouvement, puisque c'est la hauteur mesurée du panneau qui est animée.
/// Replié, le contenu est retiré de l'arbre visuel : il ne prend pas de place et le clavier ne peut plus l'atteindre.
/// </summary>
public class RevealPanel : Decorator
{
    private static readonly Duration OpenDuration = new(TimeSpan.FromMilliseconds(220));
    private static readonly Duration CloseDuration = new(TimeSpan.FromMilliseconds(160));

    public static readonly DependencyProperty IsOpenProperty = DependencyProperty.Register(
        nameof(IsOpen), typeof(bool), typeof(RevealPanel), new PropertyMetadata(false, OnIsOpenChanged));

    /// <summary>Avancement de l'ouverture, de 0 (replié) à 1 (déroulé) ; c'est lui qui est animé.</summary>
    private static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(RevealPanel),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsMeasure, OnProgressChanged));

    static RevealPanel()
    {
        // Valeur par défaut du type plutôt que valeur locale : voir MeterBar.
        ClipToBoundsProperty.OverrideMetadata(typeof(RevealPanel), new FrameworkPropertyMetadata(true));
    }

    public RevealPanel()
    {
        Loaded += (_, _) => SyncChildVisibility();
    }

    public bool IsOpen
    {
        get => (bool)GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    private double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    protected override Size MeasureOverride(Size constraint)
    {
        if (Child is null) return default;

        // Le contenu est mesuré à sa hauteur naturelle, quelle que soit l'avancée : seule la place réservée varie.
        Child.Measure(new Size(constraint.Width, double.PositiveInfinity));
        return new Size(Child.DesiredSize.Width, Child.DesiredSize.Height * Progress);
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        Child?.Arrange(new Rect(0, 0, arrangeSize.Width, Child.DesiredSize.Height));
        return arrangeSize;
    }

    private static void OnIsOpenChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((RevealPanel)d).Animate((bool)e.NewValue);

    private static void OnProgressChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((RevealPanel)d).Opacity = (double)e.NewValue;

    private void Animate(bool open)
    {
        double target = open ? 1 : 0;

        if (open && Child is not null) Child.Visibility = Visibility.Visible;

        // Pas encore à l'écran, ou animations coupées dans Windows : on passe directement à l'état final.
        if (!IsLoaded || !SystemParameters.ClientAreaAnimation)
        {
            BeginAnimation(ProgressProperty, null);
            Progress = target;
            SyncChildVisibility();
            return;
        }

        double from = Progress;
        Progress = target;

        var animation = new DoubleAnimation(from, target, open ? OpenDuration : CloseDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop,
        };
        animation.Completed += (_, _) =>
        {
            // Rouvert entre-temps : ce n'est plus la fin de cette fermeture.
            if (!IsOpen) SyncChildVisibility();
        };
        BeginAnimation(ProgressProperty, animation);
    }

    private void SyncChildVisibility()
    {
        if (Child is not null) Child.Visibility = IsOpen ? Visibility.Visible : Visibility.Collapsed;
    }
}
