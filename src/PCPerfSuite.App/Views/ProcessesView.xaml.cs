using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PCPerfSuite.App.ViewModels;

namespace PCPerfSuite.App.Views;

/// <summary>
/// Code-behind volontairement présent : tout ce qui suit relève de la vue seule (position du pointeur, focus
/// clavier, sélection au clic droit) et n'a aucun sens dans le ViewModel, qui ne connaît pas la souris.
/// </summary>
public partial class ProcessesView : UserControl
{
    public ProcessesView()
    {
        InitializeComponent();
    }

    private ProcessesViewModel? ViewModel => DataContext as ProcessesViewModel;

    // Le classement se fige tant que le pointeur est sur la liste : c'est ce qui permet de viser une ligne
    // sans qu'elle se dérobe. Les valeurs, elles, continuent de se mettre à jour.
    private void OnListMouseEnter(object sender, MouseEventArgs e)
    {
        if (ViewModel is { } vm) vm.IsPointerOverList = true;
    }

    /// <summary>Un menu contextuel s'ouvre dans son propre popup : le pointeur quitte la liste et le focus
    /// clavier part avec lui, donc les deux verrous qui empêchent les lignes de bouger tombent au moment
    /// précis où un menu « Terminer… » est ouvert. On les remplace par celui-ci le temps du menu.</summary>
    private void OnListContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (ViewModel is { } vm) vm.IsContextMenuOpen = true;
    }

    private void OnListContextMenuClosing(object sender, ContextMenuEventArgs e)
    {
        if (ViewModel is { } vm) vm.IsContextMenuOpen = false;
    }

    private void OnListMouseLeave(object sender, MouseEventArgs e)
    {
        if (ViewModel is { } vm) vm.IsPointerOverList = false;
    }

    private void OnListGotFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (ViewModel is { } vm) vm.IsListFocused = true;
    }

    private void OnListLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (ViewModel is { } vm) vm.IsListFocused = false;
    }

    /// <summary>Un clic droit hors de la sélection la remplace : ce qui est surligné doit être exactement ce
    /// que le menu contextuel va terminer.</summary>
    private void OnListRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(List, (DependencyObject)e.OriginalSource) is not ListBoxItem item) return;
        if (item.IsSelected) return;

        List.SelectedItems.Clear();
        item.IsSelected = true;
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || ViewModel is not { } vm) return;

        if (vm.TerminateSelectionCommand.CanExecute(null)) vm.TerminateSelectionCommand.Execute(null);
        e.Handled = true;
    }

    /// <summary>Ctrl+F place le curseur dans la recherche, comme partout ailleurs sous Windows.</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Key == Key.F5 && ViewModel is { } viewModel)
        {
            viewModel.RefreshNowCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key != Key.F || (Keyboard.Modifiers & ModifierKeys.Control) == 0) return;

        SearchBox.Focus();
        SearchBox.SelectAll();
        e.Handled = true;
    }
}
