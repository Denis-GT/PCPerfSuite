using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using PCPerfSuite.App.Utils;
using PCPerfSuite.Core.Benchmark.Worker;
using PCPerfSuite.Core.Safety;
using PCPerfSuite.Core.Safety.Events;
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

        // Modes secondaires de l'exe (décision D2) : lus avant le mutex d'instance unique et avant toute lecture du
        // dossier de données. Un argument inconnu est refusé sans fenêtre ; le worker de bench ne prend ni mutex (il
        // cohabite avec l'app qui l'a lancé), ni fenêtre, ni reprise au démarrage.
        SecondaryMode mode = SecondaryModes.Parse(e.Args);
        if (mode.Kind == SecondaryModeKind.Refused)
        {
            Shutdown(SecondaryModes.RefusedExitCode);
            return;
        }
        if (mode.Kind == SecondaryModeKind.BenchWorker)
        {
            RunBenchWorker(mode.PipeName!);
            return;
        }

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
        {
            CrashLog.Record(args.ExceptionObject as Exception, "thread de fond");
            // L'app va mourir : ses opérations en cours sont closes ici, sinon le prochain arrêt de Windows, même des
            // heures plus tard, leur serait imputé au lancement suivant.
            if (args.IsTerminating) SessionJournal.Current.AbandonAll();
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            CrashLog.Record(args.Exception, "tâche non observée");
            args.SetObserved();
        };

        // Lancée par la tâche de démarrage de Windows (voir StartupTask) : l'app démarre dans la zone de notification.
        bool launchedByWindows = mode.LaunchedByWindows;

        // Reprise des opérations restées en cours au dernier arrêt (journal de session), avant toute fenêtre et avant
        // que MainViewModel ne réapplique des réglages. Elle lit AppDataPaths.Current, qui fige le dossier de données :
        // le choix de la racine portable (#13) et les modes secondaires de l'exe (#10) restent au-dessus.
        StartupRecoveryReport recovery = RunStartupRecovery();

        try
        {
            var window = new MainWindow(recovery);
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

    /// <summary>Mode worker de bench (<c>--bench-worker &lt;tube&gt;</c>) : aucune fenêtre, aucun réglage lu, juste le
    /// tube. Un thread dédié fait tout le travail ; l'app quitte ensuite avec le code de sortie du worker. Ses messages
    /// et un plantage éventuel vont au journal des erreurs, jamais dans une boîte de dialogue (personne ne la verrait,
    /// et elle garderait la charge en vie).</summary>
    private void RunBenchWorker(string pipeName)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) => CrashLog.Record(args.ExceptionObject as Exception, "worker de bench");
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            CrashLog.Record(args.Exception, "worker de bench, tâche non observée");
            args.SetObserved();
        };

        var thread = new Thread(() =>
        {
            int exitCode;
            try
            {
                exitCode = BenchWorkerHost.Run(pipeName, message => CrashLog.RecordMessage(message, "worker de bench", surfaceAsLastError: false));
            }
            catch (Exception ex)
            {
                CrashLog.Record(ex, "worker de bench");
                exitCode = BenchWorkerHost.ExitCrashed;
            }
            Dispatcher.InvokeAsync(() => Shutdown(exitCode));
        })
        { IsBackground = true, Name = "PCPerfSuite.BenchWorker" };
        thread.Start();
    }

    /// <summary>Reprise au lancement (<see cref="StartupRecovery"/>), sans fenêtre : son bilan va au diagnostic. Ne
    /// bloque jamais le lancement : une erreur est journalisée et l'app démarre quand même.</summary>
    private static StartupRecoveryReport RunStartupRecovery()
    {
        try
        {
            var recovery = new StartupRecovery(
                SessionJournal.Current,
                StartupRecoveryHandlers.Create(),
                (since, cancellationToken) => SystemEventReader.ReadSince(since, SystemEventReader.RecoveryKinds, cancellationToken),
                TimeProvider.System,
                (exception, origin) => CrashLog.Record(exception, origin));
            return recovery.Run();
        }
        catch (Exception ex)
        {
            CrashLog.Record(ex, "reprise au démarrage");
            return StartupRecoveryReport.Failed(ex.Message);
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
