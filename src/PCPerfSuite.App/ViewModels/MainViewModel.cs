using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCPerfSuite.Core.Hardware;
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

    private readonly MonitoringViewModel _monitoring;
    private readonly ProcessesViewModel _processes;
    private readonly FanCurvesViewModel _fans;
    private readonly GpuControlViewModel _gpu;
    private readonly OverlayViewModel _overlay;

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

    public MainViewModel()
    {
        _monitoring = new MonitoringViewModel(_hardware);
        _processes = new ProcessesViewModel(_monitoring);
        _fans = new FanCurvesViewModel(_hardware, _gpuControl, _monitoring);
        _gpu = new GpuControlViewModel(_gpuControl, _monitoring);
        _overlay = new OverlayViewModel(_monitoring);
        AppSettings = new AppSettingsViewModel(new CompatibilityViewModel(_hardware, _monitoring, _fans, _gpu));
        AppSettingsNav = new NavEntry("Paramètres", Glyph(0xE713), AppSettings);

        NavItems = new ObservableCollection<NavEntry>
        {
            new("Monitoring", Glyph(0xE9D9), _monitoring),
            new("Processus", Glyph(0xE9F5), _processes),
            new("Nettoyage", Glyph(0xE74D), Cleanup),
            new("Stockage", Glyph(0xEDA2), Storage),
            new("Optimisation Windows", Glyph(0xEC4A), Optimization),
            new("Ventilateurs", Glyph(0xE9CA), _fans),
            new("GPU", Glyph(0xE950), _gpu),
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
    }

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
        _fans.Dispose();
        _gpu.Dispose();
        _overlay.Dispose();
        _monitoring.Dispose();
        _gpuControl.Dispose();
        _hardware.Dispose();
    }
}
