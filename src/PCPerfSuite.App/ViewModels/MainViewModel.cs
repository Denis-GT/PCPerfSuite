using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PCPerfSuite.Core.Hardware;
using PCPerfSuite.Core.SystemInfo;

namespace PCPerfSuite.App.ViewModels;

public sealed record NavEntry(string Title, object ViewModel);

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly HardwareMonitorService _hardware = new();
    private readonly MonitoringViewModel _monitoring;
    private readonly FanCurvesViewModel _fans;
    private readonly GpuControlViewModel _gpuControl;
    private readonly OverlayViewModel _overlay;

    public bool IsElevated { get; } = ElevationHelper.IsAdministrator();
    public bool ShowElevationBanner => !IsElevated;

    // Exposées individuellement (plutôt qu'un seul "CurrentViewModel" swappé au clic) pour que
    // MainWindow puisse instancier chaque vue une seule fois et ne faire varier que sa Visibility :
    // un ContentControl relié à une seule propriété détruit et recrée la vue à chaque changement
    // d'onglet, ce qui remettait à zéro les graphiques (Sparkline) du Monitoring.
    public MonitoringViewModel Monitoring => _monitoring;
    public CleanupViewModel Cleanup { get; } = new();
    public StorageViewModel Storage { get; } = new();
    public SettingsViewModel Settings { get; } = new();
    public FanCurvesViewModel Fans => _fans;
    public GpuControlViewModel Gpu => _gpuControl;
    public OverlayViewModel Overlay => _overlay;

    public ObservableCollection<NavEntry> NavItems { get; }

    [ObservableProperty] private NavEntry? selectedNavItem;

    public MainViewModel()
    {
        _monitoring = new MonitoringViewModel(_hardware);
        _fans = new FanCurvesViewModel(_hardware, _monitoring);
        _gpuControl = new GpuControlViewModel(_monitoring);
        _overlay = new OverlayViewModel(_monitoring);

        NavItems = new ObservableCollection<NavEntry>
        {
            new("Monitoring", _monitoring),
            new("Nettoyage", Cleanup),
            new("Stockage", Storage),
            new("Paramètres Windows", Settings),
            new("Ventilateurs", _fans),
            new("GPU", _gpuControl),
            new("Overlay", _overlay),
        };

        SelectedNavItem = NavItems[0];
    }

    public void Dispose()
    {
        _fans.Dispose();
        _gpuControl.Dispose();
        _overlay.Dispose();
        _monitoring.Dispose();
        _hardware.Dispose();
    }
}
