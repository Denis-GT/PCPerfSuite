using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using PCPerfSuite.App.Utils;
using PCPerfSuite.Core.SystemInfo;

namespace PCPerfSuite.App;

public partial class App : System.Windows.Application
{
    /// <summary>Identifie l'instance unique de l'app pour l'utilisateur courant. Nommé par session
    /// (préfixe Local\) pour ne jamais se heurter à une autre session du même utilisateur (Bureau à
    /// distance) ni exiger de droits particuliers.</summary>
    private const string InstanceMutexName = @"Local\PCPerfSuite.SingleInstance";

    /// <summary>Signalé par une seconde instance pour demander à la première de se montrer. Un
    /// événement plutôt qu'un canal nommé : il suffit de réveiller un thread d'attente, sans échanger
    /// de données.</summary>
    private const string ActivateEventName = @"Local\PCPerfSuite.ActivateRequest";

    private Mutex? _instanceMutex;
    /// <summary>Au-delà, plus de boîte de dialogue : une erreur qui se répète (liaison de données, rendu
    /// d'un contrôle) en produirait une par image, au point de rendre l'app impossible à fermer. Elles
    /// continuent d'être journalisées.</summary>
    private const int MaxDialogs = 3;

    /// <summary>Erreurs déjà montrées, par type et première ligne de pile : la même erreur ne dérange
    /// l'utilisateur qu'une fois.</summary>
    private readonly HashSet<string> _reported = new();

    private int _dialogsShown;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out bool createdNew);
        if (!createdNew)
        {
            // Une instance tourne déjà (icône de la zone de notification ou fenêtre ouverte) : on lui
            // demande de se montrer et on quitte, plutôt que de faire cohabiter deux régulateurs de
            // ventilateurs ou deux overclocks qui s'écraseraient l'un l'autre.
            try
            {
                using var activateEvent = EventWaitHandle.OpenExisting(ActivateEventName);
                activateEvent.Set();
            }
            catch (WaitHandleCannotBeOpenedException) { /* la première instance vient de se fermer entre-temps */ }

            _instanceMutex.Dispose();
            _instanceMutex = null;
            Shutdown();
            return;
        }

        Exit += (_, _) => { _instanceMutex?.ReleaseMutex(); _instanceMutex?.Dispose(); };

        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // Les liaisons de données fautives que WPF ne juge pas dignes d'une exception n'apparaissaient
        // nulle part : la valeur restait vide à l'écran, sans explication ni trace dans le rapport de bug.
        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Listeners.Add(new BindingErrorListener());
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;

        // Le thread d'interface n'est pas le seul à travailler : la boucle de relevé a le sien, et les
        // ViewModels partent en Task.Run. Une exception y passait jusqu'ici sans laisser la moindre trace.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            CrashLog.Record(args.ExceptionObject as Exception, "thread de fond");

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            CrashLog.Record(args.Exception, "tâche non observée");
            args.SetObserved();
        };

        // Lancée par la tâche de démarrage de Windows (voir StartupTask) : l'app démarre dans la zone de notification.
        bool launchedByWindows = e.Args.Contains(StartupTask.LaunchArgument, StringComparer.OrdinalIgnoreCase);

        try
        {
            var window = new MainWindow();
            MainWindow = window;
            if (launchedByWindows) window.StartInTray();
            else window.Show();

            StartActivationListener(window);
        }
        catch (Exception ex)
        {
            // La fenêtre se créait avant par StartupUri, et une erreur ici arrêtait l'app. Sans ce filet, le
            // gestionnaire d'erreurs ci-dessus l'avalerait et, l'arrêt étant explicite, un processus invisible
            // resterait en vie sans fenêtre ni icône pour le quitter.
            CrashLog.Record(ex, "démarrage");
            MessageBox.Show(
                $"PCPerfSuite n'a pas pu démarrer :\n\n{ex.Message}\n\nLe détail est enregistré dans :\n{CrashLog.FilePath}",
                "PCPerfSuite", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <summary>Écoute, sur un thread dédié, les demandes d'activation envoyées par une seconde
    /// instance (voir <see cref="OnStartup"/>) et ramène la fenêtre existante au premier plan.</summary>
    private void StartActivationListener(MainWindow window)
    {
        var activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        Exit += (_, _) => activateEvent.Dispose();

        var thread = new Thread(() =>
        {
            while (true)
            {
                try { activateEvent.WaitOne(); }
                catch (ObjectDisposedException) { return; }

                Dispatcher.Invoke(() =>
                {
                    try { window.ActivateFromOtherInstance(); }
                    catch (Exception ex) { CrashLog.Record(ex, "activation seconde instance"); }
                });
            }
        })
        { IsBackground = true, Name = "PCPerfSuite.ActivationListener" };
        thread.Start();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        CrashLog.Record(e.Exception, "interface");

        string key = $"{e.Exception.GetType().FullName}|{e.Exception.StackTrace?.Split('\n').FirstOrDefault()}";
        if (!_reported.Add(key) || _dialogsShown >= MaxDialogs) return;
        _dialogsShown++;

        MessageBox.Show(
            $"Une erreur inattendue est survenue :\n\n{e.Exception.Message}\n\nL'app va continuer, mais cette action a " +
            $"peut-être échoué. Le détail est enregistré dans :\n{CrashLog.FilePath}",
            "PCPerfSuite", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
