using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCPerfSuite.Core.Hardware;
using PCPerfSuite.Core.Hardware.Cpu;
using PCPerfSuite.Core.PowerSettings;

namespace PCPerfSuite.App.ViewModels;

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

    /// <summary>Bloque l'application pendant qu'on repositionne plusieurs curseurs d'un coup.</summary>
    private bool _suppressApply;

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

    // Relevés en direct, pris sur le monitoring partagé plutôt que par un second sondage du matériel.
    [ObservableProperty] private double? powerWatts;
    [ObservableProperty] private double? packageTempC;
    [ObservableProperty] private double? maxClockMhz;
    [ObservableProperty] private double? loadPercent;

    public CpuControlViewModel(CpuControlService cpu, MonitoringViewModel monitoring)
    {
        _cpu = cpu;
        _monitoring = monitoring;

        AppSettings settings = AppSettingsStore.Load();
        applyAtStartup = settings.Cpu.ApplyAtStartup;
        riskAccepted = settings.Cpu.RiskAccepted;
        _cpu.KeepLimitsOnExit = applyAtStartup;

        CpuName = _cpu.Platform.Name;
        PlatformText = $"{_cpu.Platform.VendorLabel} · {_cpu.Backend.Description}";

        DriverText = PawnIoDriver.IsInstalled
            ? $"Pilote PawnIO {PawnIoDriver.Version} détecté."
            : PawnIoDriver.UnavailableReason ?? "Pilote PawnIO indisponible.";
        IsDriverMissing = !PawnIoDriver.IsInstalled && _cpu.Platform.Vendor is CpuVendor.Intel or CpuVendor.Amd;

        CpuCapability capability = _cpu.Backend.PowerLimit;
        IsPowerLimitAvailable = capability.CanWrite;
        UnavailableReason = capability.Reason ?? "";

        LoadLimits(settings);

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

    public void Dispose()
    {
        _monitoring.SnapshotUpdated -= OnSnapshotUpdated;
        _cpu.EmergencyRestored -= OnEmergencyRestored;
    }
}
