using System.Windows;
using System.Windows.Threading;
using PCPerfSuite.App.Utils;
using PCPerfSuite.Core.SystemInfo;

namespace PCPerfSuite.App;

public partial class App : System.Windows.Application
{
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

        DispatcherUnhandledException += OnDispatcherUnhandledException;

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
