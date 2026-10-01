using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCPerfSuite.Core.Installations;
using PCPerfSuite.Core.SystemInfo;

namespace PCPerfSuite.App.ViewModels;

/// <summary>Une rubrique de la Boîte à outils (Diagnostic, Stress et bench…) et ses outils.</summary>
public sealed record ToolGroupViewModel(string Title, IReadOnlyList<ToolItemViewModel> Items);

/// <summary>
/// Page Outils › Boîte à outils : les outils utiles au diagnostic et au réglage, téléchargés depuis leur source
/// officielle et vérifiés (empreinte du catalogue, signature et éditeur figés), ou la page officielle quand il n'y a pas
/// de lien direct fiable.
///
/// Rien n'est lu au démarrage (<see cref="IPageLifecycle"/>) : à la première ouverture, la copie en cache du catalogue
/// et l'état de chaque outil (registre, dossier sécurisé), puis le catalogue en ligne. Rien n'est jamais installé sans
/// un clic, et une installation est confirmée avant (« Non » par défaut).
/// </summary>
public sealed partial class ToolboxViewModel : ObservableObject, IPageLifecycle, IDisposable
{
    private const string DialogTitle = "PCPerfSuite — Boîte à outils";
    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");

    private readonly ToolCatalogStore _catalog;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _refreshTask;
    private Task? _statesTask;
    private bool _refreshAgain;
    private bool _loadRequested;

    /// <summary>Dossiers de travail plus vieux que ça (opération interrompue) : effacés à la première ouverture.</summary>
    private static readonly TimeSpan StaleWorkFolderAge = TimeSpan.FromHours(6);

    public ToolboxViewModel(ToolCatalogStore catalog)
    {
        _catalog = catalog;
        Items = ToolCatalog.All.Select(tool => new ToolItemViewModel(tool, this)).ToList();
        Groups = Items
            .GroupBy(item => item.Definition.Category)
            .OrderBy(group => group.Key)
            .Select(group => new ToolGroupViewModel(ToolCatalog.CategoryTitle(group.Key), group.ToList()))
            .ToList();
    }

    /// <summary>Annulé à la fermeture de l'app : les téléchargements en cours s'arrêtent (un installeur lancé, jamais).</summary>
    public CancellationToken ShutdownToken => _shutdown.Token;

    public IReadOnlyList<ToolItemViewModel> Items { get; }
    public IReadOnlyList<ToolGroupViewModel> Groups { get; }

    /// <summary>winget présent sur ce compte (repli proposé quand un lien direct ne répond plus).</summary>
    public bool HasWinget { get; private set; }

    /// <summary>Posé par MainViewModel (voir <see cref="IPageLifecycle"/>).</summary>
    [ObservableProperty] private bool isPageShown;

    [ObservableProperty] private string catalogText = "Catalogue : pas encore lu.";

    /// <summary>Catalogue en ligne refusé pour une raison qui n'est pas un simple réseau coupé (signature, retour en arrière).</summary>
    [ObservableProperty] private string? catalogWarning;

    /// <summary>Dossier sécurisé refusé : les outils portables ne peuvent pas être installés, et pourquoi.</summary>
    [ObservableProperty] private string? folderWarning;

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(RefreshCommand))] private bool isRefreshing;

    /// <summary>App lancée sous un autre compte que la personne devant l'écran : les fichiers vont dans les
    /// Téléchargements publics, et la page le dit d'avance.</summary>
    public string? OtherProfileMessage => SessionUser.IsOtherProfile
        ? $"PCPerfSuite tourne sous le compte {SessionUser.ProcessAccount}, pas sous {SessionUser.InteractiveAccount} : les " +
          $"fichiers téléchargés vont dans les Téléchargements publics{(UserDownloads.Choose() is { } folder ? $" ({folder.Path})" : "")}, " +
          "visibles de tous les comptes."
        : null;

    /// <summary>Première ouverture : lecture du catalogue et de l'état des outils, jamais dans le constructeur.</summary>
    partial void OnIsPageShownChanged(bool value)
    {
        if (!value || _loadRequested) return;

        _loadRequested = true;
        _ = RefreshAsync();
    }

    /// <summary>Relit le catalogue en ligne et l'état de chaque outil. Une demande pendant une relecture en relance une
    /// juste après, et l'appelant attend qu'elle soit finie.</summary>
    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task RefreshAsync()
    {
        if (_refreshTask is { IsCompleted: false })
        {
            _refreshAgain = true;
            return _refreshTask;
        }

        return _refreshTask = RunRefreshAsync();
    }

    private bool CanRefresh() => !IsRefreshing;

    private async Task RunRefreshAsync()
    {
        IsRefreshing = true;
        try
        {
            do
            {
                _refreshAgain = false;
                CatalogText = "Catalogue : lecture…";

                await Task.Run(() =>
                {
                    _catalog.LoadCache();
                    OfficialInstaller.PurgeStaleWorkFolders(StaleWorkFolderAge);
                });
                ApplyCatalog(_catalog.Status, checkingOnline: true);
                await RefreshStatesAsync();

                ToolCatalogStatus status = await _catalog.RefreshOnlineAsync(ShutdownToken);
                ApplyCatalog(status, checkingOnline: false);
            }
            while (_refreshAgain && !ShutdownToken.IsCancellationRequested);
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    /// <summary>La fenêtre revient au premier plan (installeur fini, OpenRGB installé à la main) : l'état des outils est
    /// relu, sans réseau.</summary>
    public void OnWindowActivated()
    {
        // Une boîte de confirmation qui se ferme réactive aussi la fenêtre : une relecture déjà en cours suffit.
        if (_loadRequested && IsPageShown && !IsRefreshing && _statesTask is not { IsCompleted: false }) _statesTask = RefreshStatesAsync();
    }

    /// <summary>État d'un seul outil, après une action.</summary>
    public async Task RefreshItemAsync(ToolItemViewModel item)
    {
        ToolInstallState state = await Task.Run(() => ToolDetection.Detect(item.Definition));
        item.State = state;
        UpdateFolderWarning(await Task.Run(ProgramDataFolder.Inspect));
    }

    private async Task RefreshStatesAsync()
    {
        List<ToolDefinition> tools = Items.Select(item => item.Definition).ToList();
        (IReadOnlyList<ToolInstallState> states, SecureFolderResult folder, bool winget) = await Task.Run(() =>
            (ToolDetection.DetectAll(tools), ProgramDataFolder.Inspect(), WingetFallback.FindExecutable() is not null));

        HasWinget = winget;
        for (int i = 0; i < Items.Count; i++)
        {
            Items[i].State = states[i];
            Items[i].IsChecked = true;
        }

        UpdateFolderWarning(folder);
    }

    private void ApplyCatalog(ToolCatalogStatus status, bool checkingOnline)
    {
        foreach (ToolItemViewModel item in Items) item.Release = _catalog.ReleaseOf(item.Definition.Id);

        ToolCatalogDocument document = status.Document;
        string date = document.GeneratedUtc is { } generated ? $" du {generated.ToLocalTime().ToString("d MMMM yyyy", French)}" : "";
        string origin = status.Origin switch
        {
            ToolCatalogOrigin.Online => "lu en ligne",
            ToolCatalogOrigin.Cache => "en ligne, gardé sur ce PC",
            _ => "intégré à l'app",
        };

        string text = $"Catalogue n° {document.Sequence}{date}, {origin}.";
        if (checkingOnline && _catalog.IsOnlineEnabled) text += " Vérification en ligne…";
        else if (status.OnlineMessage is { } online && !status.OnlineRefusalIsSuspicious) text += $" {Capitalize(online)}.";

        CatalogText = text;
        CatalogWarning = status.OnlineRefusalIsSuspicious && status.OnlineMessage is { } suspicious
            ? $"{Capitalize(suspicious)}. Si cela se répète, signale-le avec le rapport de Paramètres › Compatibilité de ce PC."
            : null;
    }

    /// <summary>Un dossier absent n'est pas un refus : il sera créé au premier outil portable.</summary>
    private void UpdateFolderWarning(SecureFolderResult folder)
        => FolderWarning = folder is { IsReady: false, IsAbsent: false }
            ? $"Les outils portables ne peuvent pas être installés : le dossier sécurisé %ProgramData%\\PCPerfSuite est refusé ({folder.Error})."
            : null;

    /// <summary>Confirmation « Non » par défaut, à chaque fois (règles communes, « Actions risquées »).</summary>
    public bool Confirm(string text)
    {
        Window? owner = Application.Current?.MainWindow;
        MessageBoxResult answer = owner is null
            ? MessageBox.Show(text, DialogTitle, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No)
            : MessageBox.Show(owner, text, DialogTitle, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        return answer == MessageBoxResult.Yes;
    }

    public bool CopyToClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch (ExternalException)
        {
            return false;
        }
    }

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpper(text[0], French) + text[1..];

    /// <summary>Annule les téléchargements en cours. La source n'est pas libérée : une opération qui se termine encore
    /// lit son jeton.</summary>
    public void Dispose() => _shutdown.Cancel();
}
