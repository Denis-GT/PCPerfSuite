using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCPerfSuite.App.Utils;
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
            $"mém {Signed(model.MemoryClockOffsetMhz)}",
        };
        if (model.PowerLimitPercent is { } power) parts.Add($"{power:0}% puissance");
        if (model.TemperatureLimitC is { } temp) parts.Add($"{temp} °C max");
        if (model.GetVoltage() is { } voltage && voltage.Value != 0)
        {
            parts.Add(voltage.Unit == GpuVoltageUnit.Percent ? $"+{voltage.Value}% tension" : $"{voltage.Value} mV");
        }

        Summary = string.Join("  •  ", parts);
    }

    private static string Signed(int value) => value >= 0 ? $"+{value}" : value.ToString(CultureInfo.CurrentCulture);
}

/// <summary>
/// Contrôle GPU (onglet "GPU") : overclocking toutes marques — NVAPI (NVIDIA), ADLX (AMD Radeon) ou
/// IGCL (Intel Arc) selon la carte — décalages d'horloge cœur/mémoire, limite de puissance, limite de
/// température et tension quand la carte les accepte, avec profils enregistrés, relevés en direct et
/// affichage de ce qui bride la carte à l'instant T. Ce que la carte ou le pilote n'expose pas est
/// signalé avec la raison, plutôt que caché sans explication.
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

    /// <summary>Chaque consigne envoyée à la carte est suivie d'une relecture du pilote et d'un
    /// enregistrement du fichier de réglages : on attend que le curseur se pose plutôt que de le faire
    /// à chaque pixel parcouru.</summary>
    private readonly Debouncer _applyDebounce = new();

    // Une clé par réglage : les quatre curseurs de l'onglet partagent le même minuteur, mais chacun garde
    // sa consigne en attente.
    private const string PowerLimitKey = "limite de puissance";
    private const string TemperatureLimitKey = "limite de température";
    private const string VoltageKey = "tension";
    private const string ClockOffsetsKey = "décalages d'horloge";

    private double _powerLimitDefault = 100;
    private double _temperatureLimitDefault;
    private double _voltageDefault;
    private GpuVoltageUnit _voltageUnit = GpuVoltageUnit.Percent;

    [ObservableProperty] private bool isAvailable;
    public bool IsUnavailable => !IsAvailable;

    /// <summary>Pourquoi le contrôle GPU est absent sur ce PC : sans administrateur, pilote muet, ou carte dont
    /// aucune API d'overclocking ne s'occupe (GPU intégré, marque exotique). Le monitoring, lui, marche partout.</summary>
    public string UnavailableMessage
    {
        get
        {
            if (!ElevationHelper.IsAdministrator())
                return "PCPerfSuite n'est pas lancé en administrateur : les pilotes graphiques refusent alors tout réglage. " +
                       "Relance l'app en administrateur.";

            MachineInfo machine = MachineInfo.Current;
            if (DedicatedGpu(machine) is { } vendor)
                return $"Un GPU {vendor} est présent, mais son pilote ne répond pas (pilote absent, trop ancien, ou GPU désactivé). " +
                       "Installe le dernier pilote du constructeur de la carte.";

            string gpus = machine.VideoControllers.Count > 0 ? string.Join(", ", machine.VideoControllers) : "aucun GPU identifié";
            return $"Aucun GPU pilotable détecté. GPU de ce PC : {gpus}. L'overclocking demande une carte NVIDIA (NVAPI), " +
                   "une AMD Radeon RX 5000 ou plus récente (ADLX), ou une Intel Arc (IGCL). Les GPU intégrés (Intel UHD/Iris, " +
                   "Radeon des processeurs AMD) n'exposent pas ces réglages. Leur monitoring (charge, températures, fréquences) " +
                   "fonctionne dans l'onglet Monitoring.";
        }
    }

    /// <summary>Marque de la carte dédiée vue par Windows, quand il y en a une : c'est elle qui devrait être
    /// pilotable, donc son pilote qu'il faut soupçonner quand l'API ne répond pas.</summary>
    private static string? DedicatedGpu(MachineInfo machine)
    {
        foreach (string name in machine.VideoControllers)
        {
            if (name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || name.Contains("GeForce", StringComparison.OrdinalIgnoreCase))
                return "NVIDIA";
            // "Radeon Graphics" tout court est l'iGPU des processeurs AMD : seules les gammes RX et Pro sont dédiées.
            if (name.Contains("Radeon RX", StringComparison.OrdinalIgnoreCase) || name.Contains("Radeon Pro", StringComparison.OrdinalIgnoreCase))
                return "AMD Radeon";
            if (name.Contains("Arc", StringComparison.OrdinalIgnoreCase))
                return "Intel Arc";
        }

        return null;
    }

    [ObservableProperty] private string gpuName = "…";

    /// <summary>Marque et API utilisée, ex. "AMD · ADLX".</summary>
    [ObservableProperty] private string vendorLabel = "";

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
    [ObservableProperty] private string? activeLimitTooltip;

    [ObservableProperty] private bool isPowerLimitSupported;
    [ObservableProperty] private double powerLimitPercent;
    [ObservableProperty] private double powerLimitMin = 50;
    [ObservableProperty] private double powerLimitMax = 100;

    [ObservableProperty] private bool isCoreOffsetSupported;
    [ObservableProperty] private double coreOffsetMhz;
    [ObservableProperty] private double coreOffsetMin = -500;
    [ObservableProperty] private double coreOffsetMax = 1000;

    [ObservableProperty] private bool isMemoryOffsetSupported;
    [ObservableProperty] private double memoryOffsetMhz;
    [ObservableProperty] private double memoryOffsetMin = -1000;
    [ObservableProperty] private double memoryOffsetMax = 2000;
    [ObservableProperty] private string memoryOffsetUnit = "MHz";

    public bool IsClockOffsetSupported => IsCoreOffsetSupported || IsMemoryOffsetSupported;

    [ObservableProperty] private bool isTemperatureLimitSupported;
    [ObservableProperty] private double temperatureLimitC;
    [ObservableProperty] private double temperatureLimitMin = 60;
    [ObservableProperty] private double temperatureLimitMax = 90;

    [ObservableProperty] private bool isVoltageSupported;
    [ObservableProperty] private double voltageValue;
    [ObservableProperty] private double voltageMin;
    [ObservableProperty] private double voltageMax = 100;
    [ObservableProperty] private string voltageLabel = "Surtension cœur";

    /// <summary>Format d'affichage de la tension : "+10 %" (NVIDIA), "+25 mV" (décalage) ou "1 150 mV"
    /// (tension absolue des Radeon RDNA 1 à 3).</summary>
    [ObservableProperty] private string voltageFormat = "{0:0}%";

    /// <summary>Unité du champ de saisie de la tension : « % » chez NVIDIA, « mV » chez AMD et Intel.</summary>
    [ObservableProperty] private string voltageUnitLabel = "%";

    public string VoltageText => string.Format(CultureInfo.CurrentCulture, VoltageFormat, VoltageValue);

    /// <summary>Ce que cette carte n'expose pas, et pourquoi — "N/D" expliqué plutôt qu'un curseur qui
    /// disparaît sans raison. Null quand tout est disponible.</summary>
    [ObservableProperty] private string? unsupportedNotes;

    /// <summary>Intel impose un accord explicite de l'utilisateur avant tout overclock.</summary>
    [ObservableProperty] private bool isWaiverRequired;

    [ObservableProperty] private bool isWaiverAccepted;
    public bool CanOverclock => !IsWaiverRequired || IsWaiverAccepted;

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
            VendorLabel = _gpuControl.Vendor switch
            {
                GpuVendor.Nvidia => "NVIDIA · NVAPI",
                GpuVendor.Amd => "AMD · ADLX",
                GpuVendor.Intel => "Intel · IGCL",
                _ => "",
            };

            IsWaiverRequired = _gpuControl.RequiresOverclockWaiver;
            isWaiverAccepted = IsWaiverRequired && _settings.Gpu.IntelOverclockWaiverAccepted
                               && _gpuControl.TryAcceptOverclockWaiver();
            OnPropertyChanged(nameof(IsWaiverAccepted));
            OnPropertyChanged(nameof(CanOverclock));

            LoadPowerLimit();
            LoadOverclock();
            UnsupportedNotes = DescribeUnsupported();
        }

        _monitoring.SnapshotUpdated += OnSnapshotUpdated;
    }

    /// <summary>Vrai quand les réglages enregistrés ont été faits sur une carte de la même marque : après
    /// un changement de carte, on ne réapplique pas un overclock pensé pour une autre.</summary>
    private bool SettingsMatchCurrentGpu => (_settings.Gpu.OverclockVendor ?? GpuVendor.Nvidia) == _gpuControl.Vendor;

    private bool ShouldReapplyAtStartup => ApplyOverclockAtStartup && SettingsMatchCurrentGpu && CanOverclock;

    private void LoadPowerLimit()
    {
        GpuControlSnapshot? snap = _gpuControl.GetSnapshot();
        if (snap is null) return;

        GpuName = snap.Name;
        IsPowerLimitSupported = snap.PowerLimitSupported;
        PowerLimitMin = snap.PowerLimitMinPercent;
        PowerLimitMax = snap.PowerLimitMaxPercent;
        _powerLimitDefault = snap.PowerLimitDefaultPercent;

        // Passe par le champ, pas la propriété : évite de déclencher OnPowerLimitPercentChanged
        // (qui appliquerait/persisterait) juste pour peupler l'affichage initial. On ne réapplique
        // explicitement que si l'utilisateur avait déjà choisi une valeur lors d'une session précédente.
        bool reapply = IsPowerLimitSupported && SettingsMatchCurrentGpu && CanOverclock
                       && _settings.Gpu.PowerLimitPercent is not null;
        powerLimitPercent = reapply ? _settings.Gpu.PowerLimitPercent!.Value : snap.PowerLimitPercent;
        OnPropertyChanged(nameof(PowerLimitPercent));

        if (reapply) _gpuControl.TrySetPowerLimitPercent((float)PowerLimitPercent);
    }

    /// <summary>Lit l'état d'overclocking de la carte, puis — uniquement si l'utilisateur l'a demandé —
    /// réapplique celui enregistré.</summary>
    private void LoadOverclock()
    {
        GpuOverclockSnapshot? snap = _gpuControl.GetOverclock();
        if (snap is null) return;

        IsCoreOffsetSupported = snap.CoreOffsetSupported;
        CoreOffsetMin = snap.CoreOffsetMinMhz;
        CoreOffsetMax = snap.CoreOffsetMaxMhz;
        IsMemoryOffsetSupported = snap.MemoryOffsetSupported;
        MemoryOffsetMin = snap.MemoryOffsetMinMhz;
        MemoryOffsetMax = snap.MemoryOffsetMaxMhz;
        MemoryOffsetUnit = snap.MemoryOffsetUnit;
        OnPropertyChanged(nameof(IsClockOffsetSupported));

        IsTemperatureLimitSupported = snap.TemperatureLimitSupported;
        TemperatureLimitMin = snap.TemperatureLimitMinC;
        TemperatureLimitMax = snap.TemperatureLimitMaxC;
        _temperatureLimitDefault = snap.TemperatureLimitDefaultC;

        IsVoltageSupported = snap.VoltageSupported;
        VoltageMin = snap.VoltageMin;
        VoltageMax = snap.VoltageMax;
        _voltageDefault = snap.VoltageDefault;
        _voltageUnit = snap.VoltageUnit;
        VoltageUnitLabel = snap.VoltageUnit == GpuVoltageUnit.Percent ? "%" : "mV";
        (VoltageLabel, VoltageFormat) = (snap.VoltageUnit, snap.VoltageIsOffset) switch
        {
            (GpuVoltageUnit.Percent, _) => ("Surtension cœur", "{0:0}%"),
            (GpuVoltageUnit.Millivolts, true) => ("Décalage de tension", "{0:+0;-0;0} mV"),
            _ => ("Tension cœur", "{0:0} mV"),
        };

        bool reapply = ShouldReapplyAtStartup;
        (int Value, GpuVoltageUnit Unit)? savedVoltage = _settings.Gpu.GetVoltage();
        int? voltageToApply = savedVoltage is { } v && v.Unit == _voltageUnit ? v.Value : null;

        _suppressApply = true;
        CoreOffsetMhz = reapply ? _settings.Gpu.CoreClockOffsetMhz : snap.CoreOffsetMhz;
        MemoryOffsetMhz = reapply ? _settings.Gpu.MemoryClockOffsetMhz : snap.MemoryOffsetMhz;
        TemperatureLimitC = reapply
            ? _settings.Gpu.TemperatureLimitC ?? snap.TemperatureLimitC
            : snap.TemperatureLimitC;
        VoltageValue = reapply ? voltageToApply ?? snap.Voltage : snap.Voltage;
        _suppressApply = false;

        if (!reapply)
        {
            ShowAppliedOffsets(snap);
            return;
        }

        if (IsClockOffsetSupported) _gpuControl.TrySetClockOffsets((int)CoreOffsetMhz, (int)MemoryOffsetMhz);
        if (IsTemperatureLimitSupported && _settings.Gpu.TemperatureLimitC is { } temp) _gpuControl.TrySetTemperatureLimit(temp);
        if (IsVoltageSupported && voltageToApply is { } volts) _gpuControl.TrySetVoltage(volts);

        OverclockStatus = "Réglages enregistrés réappliqués au démarrage.";
        ShowAppliedOffsets(_gpuControl.GetOverclock());
    }

    /// <summary>Liste ce que la carte n'expose pas, avec la raison propre à la marque.</summary>
    private string? DescribeUnsupported()
    {
        string driver = _gpuControl.Vendor switch
        {
            GpuVendor.Amd => "le pilote AMD (ADLX)",
            GpuVendor.Intel => "le pilote Intel (IGCL)",
            _ => "le pilote NVIDIA (NVAPI)",
        };

        var missing = new List<string>();
        if (!IsPowerLimitSupported)
        {
            missing.Add(_gpuControl.Vendor == GpuVendor.Nvidia
                ? "limite de puissance (sur les GPU portables, c'est le constructeur du PC qui la fixe)"
                : "limite de puissance");
        }
        if (!IsCoreOffsetSupported) missing.Add("fréquence du cœur");
        if (!IsMemoryOffsetSupported) missing.Add("fréquence mémoire");
        if (!IsTemperatureLimitSupported)
        {
            missing.Add(_gpuControl.Vendor == GpuVendor.Amd
                ? "limite de température (AMD ne la propose pas au réglage)"
                : "limite de température");
        }
        if (!IsVoltageSupported)
        {
            missing.Add(_gpuControl.Vendor == GpuVendor.Nvidia
                ? "tension (NVIDIA ne l'ouvre qu'aux GPU Pascal, GTX 10xx)"
                : "tension");
        }

        return missing.Count == 0
            ? null
            : $"N/D sur cette carte, non exposé par {driver} : {string.Join(", ", missing)}.";
    }

    partial void OnIsAvailableChanged(bool value) => OnPropertyChanged(nameof(IsUnavailable));

    partial void OnIsCoreOffsetSupportedChanged(bool value) => OnPropertyChanged(nameof(IsClockOffsetSupported));

    partial void OnIsMemoryOffsetSupportedChanged(bool value) => OnPropertyChanged(nameof(IsClockOffsetSupported));

    partial void OnIsWaiverRequiredChanged(bool value) => OnPropertyChanged(nameof(CanOverclock));

    partial void OnIsWaiverAcceptedChanged(bool value)
    {
        if (value && !_gpuControl.TryAcceptOverclockWaiver())
        {
            OverclockStatus = "Le pilote Intel a refusé l'accord d'overclocking.";
            IsWaiverAccepted = false; // repasse par ici avec false, qui enregistre le refus
            return;
        }

        OnPropertyChanged(nameof(CanOverclock));

        AppSettings settings = AppSettingsStore.Load();
        settings.Gpu.IntelOverclockWaiverAccepted = IsWaiverAccepted;
        AppSettingsStore.Save(settings);
    }

    partial void OnPowerLimitPercentChanged(double value)
    {
        if (!IsAvailable || _suppressApply || !IsPowerLimitSupported) return;

        _applyDebounce.Schedule(PowerLimitKey, () =>
        {
            if (!_gpuControl.TrySetPowerLimitPercent((float)PowerLimitPercent))
            {
                OverclockStatus = "Le pilote a refusé la limite de puissance.";
            }
            Persist();
        });
    }

    partial void OnCoreOffsetMhzChanged(double value) => ApplyClockOffsets();

    partial void OnMemoryOffsetMhzChanged(double value) => ApplyClockOffsets();

    partial void OnTemperatureLimitCChanged(double value)
    {
        if (!IsAvailable || _suppressApply || !IsTemperatureLimitSupported) return;

        _applyDebounce.Schedule(TemperatureLimitKey, () =>
        {
            double limit = TemperatureLimitC;
            OverclockStatus = _gpuControl.TrySetTemperatureLimit((int)Math.Round(limit))
                ? $"Limite de température : {limit:0} °C."
                : "Le pilote a refusé la limite de température.";
            Persist();
        });
    }

    partial void OnVoltageFormatChanged(string value) => OnPropertyChanged(nameof(VoltageText));

    partial void OnVoltageValueChanged(double value)
    {
        OnPropertyChanged(nameof(VoltageText));
        if (!IsAvailable || _suppressApply || !IsVoltageSupported) return;

        _applyDebounce.Schedule(VoltageKey, () =>
        {
            OverclockStatus = _gpuControl.TrySetVoltage((int)Math.Round(VoltageValue))
                ? $"{VoltageLabel} : {VoltageText}."
                : "Le pilote a refusé la tension.";
            Persist();
        });
    }

    partial void OnApplyOverclockAtStartupChanged(bool value)
    {
        _gpuControl.KeepOverclockOnExit = value;
        Persist();
    }

    private void ApplyClockOffsets()
    {
        if (!IsAvailable || _suppressApply || !IsClockOffsetSupported) return;
        _applyDebounce.Schedule(ClockOffsetsKey, ApplyClockOffsetsNow);
    }

    private void ApplyClockOffsetsNow()
    {
        if (!IsAvailable || _suppressApply || !IsClockOffsetSupported) return;

        int core = (int)Math.Round(CoreOffsetMhz);
        int memory = (int)Math.Round(MemoryOffsetMhz);

        OverclockStatus = _gpuControl.TrySetClockOffsets(core, memory)
            ? $"Décalages appliqués : cœur {Signed(core)} MHz, mémoire {Signed(memory)} {MemoryOffsetUnit}."
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

        var parts = new List<string>();
        if (snapshot.CoreOffsetSupported) parts.Add($"cœur {Signed(snapshot.CoreOffsetMhz)} MHz");
        if (snapshot.MemoryOffsetSupported) parts.Add($"mémoire {Signed(snapshot.MemoryOffsetMhz)} {snapshot.MemoryOffsetUnit}");
        AppliedOffsetsText = $"Retenu par le pilote : {string.Join(", ", parts)}.";
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
        if (IsVoltageSupported) VoltageValue = _voltageDefault;
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
            PowerLimitPercent = IsPowerLimitSupported ? (float)PowerLimitPercent : null,
            TemperatureLimitC = IsTemperatureLimitSupported ? (int)Math.Round(TemperatureLimitC) : null,
            VoltageValue = IsVoltageSupported ? (int)Math.Round(VoltageValue) : null,
            VoltageUnit = IsVoltageSupported ? _voltageUnit : null,
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

        if (!CanOverclock)
        {
            OverclockStatus = "Accepte d'abord l'avertissement Intel ci-dessus pour appliquer un profil.";
            return;
        }

        GpuOverclockProfile model = profile.Model;
        int? voltage = model.GetVoltage() is { } v && v.Unit == _voltageUnit ? v.Value : null;

        _suppressApply = true;
        if (IsCoreOffsetSupported) CoreOffsetMhz = Math.Clamp(model.CoreClockOffsetMhz, CoreOffsetMin, CoreOffsetMax);
        if (IsMemoryOffsetSupported) MemoryOffsetMhz = Math.Clamp(model.MemoryClockOffsetMhz, MemoryOffsetMin, MemoryOffsetMax);
        if (IsPowerLimitSupported && model.PowerLimitPercent is { } power)
            PowerLimitPercent = Math.Clamp(power, PowerLimitMin, PowerLimitMax);
        if (IsTemperatureLimitSupported && model.TemperatureLimitC is { } temp)
            TemperatureLimitC = Math.Clamp(temp, TemperatureLimitMin, TemperatureLimitMax);
        if (IsVoltageSupported && voltage is { } volts)
            VoltageValue = Math.Clamp(volts, VoltageMin, VoltageMax);
        _suppressApply = false;

        bool ok = !IsClockOffsetSupported
                  || _gpuControl.TrySetClockOffsets((int)Math.Round(CoreOffsetMhz), (int)Math.Round(MemoryOffsetMhz));
        if (IsPowerLimitSupported) _gpuControl.TrySetPowerLimitPercent((float)PowerLimitPercent);
        if (IsTemperatureLimitSupported) _gpuControl.TrySetTemperatureLimit((int)Math.Round(TemperatureLimitC));
        if (IsVoltageSupported && voltage is not null) _gpuControl.TrySetVoltage((int)Math.Round(VoltageValue));

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

        RefreshActiveLimit();
    }

    /// <summary>Intervalle minimal entre deux interrogations du pilote sur ce qui bride la carte.</summary>
    private static readonly TimeSpan ActiveLimitInterval = TimeSpan.FromSeconds(1);

    private DateTime _lastActiveLimitRead = DateTime.MinValue;

    /// <summary>
    /// Ce qui bride la carte, au plus une fois par seconde. L'appel est synchrone et part sur le thread
    /// UI (les abonnés au relevé y sont appelés) : le faire à chaque relevé, soit jusqu'à dix fois par
    /// seconde, coûterait dix allers-retours pilote par seconde pour un simple indicateur textuel que
    /// personne ne lit à cette cadence.
    /// </summary>
    private void RefreshActiveLimit()
    {
        DateTime now = DateTime.UtcNow;
        if (now - _lastActiveLimitRead < ActiveLimitInterval) return;
        _lastActiveLimitRead = now;

        GpuPerformanceLimit? limit = _gpuControl.GetActiveLimit();
        ActiveLimitText = DescribeLimit(limit);
        ActiveLimitTooltip = limit is null
            ? "N/D : le pilote de cette carte n'indique pas ce qui limite sa fréquence."
            : null;
    }

    private static string DescribeLimit(GpuPerformanceLimit? limit)
    {
        if (limit is not { } value) return "N/D";
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

        gpu.ApplyOverclockAtStartup = ApplyOverclockAtStartup;
        gpu.OverclockProfiles = Profiles.Select(p => p.Model).ToList();

        // Sans carte pilotable, on ne touche pas aux réglages d'overclock enregistrés : ils restent
        // valables pour la carte sur laquelle ils ont été faits.
        if (!IsAvailable)
        {
            AppSettingsStore.Save(settings);
            return;
        }

        gpu.PowerLimitPercent = IsPowerLimitSupported ? (float)PowerLimitPercent : null;
        gpu.CoreClockOffsetMhz = (int)Math.Round(CoreOffsetMhz);
        gpu.MemoryClockOffsetMhz = (int)Math.Round(MemoryOffsetMhz);
        gpu.TemperatureLimitC = IsTemperatureLimitSupported ? (int)Math.Round(TemperatureLimitC) : null;
        gpu.VoltageValue = IsVoltageSupported ? (int)Math.Round(VoltageValue) : null;
        gpu.VoltageUnit = IsVoltageSupported ? _voltageUnit : null;
        gpu.VoltageBoostPercent = null;
        gpu.OverclockVendor = _gpuControl.Vendor;

        AppSettingsStore.Save(settings);
    }

    /// <summary>Le réglage encore en attente est appliqué avant de partir, pour ne pas le perdre si
    /// l'utilisateur ferme l'app juste après avoir lâché un curseur.</summary>
    public void Dispose()
    {
        _applyDebounce.Flush();
        _monitoring.SnapshotUpdated -= OnSnapshotUpdated;
    }
}
