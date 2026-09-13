using System.Windows;
using System.Windows.Threading;

namespace PCPerfSuite.App;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            $"Une erreur inattendue est survenue :\n\n{e.Exception.Message}\n\nL'app va continuer, mais cette action a peut-être échoué.",
            "PCPerfSuite", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
