using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCPerfSuite.Core.Hardware;
using PCPerfSuite.Core.PowerSettings;
using PCPerfSuite.Core.SystemInfo;

namespace PCPerfSuite.App.ViewModels;

/// <summary>
/// Un ventilateur pilotable — de la carte mère ou du GPU, l'onglet les traite pareil. Le ViewModel
/// écrit directement dans la configuration persistée (FanCurveConfig) : c'est elle que lit le
/// régulateur, donc un réglage modifié prend effet au relevé suivant sans recopie intermédiaire.
/// </summary>
public sealed partial class FanControlItemViewModel : ObservableObject
{
    private readonly IFanController _controller;
    private readonly FanCurveConfig _config;
    private readonly FanCurveRegulator _regulator = new();
    private readonly Action _persist;
    private readonly Action<FanControlItemViewModel> _copyToAll;

    public string FanId { get; }
    public string DisplayName { get; }

    /// <summary>Ventilateur du GPU : la source de température par défaut et les libellés diffèrent.</summary>
    public bool IsGpu { get; }

    public ObservableCollection<FanCurvePoint> Points { get; }

    [ObservableProperty] private double rpm;
    [ObservableProperty] private double? currentPercent;
    [ObservableProperty] private double? targetPercent;
    [ObservableProperty] private double? sourceTempC;
    [ObservableProperty] private FanControlMode mode;
    [ObservableProperty] private double manualPercent;
    [ObservableProperty] private FanTempSource source;
    [ObservableProperty] private double hysteresisC;
    [ObservableProperty] private double minPercent;
    [ObservableProperty] private double maxPercent;
    [ObservableProperty] private bool stopWhenCool;
    [ObservableProperty] private double stopBelowTempC;

    public FanControlItemViewModel(
        FanCurveConfig config,
        string displayName,
        bool isGpu,
        IFanController controller,
        Action persist,
        Action<FanControlItemViewModel> copyToAll)
    {
        _config = config;
        _controller = controller;
        _persist = persist;
        _copyToAll = copyToAll;

        FanId = config.ControlSensorId;
        DisplayName = displayName;
        IsGpu = isGpu;

        Points = new ObservableCollection<FanCurvePoint>(config.Points);
        mode = config.Mode;
        manualPercent = config.ManualPercent;
        source = config.Source;
        hysteresisC = config.HysteresisC;
        minPercent = config.MinPercent;
        maxPercent = config.MaxPercent;
        stopWhenCool = config.StopBelowTempC is not null;
        stopBelowTempC = config.StopBelowTempC ?? 40;
    }

    /// <summary>Au-delà de cette température, un ventilateur GPU piloté par l'app passe à 100% quel que
    /// soit le mode choisi — garde-fou indépendant des réglages utilisateur. En mode Auto, c'est la
    /// protection du VBIOS qui joue ce rôle.</summary>
    private const float GpuCriticalTempC = 88f;

    /// <summary>Applique le mode courant au ventilateur pour ce relevé.</summary>
    public void Apply(float? tempC)
    {
        SourceTempC = tempC;

        float? target = Mode switch
        {
            FanControlMode.Manual => (float)ManualPercent,
            FanControlMode.Curve when tempC is { } t => _regulator.Evaluate(Points, t, _config),
            _ => null,
        };

        if (target is not null && IsGpu && tempC is { } temp && temp >= GpuCriticalTempC) target = 100;

        if (target is { } percent)
        {
            _controller.TrySetPercent(FanId, percent);
            TargetPercent = percent;
        }
        else
        {
            TargetPercent = null;
        }
    }

    public void RestoreAuto()
    {
        if (Mode == FanControlMode.Auto) return;
        _controller.TrySetAuto(FanId);
    }

    /// <summary>Recopie la courbe et les réglages de régulation d'un autre ventilateur.</summary>
    public void CopyFrom(FanControlItemViewModel other)
    {
        Points.Clear();
        foreach (FanCurvePoint p in other.Points) Points.Add(new FanCurvePoint { TempC = p.TempC, Percent = p.Percent });

        HysteresisC = other.HysteresisC;
        MinPercent = other.MinPercent;
        MaxPercent = other.MaxPercent;
        StopWhenCool = other.StopWhenCool;
        StopBelowTempC = other.StopBelowTempC;
        NotifyPointsEdited();
    }

    partial void OnModeChanged(FanControlMode value)
    {
        _config.Mode = value;
        if (value == FanControlMode.Auto)
        {
            _controller.TrySetAuto(FanId);
            TargetPercent = null;
        }
        _regulator.Reset();
        _persist();
    }

    partial void OnManualPercentChanged(double value)
    {
        _config.ManualPercent = (float)value;
        _persist();
    }

    partial void OnSourceChanged(FanTempSource value)
    {
        _config.Source = value;
        _regulator.Reset();
        _persist();
    }

    partial void OnHysteresisCChanged(double value)
    {
        _config.HysteresisC = (float)value;
        _regulator.Reset();
        _persist();
    }

    partial void OnMinPercentChanged(double value)
    {
        _config.MinPercent = (float)value;
        if (value > MaxPercent) MaxPercent = value;
        _persist();
    }

    partial void OnMaxPercentChanged(double value)
    {
        _config.MaxPercent = (float)value;
        if (value < MinPercent) MinPercent = value;
        _persist();
    }

    partial void OnStopWhenCoolChanged(bool value) => ApplyStopThreshold();

    partial void OnStopBelowTempCChanged(double value) => ApplyStopThreshold();

    private void ApplyStopThreshold()
    {
        _config.StopBelowTempC = StopWhenCool ? (float)StopBelowTempC : null;
        _regulator.Reset();
        _persist();
    }

    [RelayCommand]
    private void ApplySilencieux() => ApplyPreset(FanCurveMath.SilencieuxPoints());

    [RelayCommand]
    private void ApplyEquilibre() => ApplyPreset(FanCurveMath.EquilibrePoints());

    [RelayCommand]
    private void ApplyPerf() => ApplyPreset(FanCurveMath.PerfPoints());

    [RelayCommand]
    private void CopyToAll() => _copyToAll(this);

    private void ApplyPreset(List<FanCurvePoint> points)
    {
        Points.Clear();
        foreach (FanCurvePoint p in points) Points.Add(p);
        NotifyPointsEdited();
    }

    public void NotifyPointsEdited()
    {
        _config.Points = Points.ToList();
        _regulator.Reset();
        _persist();
    }
}

/// <summary>
/// Pilotage des ventilateurs (onglet "Ventilateurs") : tous les ventilateurs pilotables au même
/// endroit — ceux de la carte mère via LibreHardwareMonitor et ceux du GPU via NVAPI — avec pour
/// chacun un mode (Auto/Manuel/Courbe), une source de température, une hystérésis et des bornes.
/// S'appuie sur les instantanés déjà produits par MonitoringViewModel plutôt que de repoller le
/// matériel en double : la consigne est recalculée à chaque nouveau relevé de température.
/// </summary>
public sealed partial class FanCurvesViewModel : ObservableObject, IDisposable
{
    private readonly HardwareMonitorService _hardware;
    private readonly GpuControlService _gpu;
    private readonly MonitoringViewModel _monitoring;
    private readonly AppSettings _settings;

    /// <summary>Identifiants des coolers GPU, résolus une seule fois : NVAPI ne les renumérote pas en
    /// cours de route, inutile de les relire à chaque relevé.</summary>
    private List<string>? _gpuFanIds;

    public ObservableCollectionEx<FanControlItemViewModel> Fans { get; } = new();

    [ObservableProperty] private bool hasGpu;

    /// <summary>Pourquoi aucun ventilateur n'est pilotable ici, adapté au PC : sans administrateur, portable (dont
    /// les ventilateurs appartiennent au firmware du constructeur) ou carte mère sans pilotage logiciel.</summary>
    public string NoFansMessage
    {
        get
        {
            if (!ElevationHelper.IsAdministrator())
                return "PCPerfSuite n'est pas lancé en administrateur : les ventilateurs ne peuvent être ni lus ni pilotés. Relance l'app en administrateur.";

            if (MachineInfo.Current.IsLaptop)
                return "Sur un portable, les ventilateurs sont pilotés par le firmware du constructeur (Armoury Crate, Legion Zone, " +
                       "Omen Gaming Hub, MSI Center, PredatorSense…). Par sécurité, PCPerfSuite ne les pilote pas : leur vitesse " +
                       "s'affiche dans le Monitoring quand la marque est prise en charge (voir Paramètres › Compatibilité de ce PC).";

            return "Aucun ventilateur pilotable détecté : la puce de gestion de la carte mère n'est pas reconnue ou n'accepte pas de " +
                   "pilotage logiciel (sur certaines cartes, il faut passer les ventilateurs en mode PWM/DC manuel dans le BIOS). " +
                   "Ce n'est pas un dysfonctionnement de PCPerfSuite.";
        }
    }

    public FanCurvesViewModel(HardwareMonitorService hardware, GpuControlService gpu, MonitoringViewModel monitoring)
    {
        _hardware = hardware;
        _gpu = gpu;
        _monitoring = monitoring;
        _settings = AppSettingsStore.Load();

        _monitoring.SnapshotUpdated += OnSnapshotUpdated;
    }

    private void OnSnapshotUpdated(HardwareSnapshot snapshot)
    {
        HasGpu = snapshot.Gpu is not null;

        List<FanReading> motherboard = snapshot.Fans.Where(f => f.CanControl).ToList();
        SyncFanList(motherboard);

        foreach (FanControlItemViewModel item in Fans)
        {
            if (item.IsGpu)
            {
                item.Rpm = snapshot.Gpu?.FanRpm ?? 0;
                item.CurrentPercent = snapshot.Gpu?.FanPercent;
            }
            else
            {
                FanReading? reading = motherboard.FirstOrDefault(f => f.PercentControlSensorId == item.FanId);
                item.Rpm = reading?.Rpm ?? 0;
                item.CurrentPercent = reading?.PercentControl;
            }

            item.Apply(TempFor(item.Source, snapshot));
        }
    }

    private void SyncFanList(List<FanReading> motherboard)
    {
        List<string> expected = motherboard.Select(f => f.PercentControlSensorId!).Concat(ResolveGpuFanIds()).ToList();

        for (int i = Fans.Count - 1; i >= 0; i--)
        {
            if (!expected.Contains(Fans[i].FanId)) Fans.RemoveAt(i);
        }

        bool added = false;

        foreach (FanReading fan in motherboard)
        {
            string id = fan.PercentControlSensorId!;
            if (Fans.Any(f => f.FanId == id)) continue;

            FanCurveConfig config = ConfigFor(id, FanTempSource.CpuPackage, ref added);
            Fans.Add(new FanControlItemViewModel(
                config, $"{fan.SensorName} — {fan.HardwareName}", isGpu: false, _hardware, Persist, CopyCurveToAll));
        }

        foreach (string id in ResolveGpuFanIds())
        {
            if (Fans.Any(f => f.FanId == id)) continue;

            FanCurveConfig config = ConfigFor(id, FanTempSource.GpuCore, ref added);
            Fans.Add(new FanControlItemViewModel(
                config, "Ventilateur GPU", isGpu: true, _gpu, Persist, CopyCurveToAll));
        }

        if (added) Persist();
    }

    /// <summary>Configuration d'un ventilateur, créée au besoin. Pour le GPU, on reprend les réglages
    /// de l'ancien onglet GPU (où vivait la courbe GPU) plutôt que de repartir de zéro.</summary>
    private FanCurveConfig ConfigFor(string fanId, FanTempSource defaultSource, ref bool added)
    {
        FanCurveConfig? config = _settings.FanCurves.FirstOrDefault(c => c.ControlSensorId == fanId);
        if (config is not null) return config;

        bool isGpu = fanId.StartsWith(GpuControlService.FanIdPrefix, StringComparison.Ordinal);
        config = new FanCurveConfig
        {
            ControlSensorId = fanId,
            Source = defaultSource,
            Mode = isGpu ? _settings.Gpu.FanMode : FanControlMode.Auto,
            ManualPercent = isGpu ? _settings.Gpu.FanManualPercent : 50,
            Points = isGpu ? _settings.Gpu.FanPoints.ToList() : FanCurveMath.EquilibrePoints(),
        };

        _settings.FanCurves.Add(config);
        added = true;
        return config;
    }

    private IEnumerable<string> ResolveGpuFanIds()
    {
        if (_gpuFanIds is not null) return _gpuFanIds;

        _gpuFanIds = new List<string>();
        if (_gpu.TryInitialize() && _gpu.GetSnapshot() is { } snap)
        {
            _gpuFanIds.AddRange(snap.Fans.Select(f => GpuControlService.FanId(f.CoolerId)));
        }

        return _gpuFanIds;
    }

    private void CopyCurveToAll(FanControlItemViewModel source)
    {
        foreach (FanControlItemViewModel item in Fans)
        {
            if (!ReferenceEquals(item, source)) item.CopyFrom(source);
        }
    }

    private static float? TempFor(FanTempSource source, HardwareSnapshot s) => source switch
    {
        FanTempSource.CpuPackage => s.Cpu.PackageTempC,
        FanTempSource.GpuCore => s.Gpu?.CoreTempC,
        FanTempSource.MotherboardSystem => s.Motherboard.SystemTempC,
        FanTempSource.HottestOfCpuGpu => Hottest(s.Cpu.PackageTempC, s.Gpu?.CoreTempC),
        _ => null,
    };

    private static float? Hottest(float? a, float? b)
        => a is null ? b : b is null ? a : Math.Max(a.Value, b.Value);

    /// <summary>Relit le fichier avant d'écrire : cet onglet n'est propriétaire que des courbes de
    /// ventilation, le reste (GPU, overlay, monitoring) appartient aux autres ViewModels.</summary>
    private void Persist()
    {
        AppSettings settings = AppSettingsStore.Load();
        settings.FanCurves = _settings.FanCurves;
        AppSettingsStore.Save(settings);
    }

    /// <summary>Rend tous les ventilateurs actuellement pilotés au firmware — appelé à la fermeture de
    /// l'app pour ne jamais laisser un ventilateur bloqué à un % logiciel une fois PCPerfSuite fermé.</summary>
    public void Dispose()
    {
        _monitoring.SnapshotUpdated -= OnSnapshotUpdated;

        foreach (FanControlItemViewModel item in Fans)
        {
            item.RestoreAuto();
        }
    }
}
