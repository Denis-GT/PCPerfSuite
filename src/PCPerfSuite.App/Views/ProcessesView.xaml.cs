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

    /// <summary>Fige la cible du menu contextuel à son ouverture. Le menu est un objet unique, partagé par
    /// toutes les lignes via le Setter du ItemContainerStyle, et il vit dans son propre popup : lier sa cible
    /// au DataContext de son PlacementTarget la rendrait solidaire d'un conteneur que la liste peut recycler
    /// pour une autre ligne pendant que le menu est ouvert — le menu changerait alors de processus sans que
    /// rien ne le montre, « Terminer… » compris. Ici la cible est copiée une fois et ne bouge plus, même si
    /// la ligne visée disparaît entre-temps.</summary>
    private void OnListContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        // WPF lève cet événement au clic droit même là où aucun menu n'est posé — barre de défilement, zone
        // vide sous la dernière ligne. Sans ce contrôle, on croirait qu'un menu s'est ouvert.
        if (e.OriginalSource is not DependencyObject source
            || ItemsControl.ContainerFromElement(List, source) is not ListBoxItem item
            || item.ContextMenu is not { } menu)
        {
            e.Handled = true;
            return;
        }

        menu.DataContext = item.DataContext;
    }

    private void OnListMouseLeave(object sender, MouseEventArgs e)
    {
        if (ViewModel is { } vm) vm.IsPointerOverList = false;
    }

    /// <summary>IsKeyboardFocusWithin plutôt que GotKeyboardFocus/LostKeyboardFocus : ces deux-là remontent
    /// depuis chaque ligne, donc une simple flèche du clavier — qui déplace le focus d'une ligne à l'autre —
    /// faisait croire à la liste qu'elle venait de le perdre, en plein milieu de la transition.</summary>
    private void OnListKeyboardFocusWithinChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (ViewModel is { } vm) vm.IsListFocused = (bool)e.NewValue;
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
