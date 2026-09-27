using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using PCPerfSuite.Core.PowerSettings;
using PCPerfSuite.Core.SystemInfo;

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

    /// <summary>Lancer PCPerfSuite à l'ouverture de session Windows, par une tâche planifiée (voir
    /// <see cref="StartupTask"/>). L'état est celui de la tâche elle-même, pas une copie dans settings.json : si
    /// l'utilisateur la supprime dans le Planificateur de tâches, l'interrupteur le reflète.</summary>
    [ObservableProperty] private bool launchAtStartup;

    /// <summary>Faux tant que l'état se lit ou s'applique, et quand ce PC ne permet pas de modifier ce réglage
    /// (app sans droits administrateur, autre compte) : l'interrupteur est alors grisé, avec sa raison dessous.</summary>
    [ObservableProperty] private bool canChangeLaunchAtStartup;

    /// <summary>Pourquoi le réglage est indisponible, ou pourquoi il a échoué. Null quand tout va bien.</summary>
    [ObservableProperty] private string? launchAtStartupMessage;

    /// <summary>Vrai pendant que l'interrupteur est remis en place par le code : l'écriture ne doit pas repartir.</summary>
    private bool _restoringLaunchAtStartup;

    private bool _launchAtStartupAvailable;
    private bool _applyingLaunchAtStartup;

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

        // Lu hors du thread d'interface (le Planificateur de tâches est un service COM), et réparé au passage si le
        // dossier de l'app a été déplacé depuis la création de la tâche.
        _ = RefreshLaunchAtStartupAsync(repair: true);
    }

    /// <summary>Relit la tâche de démarrage : à l'ouverture des Paramètres, pour refléter une suppression faite à la
    /// main dans le Planificateur de tâches. Sans effet pendant qu'un changement est en cours d'application.</summary>
    public async Task RefreshLaunchAtStartupAsync(bool repair = false)
    {
        if (_applyingLaunchAtStartup) return;

        StartupTaskInfo info = await Task.Run(() =>
        {
            if (repair) StartupTask.RepairIfTargetMissing();
            return StartupTask.Read();
        });

        if (_applyingLaunchAtStartup) return;
        ApplyStartupInfo(info);
    }

    private void ApplyStartupInfo(StartupTaskInfo info)
    {
        _restoringLaunchAtStartup = true;
        LaunchAtStartup = info.IsEnabled;
        _restoringLaunchAtStartup = false;

        _launchAtStartupAvailable = info.UnavailableReason is null;
        CanChangeLaunchAtStartup = _launchAtStartupAvailable;
        LaunchAtStartupMessage = info.UnavailableReason ?? OtherCopyMessage(info);
    }

    /// <summary>La tâche existe mais lance une autre copie de l'app, toujours présente : on ne la touche pas (voir
    /// <see cref="StartupTask.RepairIfTargetMissing"/>), mais on le dit, avec le moyen de la reprendre.</summary>
    private static string? OtherCopyMessage(StartupTaskInfo info)
    {
        if (!info.IsEnabled || string.IsNullOrEmpty(info.Target) || Environment.ProcessPath is not { } current) return null;

        return string.Equals(Path.GetFullPath(info.Target), Path.GetFullPath(current), StringComparison.OrdinalIgnoreCase)
            ? null
            : $"Le démarrage automatique lance actuellement une autre copie de PCPerfSuite : {info.Target}. " +
              "Désactive puis réactive l'option pour que ce soit celle-ci.";
    }

    partial void OnLaunchAtStartupChanged(bool value)
    {
        if (_restoringLaunchAtStartup) return;
        _ = ApplyLaunchAtStartupAsync(value);
    }

    /// <summary>Crée ou supprime la tâche hors du thread d'interface. En cas d'échec, l'interrupteur revient à sa
    /// position d'origine et le message dit pourquoi : il ne doit jamais afficher un état qui n'existe pas.</summary>
    private async Task ApplyLaunchAtStartupAsync(bool enable)
    {
        _applyingLaunchAtStartup = true;
        CanChangeLaunchAtStartup = false;

        string? error = null;
        bool succeeded = await Task.Run(() => enable ? StartupTask.TryEnable(out error) : StartupTask.TryDisable(out error));

        if (!succeeded)
        {
            _restoringLaunchAtStartup = true;
            LaunchAtStartup = !enable;
            _restoringLaunchAtStartup = false;

            CanChangeLaunchAtStartup = _launchAtStartupAvailable;
            LaunchAtStartupMessage = error;
            _applyingLaunchAtStartup = false;
            return;
        }

        // Relit l'état réel plutôt que de supposer : c'est lui qui fait foi.
        _applyingLaunchAtStartup = false;
        await RefreshLaunchAtStartupAsync();
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
