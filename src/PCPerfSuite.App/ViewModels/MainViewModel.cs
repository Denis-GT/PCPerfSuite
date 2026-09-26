using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCPerfSuite.Core.Hardware;
using PCPerfSuite.Core.Hardware.Cpu;
using PCPerfSuite.Core.SystemInfo;

namespace PCPerfSuite.App.ViewModels;

/// <summary>Entrée de la navigation latérale. <paramref name="Icon"/> est un glyphe de Segoe Fluent Icons.</summary>
public sealed record NavEntry(string Title, string Icon, object ViewModel);

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly HardwareMonitorService _hardware = new();

    /// <summary>Un seul service NVAPI pour toute l'app : l'onglet GPU (overclocking) et l'onglet
    /// Ventilateurs (ventilateur GPU) parlent à la même carte.</summary>
    private readonly GpuControlService _gpuControl = new();

    /// <summary>Réglage bas niveau du processeur : réglages d'alimentation Windows sur toutes les
    /// plateformes, et limites de puissance en watts via PawnIO sur Intel/AMD.</summary>
    private readonly CpuControlService _cpuControl = new();

    private readonly MonitoringViewModel _monitoring;
    private readonly ProcessesViewModel _processes;
    private readonly FanCurvesViewModel _fans;
    private readonly GpuControlViewModel _gpu;
    private readonly CpuControlViewModel _cpu;
    private readonly OverlayViewModel _overlay;

    /// <summary>Logiciels externes (PawnIO, RTSS) : partagé par l'onglet Processeur, Paramètres et le diagnostic.</summary>
    private readonly InstallationsViewModel _installations = new();

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
    public OptimizationViewModel Optimization { get; } = new();
    public AppSettingsViewModel AppSettings { get; }
    public FanCurvesViewModel Fans => _fans;
    public GpuControlViewModel Gpu => _gpu;
    public CpuControlViewModel Cpu => _cpu;
    public OverlayViewModel Overlay => _overlay;

    public ObservableCollection<NavEntry> NavItems { get; }

    /// <summary>Bouton "Paramètres" en bas de la barre latérale, hors de <see cref="NavItems"/>.</summary>
    public NavEntry AppSettingsNav { get; }

    /// <summary>Élément sélectionné dans la liste de navigation ; null quand les Paramètres sont affichés.</summary>
    [ObservableProperty] private NavEntry? selectedNavItem;

    /// <summary>Page affichée : un élément de la liste, ou les Paramètres (qui n'en font pas partie). Les vues de
    /// MainWindow s'affichent d'après son titre.</summary>
    [ObservableProperty] private NavEntry? currentPage;

    public bool IsAppSettingsSelected => ReferenceEquals(CurrentPage, AppSettingsNav);

    /// <summary>Vrai tant que la fenêtre est affichée et non réduite : posé par <see cref="MainWindow"/>. Un
    /// clignotement dans une fenêtre cachée ne sert à personne, et coûterait des images pour rien.</summary>
    [ObservableProperty] private bool isWindowShown;

    /// <summary>Le bouton Paramètres clignote tant qu'un logiciel manque, que la fenêtre est visible et que la page
    /// affichée n'est pas Paramètres : une fois dedans, c'est l'onglet Installations qui prend le relais.</summary>
    public bool IsAppSettingsBlinking => _installations.HasMissing && IsWindowShown && !IsAppSettingsSelected;

    /// <summary>Info-bulle du bouton Paramètres : quoi installer, et pourquoi. Null, donc pas d'info-bulle, quand
    /// rien ne manque.</summary>
    public string? AppSettingsToolTip => _installations.MissingSummary;

    public MainViewModel()
    {
        _monitoring = new MonitoringViewModel(_hardware);
        _processes = new ProcessesViewModel(_monitoring);
        _fans = new FanCurvesViewModel(_hardware, _gpuControl, _monitoring);
        _gpu = new GpuControlViewModel(_gpuControl, _monitoring);
        _cpu = new CpuControlViewModel(_cpuControl, _monitoring, _installations.PawnIo);
        _hardware.PreferredGpuVendor = _gpuControl.Vendor;
        _overlay = new OverlayViewModel(_monitoring);
        AppSettings = new AppSettingsViewModel(
            new CompatibilityViewModel(_hardware, _monitoring, _processes, _fans, _gpu, _installations), _installations);
        AppSettingsNav = new NavEntry("Paramètres", Glyph(0xE713), AppSettings);
        _installations.PropertyChanged += (_, _) => UpdateAttention();

        NavItems = new ObservableCollection<NavEntry>
        {
            new("Monitoring", Glyph(0xE9D9), _monitoring),
            new("Processus", Glyph(0xE9F5), _processes),
            new("Nettoyage", Glyph(0xE74D), Cleanup),
            new("Stockage", Glyph(0xEDA2), Storage),
            new("Optimisation Windows", Glyph(0xEC4A), Optimization),
            new("Ventilateurs", Glyph(0xE9CA), _fans),
            new("GPU", Glyph(0xE950), _gpu),
            new("Processeur", Glyph(0xE964), _cpu),
            new("Overlay", Glyph(0xE7FC), _overlay),
        };

        SelectedNavItem = NavItems[0];
    }

    /// <summary>Un choix dans la liste l'affiche ; la désélection (passage aux Paramètres) ne change rien.</summary>
    partial void OnSelectedNavItemChanged(NavEntry? value)
    {
        if (value is not null) CurrentPage = value;
    }

    /// <summary>La liste des processus ne se relève que quand son onglet est affiché : contrairement au
    /// Monitoring, dont l'overlay a besoin en permanence, une liste cachée ne sert à personne et son relevé
    /// coûte bien plus cher qu'une lecture de capteurs. On compare le ViewModel plutôt que le titre, pour ne
    /// pas dépendre d'une chaîne.</summary>
    partial void OnCurrentPageChanged(NavEntry? value)
    {
        _processes.IsActive = ReferenceEquals(value?.ViewModel, _processes);
        OnPropertyChanged(nameof(IsAppSettingsSelected));
        UpdateAttention();

        // L'utilisateur a pu installer quelque chose depuis la dernière fois : on relit à l'ouverture des Paramètres.
        if (IsAppSettingsSelected) _ = _installations.RefreshAsync();
    }

    partial void OnIsWindowShownChanged(bool value) => UpdateAttention();

    /// <summary>Recalcule ce qui dépend à la fois des logiciels manquants, de la page affichée et de la visibilité
    /// de la fenêtre : le clignotement du bouton Paramètres, son info-bulle, et celui de l'onglet Installations.</summary>
    private void UpdateAttention()
    {
        OnPropertyChanged(nameof(IsAppSettingsBlinking));
        OnPropertyChanged(nameof(AppSettingsToolTip));
        AppSettings.IsPageShown = IsAppSettingsSelected && IsWindowShown;
    }

    /// <summary>La fenêtre revient au premier plan : c'est le moment où l'on découvre que l'utilisateur a installé
    /// RTSS ou lancé son installeur dans une autre fenêtre. Une relecture du registre, hors du thread d'interface.</summary>
    public void OnWindowActivated() => _ = _installations.RefreshAsync();

    /// <summary>Bouton "Paramètres" : la liste se désélectionne, pour qu'un seul élément paraisse actif.</summary>
    [RelayCommand]
    private void ShowAppSettings()
    {
        SelectedNavItem = null;
        CurrentPage = AppSettingsNav;
    }

    private static string Glyph(int codePoint) => char.ConvertFromUtf32(codePoint);

    /// <summary>Ordre important : les ventilateurs repassent en automatique avant que le service NVAPI
    /// ne rende la carte au pilote et ne décharge NVAPI.</summary>
    public void Dispose()
    {
        // Avant le Monitoring : la liste des processus est abonnée à ses relevés.
        _processes.Dispose();
        _installations.Dispose();
        _fans.Dispose();
        _gpu.Dispose();
        _cpu.Dispose();
        _overlay.Dispose();
        _monitoring.Dispose();
        _gpuControl.Dispose();
        _cpuControl.Dispose();
        _hardware.Dispose();
    }
}
