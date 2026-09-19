using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCPerfSuite.Core.Hardware;
using PCPerfSuite.Core.PowerSettings;
using PCPerfSuite.Core.SystemInfo;

namespace PCPerfSuite.App.ViewModels;

/// <summary>Un profil d'overclocking enregistré, tel qu'affiché dans la liste des profils.</summary>
public sealed class GpuProfileViewModel
{
    public GpuOverclockProfile Model { get; }
    public string Name => Model.Name;
    public string Summary { get; }

    public GpuProfileViewModel(GpuOverclockProfile model)
    {
        Model = model;

        var parts = new List<string>
        {
            $"cœur {Signed(model.CoreClockOffsetMhz)} MHz",
            $"mém {Signed(model.MemoryClockOffsetMhz)} MHz",
        };
        if (model.PowerLimitPercent is { } power) parts.Add($"{power:0}% puissance");
        if (model.TemperatureLimitC is { } temp) parts.Add($"{temp} °C max");
        if (model.VoltageBoostPercent is { } volts && volts > 0) parts.Add($"+{volts}% tension");

        Summary = string.Join("  •  ", parts);
    }

    private static string Signed(int value) => value >= 0 ? $"+{value}" : value.ToString(CultureInfo.CurrentCulture);
}

/// <summary>
/// Contrôle GPU (onglet "GPU") : overclocking complet via NVAPI — décalages d'horloge cœur/mémoire,
/// limite de puissance, limite de température, surtension quand la carte l'accepte — avec profils
/// enregistrés, relevés en direct et affichage de ce qui bride la carte à l'instant T.
///
/// Les ventilateurs (GPU compris) sont regroupés dans l'onglet "Ventilateurs" : un seul endroit pour
/// toutes les courbes plutôt que deux interfaces qui se marchent dessus.
///
/// Politique de sécurité : par défaut, rien n'est réappliqué au démarrage et tout est rendu au pilote
/// en quittant. C'est la case "Appliquer au démarrage" qui rend l'overclock persistant, dans les deux
/// sens (réappliqué au lancement, conservé à la fermeture).
/// </summary>
public sealed partial class GpuControlViewModel : ObservableObject, IDisposable
{
    private readonly GpuControlService _gpuControl;
    private readonly MonitoringViewModel _monitoring;
    private readonly AppSettings _settings;

    /// <summary>Bloque l'application/l'enregistrement pendant qu'on repositionne plusieurs curseurs
    /// d'un coup (profil, réinitialisation), pour ne pas envoyer de consigne intermédiaire à la carte.</summary>
    private bool _suppressApply;

    private double _powerLimitDefault = 100;
    private double _temperatureLimitDefault;

    [ObservableProperty] private bool isAvailable;
    public bool IsUnavailable => !IsAvailable;

    /// <summary>Pourquoi le contrôle GPU est absent sur ce PC : sans administrateur, pilote NVIDIA muet, ou GPU
    /// d'une autre marque (le contrôle ne passe que par NVAPI, le monitoring, lui, couvre toutes les marques).</summary>
    public string UnavailableMessage
    {
        get
        {
            if (!ElevationHelper.IsAdministrator())
                return "PCPerfSuite n'est pas lancé en administrateur : le pilote NVIDIA refuse alors tout réglage. Relance l'app en administrateur.";

            MachineInfo machine = MachineInfo.Current;
            if (machine.HasNvidiaGpu)
                return "Un GPU NVIDIA est présent, mais son pilote ne répond pas (pilote absent, trop ancien ou GPU désactivé). " +
                       "Installe le dernier pilote depuis nvidia.com.";

            string gpus = machine.VideoControllers.Count > 0 ? string.Join(", ", machine.VideoControllers) : "aucun GPU identifié";
            return "Le contrôle GPU (overclocking, limites de puissance et de température) n'est disponible que pour les GPU NVIDIA " +
                   $"pour l'instant. GPU de ce PC : {gpus}. Leur monitoring (charge, températures, fréquences) fonctionne dans l'onglet Monitoring.";
        }
    }

    [ObservableProperty] private string gpuName = "…";

    // Relevés en direct (lus par le monitoring partagé, pas par un second sondage du matériel).
    [ObservableProperty] private double? loadPercent;
    [ObservableProperty] private double? coreTempC;
    [ObservableProperty] private double? hotSpotTempC;
    [ObservableProperty] private double? coreClockMhz;
    [ObservableProperty] private double? memoryClockMhz;
    [ObservableProperty] private double? powerWatts;
    [ObservableProperty] private double? vramUsedMb;
    [ObservableProperty] private double? fanRpm;
    [ObservableProperty] private double? fanPercent;
    [ObservableProperty] private string activeLimitText = "--";

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

    /// <summary>Ce que le pilote a réellement retenu après la dernière application — il rabote une
    /// demande hors plage sans prévenir, autant le montrer.</summary>
    [ObservableProperty] private string appliedOffsetsText = "";

    public ObservableCollection<GpuProfileViewModel> Profiles { get; } = new();
    [ObservableProperty] private string newProfileName = "";

    public GpuControlViewModel(GpuControlService gpuControl, MonitoringViewModel monitoring)
    {
        _gpuControl = gpuControl;
        _monitoring = monitoring;
        _settings = AppSettingsStore.Load();

        applyOverclockAtStartup = _settings.Gpu.ApplyOverclockAtStartup;

        IsAvailable = _gpuControl.TryInitialize();
        _gpuControl.KeepOverclockOnExit = applyOverclockAtStartup;

        foreach (GpuOverclockProfile profile in _settings.Gpu.OverclockProfiles)
        {
            Profiles.Add(new GpuProfileViewModel(profile));
        }

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

        if (!ApplyOverclockAtStartup)
        {
            ShowAppliedOffsets(snap);
            return;
        }

        if (IsClockOffsetSupported) _gpuControl.TrySetClockOffsets((int)CoreOffsetMhz, (int)MemoryOffsetMhz);
        if (IsTemperatureLimitSupported && _settings.Gpu.TemperatureLimitC is { } temp) _gpuControl.TrySetTemperatureLimit(temp);
        if (IsVoltageBoostSupported && _settings.Gpu.VoltageBoostPercent is { } volts) _gpuControl.TrySetVoltageBoostPercent(volts);

        OverclockStatus = "Réglages enregistrés réappliqués au démarrage.";
        ShowAppliedOffsets(_gpuControl.GetOverclock());
    }

    partial void OnIsAvailableChanged(bool value) => OnPropertyChanged(nameof(IsUnavailable));

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

        ShowAppliedOffsets(_gpuControl.GetOverclock());
        Persist();
    }

    /// <summary>Relit la carte après coup : si le pilote a rogné la demande, la valeur affichée ici
    /// diffère de celle des curseurs, ce qui explique un overclock "qui ne monte pas".</summary>
    private void ShowAppliedOffsets(GpuOverclockSnapshot? snapshot)
    {
        if (snapshot is not { ClockOffsetsSupported: true })
        {
            AppliedOffsetsText = "";
            return;
        }

        AppliedOffsetsText = $"Retenu par le pilote : cœur {Signed(snapshot.CoreOffsetMhz)} MHz, "
                             + $"mémoire {Signed(snapshot.MemoryOffsetMhz)} MHz.";
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
        ShowAppliedOffsets(_gpuControl.GetOverclock());
        Persist();
    }

    [RelayCommand]
    private void SaveProfile()
    {
        string name = NewProfileName.Trim();
        if (name.Length == 0) name = $"Profil {Profiles.Count + 1}";

        var profile = new GpuOverclockProfile
        {
            Name = name,
            CoreClockOffsetMhz = (int)Math.Round(CoreOffsetMhz),
            MemoryClockOffsetMhz = (int)Math.Round(MemoryOffsetMhz),
            PowerLimitPercent = (float)PowerLimitPercent,
            TemperatureLimitC = IsTemperatureLimitSupported ? (int)Math.Round(TemperatureLimitC) : null,
            VoltageBoostPercent = IsVoltageBoostSupported ? (int)Math.Round(VoltageBoostPercent) : null,
        };

        // Même nom = on remplace, pour pouvoir mettre un profil à jour sans le supprimer d'abord.
        GpuProfileViewModel? existing = Profiles.FirstOrDefault(
            p => string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase));
        if (existing is not null) Profiles.Remove(existing);

        Profiles.Add(new GpuProfileViewModel(profile));
        NewProfileName = "";
        OverclockStatus = $"Profil « {name} » enregistré.";
        Persist();
    }

    [RelayCommand]
    private void ApplyProfile(GpuProfileViewModel? profile)
    {
        if (profile is null || !IsAvailable) return;

        GpuOverclockProfile model = profile.Model;

        _suppressApply = true;
        if (IsClockOffsetSupported)
        {
            CoreOffsetMhz = Math.Clamp(model.CoreClockOffsetMhz, CoreOffsetMin, CoreOffsetMax);
            MemoryOffsetMhz = Math.Clamp(model.MemoryClockOffsetMhz, MemoryOffsetMin, MemoryOffsetMax);
        }
        if (model.PowerLimitPercent is { } power) PowerLimitPercent = Math.Clamp(power, PowerLimitMin, PowerLimitMax);
        if (IsTemperatureLimitSupported && model.TemperatureLimitC is { } temp)
            TemperatureLimitC = Math.Clamp(temp, TemperatureLimitMin, TemperatureLimitMax);
        if (IsVoltageBoostSupported && model.VoltageBoostPercent is { } volts)
            VoltageBoostPercent = Math.Clamp(volts, 0, 100);
        _suppressApply = false;

        bool ok = !IsClockOffsetSupported
                  || _gpuControl.TrySetClockOffsets((int)Math.Round(CoreOffsetMhz), (int)Math.Round(MemoryOffsetMhz));
        _gpuControl.TrySetPowerLimitPercent((float)PowerLimitPercent);
        if (IsTemperatureLimitSupported) _gpuControl.TrySetTemperatureLimit((int)Math.Round(TemperatureLimitC));
        if (IsVoltageBoostSupported) _gpuControl.TrySetVoltageBoostPercent((int)Math.Round(VoltageBoostPercent));

        OverclockStatus = ok
            ? $"Profil « {profile.Name} » appliqué."
            : $"Profil « {profile.Name} » appliqué, mais le pilote a refusé les décalages d'horloge.";

        ShowAppliedOffsets(_gpuControl.GetOverclock());
        Persist();
    }

    [RelayCommand]
    private void DeleteProfile(GpuProfileViewModel? profile)
    {
        if (profile is null) return;

        Profiles.Remove(profile);
        Persist();
    }

    private void OnSnapshotUpdated(HardwareSnapshot snapshot)
    {
        if (!IsAvailable) return;

        LoadPercent = snapshot.Gpu?.LoadPercent;
        CoreTempC = snapshot.Gpu?.CoreTempC;
        HotSpotTempC = snapshot.Gpu?.HotSpotTempC;
        CoreClockMhz = snapshot.Gpu?.CoreClockMhz;
        MemoryClockMhz = snapshot.Gpu?.MemoryClockMhz;
        PowerWatts = snapshot.Gpu?.PowerWatts;
        VramUsedMb = snapshot.Gpu?.VramUsedMb;
        FanRpm = snapshot.Gpu?.FanRpm;
        FanPercent = snapshot.Gpu?.FanPercent;

        ActiveLimitText = DescribeLimit(_gpuControl.GetActiveLimit());
    }

    private static string DescribeLimit(GpuPerformanceLimit? limit)
    {
        if (limit is not { } value) return "--";
        if (value == GpuPerformanceLimit.None) return "aucun";

        var reasons = new List<string>();
        if (value.HasFlag(GpuPerformanceLimit.Power)) reasons.Add("puissance");
        if (value.HasFlag(GpuPerformanceLimit.Temperature)) reasons.Add("température");
        if (value.HasFlag(GpuPerformanceLimit.Voltage)) reasons.Add("tension");
        if (value.HasFlag(GpuPerformanceLimit.NoLoad)) reasons.Add("pas de charge");
        if (value.HasFlag(GpuPerformanceLimit.Other)) reasons.Add("autre");

        return reasons.Count > 0 ? string.Join(", ", reasons) : "aucun";
    }

    /// <summary>Relit le fichier avant d'écrire : les autres onglets (ventilateurs, overlay) enregistrent
    /// aussi leurs réglages, et repartir d'une copie chargée au démarrage les effacerait.</summary>
    private void Persist()
    {
        if (_suppressApply) return;

        AppSettings settings = AppSettingsStore.Load();
        GpuControlSettings gpu = settings.Gpu;

        gpu.PowerLimitPercent = (float)PowerLimitPercent;
        gpu.CoreClockOffsetMhz = (int)Math.Round(CoreOffsetMhz);
        gpu.MemoryClockOffsetMhz = (int)Math.Round(MemoryOffsetMhz);
        gpu.TemperatureLimitC = IsTemperatureLimitSupported ? (int)Math.Round(TemperatureLimitC) : null;
        gpu.VoltageBoostPercent = IsVoltageBoostSupported ? (int)Math.Round(VoltageBoostPercent) : null;
        gpu.ApplyOverclockAtStartup = ApplyOverclockAtStartup;
        gpu.OverclockProfiles = Profiles.Select(p => p.Model).ToList();

        AppSettingsStore.Save(settings);
    }

    public void Dispose() => _monitoring.SnapshotUpdated -= OnSnapshotUpdated;
}
