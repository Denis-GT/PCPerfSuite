using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;

namespace PCPerfSuite.App.Controls;

/// <summary>
/// Fait d'un TextBox un champ d'entier : il n'accepte que des chiffres (et le signe « - » quand le minimum est
/// négatif), ramène la valeur dans ses bornes, et ne la transmet au ViewModel qu'à Entrée ou à la perte du
/// focus — jamais à chaque frappe, pour ne pas écrire dans le matériel une valeur à moitié tapée.
///
/// Toutes les valeurs de l'app qui se saisissent ainsi sont des entiers (pourcentages, °C, MHz, watts, pixels,
/// millisecondes) : pas de décimales, donc pas de piège de séparateur — les liaisons WPF parlent en-US, pas dans
/// la culture de Windows, et « 0,5 » y serait lu 5.
///
/// Un champ texte ordinaire (nom de profil…) ne pose simplement pas <see cref="IsNumericProperty"/>.
/// </summary>
public static class NumericInput
{
    /// <summary>Longueur maximale acceptée : bien assez pour les valeurs réelles, et évite un dépassement en
    /// cas de collage d'un très long nombre.</summary>
    private const int MaxLength = 9;

    /// <summary>Les flèches haut/bas modifient le champ aussitôt, mais la valeur n'est transmise qu'après cette
    /// pause : maintenir la flèche enfoncée ne doit pas écrire dans le matériel à chaque répétition de touche.</summary>
    private static readonly TimeSpan ArrowCommitDelay = TimeSpan.FromMilliseconds(500);

    private static readonly ConditionalWeakTable<TextBox, DispatcherTimer> ArrowTimers = new();

    public static readonly DependencyProperty IsNumericProperty = DependencyProperty.RegisterAttached(
        "IsNumeric", typeof(bool), typeof(NumericInput), new PropertyMetadata(false, OnIsNumericChanged));

    /// <summary>Borne basse. 0 par défaut : le signe « - » n'est accepté que si elle est négative.</summary>
    public static readonly DependencyProperty MinimumProperty = DependencyProperty.RegisterAttached(
        "Minimum", typeof(double), typeof(NumericInput), new PropertyMetadata(0d));

    /// <summary>Borne haute. Sans effet tant qu'elle est inférieure au minimum (bornes pas encore connues).</summary>
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.RegisterAttached(
        "Maximum", typeof(double), typeof(NumericInput), new PropertyMetadata(double.PositiveInfinity));

    /// <summary>Pas des flèches. Au-dessus de 1, la valeur saisie est aussi arrondie au multiple de ce pas
    /// (compté depuis le minimum), comme le faisait le curseur qu'elle remplace — certains réglages matériels
    /// n'existent que par paliers.</summary>
    public static readonly DependencyProperty StepProperty = DependencyProperty.RegisterAttached(
        "Step", typeof(double), typeof(NumericInput), new PropertyMetadata(1d));

    /// <summary>Le contenu a été modifié par l'utilisateur depuis la dernière validation. Sans ce garde-fou, cliquer
    /// dans le champ puis ailleurs « validerait » la valeur d'origine — arrondie au pas ou ramenée dans des
    /// bornes qui ont changé entre-temps — et l'écrirait dans le matériel sans que personne n'ait rien demandé.</summary>
    private static readonly DependencyProperty IsDirtyProperty = DependencyProperty.RegisterAttached(
        "IsDirty", typeof(bool), typeof(NumericInput), new PropertyMetadata(false));

    private static readonly DependencyPropertyKey IsOutOfRangePropertyKey = DependencyProperty.RegisterAttachedReadOnly(
        "IsOutOfRange", typeof(bool), typeof(NumericInput), new PropertyMetadata(false));

    /// <summary>Vrai quand la saisie en cours est hors des bornes ET qu'aucune suite de chiffres ne peut l'y ramener :
    /// « 1 » avec un minimum de 100 n'en est pas (on tape « 150 »), « 99 » avec un maximum de 48 en est une. Repasse à
    /// faux dès que la valeur redevient valide, et à la validation ou à l'abandon de la saisie. Sert aux champs qui
    /// n'annoncent leur plage qu'en cas d'erreur (voir NumberField).</summary>
    public static readonly DependencyProperty IsOutOfRangeProperty = IsOutOfRangePropertyKey.DependencyProperty;

    public static bool GetIsOutOfRange(DependencyObject element) => (bool)element.GetValue(IsOutOfRangeProperty);

    public static bool GetIsNumeric(DependencyObject element) => (bool)element.GetValue(IsNumericProperty);
    public static void SetIsNumeric(DependencyObject element, bool value) => element.SetValue(IsNumericProperty, value);

    public static double GetMinimum(DependencyObject element) => (double)element.GetValue(MinimumProperty);
    public static void SetMinimum(DependencyObject element, double value) => element.SetValue(MinimumProperty, value);

    public static double GetMaximum(DependencyObject element) => (double)element.GetValue(MaximumProperty);
    public static void SetMaximum(DependencyObject element, double value) => element.SetValue(MaximumProperty, value);

    public static double GetStep(DependencyObject element) => (double)element.GetValue(StepProperty);
    public static void SetStep(DependencyObject element, double value) => element.SetValue(StepProperty, value);

    private static void OnIsNumericChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box) return;

        box.PreviewTextInput -= OnPreviewTextInput;
        box.PreviewKeyDown -= OnPreviewKeyDown;
        box.LostKeyboardFocus -= OnLostKeyboardFocus;
        box.GotKeyboardFocus -= OnGotKeyboardFocus;
        box.TextChanged -= OnTextChanged;
        box.PreviewMouseLeftButtonDown -= OnPreviewMouseLeftButtonDown;
        DataObject.RemovePastingHandler(box, OnPasting);

        if (e.NewValue is not true) return;

        box.PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
        box.PreviewTextInput += OnPreviewTextInput;
        box.PreviewKeyDown += OnPreviewKeyDown;
        box.LostKeyboardFocus += OnLostKeyboardFocus;
        box.GotKeyboardFocus += OnGotKeyboardFocus;
        box.TextChanged += OnTextChanged;
        DataObject.AddPastingHandler(box, OnPasting);

        // Un éditeur de saisie (japonais, chinois…) composerait des caractères qu'on refuserait un à un.
        InputMethod.SetIsInputMethodEnabled(box, false);
    }

    // ----- Saisie -----

    private static void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        var box = (TextBox)sender;
        e.Handled = !IsAcceptable(box, Proposed(box, e.Text));
    }

    private static void OnPasting(object sender, DataObjectPastingEventArgs e)
    {
        var box = (TextBox)sender;

        // Le presse-papiers apporte souvent un espace ou un retour à la ligne : on les retire plutôt que de
        // refuser un nombre par ailleurs valide. Tout le reste est refusé en bloc.
        string? text = (e.DataObject.GetData(DataFormats.UnicodeText) as string)?.Trim();

        e.CancelCommand();
        if (text is { Length: > 0 } && IsAcceptable(box, Proposed(box, text)))
        {
            box.SelectedText = text;
        }
    }

    private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var box = (TextBox)sender;

        switch (e.Key)
        {
            case Key.Space:
                // L'espace ne passe pas par PreviewTextInput.
                e.Handled = true;
                break;

            case Key.Enter:
                Commit(box);
                e.Handled = true;
                break;

            case Key.Escape:
                // Abandonne la saisie en cours : le champ reprend la valeur du ViewModel.
                StopArrowTimer(box);
                box.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
                box.SetValue(IsDirtyProperty, false);
                box.SetValue(IsOutOfRangePropertyKey, false);
                e.Handled = true;
                break;

            case Key.Up:
                Nudge(box, +StepOf(box));
                e.Handled = true;
                break;

            case Key.Down:
                Nudge(box, -StepOf(box));
                e.Handled = true;
                break;
        }
    }

    private static void OnGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        => ((TextBox)sender).SelectAll();

    /// <summary>Un clic dans un champ qui n'a pas encore le focus le donne SANS poser le curseur : c'est ce qui
    /// fait qu'un clic sélectionne tout (le SelectAll de <see cref="OnGotKeyboardFocus"/> tient alors), et qu'une
    /// frappe remplace la valeur. Sans cela, le relâchement du bouton place le curseur et désélectionne aussitôt.
    /// Les clics suivants, champ déjà actif, placent le curseur normalement.</summary>
    private static void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var box = (TextBox)sender;
        if (box.IsKeyboardFocusWithin) return;

        box.Focus();
        e.Handled = true;
    }

    private static void OnLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        => Commit((TextBox)sender);

    /// <summary>Un changement de texte pendant que le champ a le focus vient de l'utilisateur ; sans le focus,
    /// c'est le ViewModel qui met le champ à jour, et ce n'est pas une saisie à valider.</summary>
    private static void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        var box = (TextBox)sender;
        if (!box.IsKeyboardFocused) return;

        box.SetValue(IsDirtyProperty, true);
        box.SetValue(IsOutOfRangePropertyKey, ComputeOutOfRange(box));
    }

    // ----- Validation -----

    /// <summary>Le texte résultant est-il un début de nombre valide ? Vide et « - » seul le sont : on est en
    /// train de taper.</summary>
    private static bool IsAcceptable(TextBox box, string proposed)
    {
        if (proposed.Length > MaxLength) return false;

        bool allowMinus = GetMinimum(box) < 0;
        for (int i = 0; i < proposed.Length; i++)
        {
            char c = proposed[i];
            if (c is >= '0' and <= '9') continue;
            if (c == '-' && i == 0 && allowMinus) continue;
            return false;
        }

        return true;
    }

    /// <summary>Le texte tapé est-il hors des bornes sans issue ? Ajouter des chiffres ne fait que déplacer la valeur
    /// dans l'intervalle [v·10^k, v·10^k + 10^k − 1] (en négatif après un « - »), avec k le nombre de chiffres encore
    /// permis : tant que l'un de ces intervalles touche la plage, on est en train de taper un nombre valide.
    /// Vide et « - » seul, bornes pas encore renseignées : rien à signaler.</summary>
    private static bool ComputeOutOfRange(TextBox box)
    {
        // Mêmes bornes entières que Clamp.
        double min = Math.Ceiling(GetMinimum(box));
        double max = Math.Floor(GetMaximum(box));
        if (max < min) return false;

        string text = box.Text.Trim();
        if (!TryParse(text, out double parsed)) return false;

        bool negative = text.StartsWith('-');
        double magnitude = Math.Abs(parsed);
        int remaining = Math.Max(0, MaxLength - text.Length);

        for (int k = 0; k <= remaining; k++)
        {
            double scale = Math.Pow(10, k);
            double low = magnitude * scale;
            double high = low + scale - 1;

            double from = negative ? -high : low;
            double to = negative ? -low : high;
            if (from <= max && to >= min) return false;
        }

        return true;
    }

    private static string Proposed(TextBox box, string input)
        => box.Text.Remove(box.SelectionStart, box.SelectionLength).Insert(box.SelectionStart, input);

    private static bool TryParse(string text, out double value)
        => double.TryParse(text.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);

    private static string Format(double value) => value.ToString("0", CultureInfo.InvariantCulture);

    private static double StepOf(TextBox box) => GetStep(box) >= 1 ? GetStep(box) : 1;

    /// <summary>Ramène une valeur dans les bornes, et sur le pas quand il y en a un.</summary>
    private static double Normalize(TextBox box, double value)
    {
        double min = GetMinimum(box);
        double step = GetStep(box);

        value = Clamp(box, Math.Round(value, MidpointRounding.AwayFromZero));

        if (step > 1 && double.IsFinite(min))
        {
            value = Clamp(box, min + Math.Round((value - min) / step, MidpointRounding.AwayFromZero) * step);
        }

        // Math.Round(-0,3) donne -0, que ToString écrit « -0 ».
        return value == 0 ? 0 : value;
    }

    private static double Clamp(TextBox box, double value)
    {
        // Le champ ne contient que des entiers : une borne à virgule (6142,5) ne doit pas laisser passer une
        // valeur à virgule. On prend l'entier le plus proche À L'INTÉRIEUR de la plage.
        double min = Math.Ceiling(GetMinimum(box));
        double max = Math.Floor(GetMaximum(box));

        // Un maximum inférieur au minimum, c'est des bornes pas encore renseignées : on ne borne pas par en haut.
        if (max >= min) value = Math.Min(value, max);
        return Math.Max(value, min);
    }

    // ----- Transmission au ViewModel -----

    /// <summary>Valide le contenu du champ : le normalise, l'écrit dans la source, puis relit la source pour
    /// afficher ce qu'elle a réellement retenu (elle peut avoir corrigé la valeur).</summary>
    private static void Commit(TextBox box)
    {
        StopArrowTimer(box);
        if (!(bool)box.GetValue(IsDirtyProperty)) return;

        BindingExpression? binding = box.GetBindingExpression(TextBox.TextProperty);

        try
        {
            if (!TryParse(box.Text, out double value))
            {
                // Champ vide ou « - » seul : on rend la valeur d'origine plutôt que d'imposer un 0 que
                // personne n'a demandé — un 0 écrit dans une limite de puissance peut être dangereux.
                binding?.UpdateTarget();
                return;
            }

            string text = Format(Normalize(box, value));
            if (box.Text != text) box.Text = text;

            if (binding is null) return;
            binding.UpdateSource();
            binding.UpdateTarget();
        }
        finally
        {
            // Après les écritures ci-dessus : elles déclenchent elles-mêmes TextChanged, pendant que le champ a le focus.
            box.SetValue(IsDirtyProperty, false);
            box.SetValue(IsOutOfRangePropertyKey, false);
        }
    }

    private static void Nudge(TextBox box, double delta)
    {
        double current = TryParse(box.Text, out double parsed) ? parsed : GetMinimum(box);
        box.Text = Format(Normalize(box, current + delta));
        box.CaretIndex = box.Text.Length;

        DispatcherTimer timer = ArrowTimers.GetValue(box, static b =>
        {
            var created = new DispatcherTimer { Interval = ArrowCommitDelay };
            created.Tick += (_, _) => Commit(b);
            return created;
        });

        // Redémarre le décompte à chaque appui.
        timer.Stop();
        timer.Start();
    }

    private static void StopArrowTimer(TextBox box)
    {
        if (ArrowTimers.TryGetValue(box, out DispatcherTimer? timer)) timer.Stop();
    }
}
