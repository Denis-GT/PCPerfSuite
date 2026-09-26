using CommunityToolkit.Mvvm.ComponentModel;
using PCPerfSuite.Core.PowerSettings;

namespace PCPerfSuite.App.ViewModels;

/// <summary>Sous-onglet des Paramètres. <paramref name="Key"/> désigne le panneau à afficher dans AppSettingsView.</summary>
public sealed record AppSettingsSection(string Key, string Title);

/// <summary>
/// "Paramètres" (bouton en bas de la barre latérale) : les réglages de PCPerfSuite lui-même, par sous-onglets.
/// Ajouter un onglet : une entrée dans <see cref="Sections"/>, puis son panneau dans AppSettingsView.
/// </summary>
public sealed partial class AppSettingsViewModel : ObservableObject
{
    public IReadOnlyList<AppSettingsSection> Sections { get; } = new[]
    {
        new AppSettingsSection("general", "Général"),
        new AppSettingsSection("installations", "Installations"),
        new AppSettingsSection("compatibility", "Compatibilité de ce PC"),
        new AppSettingsSection("themes", "Thèmes"),
    };

    [ObservableProperty] private AppSettingsSection selectedSection;

    /// <summary>Lue par <see cref="MainWindow"/> à chaque fermeture de la fenêtre.</summary>
    [ObservableProperty] private bool minimizeToTrayOnClose;

    /// <summary>Logiciels externes dont l'app a besoin (PawnIO, RTSS) : état et installation.</summary>
    public InstallationsViewModel Installations { get; }

    /// <summary>Diagnostic "Compatibilité de ce PC".</summary>
    public CompatibilityViewModel Compatibility { get; }

    /// <summary>Espace réservé du futur onglet des thèmes.</summary>
    public ComingSoonViewModel Themes { get; } = new(
        "Thèmes",
        "Personnalisation de l'apparence de PCPerfSuite, en préparation.",
        new[]
        {
            "Mode clair, sombre ou selon Windows.",
            "Couleur d'accent au choix, ou celle de Windows.",
            "Effet de verre (Mica / Acrylic) activable ou non.",
        });

    public AppSettingsViewModel(CompatibilityViewModel compatibility, InstallationsViewModel installations)
    {
        Compatibility = compatibility;
        Installations = installations;
        selectedSection = Sections[0];

        // Le champ plutôt que la propriété : passer par la propriété déclencherait l'enregistrement
        // du fichier au démarrage, avant toute action de l'utilisateur.
        minimizeToTrayOnClose = AppSettingsStore.Load().Window?.MinimizeToTrayOnClose ?? true;
    }

    partial void OnMinimizeToTrayOnCloseChanged(bool value)
    {
        // Relit le fichier plutôt que de garder une copie : les autres onglets y écrivent aussi.
        AppSettings settings = AppSettingsStore.Load();
        AppWindowSettings window = settings.Window ?? new AppWindowSettings();
        window.MinimizeToTrayOnClose = value;
        settings.Window = window;
        AppSettingsStore.Save(settings);
    }
}
