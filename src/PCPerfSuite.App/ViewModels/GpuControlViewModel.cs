using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCPerfSuite.App.Utils;
using PCPerfSuite.Core.Hardware;
using PCPerfSuite.Core.Hardware.Gpu;
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
public sealed partial class GpuControlViewModel : ObservableObject, IDisposable, IBackgroundSensorConsumer
{
    private readonly GpuControlService _gpuControl;
    private readonly MonitoringViewModel _monitoring;

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

    /// <summary>Vrai quand la plage d'un décalage n'est pas celle de la carte mais une plage prudente par défaut
    /// (NVAPI sans limites exploitables) : dit dans <see cref="UnsupportedNotes"/>.</summary>
    private bool _clockRangeIsFallback;

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

            // Le refus vient de PCPerfSuite, pas de la carte : sans cette branche, l'utilisateur lisait que
            // son pilote ne répond pas, le réinstallait, et voyait le même message — le témoin, lui, ne
            // bouge pas. C'est ici que la règle 3 veut la raison exacte, pas seulement dans le diagnostic.
            if (AdlxProbeGuard.PreviousAttemptCrashed && DedicatedGpu(machine) == "AMD Radeon")
            {
                return "PCPerfSuite ne s'est pas relancée deux fois de suite après avoir interrogé le pilote AMD (ADLX) : " +
                       "le contrôle GPU n'est donc plus tenté, pour que l'app démarre. Après une mise à jour du pilote " +
                       $"Adrenalin, supprime le fichier {AdlxProbeGuard.SentinelFilePath} pour lui rendre sa chance " +
                       "(voir Paramètres › Compatibilité de ce PC).";
            }

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

    /// <summary>NVAPI (NVIDIA) est la seule API testée sur une vraie machine à ce jour (voir CLAUDE.md,
    /// règle 6) ; ADLX (AMD) et IGCL (Intel) reposent sur une intégration native pas encore vérifiée sur
    /// le terrain, et une AccessViolation dans l'une d'elles ne peut pas être rattrapée par .NET.</summary>
    public bool IsExperimentalBackend => _gpuControl.Vendor is GpuVendor.Amd or GpuVendor.Intel;

    public string? ExperimentalNotice => IsExperimentalBackend
        ? $"Intégration {VendorLabel} pas encore vérifiée sur une machine réelle : à utiliser avec prudence."
        : null;

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
        AppSettings settings = AppSettingsStore.Load();

        applyOverclockAtStartup = settings.Gpu.ApplyOverclockAtStartup;

        IsAvailable = _gpuControl.TryInitialize();
        _gpuControl.KeepOverclockOnExit = applyOverclockAtStartup;

        foreach (GpuOverclockProfile profile in settings.Gpu.OverclockProfiles)
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
            OnPropertyChanged(nameof(IsExperimentalBackend));
            OnPropertyChanged(nameof(ExperimentalNotice));

            IsWaiverRequired = _gpuControl.RequiresOverclockWaiver;
            isWaiverAccepted = IsWaiverRequired && settings.Gpu.IntelOverclockWaiverAccepted
                               && _gpuControl.TryAcceptOverclockWaiver();
            OnPropertyChanged(nameof(IsWaiverAccepted));
            OnPropertyChanged(nameof(CanOverclock));

            LoadPowerLimit();
            LoadOverclock();
            UnsupportedNotes = DescribeUnsupported();

            // Rien n'est posé au lancement sans la case « Appliquer au démarrage ».
            if (ShouldReapplyAtStartup(settings.Gpu))
            {
                ReapplySaved(settings.Gpu, "Réglages enregistrés réappliqués au démarrage.");
            }
            else if (ApplyOverclockAtStartup && !SettingsMatchCurrentGpu(settings.Gpu))
            {
                // Règle 3 : dire pourquoi « Appliquer au démarrage » n'a rien fait.
                GpuIdentity other = settings.Gpu.OverclockGpu ?? new GpuIdentity(settings.Gpu.OverclockVendor ?? GpuVendor.Nvidia);
                OverclockStatus = $"Réglages enregistrés non réappliqués : ils ont été faits sur une autre carte ({other.Describe()}).";
            }
        }

        _monitoring.SnapshotUpdated += OnSnapshotUpdated;
        InitializeSafety();
    }

    /// <summary>Vrai quand les réglages enregistrés ont été faits sur cette carte : même marque et, quand ils sont
    /// connus des deux côtés, même nom et mêmes identifiants PCI (<see cref="GpuIdentity.Matches"/>). Après un
    /// changement de carte, on ne réapplique pas un overclock pensé pour une autre. Un fichier d'avant, qui n'a que la
    /// marque (null = NVIDIA, seule marque gérée alors), se compare sur elle seule.</summary>
    private bool SettingsMatchCurrentGpu(GpuControlSettings saved)
    {
        GpuIdentity? current = _gpuControl.Identity ?? (_gpuControl.Vendor is { } vendor ? new GpuIdentity(vendor) : null);
        if (current is null) return false;

        return GpuIdentity.Matches(saved.OverclockGpu ?? new GpuIdentity(saved.OverclockVendor ?? GpuVendor.Nvidia), current);
    }

    private bool ShouldReapplyAtStartup(GpuControlSettings saved)
        => ApplyOverclockAtStartup && SettingsMatchCurrentGpu(saved) && CanOverclock;

    /// <summary>Lit la limite de puissance de la carte, sans rien lui écrire (lancement, réveil de veille).</summary>
    private void LoadPowerLimit()
    {
        GpuControlSnapshot? snap = _gpuControl.GetSnapshot();
        if (snap is null) return;

        GpuName = snap.Name;
        IsPowerLimitSupported = snap.PowerLimitSupported;
        PowerLimitMin = snap.PowerLimitMinPercent;
        PowerLimitMax = snap.PowerLimitMaxPercent;
        _powerLimitDefault = snap.PowerLimitDefaultPercent;

        // Sous _suppressApply : OnPowerLimitPercentChanged appliquerait et enregistrerait, pour un simple affichage.
        _suppressApply = true;
        PowerLimitPercent = snap.PowerLimitPercent;
        _suppressApply = false;
    }

    /// <summary>Lit l'état d'overclocking de la carte (plages et valeurs), sans rien lui écrire.</summary>
    private void LoadOverclock()
    {
        GpuOverclockSnapshot? snap = _gpuControl.GetOverclock();
        if (snap is null) return;

        _clockRangeIsFallback = snap.CoreOffsetRangeIsFallback || snap.MemoryOffsetRangeIsFallback;

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

        _suppressApply = true;
        CoreOffsetMhz = snap.CoreOffsetMhz;
        MemoryOffsetMhz = snap.MemoryOffsetMhz;
        TemperatureLimitC = snap.TemperatureLimitC;
        VoltageValue = snap.Voltage;
        _suppressApply = false;

        ShowAppliedOffsets(snap);
    }

    /// <summary>
    /// Réapplique les réglages enregistrés, bornés aux plages de cette carte, puis montre ce qu'elle a retenu. Seulement
    /// pour une carte reconnue (<see cref="ShouldReapplyAtStartup"/>, vérifié par l'appelant). Un réglage que la carte
    /// n'expose pas, jamais choisi, ou dans une autre unité de tension (50 % ≠ 50 mV) reste tel quel.
    /// </summary>
    private void ReapplySaved(GpuControlSettings saved, string context)
    {
        int? voltage = saved.GetVoltage() is { } v && v.Unit == _voltageUnit ? v.Value : null;
        var request = new GpuOverclockRequest
        {
            CoreOffsetMhz = IsCoreOffsetSupported ? (int)ClampTo(saved.CoreClockOffsetMhz, CoreOffsetMin, CoreOffsetMax) : null,
            MemoryOffsetMhz = IsMemoryOffsetSupported ? (int)ClampTo(saved.MemoryClockOffsetMhz, MemoryOffsetMin, MemoryOffsetMax) : null,
            PowerLimitPercent = IsPowerLimitSupported && saved.PowerLimitPercent is { } power
                ? (float)ClampTo(power, PowerLimitMin, PowerLimitMax) : null,
            TemperatureLimitC = IsTemperatureLimitSupported && saved.TemperatureLimitC is { } temp
                ? (int)ClampTo(temp, TemperatureLimitMin, TemperatureLimitMax) : null,
            Voltage = IsVoltageSupported && voltage is { } volts ? (int)ClampTo(volts, VoltageMin, VoltageMax) : null,
            VoltageUnit = _voltageUnit,
        };

        _suppressApply = true;
        if (request.CoreOffsetMhz is { } core) CoreOffsetMhz = core;
        if (request.MemoryOffsetMhz is { } memory) MemoryOffsetMhz = memory;
        if (request.PowerLimitPercent is { } percent) PowerLimitPercent = percent;
        if (request.TemperatureLimitC is { } celsius) TemperatureLimitC = celsius;
        if (request.Voltage is { } value) VoltageValue = value;
        _suppressApply = false;

        GpuApplyReport report = _gpuControl.ApplyAndVerify(request);
        OverclockStatus = Summarize(report, context);
        AppliedOffsetsText = report.Describe();
    }

    /// <summary>Le statut d'une application, d'après ce que la carte a relu.</summary>
    private static string Summarize(GpuApplyReport report, string done)
    {
        if (report.AnyRefused) return $"{done} Le pilote en a refusé une partie.";
        if (!report.AllRetained) return $"{done} La carte n'a pas tout retenu tel quel.";
        return done;
    }

    /// <summary>Math.Clamp lève si la plage est inversée ; une plage absurde lue sur un pilote ne doit rien casser.</summary>
    private static double ClampTo(double value, double min, double max) => max < min ? value : Math.Clamp(value, min, max);

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

        var notes = new List<string>();
        if (missing.Count > 0) notes.Add($"N/D sur cette carte, non exposé par {driver} : {string.Join(", ", missing)}.");
        if (_clockRangeIsFallback)
        {
            notes.Add("Plage prudente par défaut pour les décalages d'horloge : le pilote n'a pas donné les limites de cette " +
                      "carte. Ce ne sont pas ses vraies limites : la valeur retenue par la carte fait foi.");
        }

        return notes.Count == 0 ? null : string.Join(" ", notes);
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

        bool accepted = IsWaiverAccepted;
        AppSettingsStore.Update(settings => settings.Gpu.IntelOverclockWaiverAccepted = accepted);
    }

    partial void OnPowerLimitPercentChanged(double value)
    {
        if (!IsAvailable || _suppressApply || !IsPowerLimitSupported) return;

        _applyDebounce.Schedule(PowerLimitKey, () =>
        {
            GpuApplyReport report = _gpuControl.ApplyAndVerify(new GpuOverclockRequest { PowerLimitPercent = (float)PowerLimitPercent });
            OverclockStatus = report.AnyRefused
                ? "Le pilote a refusé la limite de puissance."
                : Summarize(report, $"Limite de puissance : {PowerLimitPercent:0} %.");
            AppliedOffsetsText = report.Describe();
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
            GpuApplyReport report = _gpuControl.ApplyAndVerify(new GpuOverclockRequest { TemperatureLimitC = (int)Math.Round(limit) });
            OverclockStatus = report.AnyRefused
                ? "Le pilote a refusé la limite de température."
                : Summarize(report, $"Limite de température : {limit:0} °C.");
            AppliedOffsetsText = report.Describe();
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
            GpuApplyReport report = _gpuControl.ApplyAndVerify(
                new GpuOverclockRequest { Voltage = (int)Math.Round(VoltageValue), VoltageUnit = _voltageUnit });
            OverclockStatus = report.AnyRefused
                ? "Le pilote a refusé la tension."
                : Summarize(report, $"{VoltageLabel} : {VoltageText}.");
            AppliedOffsetsText = report.Describe();
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

        GpuApplyReport report = _gpuControl.ApplyAndVerify(new GpuOverclockRequest
        {
            CoreOffsetMhz = IsCoreOffsetSupported ? core : null,
            MemoryOffsetMhz = IsMemoryOffsetSupported ? memory : null,
        });

        OverclockStatus = report.AnyRefused
            ? "Le pilote a refusé les décalages d'horloge (carte verrouillée, ou app lancée sans les droits administrateur)."
            : Summarize(report, $"Décalages appliqués : cœur {Signed(core)} MHz, mémoire {Signed(memory)} {MemoryOffsetUnit}.");
        AppliedOffsetsText = report.Describe();
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
        CancelPendingApplies();

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

        // Une consigne de curseur encore en attente écraserait le profil juste après.
        CancelPendingApplies();

        _suppressApply = true;
        if (IsCoreOffsetSupported) CoreOffsetMhz = ClampTo(model.CoreClockOffsetMhz, CoreOffsetMin, CoreOffsetMax);
        if (IsMemoryOffsetSupported) MemoryOffsetMhz = ClampTo(model.MemoryClockOffsetMhz, MemoryOffsetMin, MemoryOffsetMax);
        if (IsPowerLimitSupported && model.PowerLimitPercent is { } power)
            PowerLimitPercent = ClampTo(power, PowerLimitMin, PowerLimitMax);
        if (IsTemperatureLimitSupported && model.TemperatureLimitC is { } temp)
            TemperatureLimitC = ClampTo(temp, TemperatureLimitMin, TemperatureLimitMax);
        if (IsVoltageSupported && voltage is { } volts)
            VoltageValue = ClampTo(volts, VoltageMin, VoltageMax);
        _suppressApply = false;

        // Les limites de puissance et de température sont reposées même absentes du profil, comme avant : ce sont alors
        // les valeurs affichées.
        GpuApplyReport report = _gpuControl.ApplyAndVerify(new GpuOverclockRequest
        {
            CoreOffsetMhz = IsCoreOffsetSupported ? (int)Math.Round(CoreOffsetMhz) : null,
            MemoryOffsetMhz = IsMemoryOffsetSupported ? (int)Math.Round(MemoryOffsetMhz) : null,
            PowerLimitPercent = IsPowerLimitSupported ? (float)PowerLimitPercent : null,
            TemperatureLimitC = IsTemperatureLimitSupported ? (int)Math.Round(TemperatureLimitC) : null,
            Voltage = IsVoltageSupported && voltage is not null ? (int)Math.Round(VoltageValue) : null,
            VoltageUnit = _voltageUnit,
        });

        OverclockStatus = Summarize(report, $"Profil « {profile.Name} » appliqué.");
        AppliedOffsetsText = report.Describe();
        Persist();
    }

    [RelayCommand]
    private void DeleteProfile(GpuProfileViewModel? profile)
    {
        if (profile is null) return;

        var result = System.Windows.MessageBox.Show(
            $"Supprimer le profil « {profile.Name} » ? Cette action ne peut pas être annulée.",
            "Supprimer le profil", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.No);
        if (result != System.Windows.MessageBoxResult.Yes) return;

        Profiles.Remove(profile);
        Persist();
    }

    private void OnSnapshotUpdated(HardwareSnapshot snapshot)
    {
        if (!IsAvailable) return;

        // La sécurité thermique d'abord, fenêtre cachée comprise : c'est elle qui retire l'overclock d'une carte
        // restée trop chaude.
        NoteSafety(snapshot);

        // Mode éco (fenêtre cachée) : le reste n'est que de l'affichage, plus une interrogation du pilote par seconde.
        if (_monitoring.IsBackgroundMode) return;

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

        // Tout est lu avant le verrou : le lambda ne fait que des affectations.
        bool applyAtStartup = ApplyOverclockAtStartup;
        List<GpuOverclockProfile> profiles = Profiles.Select(p => p.Model).ToList();
        bool available = IsAvailable;
        float? power = IsPowerLimitSupported ? (float)PowerLimitPercent : null;
        int core = (int)Math.Round(CoreOffsetMhz);
        int memory = (int)Math.Round(MemoryOffsetMhz);
        int? temperature = IsTemperatureLimitSupported ? (int)Math.Round(TemperatureLimitC) : null;
        int? voltage = IsVoltageSupported ? (int)Math.Round(VoltageValue) : null;
        GpuVoltageUnit? voltageUnit = IsVoltageSupported ? _voltageUnit : null;
        GpuVendor? vendor = _gpuControl.Vendor;
        GpuIdentity? identity = _gpuControl.Identity;

        AppSettingsStore.Update(settings =>
        {
            GpuControlSettings gpu = settings.Gpu;
            gpu.ApplyOverclockAtStartup = applyAtStartup;
            gpu.OverclockProfiles = profiles;

            // Sans carte pilotable, on ne touche pas aux réglages d'overclock enregistrés : ils restent
            // valables pour la carte sur laquelle ils ont été faits.
            if (!available) return;

            gpu.PowerLimitPercent = power;
            gpu.CoreClockOffsetMhz = core;
            gpu.MemoryClockOffsetMhz = memory;
            gpu.TemperatureLimitC = temperature;
            gpu.VoltageValue = voltage;
            gpu.VoltageUnit = voltageUnit;
            gpu.VoltageBoostPercent = null;
            gpu.OverclockVendor = vendor;
            gpu.OverclockGpu = identity;
        });
    }

    /// <summary>Le réglage encore en attente est appliqué avant de partir, pour ne pas le perdre si
    /// l'utilisateur ferme l'app juste après avoir lâché un curseur.</summary>
    public void Dispose()
    {
        _applyDebounce.Flush();
        _monitoring.SnapshotUpdated -= OnSnapshotUpdated;
        DisposeSafety();
    }
}
