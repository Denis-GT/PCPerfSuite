using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using PCPerfSuite.App.Interop;
using PCPerfSuite.App.ViewModels;
using PCPerfSuite.Core.PowerSettings;

namespace PCPerfSuite.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();
    private readonly TrayIcon _tray;

    /// <summary>Vrai dès qu'une vraie sortie est engagée, pour que <see cref="OnClosing"/> laisse
    /// passer la fermeture au lieu de masquer la fenêtre une fois de plus.</summary>
    private bool _isQuitting;

    /// <summary>Le message d'accueil n'est tenté qu'une fois par session : inutile de relire le
    /// fichier de réglages à chaque fermeture de fenêtre.</summary>
    private bool _trayHintHandled;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;

        _tray = new TrayIcon("PCPerfSuite");
        _tray.Activated += RestoreFromTray;
        _tray.MenuRequested += OpenTrayMenu;

        Closing += OnClosing;
        Closed += OnClosed;

        // Au retour dans la fenêtre (depuis le navigateur ou un installeur), l'état des logiciels externes est relu.
        Activated += (_, _) => _viewModel.OnWindowActivated();

        // Le clignotement s'arrête quand la fenêtre est rangée dans la zone de notification ou réduite : personne ne
        // le verrait, et l'animation entretiendrait le rendu pour rien.
        IsVisibleChanged += (_, _) => UpdateWindowShown();
        StateChanged += (_, _) => UpdateWindowShown();
        UpdateWindowShown();

        // Fermeture de session Windows : ne surtout pas annuler la fermeture, sinon l'arrêt du PC
        // reste bloqué sur PCPerfSuite.
        Application.Current.SessionEnding += (_, _) => _isQuitting = true;

        // Filet : retire l'icône même si l'app s'arrête par un chemin qui ne passe pas par la fenêtre.
        Application.Current.Exit += (_, _) => _tray.Dispose();

        WindowBackdrop.Apply(this);
    }

    /// <summary>La croix masque la fenêtre au lieu de quitter : le monitoring, les courbes de
    /// ventilation et l'overlay en jeu continuent de tourner, ce qui est tout l'intérêt de l'app une
    /// fois réglée. On lit le réglage sur le ViewModel vivant, pour qu'un changement dans l'onglet
    /// Paramètres prenne effet immédiatement, sans relire le fichier à chaque fermeture.</summary>
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_isQuitting || !_viewModel.AppSettings.MinimizeToTrayOnClose) return;

        // Sans icône réellement inscrite auprès du shell, masquer la fenêtre la rendrait
        // irrécupérable : on laisse alors la fermeture suivre son cours normal.
        if (!_tray.IsAvailable) return;

        e.Cancel = true;
        Hide();
        ShowTrayHintOnce();
    }

    /// <summary>Point de sortie unique : l'ordre de démontage de <see cref="MainViewModel.Dispose"/>
    /// (ventilateurs repassés en automatique avant que NVAPI ne soit déchargé, créneau RTSS libéré)
    /// s'exécute ici et une seule fois, une fenêtre ne déclenchant Closed qu'au plus une fois.</summary>
    private void OnClosed(object? sender, EventArgs e)
    {
        _tray.Dispose();
        _viewModel.Dispose();
        Application.Current.Shutdown();
    }

    private void UpdateWindowShown() => _viewModel.IsWindowShown = IsVisible && WindowState != WindowState.Minimized;

    private void RestoreFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        WindowActivator.Restore(this);
    }

    private void OpenTrayMenu(Point screenPoint)
    {
        if (TryFindResource("TrayMenu") is not ContextMenu menu) return;

        // Sans fenêtre au premier plan à nous, Windows ne referme pas le menu quand on clique à côté.
        WindowActivator.SetForeground(_tray.Handle);

        // Le menu se place par rapport à l'écran (pas de PlacementTarget) : la fenêtre est masquée.
        Point position = _tray.DeviceToDip(screenPoint);
        menu.Placement = PlacementMode.AbsolutePoint;
        menu.HorizontalOffset = position.X;
        menu.VerticalOffset = position.Y;
        menu.IsOpen = true;
    }

    private void OnTrayShow(object sender, RoutedEventArgs e) => RestoreFromTray();

    private void OnTrayQuit(object sender, RoutedEventArgs e)
    {
        _isQuitting = true;

        // Retirée avant la fermeture : le démontage du matériel prend un instant, autant que l'icône
        // disparaisse au clic plutôt qu'une seconde plus tard.
        _tray.Dispose();
        Close();
    }

    /// <summary>Une fenêtre qui disparaît sans se fermer déroute la première fois : on l'explique une
    /// seule fois, à la première fermeture.</summary>
    private void ShowTrayHintOnce()
    {
        if (_trayHintHandled) return;
        _trayHintHandled = true;

        AppSettings settings = AppSettingsStore.Load();
        AppWindowSettings window = settings.Window ?? new AppWindowSettings();
        if (window.TrayHintShown) return;

        _tray.ShowHint("PCPerfSuite continue en arrière-plan",
            "Le monitoring et l'overlay tournent toujours. Clic sur l'icône pour rouvrir la fenêtre, clic droit pour quitter.");

        window.TrayHintShown = true;
        settings.Window = window;
        AppSettingsStore.Save(settings);
    }
}
