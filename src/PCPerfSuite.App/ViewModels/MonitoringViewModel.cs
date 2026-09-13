using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using PCPerfSuite.Core.Hardware;

namespace PCPerfSuite.App.ViewModels;

public sealed partial class MonitoringViewModel : ObservableObject, IDisposable
{
    private readonly HardwareMonitorService _hardware = new();
    private readonly DispatcherTimer _timer;

    public event Action<HardwareSnapshot>? SnapshotUpdated;

    [ObservableProperty] private string cpuName = "…";
    [ObservableProperty] private double cpuLoad;
    [ObservableProperty] private double? cpuTemp;
    [ObservableProperty] private double? cpuPower;
    [ObservableProperty] private double? cpuClock;

    [ObservableProperty] private bool hasGpu;
    [ObservableProperty] private string gpuName = "…";
    [ObservableProperty] private string gpuVendor = "";
    [ObservableProperty] private double gpuLoad;
    [ObservableProperty] private double? gpuTemp;
    [ObservableProperty] private double? gpuHotspot;
    [ObservableProperty] private double? gpuCoreClock;
    [ObservableProperty] private double? gpuMemClock;
    [ObservableProperty] private double? gpuPower;
    [ObservableProperty] private double? gpuVramUsed;
    [ObservableProperty] private double? gpuVramTotal;
    [ObservableProperty] private double gpuVramLoad;
    [ObservableProperty] private double? gpuFanRpm;

    [ObservableProperty] private double memLoad;
    [ObservableProperty] private double? memUsedGb;
    [ObservableProperty] private double? memTotalGb;

    [ObservableProperty] private string motherboardName = "…";
    [ObservableProperty] private double? motherboardTemp;

    public ObservableCollectionEx<FanReading> Fans { get; } = new();

    [ObservableProperty] private string? errorMessage;

    public MonitoringViewModel()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();

        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        try
        {
            HardwareSnapshot snapshot = await Task.Run(() => _hardware.GetSnapshot());
            Apply(snapshot);
            ErrorMessage = null;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Lecture des capteurs impossible : {ex.Message}";
        }
    }

    private void Apply(HardwareSnapshot s)
    {
        CpuName = s.Cpu.Name;
        CpuLoad = s.Cpu.LoadPercent ?? 0;
        CpuTemp = s.Cpu.PackageTempC;
        CpuPower = s.Cpu.PowerWatts;
        CpuClock = s.Cpu.MaxClockMhz;

        HasGpu = s.Gpu is not null;
        if (s.Gpu is { } gpu)
        {
            GpuName = gpu.Name;
            GpuVendor = gpu.Vendor;
            GpuLoad = gpu.LoadPercent ?? 0;
            GpuTemp = gpu.CoreTempC;
            GpuHotspot = gpu.HotSpotTempC;
            GpuCoreClock = gpu.CoreClockMhz;
            GpuMemClock = gpu.MemoryClockMhz;
            GpuPower = gpu.PowerWatts;
            GpuVramUsed = gpu.VramUsedMb;
            GpuVramTotal = gpu.VramTotalMb;
            GpuVramLoad = gpu.VramUsedMb is { } used && gpu.VramTotalMb is { } total and > 0
                ? used / total * 100 : 0;
            GpuFanRpm = gpu.FanRpm;
        }

        MemLoad = s.Memory.LoadPercent ?? 0;
        MemUsedGb = s.Memory.UsedGb;
        MemTotalGb = s.Memory.TotalGb;

        MotherboardName = s.Motherboard.Name;
        MotherboardTemp = s.Motherboard.SystemTempC;

        Fans.ReplaceAll(s.Fans);

        SnapshotUpdated?.Invoke(s);
    }

    public void Dispose()
    {
        _timer.Stop();
        _hardware.Dispose();
    }
}
