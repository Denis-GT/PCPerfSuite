using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace PCPerfSuite.App.Controls;

/// <summary>
/// Champ de saisie d'un entier réglable — limite de puissance, décalage d'horloge, marge de l'overlay… —
/// qui remplace les curseurs : on tape la valeur exacte au lieu de viser un pixel. Voir <see cref="NumericInput"/>
/// pour ce que fait le champ (chiffres seulement, bornes, flèches, validation à Entrée ou à la perte du focus).
/// </summary>
public partial class NumberField : UserControl
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(NumberField),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(double), typeof(NumberField), new PropertyMetadata(0d, OnHintInputChanged));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(NumberField),
        new PropertyMetadata(double.PositiveInfinity, OnHintInputChanged));

    /// <summary>Pas des flèches ; au-dessus de 1, la valeur est aussi arrondie à ce pas (voir NumericInput.Step).</summary>
    public static readonly DependencyProperty StepProperty = DependencyProperty.Register(
        nameof(Step), typeof(double), typeof(NumberField), new PropertyMetadata(1d));

    /// <summary>Unité affichée dans le cadre, après le nombre (« % », « °C », « MHz »).</summary>
    public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(
        nameof(Unit), typeof(string), typeof(NumberField), new PropertyMetadata(""));

    /// <summary>Affiche « de 50 à 120 » à côté du champ. À couper quand la plage va de soi (0 à 100 %) et que la
    /// place manque.</summary>
    public static readonly DependencyProperty ShowRangeProperty = DependencyProperty.Register(
        nameof(ShowRange), typeof(bool), typeof(NumberField), new PropertyMetadata(true, OnHintInputChanged));

    /// <summary>Précision ajoutée après la plage (« = Désactivée »), vide si rien à dire.</summary>
    public static readonly DependencyProperty NoteProperty = DependencyProperty.Register(
        nameof(Note), typeof(string), typeof(NumberField), new PropertyMetadata("", OnHintInputChanged));

    /// <summary>Texte à côté du champ : « de 50 à 120 · note ». Null quand il n'y a rien à afficher.</summary>
    public static readonly DependencyProperty HintTextProperty = DependencyProperty.Register(
        nameof(HintText), typeof(string), typeof(NumberField), new PropertyMetadata(null));

    public NumberField()
    {
        InitializeComponent();
    }

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Minimum
    {
        get => (double)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public double Step
    {
        get => (double)GetValue(StepProperty);
        set => SetValue(StepProperty, value);
    }

    public bool ShowRange
    {
        get => (bool)GetValue(ShowRangeProperty);
        set => SetValue(ShowRangeProperty, value);
    }

    public string Unit
    {
        get => (string)GetValue(UnitProperty);
        set => SetValue(UnitProperty, value);
    }

    public string Note
    {
        get => (string)GetValue(NoteProperty);
        set => SetValue(NoteProperty, value);
    }

    public string? HintText
    {
        get => (string?)GetValue(HintTextProperty);
        private set => SetValue(HintTextProperty, value);
    }

    private static void OnHintInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((NumberField)d).UpdateHint();

    private void UpdateHint()
    {
        var parts = new List<string>(2);

        // Bornes pas encore renseignées (maximum au plus bas, ou infini) : pas de plage à annoncer.
        if (ShowRange && double.IsFinite(Maximum) && Maximum > Minimum)
        {
            // Bornes arrondies vers l'intérieur, comme NumericInput : la plage annoncée est celle qu'on peut saisir.
            parts.Add(string.Format(CultureInfo.CurrentCulture, "de {0:0} à {1:0}", Math.Ceiling(Minimum), Math.Floor(Maximum)));
        }
        if (!string.IsNullOrEmpty(Note)) parts.Add(Note);

        HintText = parts.Count > 0 ? string.Join(" · ", parts) : null;
    }
}
