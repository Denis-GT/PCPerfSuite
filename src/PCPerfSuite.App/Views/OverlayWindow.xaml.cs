using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using PCPerfSuite.App.Interop;
using PCPerfSuite.App.ViewModels;
using PCPerfSuite.Core.Hardware.Displays;
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
    /// <summary>Replacements au plus après un changement d'échelle, par demande de placement : arriver sur un écran
    /// d'un autre DPI redimensionne la fenêtre, qui doit être replacée, sans jamais boucler.</summary>
    private const int MaxDpiReplacements = 3;

    private OverlayViewModel? _viewModel;
    private TopmostKeeper? _topmost;
    private IntPtr _hwnd;
    private int _dpiReplacements;

    public OverlayWindow()
    {
        InitializeComponent();
        ClickThroughWindow.Apply(this);

        // Sans Left/Top, la fenêtre est créée « à la position par défaut » et Windows la range en cascade à son premier
        // affichage, par-dessus le placement fait à Loaded : position explicite d'abord, puis placement en pixels une
        // fois la fenêtre affichée (ContentRendered).
        Left = 0;
        Top = 0;

        SourceInitialized += OnSourceInitialized;
        SizeChanged += (_, _) => Place();
        DpiChanged += OnDpiChanged;
        Loaded += OnLoaded;
        ContentRendered += (_, _) => Reposition();
        Closed += OnClosed;
    }

    /// <summary>Remet la fenêtre devant les autres fenêtres toujours au-dessus (barre des tâches...), à chaque rendu. En
    /// mode « écran du jeu », revérifie au passage l'écran du jeu : il a pu se déplacer sans repasser au premier plan.</summary>
    public void BringToTop()
    {
        _topmost?.BringToTop();
        if (_viewModel?.Screen.NoteForeground(IntPtr.Zero) == true) Reposition();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        _topmost = new TopmostKeeper(_hwnd);
        _topmost.ForegroundChanged += OnForegroundChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _viewModel = DataContext as OverlayViewModel;
        if (_viewModel is not null)
        {
            _viewModel.LayoutChanged += Reposition;
            _viewModel.Screen.NoteForeground(DisplayTopology.ForegroundWindow());
        }
        Reposition();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_viewModel is not null) _viewModel.LayoutChanged -= Reposition;
        _viewModel = null;
        if (_topmost is not null)
        {
            _topmost.ForegroundChanged -= OnForegroundChanged;
            _topmost.Dispose();
            _topmost = null;
        }
    }

    /// <summary>Mode « écran du jeu » : suit la fenêtre au premier plan (le filtre du shell est dans le ViewModel).</summary>
    private void OnForegroundChanged(IntPtr hwnd)
    {
        if (_viewModel?.Screen.NoteForeground(hwnd) == true) Reposition();
    }

    /// <summary>L'échelle de l'écran a changé, ou la fenêtre vient d'arriver sur un écran d'une autre échelle : les
    /// échelles lues ne valent plus, et WPF a redimensionné la fenêtre. On relit, on replace, et on s'arrête dès que la
    /// position ne bouge plus (<see cref="Place"/>) ou après quelques essais.</summary>
    private void OnDpiChanged(object sender, DpiChangedEventArgs e)
    {
        if (_viewModel is null || ++_dpiReplacements > MaxDpiReplacements) return;

        Dispatcher.BeginInvoke(() =>
        {
            _viewModel?.Screen.RefreshTopology();
            Place();
        }, DispatcherPriority.Loaded);
    }

    /// <summary>Nouvelle demande de placement (réglage, écran, jeu) : les replacements dus au DPI repartent de zéro.</summary>
    private void Reposition()
    {
        _dpiReplacements = 0;
        Place();
    }

    /// <summary>Place la fenêtre dans le coin choisi de l'écran retenu, en pixels physiques. On vise l'écran entier (et
    /// pas la zone de travail) : en jeu, la barre des tâches est masquée.</summary>
    private void Place()
    {
        if (_viewModel is null || _hwnd == IntPtr.Zero) return;

        DisplayMonitor? target = _viewModel.Screen.ResolveTarget();
        if (target is null)
        {
            PlaceOnPrimaryInDips();
            return;
        }

        // Échelle relue maintenant : l'utilisateur a pu la changer sur cet écran sans que l'overlay y soit.
        double scale = DisplayTopology.ReadScale(target.Handle) ?? target.Scale;
        OverlayAppearanceViewModel appearance = _viewModel.Appearance;
        PixelPoint position = OverlayPlacement.Compute(appearance.Anchor, appearance.MarginX, appearance.MarginY,
            target.Bounds, scale, ActualWidth, ActualHeight);

        if (WindowPlacement.GetPosition(_hwnd) == position)
        {
            // Position stable : un prochain changement d'échelle aura droit à ses propres replacements. Une vraie
            // boucle ne devient jamais stable, la limite tient toujours.
            _dpiReplacements = 0;
            return;
        }
        WindowPlacement.MoveTo(_hwnd, position);
    }

    /// <summary>Dernier recours quand aucun écran n'a pu être lu : l'ancien calcul, en DIP, sur l'écran principal
    /// supposé en (0,0).</summary>
    private void PlaceOnPrimaryInDips()
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
