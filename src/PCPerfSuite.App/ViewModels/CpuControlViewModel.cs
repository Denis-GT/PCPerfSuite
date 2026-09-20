using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCPerfSuite.App.Utils;
using PCPerfSuite.Core.Hardware;
using PCPerfSuite.Core.Hardware.Cpu;
using PCPerfSuite.Core.PowerSettings;

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

    [ObservableProperty] private CpuPowerChoice? acChoice;
    [ObservableProperty] private CpuPowerChoice? batteryChoice;
    [ObservableProperty] private double acValue;
    [ObservableProperty] private double batteryValue;

    public string AcText => Format(AcValue);
    public string BatteryText => Format(BatteryValue);

    public CpuPowerSettingViewModel(
        CpuPowerTuningService service, CpuPowerSetting setting, uint onAc, uint onBattery, Action<string> report)
    {
        _service = service;
        _setting = setting;
        _report = report;
        ShowBattery = service.HasBattery;

        _suppressWrite = true;
        acValue = onAc;
        batteryValue = onBattery;
        acChoice = setting.Choices?.FirstOrDefault(c => c.Value == onAc) ?? setting.Choices?.FirstOrDefault();
        batteryChoice = setting.Choices?.FirstOrDefault(c => c.Value == onBattery) ?? setting.Choices?.FirstOrDefault();
        _suppressWrite = false;
    }

    private string Format(double value)
    {
        if (_setting.ZeroLabel is { } zero && value == 0) return zero;
        return $"{value:0}{_setting.Unit}";
    }

    partial void OnAcValueChanged(double value)
    {
        OnPropertyChanged(nameof(AcText));
        Write();
    }

    partial void OnBatteryValueChanged(double value)
    {
        OnPropertyChanged(nameof(BatteryText));
        Write();
    }

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

    /// <summary>Ramène une valeur venue d'un profil dans ce que ce réglage accepte ici : une option de
    /// liste absente de cette machine est refusée plutôt que rabotée (un « mode boost 4 » ramené à 2
    /// appliquerait un réglage que personne n'a demandé), une valeur numérique est bornée.</summary>
    private uint? Sanitize(uint value)
    {
        if (_setting.Choices is { } choices) return choices.Any(c => c.Value == value) ? value : null;
        return (uint)Math.Clamp(value, _setting.Min, _setting.Max);
    }

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
/// Onglet "Processeur" : la limite de puissance du CPU, en watts.
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
public sealed partial class CpuControlViewModel : ObservableObject, IDisposable
{
    private readonly CpuControlService _cpu;
    private readonly MonitoringViewModel _monitoring;
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

    /// <summary>Vrai quand le pilote PawnIO manque : l'interface propose alors de l'installer.</summary>
    [ObservableProperty] private bool isDriverMissing;

    [ObservableProperty] private bool isPowerLimitAvailable;
    public bool IsPowerLimitUnavailable => !IsPowerLimitAvailable;

    /// <summary>Pourquoi la limite de puissance n'est pas réglable ici — affiché tel quel.</summary>
    [ObservableProperty] private string unavailableReason = "";

    [ObservableProperty] private bool hasBurstLimit;

    [ObservableProperty] private double sustainedWatts;
    [ObservableProperty] private double burstWatts;
    [ObservableProperty] private double minWatts = 5;
    [ObservableProperty] private double maxWatts = 100;

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

    public CpuControlViewModel(CpuControlService cpu, MonitoringViewModel monitoring)
    {
        _cpu = cpu;
        _monitoring = monitoring;
        _powerTuning = new CpuPowerTuningService(cpu.Platform);

        AppSettings settings = AppSettingsStore.Load();
        applyAtStartup = settings.Cpu.ApplyAtStartup;
        riskAccepted = settings.Cpu.RiskAccepted;
        _cpu.KeepLimitsOnExit = applyAtStartup;

        CpuName = _cpu.Platform.Name;
        PlatformText = $"{_cpu.Platform.VendorLabel} · {_cpu.Backend.Description}";

        DriverText = PawnIoDriver.IsInstalled
            ? $"Pilote PawnIO {PawnIoDriver.Version} détecté (API {PawnIoDriver.ApiVersion})."
            : PawnIoDriver.UnavailableReason ?? "Pilote PawnIO indisponible.";
        IsDriverMissing = !PawnIoDriver.IsInstalled && _cpu.Platform.Vendor is CpuVendor.Intel or CpuVendor.Amd;

        CpuCapability capability = _cpu.Backend.PowerLimit;
        IsPowerLimitAvailable = capability.CanWrite;
        UnavailableReason = capability.Reason ?? "";

        LoadLimits(settings);
        LoadPowerSettings();

        // Après LoadPowerSettings : le résumé d'un profil se lit dans les libellés et les unités des
        // réglages réellement exposés par cette machine.
        foreach (CpuProfile profile in settings.Cpu.Profiles)
        {
            Profiles.Add(new CpuProfileViewModel(profile, BuildSummary(profile)));
        }

        _cpu.EmergencyRestored += OnEmergencyRestored;
        _monitoring.SnapshotUpdated += OnSnapshotUpdated;
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

        _suppressApply = true;
        SustainedWatts = snapshot.SustainedWatts;
        BurstWatts = snapshot.BurstWatts ?? snapshot.SustainedWatts;
        _suppressApply = false;

        Status = $"Limites actuelles : {snapshot.SustainedWatts:0} W en soutenu"
                 + (snapshot.BurstWatts is { } burst ? $", {burst:0} W en pointe." : ".");

        if (!ApplyAtStartup || settings.Cpu.SustainedWatts is not { } storedSustained) return;

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
                _powerTuning, setting, onAc, onBattery, message => PowerSettingsStatus = message));
        }

        if (PowerSettings.Count == 0)
        {
            PowerSettingsStatus = "Aucun réglage d'alimentation processeur n'est exposé par ce Windows.";
        }
    }

    partial void OnIsPowerLimitAvailableChanged(bool value)
    {
        OnPropertyChanged(nameof(IsPowerLimitUnavailable));
        OnPropertyChanged(nameof(NeedsRiskAcceptance));
    }

    partial void OnRiskAcceptedChanged(bool value) => OnPropertyChanged(nameof(NeedsRiskAcceptance));

    partial void OnSustainedWattsChanged(double value)
    {
        // La limite de pointe ne peut pas passer sous la limite soutenue : on la pousse avec.
        if (HasBurstLimit && BurstWatts < value)
        {
            BurstWatts = value;
            return; // OnBurstWattsChanged appliquera les deux.
        }

        Apply();
    }

    partial void OnBurstWattsChanged(double value) => Apply();

    partial void OnApplyAtStartupChanged(bool value)
    {
        _cpu.KeepLimitsOnExit = value;
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

        bool ok = _cpu.TryRestoreDefaults(out string message);
        Status = ok ? message : $"Retour aux limites d'origine refusé : {message}";

        CpuPowerLimitSnapshot? snapshot = _cpu.ReadPowerLimits();
        _suppressApply = true;
        SustainedWatts = snapshot?.SustainedWatts ?? _defaultSustainedWatts;
        BurstWatts = snapshot?.BurstWatts ?? _defaultBurstWatts;
        _suppressApply = false;

        AppSettings settings = AppSettingsStore.Load();
        settings.Cpu.SustainedWatts = null;
        settings.Cpu.BurstWatts = null;
        AppSettingsStore.Save(settings);
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

        CpuProfile model = profile.Model;
        int applied = 0;
        int skipped = 0;

        foreach ((string id, CpuProfilePowerValue value) in model.PowerSettings)
        {
            CpuPowerSettingViewModel? setting = PowerSettings.FirstOrDefault(s => s.Id == id);
            if (setting is null || !setting.ApplyFromProfile(value))
            {
                skipped++;
                continue;
            }

            applied++;
        }

        var parts = new List<string>
        {
            applied == 0
                ? "aucun réglage d'alimentation applicable"
                : Plural(applied, "réglage d'alimentation appliqué", "réglages d'alimentation appliqués"),
        };

        if (skipped > 0) parts.Add(Plural(skipped, "ignoré, absent de ce PC", "ignorés, absents de ce PC"));

        string watts = ApplyProfileWatts(model);
        if (watts.Length > 0) parts.Add(watts);

        ProfileStatus = $"Profil « {profile.Name} » : {string.Join(" · ", parts)}.";
    }

    [RelayCommand]
    private void DeleteProfile(CpuProfileViewModel? profile)
    {
        if (profile is null) return;

        Profiles.Remove(profile);
        ProfileStatus = $"Profil « {profile.Name} » supprimé.";
        PersistProfiles();
    }

    /// <summary>Volet « watts » d'un profil. Renvoie ce qu'il y a à en dire, ou une chaîne vide si le
    /// profil n'en portait pas.</summary>
    private string ApplyProfileWatts(CpuProfile model)
    {
        if (model.SustainedWatts is not { } sustained) return "";
        if (!IsPowerLimitAvailable) return "limites en watts ignorées, ce PC ne les expose pas";
        if (!RiskAccepted) return "limites en watts ignorées, l'avertissement n'a pas encore été accepté";

        // La limite soutenue d'abord : la poser pousse la limite de pointe si elle passait en dessous,
        // et c'est ensuite celle du profil qui doit avoir le dernier mot.
        _suppressApply = true;
        SustainedWatts = Math.Clamp(sustained, MinWatts, MaxWatts);
        if (HasBurstLimit) BurstWatts = Math.Clamp(model.BurstWatts ?? sustained, MinWatts, MaxWatts);
        _suppressApply = false;

        // Sans passer par le débounce : appliquer un profil est un geste ponctuel dont on veut voir
        // l'effet — et le message de relecture — tout de suite.
        _applyDebounce.Cancel(PowerLimitKey);
        ApplyNow();
        return $"limites posées à {SustainedWatts:0} W";
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
        AppSettings settings = AppSettingsStore.Load();
        settings.Cpu.Profiles = Profiles.Select(p => p.Model).ToList();
        AppSettingsStore.Save(settings);
    }

    private static string Plural(int count, string singular, string plural)
        => count > 1 ? $"{count} {plural}" : $"{count} {singular}";

    [RelayCommand]
    private void OpenDriverSite()
    {
        try
        {
            Process.Start(new ProcessStartInfo(PawnIoDriver.DownloadUrl) { UseShellExecute = true });
        }
        catch
        {
            Status = $"Impossible d'ouvrir le navigateur. L'adresse est : {PawnIoDriver.DownloadUrl}";
        }
    }

    private void OnEmergencyRestored(string message)
    {
        Status = message;

        CpuPowerLimitSnapshot? snapshot = _cpu.ReadPowerLimits();
        if (snapshot is null) return;

        _suppressApply = true;
        SustainedWatts = snapshot.SustainedWatts;
        BurstWatts = snapshot.BurstWatts ?? snapshot.SustainedWatts;
        _suppressApply = false;
    }

    private void OnSnapshotUpdated(HardwareSnapshot snapshot)
    {
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

        AppSettings settings = AppSettingsStore.Load();
        settings.Cpu.SustainedWatts = IsPowerLimitAvailable && RiskAccepted ? (float)SustainedWatts : null;
        settings.Cpu.BurstWatts = IsPowerLimitAvailable && RiskAccepted && HasBurstLimit ? (float)BurstWatts : null;
        settings.Cpu.ApplyAtStartup = ApplyAtStartup;
        settings.Cpu.RiskAccepted = RiskAccepted;
        AppSettingsStore.Save(settings);
    }

    /// <summary>Les réglages encore en attente sont appliqués avant de partir : un utilisateur qui ferme
    /// l'app juste après avoir lâché un curseur doit retrouver son réglage au prochain lancement.</summary>
    public void Dispose()
    {
        _applyDebounce.Flush();
        foreach (CpuPowerSettingViewModel setting in PowerSettings) setting.FlushPendingWrite();

        _monitoring.SnapshotUpdated -= OnSnapshotUpdated;
        _cpu.EmergencyRestored -= OnEmergencyRestored;
    }
}
