using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCPerfSuite.App.Utils;
using PCPerfSuite.Core.Hardware;
using PCPerfSuite.Core.PowerSettings;

namespace PCPerfSuite.App.ViewModels;

public sealed record RefreshRateOption(string Label, int Milliseconds);

public sealed partial class DiskItemViewModel : ObservableObject
{
    private readonly DiskHealthService _health;

    public string Identifier { get; }

    [ObservableProperty] private string name = "…";
    [ObservableProperty] private double usedPercent;
    [ObservableProperty] private double? readRateBytesPerSecond;
    [ObservableProperty] private double? writeRateBytesPerSecond;
    [ObservableProperty] private double? temperatureC;
    [ObservableProperty] private double? remainingLifePercent;

    [ObservableProperty] private bool isTesting;
    [ObservableProperty] private DiskHealthStatus? healthStatus;
    [ObservableProperty] private string? healthSummary;

    public string ReadRateDisplay => ByteFormatter.FormatRate(ReadRateBytesPerSecond);
    public string WriteRateDisplay => ByteFormatter.FormatRate(WriteRateBytesPerSecond);
    public string TemperatureDisplay => TemperatureC is { } t ? $"{t:0.#} °C" : "--";
    public string RemainingLifeDisplay => RemainingLifePercent is { } l ? $"{l:0}%" : "--";
    public string TestButtonLabel => IsTesting ? "Test en cours…" : "Tester l'état";

    public DiskItemViewModel(DiskHealthService health, string identifier)
    {
        _health = health;
        Identifier = identifier;
    }

    public void Apply(DiskSnapshot s)
    {
        Name = s.Name;
        UsedPercent = s.UsedPercent ?? 0;
        ReadRateBytesPerSecond = s.ReadRateBytesPerSecond;
        WriteRateBytesPerSecond = s.WriteRateBytesPerSecond;
        TemperatureC = s.TemperatureC;
        RemainingLifePercent = s.RemainingLifePercent;
    }

    partial void OnReadRateBytesPerSecondChanged(double? value) => OnPropertyChanged(nameof(ReadRateDisplay));
    partial void OnWriteRateBytesPerSecondChanged(double? value) => OnPropertyChanged(nameof(WriteRateDisplay));
    partial void OnTemperatureCChanged(double? value) => OnPropertyChanged(nameof(TemperatureDisplay));
    partial void OnRemainingLifePercentChanged(double? value) => OnPropertyChanged(nameof(RemainingLifeDisplay));
    partial void OnIsTestingChanged(bool value) => OnPropertyChanged(nameof(TestButtonLabel));

    [RelayCommand]
    private async Task TestAsync()
    {
        IsTesting = true;
        HealthSummary = null;
        try
        {
            DiskHealthReport report = await _health.CheckAsync(Name);
            HealthStatus = report.Status;
            HealthSummary = BuildSummary(report);
        }
        catch (Exception ex)
        {
            HealthStatus = DiskHealthStatus.Unknown;
            HealthSummary = $"Échec du test : {ex.Message}";
        }
        finally
        {
            IsTesting = false;
        }
    }

    private static string BuildSummary(DiskHealthReport r)
    {
        if (r.ErrorMessage is not null) return r.ErrorMessage;

        string headline = r.Status switch
        {
            DiskHealthStatus.Healthy => "Sain (Windows Storage Management)",
            DiskHealthStatus.Warning => "Attention",
            DiskHealthStatus.Unhealthy => "Défaillance imminente — sauvegarde recommandée",
            _ => "État inconnu",
        };

        var details = new List<string>();
        if (r.PowerOnHours is { } hours) details.Add($"{hours} h sous tension");
        if (r.WearPercent is { } wear) details.Add($"{wear:0}% d'usure");
        if (r.ReadErrorsTotal is { } re && r.WriteErrorsTotal is { } we && re + we > 0)
            details.Add($"{re + we} erreur(s) E/S cumulée(s)");

        return details.Count > 0 ? $"{headline} — {string.Join(", ", details)}" : headline;
    }
}

public sealed partial class MonitoringViewModel : ObservableObject, IDisposable
{
    private readonly HardwareMonitorService _hardware;
    private readonly DiskHealthService _diskHealth = new();
    private readonly DispatcherTimer _timer;

    public event Action<HardwareSnapshot>? SnapshotUpdated;

    public static IReadOnlyList<RefreshRateOption> RefreshRateOptions { get; } = new List<RefreshRateOption>
    {
        new("250 ms", 250),
        new("500 ms", 500),
        new("1 seconde", 1000),
        new("2 secondes", 2000),
        new("5 secondes", 5000),
    };

    [ObservableProperty] private RefreshRateOption selectedRefreshRate;

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
    [ObservableProperty] private double? motherboardVrmTemp;

    public ObservableCollectionEx<FanReading> Fans { get; } = new();
    public ObservableCollectionEx<SensorReading> MotherboardOtherTemps { get; } = new();
    public ObservableCollectionEx<SensorReading> MotherboardVoltages { get; } = new();
    public ObservableCollectionEx<DiskItemViewModel> Disks { get; } = new();

    [ObservableProperty] private string? errorMessage;

    public MonitoringViewModel(HardwareMonitorService hardware)
    {
        _hardware = hardware;

        AppSettings settings = AppSettingsStore.Load();
        selectedRefreshRate = RefreshRateOptions.FirstOrDefault(o => o.Milliseconds == settings.MonitoringRefreshMs)
                              ?? RefreshRateOptions[2];

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(selectedRefreshRate.Milliseconds),
        };
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();

        _ = RefreshAsync();
    }

    partial void OnSelectedRefreshRateChanged(RefreshRateOption value)
    {
        _timer.Interval = TimeSpan.FromMilliseconds(value.Milliseconds);

        AppSettings settings = AppSettingsStore.Load();
        settings.MonitoringRefreshMs = value.Milliseconds;
        AppSettingsStore.Save(settings);
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
        MotherboardVrmTemp = s.Motherboard.VrmTempC;
        MotherboardOtherTemps.ReplaceAll(s.Motherboard.OtherTemperatures);
        MotherboardVoltages.ReplaceAll(s.Motherboard.Voltages);

        Fans.ReplaceAll(s.Fans);
        ApplyDisks(s.Disks);

        SnapshotUpdated?.Invoke(s);
    }

    private void ApplyDisks(IReadOnlyList<DiskSnapshot> snapshots)
    {
        // Reconciliation par identifiant (pas un ReplaceAll aveugle) pour ne pas effacer un test
        // de santé en cours/terminé sur une carte à chaque tick de rafraîchissement.
        for (int i = Disks.Count - 1; i >= 0; i--)
        {
            if (snapshots.All(s => s.Identifier != Disks[i].Identifier))
            {
                Disks.RemoveAt(i);
            }
        }

        foreach (DiskSnapshot snap in snapshots)
        {
            DiskItemViewModel? existing = Disks.FirstOrDefault(d => d.Identifier == snap.Identifier);
            if (existing is null)
            {
                var item = new DiskItemViewModel(_diskHealth, snap.Identifier);
                item.Apply(snap);
                Disks.Add(item);
            }
            else
            {
                existing.Apply(snap);
            }
        }
    }

    public void Dispose()
    {
        _timer.Stop();
    }
}
