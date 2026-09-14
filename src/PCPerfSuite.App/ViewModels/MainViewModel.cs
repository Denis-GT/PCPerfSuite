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

    public ObservableCollection<NavEntry> NavItems { get; }

    [ObservableProperty] private NavEntry? selectedNavItem;
    [ObservableProperty] private object? currentViewModel;

    public MainViewModel()
    {
        _monitoring = new MonitoringViewModel(_hardware);
        _fans = new FanCurvesViewModel(_hardware, _monitoring);
        _gpuControl = new GpuControlViewModel(_monitoring);
        _overlay = new OverlayViewModel(_monitoring);

        var cleanup = new CleanupViewModel();
        var storage = new StorageViewModel();
        var settings = new SettingsViewModel();

        NavItems = new ObservableCollection<NavEntry>
        {
            new("Monitoring", _monitoring),
            new("Nettoyage", cleanup),
            new("Stockage", storage),
            new("Paramètres Windows", settings),
            new("Ventilateurs", _fans),
            new("GPU", _gpuControl),
            new("Overlay", _overlay),
        };

        SelectedNavItem = NavItems[0];
        CurrentViewModel = _monitoring;
    }

    partial void OnSelectedNavItemChanged(NavEntry? value)
    {
        if (value is not null) CurrentViewModel = value.ViewModel;
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
