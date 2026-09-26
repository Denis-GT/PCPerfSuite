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

    /// <summary>Donne le focus au champ, texte sélectionné, quand il devient visible : un champ de renommage qui
    /// apparaît sur place doit pouvoir être saisi sans cliquer dedans.</summary>
    public static readonly DependencyProperty FocusWhenVisibleProperty = DependencyProperty.RegisterAttached(
        "FocusWhenVisible", typeof(bool), typeof(TextBoxBehaviors), new PropertyMetadata(false, OnFocusWhenVisibleChanged));

    public static void SetFocusWhenVisible(DependencyObject element, bool value)
        => element.SetValue(FocusWhenVisibleProperty, value);

    public static bool GetFocusWhenVisible(DependencyObject element)
        => (bool)element.GetValue(FocusWhenVisibleProperty);

    private static void OnFocusWhenVisibleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box) return;

        box.IsVisibleChanged -= OnVisibleChanged;
        if (e.NewValue is true) box.IsVisibleChanged += OnVisibleChanged;
    }

    private static void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true || sender is not TextBox box) return;

        // Après la mise en page : le champ vient d'être rendu visible et ne peut pas encore prendre le focus.
        box.Dispatcher.BeginInvoke(() =>
        {
            box.Focus();
            box.SelectAll();
        }, System.Windows.Threading.DispatcherPriority.Input);
    }
}
