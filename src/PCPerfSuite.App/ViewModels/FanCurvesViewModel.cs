using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCPerfSuite.App.Utils;
using PCPerfSuite.Core.Hardware;
using PCPerfSuite.Core.PowerSettings;

namespace PCPerfSuite.App.ViewModels;

public sealed partial class FanControlItemViewModel : ObservableObject
{
    private readonly HardwareMonitorService _hardware;
    private readonly Action _persist;

    public string ControlSensorId { get; }
    public string DisplayName { get; }

    public ObservableCollection<FanCurvePoint> Points { get; }

    [ObservableProperty] private double rpm;
    [ObservableProperty] private double? currentPercent;
    [ObservableProperty] private double? targetPercent;
    [ObservableProperty] private double? sourceTempC;
    [ObservableProperty] private FanControlMode mode;
    [ObservableProperty] private double manualPercent;
    [ObservableProperty] private FanTempSource source;

    public FanControlItemViewModel(FanCurveConfig config, string hardwareName, string sensorName,
        HardwareMonitorService hardware, Action persist)
    {
        ControlSensorId = config.ControlSensorId;
        DisplayName = $"{sensorName} — {hardwareName}";
        Points = new ObservableCollection<FanCurvePoint>(config.Points);
        mode = config.Mode;
        manualPercent = config.ManualPercent;
        source = config.Source;

        _hardware = hardware;
        _persist = persist;
    }

    partial void OnModeChanged(FanControlMode value)
    {
        if (value == FanControlMode.Auto)
        {
            _hardware.TrySetFanAuto(ControlSensorId);
            TargetPercent = null;
        }
        _persist();
    }

    partial void OnManualPercentChanged(double value) => _persist();

    partial void OnSourceChanged(FanTempSource value) => _persist();

    [RelayCommand]
    private void ApplySilencieux() => ApplyPreset(FanCurveMath.SilencieuxPoints());

    [RelayCommand]
    private void ApplyEquilibre() => ApplyPreset(FanCurveMath.EquilibrePoints());

    [RelayCommand]
    private void ApplyPerf() => ApplyPreset(FanCurveMath.PerfPoints());

    private void ApplyPreset(List<FanCurvePoint> points)
    {
        Points.Clear();
        foreach (FanCurvePoint p in points) Points.Add(p);
        _persist();
    }

    public void NotifyPointsEdited() => _persist();
}

/// <summary>
/// Pilotage des ventilateurs (onglet "Ventilateurs") : un fan pilotable = un mode (Auto/Manuel/Courbe)
/// + éventuellement une courbe température→%. S'appuie sur les instantanés déjà produits par
/// MonitoringViewModel (même HardwareMonitorService partagé) plutôt que de repoller le matériel en
/// double — la courbe est réappliquée à chaque nouveau relevé de température.
/// </summary>
public sealed partial class FanCurvesViewModel : ObservableObject, IDisposable
{
    private readonly HardwareMonitorService _hardware;
    private readonly MonitoringViewModel _monitoring;
    private readonly AppSettings _settings;

    public ObservableCollectionEx<FanControlItemViewModel> Fans { get; } = new();

    [ObservableProperty] private bool hasGpu;

    public FanCurvesViewModel(HardwareMonitorService hardware, MonitoringViewModel monitoring)
    {
        _hardware = hardware;
        _monitoring = monitoring;
        _settings = AppSettingsStore.Load();

        _monitoring.SnapshotUpdated += OnSnapshotUpdated;
    }

    private void OnSnapshotUpdated(HardwareSnapshot snapshot)
    {
        HasGpu = snapshot.Gpu is not null;

        List<FanReading> controllable = snapshot.Fans.Where(f => f.CanControl).ToList();
        SyncFanList(controllable);

        foreach (FanControlItemViewModel item in Fans)
        {
            FanReading? reading = controllable.FirstOrDefault(f => f.PercentControlSensorId == item.ControlSensorId);
            item.Rpm = reading?.Rpm ?? 0;
            item.CurrentPercent = reading?.PercentControl;
            item.SourceTempC = TempFor(item.Source, snapshot);

            switch (item.Mode)
            {
                case FanControlMode.Manual:
                    _hardware.TrySetFanPercent(item.ControlSensorId, (float)item.ManualPercent);
                    item.TargetPercent = item.ManualPercent;
                    break;

                case FanControlMode.Curve:
                    if (item.SourceTempC is { } t)
                    {
                        float target = FanCurveMath.Evaluate(item.Points, (float)t);
                        _hardware.TrySetFanPercent(item.ControlSensorId, target);
                        item.TargetPercent = target;
                    }
                    else
                    {
                        item.TargetPercent = null;
                    }
                    break;

                default:
                    item.TargetPercent = null;
                    break;
            }
        }
    }

    private void SyncFanList(List<FanReading> controllable)
    {
        for (int i = Fans.Count - 1; i >= 0; i--)
        {
            if (controllable.All(f => f.PercentControlSensorId != Fans[i].ControlSensorId))
            {
                Fans.RemoveAt(i);
            }
        }

        bool added = false;
        foreach (FanReading fan in controllable)
        {
            if (Fans.Any(f => f.ControlSensorId == fan.PercentControlSensorId)) continue;

            FanCurveConfig? config = _settings.FanCurves.FirstOrDefault(c => c.ControlSensorId == fan.PercentControlSensorId);
            if (config is null)
            {
                config = new FanCurveConfig { ControlSensorId = fan.PercentControlSensorId! };
                _settings.FanCurves.Add(config);
                added = true;
            }

            Fans.Add(new FanControlItemViewModel(config, fan.HardwareName, fan.SensorName, _hardware, Persist));
        }

        if (added) AppSettingsStore.Save(_settings);
    }

    private static float? TempFor(FanTempSource source, HardwareSnapshot s) => source switch
    {
        FanTempSource.CpuPackage => s.Cpu.PackageTempC,
        FanTempSource.GpuCore => s.Gpu?.CoreTempC,
        FanTempSource.MotherboardSystem => s.Motherboard.SystemTempC,
        _ => null,
    };

    private void Persist()
    {
        foreach (FanControlItemViewModel item in Fans)
        {
            FanCurveConfig? config = _settings.FanCurves.FirstOrDefault(c => c.ControlSensorId == item.ControlSensorId);
            if (config is null) continue;

            config.Mode = item.Mode;
            config.ManualPercent = (float)item.ManualPercent;
            config.Source = item.Source;
            config.Points = item.Points.ToList();
        }

        AppSettingsStore.Save(_settings);
    }

    /// <summary>Rend tous les ventilateurs actuellement pilotés au firmware — appelé à la fermeture de
    /// l'app pour ne jamais laisser un ventilateur bloqué à un % logiciel une fois PCPerfSuite fermé.</summary>
    public void Dispose()
    {
        _monitoring.SnapshotUpdated -= OnSnapshotUpdated;

        foreach (FanControlItemViewModel item in Fans)
        {
            if (item.Mode != FanControlMode.Auto)
            {
                _hardware.TrySetFanAuto(item.ControlSensorId);
            }
        }
    }
}
