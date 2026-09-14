using System.Windows;
using PCPerfSuite.App.Interop;
using PCPerfSuite.App.ViewModels;
using PCPerfSuite.Core.Overlay;

namespace PCPerfSuite.App.Views;

/// <summary>
/// Overlay "maison" : fenêtre transparente, sans bordure, toujours au-dessus et traversante pour la
/// souris. Elle s'affiche par-dessus les jeux en mode fenêtré ou sans bordure (la très grande majorité
/// des jeux récents) ; en plein écran exclusif, seul le canal RTSS peut dessiner, c'est une limite de
/// Windows et pas de l'app.
/// </summary>
public partial class OverlayWindow : Window
{
    private OverlayViewModel? _viewModel;

    public OverlayWindow()
    {
        InitializeComponent();
        ClickThroughWindow.Apply(this);

        SizeChanged += (_, _) => Reposition();
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _viewModel = DataContext as OverlayViewModel;
        if (_viewModel is not null) _viewModel.LayoutChanged += Reposition;
        Reposition();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_viewModel is not null) _viewModel.LayoutChanged -= Reposition;
        _viewModel = null;
    }

    /// <summary>Replace la fenêtre dans le coin choisi. On vise l'écran entier (et pas la zone de
    /// travail) : en jeu, la barre des tâches est masquée.</summary>
    private void Reposition()
    {
        if (_viewModel is null) return;

        double screenWidth = SystemParameters.PrimaryScreenWidth;
        double screenHeight = SystemParameters.PrimaryScreenHeight;
        double marginX = _viewModel.Appearance.MarginX;
        double marginY = _viewModel.Appearance.MarginY;

        Left = _viewModel.Appearance.Anchor switch
        {
            OverlayAnchor.TopLeft or OverlayAnchor.MiddleLeft or OverlayAnchor.BottomLeft => marginX,
            OverlayAnchor.TopCenter or OverlayAnchor.MiddleCenter or OverlayAnchor.BottomCenter
                => (screenWidth - ActualWidth) / 2,
            _ => screenWidth - ActualWidth - marginX,
        };

        Top = _viewModel.Appearance.Anchor switch
        {
            OverlayAnchor.TopLeft or OverlayAnchor.TopCenter or OverlayAnchor.TopRight => marginY,
            OverlayAnchor.MiddleLeft or OverlayAnchor.MiddleCenter or OverlayAnchor.MiddleRight
                => (screenHeight - ActualHeight) / 2,
            _ => screenHeight - ActualHeight - marginY,
        };
    }
}
