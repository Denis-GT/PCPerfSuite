using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PCPerfSuite.App.Controls;

/// <summary>
/// Valide un champ texte à la touche Entrée. Sans ça, WPF n'écrit la valeur dans le ViewModel qu'à la
/// perte du focus, ce qui donne l'impression qu'une saisie "ne prend pas".
/// </summary>
public static class TextBoxBehaviors
{
    public static readonly DependencyProperty CommitOnEnterProperty = DependencyProperty.RegisterAttached(
        "CommitOnEnter", typeof(bool), typeof(TextBoxBehaviors), new PropertyMetadata(false, OnCommitOnEnterChanged));

    public static void SetCommitOnEnter(DependencyObject element, bool value)
        => element.SetValue(CommitOnEnterProperty, value);

    public static bool GetCommitOnEnter(DependencyObject element)
        => (bool)element.GetValue(CommitOnEnterProperty);

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
}
