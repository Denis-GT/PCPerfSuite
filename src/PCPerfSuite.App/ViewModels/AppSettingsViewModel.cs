using CommunityToolkit.Mvvm.ComponentModel;
using PCPerfSuite.Core.PowerSettings;

namespace PCPerfSuite.App.ViewModels;

/// <summary>Sous-onglet des Paramètres. <see cref="Key"/> désigne le panneau à afficher dans AppSettingsView ;
/// <see cref="NeedsAttention"/> fait clignoter l'onglet (voir Attention.IsBlinking) et <see cref="ToolTip"/> dit pourquoi.</summary>
public sealed partial class AppSettingsSection : ObservableObject
{
    public AppSettingsSection(string key, string title)
    {
        Key = key;
        Title = title;
    }

    public string Key { get; }
    public string Title { get; }

    [ObservableProperty] private bool needsAttention;
    [ObservableProperty] private string? toolTip;
}

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

    /// <summary>Vrai quand la page Paramètres est affichée et la fenêtre visible : posé par <see cref="MainViewModel"/>.
    /// Un onglet ne clignote que s'il peut être vu.</summary>
    [ObservableProperty] private bool isPageShown;

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

        Installations.PropertyChanged += (_, _) => UpdateAttention();
        UpdateAttention();
    }

    partial void OnIsPageShownChanged(bool value) => UpdateAttention();

    partial void OnSelectedSectionChanged(AppSettingsSection value) => UpdateAttention();

    /// <summary>L'onglet Installations clignote tant qu'un logiciel manque, que les Paramètres sont sous les yeux de
    /// l'utilisateur, et qu'il n'est pas déjà dessus : une fois l'onglet ouvert, c'est son contenu qui parle.</summary>
    private void UpdateAttention()
    {
        AppSettingsSection section = Sections.First(s => s.Key == "installations");
        section.NeedsAttention = Installations.HasMissing && IsPageShown && !ReferenceEquals(SelectedSection, section);
        section.ToolTip = Installations.MissingSummary;
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
