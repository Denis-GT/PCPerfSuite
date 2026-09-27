using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
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

        RestoreWindowBounds();

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
        // Shutdown() dans un finally : avec OnExplicitShutdown, une exception dans _tray.Dispose() ou
        // _viewModel.Dispose() laisserait sinon un processus sans fenêtre ni icône tourner indéfiniment
        // — exactement le « processus invisible » que OnExplicitShutdown cherche à éviter.
        try
        {
            SaveWindowBounds();
            _tray.Dispose();
            _viewModel.Dispose();
        }
        finally
        {
            Application.Current.Shutdown();
        }
    }

    /// <summary>Reprend la taille et la position de la dernière fermeture normale (U4 du rapport de
    /// revue), en s'assurant qu'elles retombent dans un écran encore branché : un portable débranché
    /// d'un second écran, ou un écran externe à une autre résolution, ne doit jamais rouvrir une fenêtre
    /// hors champ ou plus grande que l'écran (barre de titre inaccessible, fenêtre impossible à bouger).</summary>
    private void RestoreWindowBounds()
    {
        double screenWidth = SystemParameters.VirtualScreenWidth;
        double screenHeight = SystemParameters.VirtualScreenHeight;

        AppWindowSettings window = AppSettingsStore.Load().Window ?? new AppWindowSettings();
        if (window.Width is not { } width || window.Height is not { } height)
        {
            // Premier lancement : la taille par défaut du XAML (1280x820) dépasse la hauteur utile d'un
            // portable 1080p à 150 % (~720 px effectifs). On la borne à l'écran même sans réglage enregistré.
            Width = Math.Min(Width, screenWidth);
            Height = Math.Min(Height, screenHeight);
            return;
        }

        Width = Math.Clamp(width, MinWidth, screenWidth);
        Height = Math.Clamp(height, MinHeight, screenHeight);

        if (window.Left is { } left && window.Top is { } top)
        {
            double screenLeft = SystemParameters.VirtualScreenLeft;
            double screenTop = SystemParameters.VirtualScreenTop;
            // La fenêtre entière doit rester visible, pas seulement son coin haut-gauche.
            Left = Math.Clamp(left, screenLeft, screenLeft + screenWidth - Width);
            Top = Math.Clamp(top, screenTop, screenTop + screenHeight - Height);
            WindowStartupLocation = WindowStartupLocation.Manual;
        }

        if (window.IsMaximized) WindowState = WindowState.Maximized;
    }

    private void SaveWindowBounds()
    {
        // RestoreBounds donne la taille/position "normale" même si la fenêtre est actuellement réduite
        // ou agrandie : jamais les dimensions d'un état temporaire.
        Rect bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        AppSettingsStore.Update(settings =>
        {
            AppWindowSettings window = settings.Window ?? new AppWindowSettings();
            window.Width = bounds.Width;
            window.Height = bounds.Height;
            window.Left = bounds.Left;
            window.Top = bounds.Top;
            window.IsMaximized = WindowState == WindowState.Maximized;
            settings.Window = window;
        });
    }

    private void UpdateWindowShown() => _viewModel.IsWindowShown = IsVisible && WindowState != WindowState.Minimized;

    /// <summary>Attente maximale de l'icône de la zone de notification au démarrage de Windows.</summary>
    private static readonly TimeSpan TrayWait = TimeSpan.FromSeconds(45);

    /// <summary>Démarrage par Windows (voir StartupTask) : la fenêtre reste cachée, l'app vit dans la zone de
    /// notification, comme après un clic sur la croix. À l'ouverture de session, l'Explorateur n'a parfois pas fini de
    /// créer la barre des tâches : l'icône s'inscrit alors dès qu'elle apparaît (message TaskbarCreated, voir
    /// TrayIcon), et on l'attend un peu. Sans icône au bout du délai, la fenêtre s'ouvre : une app sans fenêtre ni
    /// icône serait introuvable.</summary>
    public void StartInTray()
    {
        if (_tray.IsAvailable) return;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        DateTime deadline = DateTime.UtcNow + TrayWait;
        timer.Tick += (_, _) =>
        {
            if (_tray.IsAvailable)
            {
                timer.Stop();
                return;
            }

            if (DateTime.UtcNow < deadline) return;

            timer.Stop();
            if (!IsVisible) RestoreFromTray();
        };
        timer.Start();
    }

    private void RestoreFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        WindowActivator.Restore(this);
    }

    /// <summary>Appelé quand une seconde copie de l'app est lancée (voir <see cref="App.OnStartup"/>
    /// et le mutex d'instance unique) : ramène cette fenêtre au premier plan au lieu de laisser
    /// l'utilisateur croire qu'un second exemplaire a démarré.</summary>
    public void ActivateFromOtherInstance() => RestoreFromTray();

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
