using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCPerfSuite.App.Utils;
using PCPerfSuite.Core.Compatibility;
using PCPerfSuite.Core.Hardware;
using PCPerfSuite.Core.Hardware.Cpu;
using PCPerfSuite.Core.Hardware.Cpu.CoreParking;
using PCPerfSuite.Core.Hardware.Cpu.Throttle;
using PCPerfSuite.Core.Hardware.Displays;
using PCPerfSuite.Core.Hardware.Gpu;
using PCPerfSuite.Core.Installations;
using PCPerfSuite.Core.PowerSettings.Animations;
using PCPerfSuite.Core.Profiles;
using PCPerfSuite.Core.Safety;
using PCPerfSuite.Core.Safety.Events;
using PCPerfSuite.Core.SystemChanges;
using PCPerfSuite.Core.SystemInfo;

namespace PCPerfSuite.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly HardwareMonitorService _hardware = new();

    /// <summary>Un seul service NVAPI pour toute l'app : l'onglet GPU (overclocking) et l'onglet
    /// Ventilateurs (ventilateur GPU) parlent à la même carte.</summary>
    private readonly GpuControlService _gpuControl = new();

    /// <summary>Réglage bas niveau du processeur : réglages d'alimentation Windows sur toutes les
    /// plateformes, et limites de puissance en watts via PawnIO sur Intel/AMD.</summary>
    private readonly CpuControlService _cpuControl = new();

    /// <summary>Parking des cœurs du plan d'alimentation : partagé par Processeur › Cœurs, le tweak d'Optimisation
    /// Windows (sa façade) et le diagnostic, inscrit au registre des modifications.</summary>
    private readonly CoreParkingService _coreParking;

    private readonly MonitoringViewModel _monitoring;
    private readonly ProcessesViewModel _processes;
    private readonly FanCurvesViewModel _fans;
    private readonly GpuControlViewModel _gpu;
    private readonly CpuControlViewModel _cpu;
    private readonly OverlayViewModel _overlay;

    /// <summary>Logiciels externes (PawnIO, RTSS) : partagé par l'onglet Processeur, Paramètres et le diagnostic.</summary>
    private readonly InstallationsViewModel _installations = new();

    /// <summary>Lignes du diagnostic « Compatibilité de ce PC » apportées par les fonctions, affichées dans cet ordre
    /// après les lignes historiques. C'est le seul endroit où inscrire un fournisseur (créé avec les services dont il
    /// a besoin, avant CompatibilityViewModel) : jamais une dépendance de plus pour CompatibilityViewModel.</summary>
    private readonly List<ICompatibilityRowProvider> _compatibilityRows = new();

    /// <summary>Fonctions qui modifient Windows durablement : chacune s'y inscrit à sa création, pour que « Tout
    /// rétablir » (mode technicien) sache qui interroger.</summary>
    public SystemChangeRegistry SystemChanges { get; } = new();

    /// <summary>Catalogue de la Boîte à outils : partagé par la page et par sa ligne du diagnostic.</summary>
    private readonly ToolCatalogStore _toolCatalog = new(ToolCatalog.All);

    public bool IsElevated { get; } = ElevationHelper.IsAdministrator();
    public bool ShowElevationBanner => !IsElevated;

    // Exposées individuellement (plutôt qu'un seul "CurrentViewModel" swappé au clic) pour que
    // MainWindow puisse instancier chaque vue une seule fois et ne faire varier que sa Visibility :
    // un ContentControl relié à une seule propriété détruit et recrée la vue à chaque changement
    // d'onglet, ce qui remettait à zéro les graphiques (Sparkline) du Monitoring.
    public MonitoringViewModel Monitoring => _monitoring;
    public ProcessesViewModel Processes => _processes;
    public CleanupViewModel Cleanup { get; } = new();
    public StorageViewModel Storage { get; } = new();
    public ToolboxViewModel Toolbox { get; }
    public OptimizationViewModel Optimization { get; }
    public AppSettingsViewModel AppSettings { get; }
    public FanCurvesViewModel Fans => _fans;
    public GpuControlViewModel Gpu => _gpu;
    public CpuControlViewModel Cpu => _cpu;
    public OverlayViewModel Overlay => _overlay;

    /// <summary>Bail de réglage : un seul pilote automatique des réglages CPU, GPU et ventilation à la fois (groupes de
    /// profils, bascule automatique, bench, recherche d'OC). Partagé par les trois onglets et la page Profils.</summary>
    private readonly TuningLease _tuningLease = new();

    /// <summary>Bannière « Réglages pilotés par… » des onglets Processeur, GPU et Ventilateurs.</summary>
    public TuningStatusViewModel Tuning { get; }

    /// <summary>Entrées de la barre latérale, dans l'ordre de <see cref="NavigationMenu.Pages"/> : MainWindow les
    /// range sous leurs en-têtes de section.</summary>
    public ObservableCollection<NavEntry> NavItems { get; }

    /// <summary>Bouton "Paramètres" en bas de la barre latérale, hors de <see cref="NavItems"/>.</summary>
    public NavEntry AppSettingsNav { get; }

    /// <summary>Élément sélectionné dans la liste de navigation ; null quand les Paramètres sont affichés.</summary>
    [ObservableProperty] private NavEntry? selectedNavItem;

    /// <summary>Page affichée : un élément de la liste, ou les Paramètres (qui n'en font pas partie). Les vues de
    /// MainWindow s'affichent d'après sa clé (<see cref="PageKeys"/>), jamais d'après son titre.</summary>
    [ObservableProperty] private NavEntry? currentPage;

    public bool IsAppSettingsSelected => CurrentPage?.Key == PageKeys.Settings;

    /// <summary>Vrai tant que la fenêtre est affichée et non réduite : posé par <see cref="MainWindow"/>. Un
    /// clignotement dans une fenêtre cachée ne sert à personne, et coûterait des images pour rien.</summary>
    [ObservableProperty] private bool isWindowShown;

    /// <summary>Le bouton Paramètres clignote tant qu'un logiciel manque, que la fenêtre est visible et que la page
    /// affichée n'est pas Paramètres : une fois dedans, c'est l'onglet Installations qui prend le relais.</summary>
    public bool IsAppSettingsBlinking => _installations.HasMissing && IsWindowShown && !IsAppSettingsSelected;

    /// <summary>Info-bulle du bouton Paramètres : quoi installer, et pourquoi. Null, donc pas d'info-bulle, quand
    /// rien ne manque.</summary>
    public string? AppSettingsToolTip => _installations.MissingSummary;

    /// <summary>Bilan de la reprise au lancement (opérations interrompues au dernier arrêt, et ce qui leur est arrivé),
    /// fait par App.OnStartup avant cette classe. Les fonctions qui ne doivent pas réappliquer un réglage après un
    /// incident le consultent.</summary>
    public StartupRecoveryReport StartupRecovery { get; }

    public MainViewModel(StartupRecoveryReport startupRecovery)
    {
        StartupRecovery = startupRecovery;
        Tuning = new TuningStatusViewModel(_tuningLease);
        _monitoring = new MonitoringViewModel(_hardware);
        _processes = new ProcessesViewModel(_monitoring);
        _fans = new FanCurvesViewModel(_hardware, _gpuControl, _monitoring);
        _gpu = new GpuControlViewModel(_gpuControl, _monitoring);
        CpuPlatform platform = _cpuControl.Platform;
        _coreParking = new CoreParkingService(platform.IsHybrid, platform.HasBattery);
        SystemChanges.Register(_coreParking);
        _cpu = new CpuControlViewModel(_cpuControl, _monitoring, _installations.PawnIo, new CoreParkingViewModel(_coreParking, platform), Tuning);
        SystemChanges.Register(_cpu.GroupPlanChanges);
        _hardware.PreferredGpuVendor = _gpuControl.Vendor;
        _hardware.PreferredGpuName = _gpuControl.GetSnapshot()?.Name;
        _overlay = new OverlayViewModel(_monitoring);
        Toolbox = new ToolboxViewModel(_toolCatalog);
        SystemChanges.Register(new ToolboxChanges());

        WindowsAnimationSettings animations = WindowsAnimationSettings.CreateDefault();
        SystemChanges.Register(animations);
        Optimization = new OptimizationViewModel(animations, _coreParking);

        _compatibilityRows.Add(new PawnIoModulesRowProvider());
        _compatibilityRows.Add(new GpuIdentityRowProvider(_gpuControl));
        _compatibilityRows.Add(new GpuThermalSafetyRowProvider(_gpuControl));
        _compatibilityRows.Add(new DisplaysRowProvider());
        _compatibilityRows.Add(new CpuThrottleRowProvider(() => _hardware.LastSnapshot));
        _compatibilityRows.Add(new CoreParkingRowProvider(_coreParking, platform));
        _compatibilityRows.Add(new WindowsEventsRowProvider());
        _compatibilityRows.Add(new SessionJournalRowProvider(startupRecovery, SessionJournal.Current));
        _compatibilityRows.Add(new WindowsAnimationsRowProvider(animations));
        _compatibilityRows.Add(new ToolboxRowProvider(_toolCatalog));

        AppSettings = new AppSettingsViewModel(
            new CompatibilityViewModel(_hardware, _monitoring, _processes, _fans, _gpu, _cpu, _installations, _compatibilityRows),
            _installations);
        AppSettingsNav = new NavEntry(NavigationMenu.Settings, AppSettings);

        // Les pages livrées. Celles du menu qui manquent ici s'affichent « bientôt disponible » (ComingSoonPages) ;
        // livrer une page, c'est l'ajouter ici avec sa vue dans MainWindow (docs/navigation.md).
        var pages = new Dictionary<string, object>
        {
            [PageKeys.Monitoring] = _monitoring,
            [PageKeys.Processes] = _processes,
            [PageKeys.Overlay] = _overlay,
            [PageKeys.Cpu] = _cpu,
            [PageKeys.Gpu] = _gpu,
            [PageKeys.Fans] = _fans,
            [PageKeys.Optimization] = Optimization,
            [PageKeys.Cleanup] = Cleanup,
            [PageKeys.Storage] = Storage,
            [PageKeys.Toolbox] = Toolbox,
        };
        // Seul un PC de bureau avéré perd les pages des portables : sur un châssis indéterminé, la page reste et dira
        // elle-même ce qu'elle trouve.
        NavItems = new ObservableCollection<NavEntry>(
            NavigationMenu.Build(pages, isDesktop: MachineInfo.Current.Chassis == ChassisKind.Desktop));

        // Abonnés une fois la liste construite : UpdateAttention la parcourt.
        _installations.PropertyChanged += (_, _) => UpdateAttention();
        AppSettings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppSettingsViewModel.EcoModeWhenHidden)) UpdateEcoMode();
        };

        SelectedNavItem = NavItems[0];

        // Démarrage dans la zone de notification (session Windows) : la fenêtre n'est jamais affichée, le mode éco
        // doit donc s'appliquer d'emblée. Au démarrage normal, il cesse dès l'affichage de la fenêtre.
        UpdateEcoMode();
    }

    /// <summary>Un choix dans la liste l'affiche ; la désélection (passage aux Paramètres) ne change rien.</summary>
    partial void OnSelectedNavItemChanged(NavEntry? value)
    {
        if (value is not null) CurrentPage = value;
    }

    partial void OnCurrentPageChanged(NavEntry? value)
    {
        OnPropertyChanged(nameof(IsAppSettingsSelected));
        UpdateAttention();
    }

    partial void OnIsWindowShownChanged(bool value)
    {
        UpdateAttention();
        UpdateEcoMode();
    }

    /// <summary>Mode éco en arrière-plan : fenêtre réduite ou dans la zone de notification, et réglage activé. Seuls
    /// l'overlay, les courbes de ventilateurs et les sécurités thermiques du GPU et du CPU continuent d'être nourris.</summary>
    private void UpdateEcoMode()
        => _monitoring.SetBackgroundMode(AppSettings.EcoModeWhenHidden && !IsWindowShown,
            new IBackgroundSensorConsumer[] { _overlay, _fans, _gpu, _cpu });

    /// <summary>Recalcule ce qui dépend à la fois des logiciels manquants, de la page affichée et de la visibilité
    /// de la fenêtre : le clignotement du bouton Paramètres et son info-bulle, puis, pour chaque page, si elle est sous
    /// les yeux de l'utilisateur (<see cref="IPageLifecycle"/>).</summary>
    private void UpdateAttention()
    {
        OnPropertyChanged(nameof(IsAppSettingsBlinking));
        OnPropertyChanged(nameof(AppSettingsToolTip));

        // Fenêtre rangée dans la zone de notification ou réduite : aucune page n'est affichée, même la dernière
        // ouverte. Processus arrête alors son relevé (bien plus coûteux qu'une lecture de capteurs), et une page qui
        // charge à la première ouverture ne charge pas pour rien.
        PageLifecycle.Update(NavItems.Append(AppSettingsNav), CurrentPage?.Key, IsWindowShown);
    }

    /// <summary>La fenêtre revient au premier plan : c'est le moment où l'on découvre que l'utilisateur a installé
    /// RTSS ou lancé son installeur dans une autre fenêtre. Une relecture du registre, hors du thread d'interface.</summary>
    public void OnWindowActivated()
    {
        _ = _installations.RefreshAsync();
        Toolbox.OnWindowActivated();
    }

    /// <summary>Bouton "Paramètres" : la liste se désélectionne, pour qu'un seul élément paraisse actif.</summary>
    [RelayCommand]
    private void ShowAppSettings()
    {
        SelectedNavItem = null;
        CurrentPage = AppSettingsNav;
    }

    /// <summary>Ordre important : le relevé s'arrête avant que les ventilateurs repassent en automatique,
    /// et ceux-ci y repassent avant que le service NVAPI ne rende la carte au pilote et ne décharge NVAPI.</summary>
    public void Dispose()
    {
        // Chaque étape est protégée individuellement : _fans (retour au firmware) et _gpu/_cpu (retrait
        // de l'overclock/des limites) sont les plus critiques de cette liste, et une exception dans une
        // étape antérieure (ex. _processes) ne doit jamais les empêcher de s'exécuter.
        DisposeSafely(_processes.Dispose, nameof(_processes));
        DisposeSafely(_installations.Dispose, nameof(_installations));
        DisposeSafely(Toolbox.Dispose, nameof(Toolbox));
        // Le relevé s'arrête AVANT le retour des ventilateurs au BIOS : il lisait encore la puce des
        // ventilateurs pendant qu'on la leur rendait, et LibreHardwareMonitor abandonne alors l'écriture
        // sans le dire (voir HardwareMonitorService.RunWithIsaBus). Les abonnés qui se désabonnent ensuite
        // d'un Monitoring déjà arrêté n'y perdent rien.
        DisposeSafely(_monitoring.Dispose, nameof(_monitoring));
        DisposeSafely(_fans.Dispose, nameof(_fans));
        DisposeSafely(_gpu.Dispose, nameof(_gpu));
        DisposeSafely(_cpu.Dispose, nameof(_cpu));
        DisposeSafely(_overlay.Dispose, nameof(_overlay));
        DisposeSafely(Tuning.Dispose, nameof(Tuning));
        DisposeSafely(_gpuControl.Dispose, nameof(_gpuControl));
        DisposeSafely(_cpuControl.Dispose, nameof(_cpuControl));
        DisposeSafely(_hardware.Dispose, nameof(_hardware));

        if (_hardware.FanReleaseProblem is { } problem)
        {
            CrashLog.Record(new InvalidOperationException(problem), "fermeture : retour des ventilateurs au BIOS");
        }
    }

    private static void DisposeSafely(Action dispose, string name)
    {
        try { dispose(); }
        catch (Exception ex) { CrashLog.Record(ex, $"fermeture {name}"); }
    }
}
