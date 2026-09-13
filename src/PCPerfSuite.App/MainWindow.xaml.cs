using System.Windows;
using PCPerfSuite.App.Interop;
using PCPerfSuite.App.ViewModels;

namespace PCPerfSuite.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        Closed += (_, _) => _viewModel.Dispose();
        WindowBackdrop.Apply(this);
    }
}
