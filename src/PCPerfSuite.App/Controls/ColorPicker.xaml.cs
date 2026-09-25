using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PCPerfSuite.App.Overlay;

namespace PCPerfSuite.App.Controls;

/// <summary>
/// Choix précis d'une couleur : carré saturation / luminosité, barre de teinte, code #RRGGBB.
///
/// L'aperçu (carré, barre, pastille, code) suit la souris en direct, mais la couleur n'est transmise à
/// <see cref="ColorHex"/> qu'AU RELÂCHEMENT — ou à Entrée / à la perte du focus pour le code. Chaque changement
/// de ColorHex enregistre les réglages et redessine l'overlay : le faire à chaque pixel de glissement écrirait le
/// fichier de réglages plusieurs dizaines de fois par seconde.
/// </summary>
public partial class ColorPicker : UserControl
{
    public static readonly DependencyProperty ColorHexProperty = DependencyProperty.Register(
        nameof(ColorHex), typeof(string), typeof(ColorPicker),
        new FrameworkPropertyMetadata("#FFFFFF", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnColorHexChanged));

    private const double ThumbRadius = 7;

    private double _hue;
    private double _saturation;
    private double _value = 1;

    /// <summary>La couleur est en train d'être transmise par ce contrôle : le retour de ColorHex ne doit pas
    /// recalculer teinte, saturation et luminosité (on perdrait la teinte d'un gris, et le pouce sauterait).</summary>
    private bool _committing;

    private bool _draggingPlane;
    private bool _draggingHue;

    public ColorPicker()
    {
        InitializeComponent();
        Loaded += (_, _) => SyncFromColorHex();
    }

    public string ColorHex
    {
        get => (string)GetValue(ColorHexProperty);
        set => SetValue(ColorHexProperty, value);
    }

    private static void OnColorHexChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var picker = (ColorPicker)d;
        if (!picker._committing) picker.SyncFromColorHex();
    }

    /// <summary>Recale l'aperçu sur la valeur liée (case de base cliquée, « Couleurs par défaut »…).</summary>
    private void SyncFromColorHex()
    {
        if (!OverlayPalette.TryParse(ColorHex, out Color color)) return;

        (double hue, double saturation, double value) = OverlayPalette.ToHsv(color);

        // Sur un gris, un blanc ou un noir la teinte n'est pas définie : on garde celle qu'on avait, pour que la
        // barre de teinte ne saute pas à 0 quand on choisit une couleur de base grise.
        if (saturation > 0 && value > 0) _hue = hue;
        _saturation = saturation;
        _value = value;

        Refresh();
    }

    // ----- Affichage -----

    private void OnLayoutChanged(object sender, SizeChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        Color current = OverlayPalette.FromHsv(_hue, _saturation, _value);

        HueFill.Fill = new SolidColorBrush(OverlayPalette.FromHsv(_hue, 1, 1));
        Preview.Background = new SolidColorBrush(current);

        // Le code n'est pas réécrit pendant qu'on le tape.
        if (!HexBox.IsKeyboardFocused) HexBox.Text = OverlayPalette.ToHex(current);

        if (Plane.ActualWidth > 0)
        {
            Canvas.SetLeft(PlaneThumb, _saturation * Plane.ActualWidth - ThumbRadius);
            Canvas.SetTop(PlaneThumb, (1 - _value) * Plane.ActualHeight - ThumbRadius);
        }

        if (HueBar.ActualWidth > 0)
        {
            Canvas.SetLeft(HueThumb, _hue / 360 * HueBar.ActualWidth - ThumbRadius);
            Canvas.SetTop(HueThumb, HueBar.ActualHeight / 2 - ThumbRadius);
        }
    }

    /// <summary>Transmet la couleur affichée à ColorHex — seulement si elle diffère de celle qui y est déjà.</summary>
    private void Commit()
    {
        string hex = OverlayPalette.ToHex(OverlayPalette.FromHsv(_hue, _saturation, _value));
        if (string.Equals(hex, ColorHex, StringComparison.OrdinalIgnoreCase)) return;

        _committing = true;
        try
        {
            ColorHex = hex;
        }
        finally
        {
            _committing = false;
        }
    }

    // ----- Carré saturation / luminosité -----

    private void OnPlaneMouseDown(object sender, MouseButtonEventArgs e)
    {
        _draggingPlane = Plane.CaptureMouse();
        UpdateFromPlane(e.GetPosition(Plane));
    }

    private void OnPlaneMouseMove(object sender, MouseEventArgs e)
    {
        if (_draggingPlane) UpdateFromPlane(e.GetPosition(Plane));
    }

    private void OnPlaneMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_draggingPlane) return;

        _draggingPlane = false;
        Plane.ReleaseMouseCapture();
        Commit();
    }

    private void UpdateFromPlane(Point position)
    {
        if (Plane.ActualWidth <= 0 || Plane.ActualHeight <= 0) return;

        // La souris peut sortir du carré pendant le glissement (elle est capturée) : on borne aux bords.
        _saturation = Math.Clamp(position.X / Plane.ActualWidth, 0, 1);
        _value = 1 - Math.Clamp(position.Y / Plane.ActualHeight, 0, 1);
        Refresh();
    }

    // ----- Barre de teinte -----

    private void OnHueMouseDown(object sender, MouseButtonEventArgs e)
    {
        _draggingHue = HueBar.CaptureMouse();
        UpdateFromHue(e.GetPosition(HueBar));
    }

    private void OnHueMouseMove(object sender, MouseEventArgs e)
    {
        if (_draggingHue) UpdateFromHue(e.GetPosition(HueBar));
    }

    private void OnHueMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_draggingHue) return;

        _draggingHue = false;
        HueBar.ReleaseMouseCapture();
        Commit();
    }

    private void UpdateFromHue(Point position)
    {
        if (HueBar.ActualWidth <= 0) return;

        // 360 est le même rouge que 0 : on s'arrête juste avant pour ne pas retomber au début de la barre.
        _hue = Math.Clamp(position.X / HueBar.ActualWidth, 0, 1) * 359.999;
        Refresh();
    }

    // ----- Code hexadécimal -----

    /// <summary>Un clic dans le champ le sélectionne en entier (comme les champs numériques) : on tape le nouveau
    /// code par-dessus. Les clics suivants placent le curseur.</summary>
    private void OnHexMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (HexBox.IsKeyboardFocusWithin) return;

        HexBox.Focus();
        e.Handled = true;
    }

    private void OnHexGotFocus(object sender, KeyboardFocusChangedEventArgs e) => HexBox.SelectAll();

    /// <summary>Seuls « # » (en tête) et les chiffres hexadécimaux passent.</summary>
    private void OnHexTextInput(object sender, TextCompositionEventArgs e)
    {
        string proposed = HexBox.Text.Remove(HexBox.SelectionStart, HexBox.SelectionLength)
            .Insert(HexBox.SelectionStart, e.Text);

        for (int i = 0; i < proposed.Length; i++)
        {
            char c = proposed[i];
            bool hexDigit = c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
            if (hexDigit || (c == '#' && i == 0)) continue;

            e.Handled = true;
            return;
        }
    }

    private void OnHexKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Space:
                e.Handled = true;
                break;

            case Key.Enter:
                ApplyHex();
                e.Handled = true;
                break;

            case Key.Escape:
                // Abandonne la saisie : le champ reprend la couleur actuelle.
                HexBox.Text = OverlayPalette.ToHex(OverlayPalette.FromHsv(_hue, _saturation, _value));
                HexBox.SelectAll();
                e.Handled = true;
                break;
        }
    }

    private void OnHexLostFocus(object sender, KeyboardFocusChangedEventArgs e) => ApplyHex();

    private void ApplyHex()
    {
        // Un code incomplet ou invalide n'est pas appliqué : le champ reprend la couleur actuelle plutôt que de
        // poser un blanc de repli qu'on n'a pas demandé.
        if (OverlayPalette.TryParse(HexBox.Text, out Color color) && HexBox.Text.TrimStart('#').Length == 6)
        {
            (double hue, double saturation, double value) = OverlayPalette.ToHsv(color);
            if (saturation > 0 && value > 0) _hue = hue;
            _saturation = saturation;
            _value = value;
            Commit();
        }

        // Après la validation : réécrit le code au format canonique, ou rend l'ancien s'il était invalide.
        Refresh();
        if (HexBox.IsKeyboardFocused) HexBox.Text = OverlayPalette.ToHex(OverlayPalette.FromHsv(_hue, _saturation, _value));
    }
}
