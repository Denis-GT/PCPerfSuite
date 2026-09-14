using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCPerfSuite.Core.Hardware;
using PCPerfSuite.Core.PowerSettings;

namespace PCPerfSuite.App.ViewModels;

/// <summary>
/// Contrôle GPU (onglet "GPU") : limite de puissance + courbe de ventilation NVAPI, appliquées à
/// chaque relevé de température du MonitoringViewModel partagé — même schéma que FanCurvesViewModel.
///
/// Ne propose PAS de décalage d'horloge cœur/mémoire : voir le commentaire en tête de
/// GpuControlService pour pourquoi (API NVAPI "Pascal only", pas utilisable sur une RTX 50xx).
/// </summary>
public sealed partial class GpuControlViewModel : ObservableObject, IDisposable
{
    /// <summary>Au-delà de cette température, le ventilateur force 100% quel que soit le mode/la
    /// courbe configurée — garde-fou indépendant des réglages utilisateur.</summary>
    private const float CriticalTempC = 88f;

    private readonly GpuControlService _gpuControl = new();
    private readonly MonitoringViewModel _monitoring;
    private readonly AppSettings _settings;

    public ObservableCollection<FanCurvePoint> FanPoints { get; }

    [ObservableProperty] private bool isAvailable;
    public bool IsUnavailable => !IsAvailable;

    [ObservableProperty] private string gpuName = "…";

    [ObservableProperty] private double? coreTempC;
    [ObservableProperty] private double? fanRpm;
    [ObservableProperty] private double? fanCurrentPercent;
    [ObservableProperty] private double? fanTargetPercent;

    [ObservableProperty] private double powerLimitPercent;
    [ObservableProperty] private double powerLimitMin = 50;
    [ObservableProperty] private double powerLimitMax = 100;

    [ObservableProperty] private FanControlMode fanMode;
    [ObservableProperty] private double fanManualPercent;

    public GpuControlViewModel(MonitoringViewModel monitoring)
    {
        _monitoring = monitoring;
        _settings = AppSettingsStore.Load();

        FanPoints = new ObservableCollection<FanCurvePoint>(_settings.Gpu.FanPoints);
        fanMode = _settings.Gpu.FanMode;
        fanManualPercent = _settings.Gpu.FanManualPercent;

        IsAvailable = _gpuControl.TryInitialize();

        if (IsAvailable)
        {
            GpuControlSnapshot? snap = _gpuControl.GetSnapshot();
            if (snap is not null)
            {
                GpuName = snap.Name;
                PowerLimitMin = snap.PowerLimitMinPercent;
                PowerLimitMax = snap.PowerLimitMaxPercent;

                // Passe par le champ, pas la propriété : évite de déclencher OnPowerLimitPercentChanged
                // (qui appliquerait/persisterait) juste pour peupler l'affichage initial. On ne réapplique
                // explicitement que si l'utilisateur avait déjà choisi une valeur lors d'une session précédente.
                powerLimitPercent = _settings.Gpu.PowerLimitPercent ?? snap.PowerLimitPercent;
                OnPropertyChanged(nameof(PowerLimitPercent));

                if (_settings.Gpu.PowerLimitPercent is { } persisted)
                {
                    _gpuControl.TrySetPowerLimitPercent((float)persisted);
                }
            }
        }

        _monitoring.SnapshotUpdated += OnSnapshotUpdated;
    }

    partial void OnIsAvailableChanged(bool value) => OnPropertyChanged(nameof(IsUnavailable));

    partial void OnFanModeChanged(FanControlMode value)
    {
        if (value == FanControlMode.Auto)
        {
            _gpuControl.TryRestoreFanAuto();
            FanTargetPercent = null;
        }
        Persist();
    }

    partial void OnFanManualPercentChanged(double value) => Persist();

    partial void OnPowerLimitPercentChanged(double value)
    {
        if (!IsAvailable) return;
        _gpuControl.TrySetPowerLimitPercent((float)value);
        Persist();
    }

    [RelayCommand]
    private void ApplySilencieux() => ApplyPreset(FanCurveMath.SilencieuxPoints());

    [RelayCommand]
    private void ApplyEquilibre() => ApplyPreset(FanCurveMath.EquilibrePoints());

    [RelayCommand]
    private void ApplyPerf() => ApplyPreset(FanCurveMath.PerfPoints());

    private void ApplyPreset(List<FanCurvePoint> points)
    {
        FanPoints.Clear();
        foreach (FanCurvePoint p in points) FanPoints.Add(p);
        Persist();
    }

    public void NotifyPointsEdited() => Persist();

    private void OnSnapshotUpdated(HardwareSnapshot snapshot)
    {
        if (!IsAvailable) return;

        CoreTempC = snapshot.Gpu?.CoreTempC;

        GpuControlSnapshot? snap = _gpuControl.GetSnapshot();
        GpuFanInfo? fan = snap?.Fans.FirstOrDefault();
        FanRpm = fan?.CurrentRpm ?? snapshot.Gpu?.FanRpm;
        FanCurrentPercent = fan?.CurrentLevelPercent ?? snapshot.Gpu?.FanPercent;

        if (snap is null) return;

        float? target = FanMode switch
        {
            FanControlMode.Manual => (float)FanManualPercent,
            FanControlMode.Curve when CoreTempC is { } t => FanCurveMath.Evaluate(FanPoints, (float)t),
            _ => null,
        };

        if (CoreTempC is { } temp && temp >= CriticalTempC)
        {
            target = 100;
        }

        if (target is { } pct)
        {
            foreach (GpuFanInfo gpuFan in snap.Fans)
            {
                _gpuControl.TrySetFanPercent(gpuFan.CoolerId, (int)Math.Round(pct));
            }
            FanTargetPercent = pct;
        }
        else if (FanMode == FanControlMode.Auto)
        {
            FanTargetPercent = null;
        }
    }

    private void Persist()
    {
        _settings.Gpu.PowerLimitPercent = (float)PowerLimitPercent;
        _settings.Gpu.FanMode = FanMode;
        _settings.Gpu.FanManualPercent = (float)FanManualPercent;
        _settings.Gpu.FanPoints = FanPoints.ToList();
        AppSettingsStore.Save(_settings);
    }

    /// <summary>Rend la limite de puissance et les ventilateurs au firmware/pilote — même politique
    /// qu'en fermant l'app que pour les ventilateurs carte mère (FanCurvesViewModel).</summary>
    public void Dispose()
    {
        _monitoring.SnapshotUpdated -= OnSnapshotUpdated;
        _gpuControl.Dispose();
    }
}
