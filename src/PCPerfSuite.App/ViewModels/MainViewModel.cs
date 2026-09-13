using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PCPerfSuite.Core.SystemInfo;

namespace PCPerfSuite.App.ViewModels;

public sealed record NavEntry(string Title, object ViewModel);

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly MonitoringViewModel _monitoring = new();

    public bool IsElevated { get; } = ElevationHelper.IsAdministrator();
    public bool ShowElevationBanner => !IsElevated;

    public ObservableCollection<NavEntry> NavItems { get; }

    [ObservableProperty] private NavEntry? selectedNavItem;
    [ObservableProperty] private object? currentViewModel;

    public MainViewModel()
    {
        var cleanup = new CleanupViewModel();
        var storage = new StorageViewModel();
        var settings = new SettingsViewModel();

        var fans = new ComingSoonViewModel(
            "Courbes de ventilation",
            "Pilotage des ventilateurs CPU, boîtier et watercooling (AIO ou custom loop), par carte mère.",
            new[]
            {
                "Détection automatique de tous les ventilateurs connectés (via la puce Super I/O de ta carte mère).",
                "Éditeur de courbe température → vitesse, par ventilateur, avec profils (Silencieux / Équilibré / Perf).",
                "Compatible ASUS/MSI/Gigabyte/ASRock là où le contrôleur embarqué le permet — certains modèles limitent ce que le logiciel peut piloter, on te le dira clairement plutôt que de deviner.",
            });

        var gpu = new ComingSoonViewModel(
            "Overclock & contrôle GPU",
            "Fréquences, tension, limite de puissance et courbe ventilo GPU — NVIDIA d'abord (ta RTX 5070 Ti), AMD/Intel ensuite.",
            new[]
            {
                "Décalages d'horloge cœur/mémoire et limite de power (%) via NVAPI.",
                "Courbe ventilo GPU dédiée, avec verrou de sécurité sur les températures.",
                "Profils applicables automatiquement au lancement d'un jeu.",
            });

        var overlay = new ComingSoonViewModel(
            "Overlay en jeu",
            "Un affichage à l'écran façon MSI Afterburner/RTSS, mais avec des polices personnalisables et une UI plus simple à configurer.",
            new[]
            {
                "Overlay superposé transparent (FPS, temps de frame, temps CPU/GPU, températures).",
                "Police, taille, couleur et position personnalisables par métrique.",
                "Limitation connue : sans pilote noyau façon RTSS, l'overlay ne s'affichera pas sur certains jeux en plein écran exclusif — on visera d'abord le mode fenêtré/sans bordure, qui couvre la grande majorité des jeux modernes.",
            });

        NavItems = new ObservableCollection<NavEntry>
        {
            new("Monitoring", _monitoring),
            new("Nettoyage", cleanup),
            new("Stockage", storage),
            new("Paramètres Windows", settings),
            new("Ventilateurs", fans),
            new("GPU", gpu),
            new("Overlay", overlay),
        };

        SelectedNavItem = NavItems[0];
        CurrentViewModel = _monitoring;
    }

    partial void OnSelectedNavItemChanged(NavEntry? value)
    {
        if (value is not null) CurrentViewModel = value.ViewModel;
    }

    public void Dispose() => _monitoring.Dispose();
}
