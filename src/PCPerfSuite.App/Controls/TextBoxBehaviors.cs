using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PCPerfSuite.App.Controls;

/// <summary>
/// Deux compléments aux champs texte : valider à la touche Entrée, et régler un nombre par pas
/// (flèches du clavier, molette, ou les deux flèches du style UnitTextBox).
/// </summary>
public static class TextBoxBehaviors
{
    /// <summary>
    /// Valide un champ texte à la touche Entrée. Sans ça, WPF n'écrit la valeur dans le ViewModel qu'à la
    /// perte du focus, ce qui donne l'impression qu'une saisie "ne prend pas".
    /// </summary>
    public static readonly DependencyProperty CommitOnEnterProperty = DependencyProperty.RegisterAttached(
        "CommitOnEnter", typeof(bool), typeof(TextBoxBehaviors), new PropertyMetadata(false, OnCommitOnEnterChanged));

    /// <summary>
    /// Active le réglage par pas sur un champ numérique : flèches Haut/Bas, molette, et les commandes
    /// <see cref="StepUpCommand"/> / <see cref="StepDownCommand"/> que déclenchent les deux petites flèches
    /// du gabarit. La valeur passe par la liaison habituelle : les bornes et la persistance restent
    /// entièrement au ViewModel, ce comportement ne fait que proposer un nombre.
    /// </summary>
    public static readonly DependencyProperty StepProperty = DependencyProperty.RegisterAttached(
        "Step", typeof(bool), typeof(TextBoxBehaviors), new PropertyMetadata(false, OnStepChanged));

    public static readonly RoutedUICommand StepUpCommand = new("Augmenter", nameof(StepUpCommand), typeof(TextBoxBehaviors));
    public static readonly RoutedUICommand StepDownCommand = new("Diminuer", nameof(StepDownCommand), typeof(TextBoxBehaviors));

    public static void SetCommitOnEnter(DependencyObject element, bool value)
        => element.SetValue(CommitOnEnterProperty, value);

    public static bool GetCommitOnEnter(DependencyObject element)
        => (bool)element.GetValue(CommitOnEnterProperty);

    public static void SetStep(DependencyObject element, bool value)
        => element.SetValue(StepProperty, value);

    public static bool GetStep(DependencyObject element)
        => (bool)element.GetValue(StepProperty);

    private static void OnCommitOnEnterChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box) return;

        box.KeyDown -= OnKeyDown;
        if (e.NewValue is true) box.KeyDown += OnKeyDown;
    }

    private static void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box) return;

        box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        e.Handled = true;
    }

    private static void OnStepChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box) return;

        box.PreviewKeyDown -= OnStepKeyDown;
        box.PreviewMouseWheel -= OnStepMouseWheel;
        // Retirer seulement les nôtres : le champ peut porter d'autres liaisons de commande.
        for (int i = box.CommandBindings.Count - 1; i >= 0; i--)
        {
            ICommand bound = box.CommandBindings[i].Command;
            if (bound == StepUpCommand || bound == StepDownCommand) box.CommandBindings.RemoveAt(i);
        }

        if (e.NewValue is not true) return;

        box.PreviewKeyDown += OnStepKeyDown;
        box.PreviewMouseWheel += OnStepMouseWheel;
        box.CommandBindings.Add(new CommandBinding(StepUpCommand, (s, _) => Step(s as TextBox, +1)));
        box.CommandBindings.Add(new CommandBinding(StepDownCommand, (s, _) => Step(s as TextBox, -1)));
    }

    private static void OnStepKeyDown(object sender, KeyEventArgs e)
    {
        int direction = e.Key switch { Key.Up => +1, Key.Down => -1, _ => 0 };
        if (direction == 0) return;

        Step(sender as TextBox, direction);
        e.Handled = true;
    }

    /// <summary>La molette ne règle le champ que s'il a le focus : sinon, faire défiler la page en
    /// passant au-dessus changerait la cadence sans qu'on l'ait demandé.</summary>
    private static void OnStepMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not TextBox box || !box.IsKeyboardFocused || e.Delta == 0) return;

        Step(box, Math.Sign(e.Delta));
        e.Handled = true;
    }

    private static void Step(TextBox? box, int direction)
    {
        if (box is null) return;
        if (!int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out int current)) return;

        long stepped = (long)current + (long)direction * StepFor(current, direction);
        box.Text = Math.Clamp(stepped, int.MinValue, int.MaxValue).ToString(CultureInfo.CurrentCulture);
        box.CaretIndex = box.Text.Length;

        // Écriture immédiate : la liaison est en LostFocus, et on attend de voir l'effet du pas tout de
        // suite. Le ViewModel ramène la valeur dans ses bornes et la renotifie s'il l'a corrigée.
        box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
    }

    /// <summary>Pas proportionnel à l'ordre de grandeur : 50 en dessous de la seconde, 500 au-delà.
    /// Sans ça, passer de 100 à 5000 ms demanderait une centaine de clics.</summary>
    private static int StepFor(int current, int direction)
    {
        // Au passage de la frontière vers le bas, on prend le pas fin pour ne pas sauter par-dessus.
        int reference = direction < 0 ? current - 1 : current;
        return reference < 1000 ? 50 : 500;
    }
}
