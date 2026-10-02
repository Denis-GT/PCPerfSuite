using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCPerfSuite.App.Utils;
using PCPerfSuite.Core.Hardware;
using PCPerfSuite.Core.Hardware.Cpu;
using PCPerfSuite.Core.PowerSettings;
using PCPerfSuite.Core.Profiles;

namespace PCPerfSuite.App.ViewModels;

/// <summary>
/// Un réglage d'alimentation processeur, tel qu'affiché : une liste déroulante pour les réglages à
/// choix, un curseur pour les autres, et deux valeurs quand la machine a une batterie.
///
/// Chaque modification est écrite dans le plan d'alimentation actif dès que le curseur se pose, puis
/// relue : si Windows ne retient pas la valeur, l'utilisateur le voit tout de suite.
/// </summary>
public sealed partial class CpuPowerSettingViewModel : ObservableObject
{
    private readonly CpuPowerTuningService _service;
    private readonly CpuPowerSetting _setting;
    private readonly Action<string> _report;

    /// <summary>Pourquoi une écriture manuelle est refusée (bail de réglage tenu par un autre), null si elle est permise.</summary>
    private readonly Func<string?> _manualRefusal;

    /// <summary>Écrire un réglage d'alimentation réapplique le plan d'alimentation entier au système
    /// (PowerSetActiveScheme) : c'est l'écriture la plus lourde de l'app, et un curseur en lèverait une
    /// par pixel parcouru.</summary>
    private readonly Debouncer _writeDebounce = new();

    private bool _suppressWrite;

    /// <summary>Identifiant stable du réglage, clé de ce réglage dans un profil enregistré.</summary>
    public string Id => _setting.Id;

    public string Label => _setting.Label;
    public string Description => _setting.Description;
    public IReadOnlyList<CpuPowerChoice>? Choices => _setting.Choices;

    public bool IsChoice => _setting.Choices is not null;
    public bool IsNumeric => _setting.Choices is null;
    public bool ShowBattery { get; }

    public double Min => _setting.Min;
    public double Max => _setting.Max;

    /// <summary>Unité du champ de saisie (« % », « MHz »), sans l'espace qui la précède dans le catalogue.</summary>
    public string UnitLabel => _setting.Unit.Trim();

    /// <summary>« 0 = Illimitée » pour un réglage dont le zéro a un sens particulier, vide sinon : sans curseur,
    /// rien d'autre ne dit qu'une valeur nulle est permise et ce qu'elle veut dire.</summary>
    public string ZeroNote => _setting.ZeroLabel is { } zero ? $"0 = {zero}" : "";

    [ObservableProperty] private CpuPowerChoice? acChoice;
    [ObservableProperty] private CpuPowerChoice? batteryChoice;
    [ObservableProperty] private double acValue;
    [ObservableProperty] private double batteryValue;

    public CpuPowerSettingViewModel(
        CpuPowerTuningService service, CpuPowerSetting setting, uint onAc, uint onBattery, Action<string> report,
        Func<string?> manualRefusal)
    {
        _service = service;
        _setting = setting;
        _report = report;
        _manualRefusal = manualRefusal;
        ShowBattery = service.HasBattery;

        _suppressWrite = true;
        acValue = onAc;
        batteryValue = onBattery;
        acChoice = setting.Choices?.FirstOrDefault(c => c.Value == onAc) ?? setting.Choices?.FirstOrDefault();
        batteryChoice = setting.Choices?.FirstOrDefault(c => c.Value == onBattery) ?? setting.Choices?.FirstOrDefault();
        _suppressWrite = false;
    }

    /// <summary>La valeur telle qu'on l'annonce dans les messages d'état : « Illimitée » plutôt que « 0 MHz ».</summary>
    private string Format(double value)
    {
        if (_setting.ZeroLabel is { } zero && value == 0) return zero;
        return $"{value:0}{_setting.Unit}";
    }

    partial void OnAcValueChanged(double value) => Write();

    partial void OnBatteryValueChanged(double value) => Write();

    partial void OnAcChoiceChanged(CpuPowerChoice? value)
    {
        if (value is not null && !_suppressWrite) AcValue = value.Value;
    }

    partial void OnBatteryChoiceChanged(CpuPowerChoice? value)
    {
        if (value is not null && !_suppressWrite) BatteryValue = value.Value;
    }

    /// <summary>Une valeur en clair, pour le résumé d'un profil : le libellé du choix, ou le nombre et
    /// son unité.</summary>
    public string Describe(uint value)
        => _setting.Choices?.FirstOrDefault(c => c.Value == value)?.Label ?? Format(value);

    /// <summary>Les valeurs en place, pour les enregistrer dans un profil.</summary>
    public CpuProfilePowerValue Capture() => new()
    {
        Ac = (uint)Math.Round(AcValue),
        Battery = ShowBattery ? (uint)Math.Round(BatteryValue) : null,
    };

    /// <summary>
    /// Repose les valeurs d'un profil, puis écrit comme n'importe quelle modification (avec sa relecture
    /// de vérification). Renvoie faux si rien n'était applicable : le profil vient peut-être d'une autre
    /// machine, où ce réglage acceptait des valeurs que ce Windows-ci ne propose pas.
    /// </summary>
    public bool ApplyFromProfile(CpuProfilePowerValue value)
    {
        uint? ac = value.Ac is { } rawAc ? Sanitize(rawAc) : null;
        uint? battery = ShowBattery && value.Battery is { } rawBattery ? Sanitize(rawBattery) : null;
        if (ac is null && battery is null) return false;

        _suppressWrite = true;
        if (ac is { } acValue)
        {
            AcValue = acValue;
            if (_setting.Choices is { } choices) AcChoice = choices.FirstOrDefault(c => c.Value == acValue) ?? AcChoice;
        }

        if (battery is { } batteryValue)
        {
            BatteryValue = batteryValue;
            if (_setting.Choices is { } choices) BatteryChoice = choices.FirstOrDefault(c => c.Value == batteryValue) ?? BatteryChoice;
        }

        _suppressWrite = false;

        Write();
        return true;
    }

    /// <summary>Voir <see cref="CpuPowerSetting.Sanitize"/>.</summary>
    private uint? Sanitize(uint value) => _setting.Sanitize(value);

    private void Write()
    {
        if (_suppressWrite) return;
        _writeDebounce.Schedule(Label, WriteNow);
    }

    /// <summary>Applique le dernier réglage en attente sans attendre le délai — fermeture de l'app.</summary>
    public void FlushPendingWrite() => _writeDebounce.Flush();

    private void WriteNow()
    {
        if (_suppressWrite) return;

        // Bail tenu par un autre (bench, recherche d'OC) : on n'écrit pas, et l'affichage revient à ce que Windows a.
        if (_manualRefusal() is { } refusal)
        {
            ReloadFromWindows();
            _report($"« {Label} » non modifié : {refusal}");
            return;
        }

        uint ac = (uint)Math.Round(AcValue);
        uint battery = ShowBattery ? (uint)Math.Round(BatteryValue) : ac;

        if (!_service.TryWrite(_setting, ac, battery))
        {
            _report($"Windows a refusé le réglage « {Label} » (app lancée sans les droits administrateur ?).");
            return;
        }

        // Relecture : Windows accepte l'écriture mais peut retenir autre chose, par exemple quand un
        // réglage est piloté par le fabricant du PC.
        if (_service.TryRead(_setting, out uint appliedAc, out uint appliedBattery)
            && (appliedAc != ac || (ShowBattery && appliedBattery != battery)))
        {
            _suppressWrite = true;
            AcValue = appliedAc;
            BatteryValue = appliedBattery;

            // Les listes déroulantes se resynchronisent aussi : sans ça, un réglage à choix continuerait
            // d'afficher la valeur demandée alors que Windows en a retenu une autre.
            if (_setting.Choices is { } choices)
            {
                AcChoice = choices.FirstOrDefault(c => c.Value == appliedAc);
                BatteryChoice = choices.FirstOrDefault(c => c.Value == appliedBattery);
            }

            _suppressWrite = false;

            _report($"« {Label} » : Windows a retenu {Format(appliedAc)} au lieu de {Format(ac)}.");
            return;
        }

        _report(ShowBattery
            ? $"« {Label} » : {Format(ac)} sur secteur, {Format(battery)} sur batterie."
            : $"« {Label} » : {Format(ac)}.");
    }
}

/// <summary>Un profil processeur enregistré, tel qu'affiché dans la liste des profils. Le résumé est
/// calculé par l'onglet, seul à connaître les libellés et les unités des réglages de CETTE machine.</summary>
public sealed class CpuProfileViewModel
{
    public CpuProfile Model { get; }
    public string Name => Model.Name;
    public string Summary { get; }

    public CpuProfileViewModel(CpuProfile model, string summary)
    {
        Model = model;
        Summary = summary;
    }
}

/// <summary>
/// Onglet "Processeur" : la limite de puissance du CPU, en watts. Le visuel par cœur et le parking sont dans le
/// sous-onglet « Cœurs » (<see cref="CoreParkingViewModel"/>).
///
/// C'est le réglage qui change le plus le comportement d'un PC, dans les deux sens. L'abaisser fait
/// chuter la température, le bruit et la consommation pour une perte de performance souvent minime —
/// c'est le réglage le plus utile sur un portable. La relever laisse le processeur tenir ses fréquences
/// plus longtemps, tant que le refroidissement suit.
///
/// Tout est annoncé plutôt que supposé : la plateforme détectée, l'état du pilote, et pour chaque
/// fonction indisponible la raison exacte. Une limite écrite est systématiquement relue — si le firmware
/// impose la sienne, l'interface le dit au lieu d'afficher une valeur que le processeur ignore.
/// </summary>
public sealed partial class CpuControlViewModel : ObservableObject, IDisposable, IBackgroundSensorConsumer
{
    private readonly CpuControlService _cpu;
    private readonly MonitoringViewModel _monitoring;

    /// <summary>Une seule ligne PawnIO pour toute l'app, celle de Paramètres › Installations : le bouton d'ici lance
    /// donc le même téléchargement, avec la même progression, et les deux onglets ne se contredisent jamais.</summary>
    public PawnIoItemViewModel PawnIo { get; }
    private readonly CpuPowerTuningService _powerTuning;

    /// <summary>Bloque l'application pendant qu'on repositionne plusieurs curseurs d'un coup.</summary>
    private bool _suppressApply;

    /// <summary>Une écriture de limite de puissance vaut un aller-retour MSR ou SMU, une relecture de
    /// vérification et un enregistrement du fichier de réglages : on attend que le curseur se pose.</summary>
    private readonly Debouncer _applyDebounce = new();

    private const string PowerLimitKey = "limites de puissance";

    private float _defaultSustainedWatts;
    private float _defaultBurstWatts;

    [ObservableProperty] private string cpuName = "…";
    [ObservableProperty] private string platformText = "";
    [ObservableProperty] private string driverText = "";

    /// <summary>Règle 6 de CLAUDE.md, décidée par le backend : la boîte aux lettres SMU d'AMD est pilotée sans
    /// documentation officielle, et l'écriture de PL1/PL2 Intel par le module IntelMSR livré avec l'app n'a pas encore
    /// été vérifiée sur une vraie machine.</summary>
    public bool IsExperimentalBackend => _cpu.Backend.IsExperimental;

    public string? ExperimentalNotice => !IsExperimentalBackend
        ? null
        : _cpu.Platform.Vendor == CpuVendor.Amd
            ? "Réglage des limites AMD par commande SMU non documentée officiellement : pas encore vérifié sur un grand nombre de machines. À utiliser avec prudence."
            : "Écriture des limites Intel par le module IntelMSR livré avec PCPerfSuite : pas encore vérifiée sur une machine réelle. " +
              "Pour essayer, baisse une limite plutôt que de la relever : sur les Core de 13e et 14e génération, tensions et " +
              "températures élevées aggravent l'instabilité « Vmin shift ».";

    /// <summary>Vrai quand le pilote PawnIO manque ou est inutilisable : l'interface propose alors de l'installer.</summary>
    [ObservableProperty] private bool isDriverMissing;

    /// <summary>Limites modifiables sur ce PC.</summary>
    [ObservableProperty] private bool isPowerLimitAvailable;

    /// <summary>Limites lues mais pas modifiables (module PawnIO sans écriture, verrou du BIOS) : elles s'affichent,
    /// champs inactifs, avec <see cref="UnavailableReason"/>.</summary>
    [ObservableProperty] private bool isPowerLimitReadOnly;

    public bool ShowPowerLimits => IsPowerLimitAvailable || IsPowerLimitReadOnly;

    public bool IsPowerLimitUnavailable => !ShowPowerLimits;

    public bool CanEditLimits => IsPowerLimitAvailable && RiskAccepted;

    /// <summary>Pourquoi la limite de puissance n'est pas réglable ici — affiché tel quel.</summary>
    [ObservableProperty] private string unavailableReason = "";

    [ObservableProperty] private bool hasBurstLimit;

    [ObservableProperty] private double sustainedWatts;
    [ObservableProperty] private double burstWatts;
    [ObservableProperty] private double minWatts = 5;
    [ObservableProperty] private double maxWatts = 100;

    /// <summary>D'où vient le maximum des champs : la limite du processeur, ou un repli. Null tant que les
    /// limites n'ont pas été lues. Lu aussi par le diagnostic « Compatibilité de ce PC ».</summary>
    public CpuMaxWattsInfo? MaxWattsInfo { get; private set; }

    /// <summary>Le maximum, sa source et — pour un repli — pourquoi le processeur n'a rien donné de mieux.
    /// Null (et donc masqué) tant qu'il n'y a rien à dire.</summary>
    [ObservableProperty] private string? maxWattsNote;

    /// <summary>L'avertissement a été accepté : tant que non, les curseurs restent inertes.</summary>
    [ObservableProperty] private bool riskAccepted;
    public bool NeedsRiskAcceptance => IsPowerLimitAvailable && !RiskAccepted;

    [ObservableProperty] private bool applyAtStartup;
    [ObservableProperty] private string status = "";

    /// <summary>Réglages d'alimentation Windows : disponibles sur les trois plateformes, sans pilote.</summary>
    public ObservableCollection<CpuPowerSettingViewModel> PowerSettings { get; } = new();

    [ObservableProperty] private string powerSettingsStatus = "";

    /// <summary>Profils enregistrés par l'utilisateur : tout l'onglet sous un nom.</summary>
    public ObservableCollection<CpuProfileViewModel> Profiles { get; } = new();

    [ObservableProperty] private string newProfileName = "";

    /// <summary>Null tant qu'aucun profil n'a été touché : la ligne d'état ne s'affiche qu'ensuite.</summary>
    [ObservableProperty] private string? profileStatus;

    /// <summary>Vrai si la machine a une batterie : les réglages ont alors deux valeurs à afficher.</summary>
    public bool ShowBatteryColumn => _powerTuning.HasBattery;

    // Relevés en direct, pris sur le monitoring partagé plutôt que par un second sondage du matériel.
    [ObservableProperty] private double? powerWatts;
    [ObservableProperty] private double? packageTempC;
    [ObservableProperty] private double? maxClockMhz;
    [ObservableProperty] private double? loadPercent;

    public CpuControlViewModel(
        CpuControlService cpu, MonitoringViewModel monitoring, PawnIoItemViewModel pawnIo, CoreParkingViewModel cores,
        TuningStatusViewModel tuning)
    {
        _cpu = cpu;
        Cores = cores;
        selectedSection = Sections[0];
        _monitoring = monitoring;
        PawnIo = pawnIo;
        Tuning = tuning;
        _powerTuning = new CpuPowerTuningService(cpu.Platform);
        _identity = CpuIdentity.Of(cpu.Platform);
        GroupPlanChanges = new ProfileGroupPowerPlanChanges(_powerTuning.GetSettings(), _powerTuning.HasBattery);

        AppSettings settings = AppSettingsStore.Load();
        applyAtStartup = settings.Cpu.ApplyAtStartup;
        riskAccepted = settings.Cpu.RiskAccepted;
        UpdateKeepLimitsOnExit();

        CpuName = _cpu.Platform.Name;
        PlatformText = $"{_cpu.Platform.VendorLabel} · {_cpu.Backend.Description}";

        UpdateDriverStatus();
        PawnIo.PropertyChanged += OnPawnIoChanged;

        CpuCapability capability = _cpu.Backend.PowerLimit;
        IsPowerLimitAvailable = capability.CanWrite;
        UnavailableReason = capability.Reason ?? "";

        LoadLimits(settings);
        IsPowerLimitReadOnly = !IsPowerLimitAvailable && capability.CanRead && MaxWattsInfo is not null;
        LoadPowerSettings();

        // Après LoadPowerSettings : le résumé d'un profil se lit dans les libellés et les unités des
        // réglages réellement exposés par cette machine.
        foreach (CpuProfile profile in settings.Cpu.Profiles)
        {
            Profiles.Add(new CpuProfileViewModel(profile, BuildSummary(profile)));
        }

        _monitoring.SnapshotUpdated += OnSnapshotUpdated;
        InitializeSafety();
    }

    /// <summary>Lit les limites en place, puis — uniquement si l'utilisateur l'a demandé — réapplique
    /// celles enregistrées. Sans ça, l'app ne touche à rien au lancement.</summary>
    private void LoadLimits(AppSettings settings)
    {
        CpuPowerLimitSnapshot? snapshot = _cpu.ReadPowerLimits();
        if (snapshot is null)
        {
            if (UnavailableReason.Length == 0)
            {
                UnavailableReason = "Les limites de puissance de ce processeur n'ont pas pu être lues.";
            }

            IsPowerLimitAvailable = false;
            return;
        }

        _defaultSustainedWatts = snapshot.DefaultSustainedWatts;
        _defaultBurstWatts = snapshot.DefaultBurstWatts ?? snapshot.DefaultSustainedWatts;
        HasBurstLimit = snapshot.BurstWatts is not null;

        MinWatts = snapshot.MinWatts;
        MaxWatts = snapshot.MaxWatts;
        MaxWattsInfo = snapshot.MaxWattsInfo;
        MaxWattsNote = $"Maximum proposé : {snapshot.MaxWattsInfo.Watts:0} W. {snapshot.MaxWattsInfo.Explanation}"
                       + (snapshot.MaxWattsInfo.IsExperimental ? $" {CpuMaxWattsInfo.ExperimentalNotice}" : "");

        _suppressApply = true;
        SustainedWatts = snapshot.SustainedWatts;
        BurstWatts = snapshot.BurstWatts ?? snapshot.SustainedWatts;
        _suppressApply = false;

        Status = $"Limites actuelles : {snapshot.SustainedWatts:0} W en soutenu"
                 + (snapshot.BurstWatts is { } burst ? $", {burst:0} W en pointe." : ".");

        // En lecture seule (verrou du BIOS, module sans écriture), on affiche ce qui est en place, jamais une valeur
        // enregistrée qu'on ne peut pas poser.
        if (!ApplyAtStartup || !IsPowerLimitAvailable || !RiskAccepted
            || settings.Cpu.SustainedWatts is not { } storedSustained) return;

        _suppressApply = true;
        SustainedWatts = Math.Clamp(storedSustained, MinWatts, MaxWatts);
        BurstWatts = Math.Clamp(settings.Cpu.BurstWatts ?? storedSustained, MinWatts, MaxWatts);
        _suppressApply = false;

        Apply();
    }

    /// <summary>Peuple les réglages d'alimentation. Un réglage que Windows ne connaît pas sur cette
    /// machine est simplement absent de la liste : l'afficher inerte n'aiderait personne.</summary>
    private void LoadPowerSettings()
    {
        foreach (CpuPowerSetting setting in _powerTuning.GetSettings())
        {
            if (!_powerTuning.TryRead(setting, out uint onAc, out uint onBattery)) continue;

            PowerSettings.Add(new CpuPowerSettingViewModel(
                _powerTuning, setting, onAc, onBattery, message => PowerSettingsStatus = message, Tuning.ManualWriteRefusal));
        }

        if (PowerSettings.Count == 0)
        {
            PowerSettingsStatus = "Aucun réglage d'alimentation processeur n'est exposé par ce Windows.";
        }
    }

    partial void OnIsPowerLimitAvailableChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowPowerLimits));
        OnPropertyChanged(nameof(IsPowerLimitUnavailable));
        OnPropertyChanged(nameof(NeedsRiskAcceptance));
        OnPropertyChanged(nameof(CanEditLimits));
    }

    partial void OnIsPowerLimitReadOnlyChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowPowerLimits));
        OnPropertyChanged(nameof(IsPowerLimitUnavailable));
    }

    partial void OnRiskAcceptedChanged(bool value)
    {
        OnPropertyChanged(nameof(NeedsRiskAcceptance));
        OnPropertyChanged(nameof(CanEditLimits));
    }

    /// <summary>Explique une valeur affichée hors de la plage saisissable (4095 W pour une plage qui s'arrête à
    /// 400 W, ou un PL2 du BIOS au-dessus du maximum du processeur) au lieu de la laisser passer pour une
    /// erreur. Vide pour une limite ordinaire.</summary>
    public string SustainedNote => OutOfRangeNote(SustainedWatts);

    public string BurstNote => OutOfRangeNote(BurstWatts);

    private string OutOfRangeNote(double watts)
    {
        // Même seuil que le résolveur : c'est le marqueur « sans limite » de la carte mère, pas une puissance.
        if (watts >= CpuMaxWattsResolver.UnlimitedWatts) return $"Pas de limite définie par le BIOS (valeur lue : {watts:0} W)";
        if (watts > MaxWatts) return $"Valeur du BIOS, au-dessus du maximum proposé ({MaxWatts:0} W)";
        return "";
    }

    /// <summary>Le maximum arrive après (ou avec) les valeurs lues : les notes « hors plage » se recalculent.</summary>
    partial void OnMaxWattsChanged(double value)
    {
        OnPropertyChanged(nameof(SustainedNote));
        OnPropertyChanged(nameof(BurstNote));
    }

    partial void OnSustainedWattsChanged(double value)
    {
        OnPropertyChanged(nameof(SustainedNote));

        // La limite de pointe ne peut pas passer sous la limite soutenue : on la pousse avec.
        if (HasBurstLimit && BurstWatts < value)
        {
            BurstWatts = value;
            return; // OnBurstWattsChanged appliquera les deux.
        }

        Apply();
    }

    partial void OnBurstWattsChanged(double value)
    {
        OnPropertyChanged(nameof(BurstNote));
        Apply();
    }

    partial void OnApplyAtStartupChanged(bool value)
    {
        UpdateKeepLimitsOnExit();

        // Recocher la case après un déclenchement de la sécurité, c'est redemander ces limites : le réveil les repose.
        if (value && !_suppressApply) _emergencyThisSession = false;
        Persist();
    }

    private void Apply()
    {
        if (_suppressApply || !IsPowerLimitAvailable || !RiskAccepted) return;
        _applyDebounce.Schedule(PowerLimitKey, ApplyNow);
    }

    private void ApplyNow()
    {
        if (_suppressApply || !IsPowerLimitAvailable || !RiskAccepted) return;
        if (RefuseManualWrite()) return;

        // Une limite posée à la main redevient l'état de démarrage, même après un groupe appliqué sans l'être.
        _wattsTransient = false;
        UpdateKeepLimitsOnExit();

        Status = _cpu.TrySetPowerLimits((float)SustainedWatts, HasBurstLimit ? (float)BurstWatts : null, out string message)
            ? message
            : $"Réglage refusé : {message}";

        Persist();
    }

    [RelayCommand]
    private void AcceptRisk()
    {
        RiskAccepted = true;
        Persist();
        Status = "Réglages déverrouillés. Les limites reviennent d'origine à la fermeture de l'app et à chaque redémarrage.";
    }

    [RelayCommand]
    private void ResetLimits()
    {
        if (!IsPowerLimitAvailable) return;

        // Une application encore en attente réécrirait la limite juste après le retour aux valeurs
        // d'origine : on l'abandonne.
        _applyDebounce.Cancel(PowerLimitKey);
        if (RefuseManualWrite()) return;

        _wattsTransient = false;
        UpdateKeepLimitsOnExit();

        bool ok = _cpu.TryRestoreDefaults(out string message);
        Status = ok ? message : $"Retour aux limites d'origine refusé : {message}";

        CpuPowerLimitSnapshot? snapshot = _cpu.ReadPowerLimits();
        _suppressApply = true;
        SustainedWatts = snapshot?.SustainedWatts ?? _defaultSustainedWatts;
        BurstWatts = snapshot?.BurstWatts ?? _defaultBurstWatts;
        _suppressApply = false;

        AppSettingsStore.Update(settings =>
        {
            settings.Cpu.SustainedWatts = null;
            settings.Cpu.BurstWatts = null;
        });
    }

    /// <summary>Enregistre tout l'onglet sous un nom : les réglages d'alimentation Windows, et les
    /// limites en watts quand elles sont disponibles et déverrouillées.</summary>
    [RelayCommand]
    private void SaveProfile()
    {
        string name = NewProfileName.Trim();
        if (name.Length == 0) name = $"Profil {Profiles.Count + 1}";

        var profile = new CpuProfile { Name = name };
        foreach (CpuPowerSettingViewModel setting in PowerSettings)
        {
            profile.PowerSettings[setting.Id] = setting.Capture();
        }

        if (IsPowerLimitAvailable && RiskAccepted)
        {
            profile.SustainedWatts = (float)SustainedWatts;
            profile.BurstWatts = HasBurstLimit ? (float)BurstWatts : null;
        }

        // Même nom = on remplace, pour pouvoir mettre un profil à jour sans le supprimer d'abord.
        CpuProfileViewModel? existing = Profiles.FirstOrDefault(
            p => string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase));
        if (existing is not null) Profiles.Remove(existing);

        Profiles.Add(new CpuProfileViewModel(profile, BuildSummary(profile)));
        NewProfileName = "";
        ProfileStatus = $"Profil « {name} » enregistré.";
        PersistProfiles();
    }

    /// <summary>
    /// Applique un profil. Il a pu être écrit sur une autre machine ou sur une autre plateforme : ce qui
    /// n'existe pas ici est ignoré, et le nombre de réglages laissés de côté est annoncé plutôt que passé
    /// sous silence. Les limites en watts ne sont posées que si ce PC les expose et que l'avertissement a
    /// été accepté — sinon le reste du profil s'applique quand même, et on dit pourquoi elles n'ont pas suivi.
    /// </summary>
    [RelayCommand]
    private void ApplyProfile(CpuProfileViewModel? profile)
    {
        if (profile is null) return;

        // Même chemin qu'un groupe de profils, sans contrôle d'identité : un profil de l'onglet n'en porte pas, il est
        // supposé fait sur ce processeur-ci, comme avant.
        ProfileStatus = $"Profil « {profile.Name} » : {ApplyTabProfile(profile.Model)}.";
    }

    [RelayCommand]
    private void DeleteProfile(CpuProfileViewModel? profile)
    {
        if (profile is null) return;

        var result = System.Windows.MessageBox.Show(
            $"Supprimer le profil « {profile.Name} » ? Cette action ne peut pas être annulée.",
            "Supprimer le profil", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.No);
        if (result != System.Windows.MessageBoxResult.Yes) return;

        Profiles.Remove(profile);
        ProfileStatus = $"Profil « {profile.Name} » supprimé.";
        PersistProfiles();
    }

    /// <summary>Résumé d'un profil dans les termes de CETTE machine : un réglage que ce PC n'expose pas
    /// n'y figure pas, puisqu'il ne serait pas appliqué non plus.</summary>
    private string BuildSummary(CpuProfile profile)
    {
        // Parcourir les réglages de la machine, et non les clés du profil : l'ordre du fichier n'est pas
        // garanti, celui du catalogue si.
        var parts = new List<string>();
        foreach (CpuPowerSettingViewModel setting in PowerSettings)
        {
            if (!profile.PowerSettings.TryGetValue(setting.Id, out CpuProfilePowerValue? value)) continue;
            if (value.Ac is not { } ac) continue;

            parts.Add($"{setting.Label} : {setting.Describe(ac)}");
        }

        if (profile.SustainedWatts is { } sustained) parts.Add($"{sustained:0} W soutenu");
        if (profile.BurstWatts is { } burst) parts.Add($"{burst:0} W en pointe");

        return parts.Count == 0
            ? "Aucun des réglages de ce profil n'existe sur ce PC."
            : string.Join("  •  ", parts);
    }

    private void PersistProfiles()
    {
        List<CpuProfile> profiles = Profiles.Select(p => p.Model).ToList();
        AppSettingsStore.Update(settings => settings.Cpu.Profiles = profiles);
    }

    private static string Plural(int count, string singular, string plural)
        => count > 1 ? $"{count} {plural}" : $"{count} {singular}";

    /// <summary>Le pilote peut être installé pendant que l'onglet est ouvert : le texte et le bouton suivent, au
    /// lieu de proposer d'installer ce qui vient de l'être.</summary>
    private void OnPawnIoChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PawnIoItemViewModel.StatusText) or nameof(PawnIoItemViewModel.StatusDetail)) UpdateDriverStatus();
    }

    private void UpdateDriverStatus()
    {
        DriverText = PawnIo.State switch
        {
            PawnIoState.Ready => $"Pilote PawnIO {PawnIoDriver.Version} détecté (API {PawnIoDriver.ApiVersion}).",
            PawnIoState.RestartRequired => "Pilote PawnIO installé : relance PCPerfSuite pour qu'il soit utilisé.",
            _ => PawnIoDriver.UnavailableReason ?? "Pilote PawnIO indisponible.",
        };

        IsDriverMissing = PawnIo.State is PawnIoState.NotInstalled or PawnIoState.Unusable
                          && _cpu.Platform.Vendor is CpuVendor.Intel or CpuVendor.Amd;
    }

    /// <summary>Limites de puissance relevées : la sécurité thermique doit continuer de lire la température du CPU,
    /// même fenêtre cachée.</summary>
    public void AddRequiredGroups(ISet<SensorGroup> into)
    {
        if (_cpu.NeedsTemperatureWatch) into.Add(SensorGroup.Cpu);
    }

    private void OnSnapshotUpdated(HardwareSnapshot snapshot)
    {
        // Mode éco : seule la sécurité thermique compte, l'affichage attendra la réouverture.
        if (_monitoring.IsBackgroundMode)
        {
            _cpu.NoteTemperature(snapshot.Cpu.PackageTempC);
            return;
        }

        PowerWatts = snapshot.Cpu.PowerWatts;
        PackageTempC = snapshot.Cpu.PackageTempC;
        MaxClockMhz = snapshot.Cpu.MaxClockMhz;
        LoadPercent = snapshot.Cpu.LoadPercent;

        _cpu.NoteTemperature(snapshot.Cpu.PackageTempC);
    }

    /// <summary>Relit le fichier avant d'écrire : les autres onglets enregistrent aussi leurs réglages.</summary>
    private void Persist()
    {
        if (_suppressApply) return;

        // Tout est lu avant le verrou : le lambda ne fait que des affectations.
        float? sustained = IsPowerLimitAvailable && RiskAccepted ? (float)SustainedWatts : null;
        float? burst = IsPowerLimitAvailable && RiskAccepted && HasBurstLimit ? (float)BurstWatts : null;
        bool applyAtStartup = ApplyAtStartup;
        bool riskAccepted = RiskAccepted;

        // Un groupe appliqué sans en faire l'état de démarrage (bascule automatique) : les limites enregistrées restent
        // celles d'avant.
        bool writeValues = !_wattsTransient;

        AppSettingsStore.Update(settings =>
        {
            if (writeValues)
            {
                settings.Cpu.SustainedWatts = sustained;
                settings.Cpu.BurstWatts = burst;
            }

            settings.Cpu.ApplyAtStartup = applyAtStartup;
            settings.Cpu.RiskAccepted = riskAccepted;
        });
    }

    /// <summary>Les réglages encore en attente sont appliqués avant de partir : un utilisateur qui ferme
    /// l'app juste après avoir lâché un curseur doit retrouver son réglage au prochain lancement.</summary>
    public void Dispose()
    {
        _applyDebounce.Flush();
        foreach (CpuPowerSettingViewModel setting in PowerSettings) setting.FlushPendingWrite();

        _monitoring.SnapshotUpdated -= OnSnapshotUpdated;
        PawnIo.PropertyChanged -= OnPawnIoChanged;
        DisposeSafety();
        Cores.Dispose();
    }
}
