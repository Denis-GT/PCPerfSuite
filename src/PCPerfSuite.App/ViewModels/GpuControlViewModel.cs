using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCPerfSuite.Core.Hardware;
using PCPerfSuite.Core.PowerSettings;

namespace PCPerfSuite.App.ViewModels;

/// <summary>
/// Contrôle GPU (onglet "GPU") : overclocking (décalages d'horloge cœur/mémoire, limite de puissance,
/// limite de température, surtension quand la carte le permet) et courbe de ventilation NVAPI,
/// appliqués à chaque relevé du MonitoringViewModel partagé — même schéma que FanCurvesViewModel.
///
/// Politique de sécurité : par défaut, rien n'est réappliqué au démarrage et tout est rendu au pilote
/// en quittant. C'est la case "Appliquer au démarrage" qui rend l'overclock persistant, dans les deux
/// sens (réappliqué au lancement, conservé à la fermeture).
/// </summary>
public sealed partial class GpuControlViewModel : ObservableObject, IDisposable
{
    /// <summary>Au-delà de cette température, le ventilateur force 100% quel que soit le mode/la
    /// courbe configurée — garde-fou indépendant des réglages utilisateur.</summary>
    private const float CriticalTempC = 88f;

    private readonly GpuControlService _gpuControl = new();
    private readonly MonitoringViewModel _monitoring;
    private readonly AppSettings _settings;

    /// <summary>Bloque l'application/l'enregistrement pendant qu'on repositionne plusieurs curseurs
    /// d'un coup (réinitialisation), pour ne pas envoyer une consigne intermédiaire à la carte.</summary>
    private bool _suppressApply;

    private double _powerLimitDefault = 100;
    private double _temperatureLimitDefault;

    public ObservableCollection<FanCurvePoint> FanPoints { get; }

    [ObservableProperty] private bool isAvailable;
    public bool IsUnavailable => !IsAvailable;

    [ObservableProperty] private string gpuName = "…";

    [ObservableProperty] private double? coreTempC;
    [ObservableProperty] private double? coreClockMhz;
    [ObservableProperty] private double? memoryClockMhz;
    [ObservableProperty] private double? powerWatts;
    [ObservableProperty] private double? fanRpm;
    [ObservableProperty] private double? fanCurrentPercent;
    [ObservableProperty] private double? fanTargetPercent;

    [ObservableProperty] private double powerLimitPercent;
    [ObservableProperty] private double powerLimitMin = 50;
    [ObservableProperty] private double powerLimitMax = 100;

    [ObservableProperty] private bool isClockOffsetSupported;
    [ObservableProperty] private double coreOffsetMhz;
    [ObservableProperty] private double coreOffsetMin = -500;
    [ObservableProperty] private double coreOffsetMax = 1000;
    [ObservableProperty] private double memoryOffsetMhz;
    [ObservableProperty] private double memoryOffsetMin = -1000;
    [ObservableProperty] private double memoryOffsetMax = 2000;

    [ObservableProperty] private bool isTemperatureLimitSupported;
    [ObservableProperty] private double temperatureLimitC;
    [ObservableProperty] private double temperatureLimitMin = 60;
    [ObservableProperty] private double temperatureLimitMax = 90;

    [ObservableProperty] private bool isVoltageBoostSupported;
    [ObservableProperty] private double voltageBoostPercent;

    [ObservableProperty] private bool applyOverclockAtStartup;
    [ObservableProperty] private string overclockStatus = "";

    [ObservableProperty] private FanControlMode fanMode;
    [ObservableProperty] private double fanManualPercent;

    public GpuControlViewModel(MonitoringViewModel monitoring)
    {
        _monitoring = monitoring;
        _settings = AppSettingsStore.Load();

        FanPoints = new ObservableCollection<FanCurvePoint>(_settings.Gpu.FanPoints);
        fanMode = _settings.Gpu.FanMode;
        fanManualPercent = _settings.Gpu.FanManualPercent;
        applyOverclockAtStartup = _settings.Gpu.ApplyOverclockAtStartup;

        IsAvailable = _gpuControl.TryInitialize();
        _gpuControl.KeepOverclockOnExit = applyOverclockAtStartup;

        if (IsAvailable)
        {
            LoadPowerLimit();
            LoadOverclock();
        }

        _monitoring.SnapshotUpdated += OnSnapshotUpdated;
    }

    private void LoadPowerLimit()
    {
        GpuControlSnapshot? snap = _gpuControl.GetSnapshot();
        if (snap is null) return;

        GpuName = snap.Name;
        PowerLimitMin = snap.PowerLimitMinPercent;
        PowerLimitMax = snap.PowerLimitMaxPercent;
        _powerLimitDefault = snap.PowerLimitDefaultPercent;

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

    /// <summary>Lit l'état d'overclocking de la carte, puis — uniquement si l'utilisateur l'a demandé —
    /// réapplique celui enregistré.</summary>
    private void LoadOverclock()
    {
        GpuOverclockSnapshot? snap = _gpuControl.GetOverclock();
        if (snap is null) return;

        IsClockOffsetSupported = snap.ClockOffsetsSupported;
        CoreOffsetMin = snap.CoreOffsetMinMhz;
        CoreOffsetMax = snap.CoreOffsetMaxMhz;
        MemoryOffsetMin = snap.MemoryOffsetMinMhz;
        MemoryOffsetMax = snap.MemoryOffsetMaxMhz;

        IsTemperatureLimitSupported = snap.TemperatureLimitSupported;
        TemperatureLimitMin = snap.TemperatureLimitMinC;
        TemperatureLimitMax = snap.TemperatureLimitMaxC;
        _temperatureLimitDefault = snap.TemperatureLimitDefaultC;

        IsVoltageBoostSupported = snap.VoltageBoostSupported;

        _suppressApply = true;
        CoreOffsetMhz = ApplyOverclockAtStartup ? _settings.Gpu.CoreClockOffsetMhz : snap.CoreOffsetMhz;
        MemoryOffsetMhz = ApplyOverclockAtStartup ? _settings.Gpu.MemoryClockOffsetMhz : snap.MemoryOffsetMhz;
        TemperatureLimitC = ApplyOverclockAtStartup
            ? _settings.Gpu.TemperatureLimitC ?? snap.TemperatureLimitC
            : snap.TemperatureLimitC;
        VoltageBoostPercent = ApplyOverclockAtStartup
            ? _settings.Gpu.VoltageBoostPercent ?? snap.VoltageBoostPercent
            : snap.VoltageBoostPercent;
        _suppressApply = false;

        if (!ApplyOverclockAtStartup) return;

        if (IsClockOffsetSupported) _gpuControl.TrySetClockOffsets((int)CoreOffsetMhz, (int)MemoryOffsetMhz);
        if (IsTemperatureLimitSupported && _settings.Gpu.TemperatureLimitC is { } temp) _gpuControl.TrySetTemperatureLimit(temp);
        if (IsVoltageBoostSupported && _settings.Gpu.VoltageBoostPercent is { } volts) _gpuControl.TrySetVoltageBoostPercent(volts);
        OverclockStatus = "Réglages enregistrés réappliqués au démarrage.";
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
        if (!IsAvailable || _suppressApply) return;
        _gpuControl.TrySetPowerLimitPercent((float)value);
        Persist();
    }

    partial void OnCoreOffsetMhzChanged(double value) => ApplyClockOffsets();

    partial void OnMemoryOffsetMhzChanged(double value) => ApplyClockOffsets();

    partial void OnTemperatureLimitCChanged(double value)
    {
        if (!IsAvailable || _suppressApply || !IsTemperatureLimitSupported) return;

        OverclockStatus = _gpuControl.TrySetTemperatureLimit((int)Math.Round(value))
            ? $"Limite de température : {value:0} °C."
            : "Le pilote a refusé la limite de température.";
        Persist();
    }

    partial void OnVoltageBoostPercentChanged(double value)
    {
        if (!IsAvailable || _suppressApply || !IsVoltageBoostSupported) return;

        OverclockStatus = _gpuControl.TrySetVoltageBoostPercent((int)Math.Round(value))
            ? $"Surtension : +{value:0}%."
            : "Le pilote a refusé la surtension.";
        Persist();
    }

    partial void OnApplyOverclockAtStartupChanged(bool value)
    {
        _gpuControl.KeepOverclockOnExit = value;
        Persist();
    }

    private void ApplyClockOffsets()
    {
        if (!IsAvailable || _suppressApply || !IsClockOffsetSupported) return;

        int core = (int)Math.Round(CoreOffsetMhz);
        int memory = (int)Math.Round(MemoryOffsetMhz);

        OverclockStatus = _gpuControl.TrySetClockOffsets(core, memory)
            ? $"Décalages appliqués : cœur {Signed(core)} MHz, mémoire {Signed(memory)} MHz."
            : "Le pilote a refusé les décalages d'horloge (carte verrouillée, ou app lancée sans les droits administrateur).";
        Persist();
    }

    private static string Signed(int value)
        => value >= 0 ? $"+{value.ToString(CultureInfo.CurrentCulture)}" : value.ToString(CultureInfo.CurrentCulture);

    [RelayCommand]
    private void ResetOverclock()
    {
        _suppressApply = true;
        CoreOffsetMhz = 0;
        MemoryOffsetMhz = 0;
        if (IsTemperatureLimitSupported) TemperatureLimitC = _temperatureLimitDefault;
        if (IsVoltageBoostSupported) VoltageBoostPercent = 0;
        PowerLimitPercent = _powerLimitDefault;
        _suppressApply = false;

        _gpuControl.RestoreOverclockDefaults();
        OverclockStatus = "Réglages d'origine restaurés.";
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
        CoreClockMhz = snapshot.Gpu?.CoreClockMhz;
        MemoryClockMhz = snapshot.Gpu?.MemoryClockMhz;
        PowerWatts = snapshot.Gpu?.PowerWatts;

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
        if (_suppressApply) return;

        _settings.Gpu.PowerLimitPercent = (float)PowerLimitPercent;
        _settings.Gpu.FanMode = FanMode;
        _settings.Gpu.FanManualPercent = (float)FanManualPercent;
        _settings.Gpu.FanPoints = FanPoints.ToList();
        _settings.Gpu.CoreClockOffsetMhz = (int)Math.Round(CoreOffsetMhz);
        _settings.Gpu.MemoryClockOffsetMhz = (int)Math.Round(MemoryOffsetMhz);
        _settings.Gpu.TemperatureLimitC = IsTemperatureLimitSupported ? (int)Math.Round(TemperatureLimitC) : null;
        _settings.Gpu.VoltageBoostPercent = IsVoltageBoostSupported ? (int)Math.Round(VoltageBoostPercent) : null;
        _settings.Gpu.ApplyOverclockAtStartup = ApplyOverclockAtStartup;
        AppSettingsStore.Save(_settings);
    }

    /// <summary>Rend la carte au pilote en quittant (limite de puissance, overclock et ventilateurs),
    /// sauf si "Appliquer au démarrage" est coché — voir GpuControlService.Dispose.</summary>
    public void Dispose()
    {
        _monitoring.SnapshotUpdated -= OnSnapshotUpdated;
        _gpuControl.Dispose();
    }
}
