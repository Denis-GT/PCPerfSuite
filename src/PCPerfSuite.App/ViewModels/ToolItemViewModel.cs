using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCPerfSuite.App.Utils;
using PCPerfSuite.Core.Installations;

namespace PCPerfSuite.App.ViewModels;

/// <summary>
/// Une ligne de la Boîte à outils : un outil, sa version du catalogue, ce qui est sur ce PC, et les boutons qui ont du
/// sens dans cet état. Le bouton principal change selon l'état (Installer, Lancer, Télécharger, Page officielle) ; les
/// autres n'apparaissent que s'ils servent.
///
/// Ces lignes ne nourrissent jamais le clignotement du bouton Paramètres (<see cref="InstallationsViewModel.HasMissing"/>) :
/// aucun de ces outils n'est requis par l'app.
/// </summary>
public sealed partial class ToolItemViewModel : ObservableObject
{
    private enum PrimaryAction
    {
        None,
        InstallPortable,
        RunInstaller,
        Launch,
        Download,
        OpenPage,
    }

    private readonly ToolboxViewModel _owner;
    private CancellationTokenSource? _operation;
    private PrimaryAction _primary;

    public ToolItemViewModel(ToolDefinition definition, ToolboxViewModel owner)
    {
        Definition = definition;
        _owner = owner;
        PrimaryCommand = new AsyncRelayCommand(RunPrimaryAsync, () => !IsBusy && PrimaryLabel is not null);
        DownloadCommand = new AsyncRelayCommand(DownloadAsync, () => !IsBusy && CanDownload);
        UpdateCommand = new AsyncRelayCommand(UpdateAsync, () => !IsBusy && CanUpdate);
        RemoveCommand = new RelayCommand(Remove, () => !IsBusy && CanRemove);
        WingetCommand = new RelayCommand(UseWinget, () => !IsBusy && CanUseWinget);
        OpenPageCommand = new RelayCommand(OpenPage, () => Definition.OfficialPage is not null);
        CopyLinkCommand = new RelayCommand(CopyLink, () => DirectLink is not null);
        CancelCommand = new RelayCommand(() => _operation?.Cancel(), () => IsBusy);
        RefreshState();
    }

    public ToolDefinition Definition { get; }

    public IAsyncRelayCommand PrimaryCommand { get; }
    public IAsyncRelayCommand DownloadCommand { get; }
    public IAsyncRelayCommand UpdateCommand { get; }
    public IRelayCommand RemoveCommand { get; }
    public IRelayCommand WingetCommand { get; }
    public IRelayCommand OpenPageCommand { get; }
    public IRelayCommand CopyLinkCommand { get; }
    public IRelayCommand CancelCommand { get; }

    public string Name => Definition.Name;
    public string Purpose => Definition.Purpose;
    public string Details => $"Éditeur : {Definition.Editor} · Licence : {Definition.Licence.Summary}";

    /// <summary>Badge « licence pro requise » (décision D5) : la version gratuite interdit l'usage commercial.</summary>
    public bool RequiresProLicence => Definition.Licence.RequiresProLicence;

    /// <summary>Badge « non signé » : l'éditeur ne signe pas ses fichiers, PCPerfSuite ne les lance jamais.</summary>
    public bool IsUnsigned => Definition.HasDirectDownload && !Definition.IsSigned;

    /// <summary>Badge « expérimental » (règle 6) : ce parcours n'a pas encore été essayé sur une vraie machine.</summary>
    public bool IsExperimental => Definition.Delivery != ToolDelivery.OfficialPageOnly && !Definition.IsVerified;

    public bool HasOfficialPage => Definition.OfficialPage is not null;

    /// <summary>Précisions sous l'outil : pourquoi pas de lien direct, ce qu'il faut savoir.</summary>
    public string? Note
    {
        get
        {
            var parts = new List<string>();
            if (Definition.NoDirectLinkReason is { } reason) parts.Add(reason);
            if (IsUnsigned)
            {
                parts.Add("Non signé par son éditeur : PCPerfSuite vérifie son empreinte, le dépose dans tes Téléchargements, et ne le lance jamais.");
            }

            if (Definition.Note is { } note) parts.Add(note);
            return parts.Count == 0 ? null : string.Join(" ", parts);
        }
    }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(VersionText), nameof(DirectLink))]
    private ToolRelease? release;

    [ObservableProperty] private ToolInstallState state = ToolInstallState.Absent;

    [ObservableProperty] private string statusText = "Vérification…";

    /// <summary>Badge d'état vert (présent) ou neutre.</summary>
    [ObservableProperty] private bool isPresent;

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(PrimaryCommand)), NotifyPropertyChangedFor(nameof(ShowPageButton))]
    private string? primaryLabel;

    /// <summary>Bouton « Page officielle » à part, sauf quand c'est déjà le bouton principal.</summary>
    public bool ShowPageButton => HasOfficialPage && _primary != PrimaryAction.OpenPage;

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(DownloadCommand))]
    private bool canDownload;

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(UpdateCommand))]
    private bool canUpdate;

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(RemoveCommand))]
    private bool canRemove;

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(WingetCommand))]
    private bool canUseWinget;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PrimaryCommand), nameof(DownloadCommand), nameof(UpdateCommand), nameof(RemoveCommand),
        nameof(WingetCommand), nameof(CancelCommand))]
    private bool isBusy;

    /// <summary>Progression, puis résultat de la dernière action ; une relecture de l'état ne l'efface pas.</summary>
    [ObservableProperty] private string? message;

    /// <summary>Lien direct versionné du catalogue, affiché pour être copié ; null sans lien vérifié.</summary>
    public string? DirectLink => Release?.Url.AbsoluteUri;

    /// <summary>« Version 3.01 · 5,2 Mo », ou la raison de l'absence de lien.</summary>
    public string? VersionText => Release is { } r
        ? $"Version {r.Version} · {ByteFormatter.Format(r.Size)}"
        : Definition.HasDirectDownload ? "Aucun lien direct vérifié dans le catalogue actuel : page officielle." : null;

    partial void OnReleaseChanged(ToolRelease? value) => RefreshState();

    partial void OnStateChanged(ToolInstallState value) => RefreshState();

    /// <summary>Recalcule l'état affiché et les boutons, d'après la version du catalogue et ce qui est sur le PC.</summary>
    private void RefreshState()
    {
        ToolDelivery delivery = Definition.Delivery;
        bool present = State.IsPresent;
        string version = State.Version is { Length: > 0 } v ? $" ({v})" : "";
        bool hasRelease = Release is not null;
        bool newer = present && hasRelease && ToolVersion.IsNewer(Release!.Version, State.Version);

        IsPresent = present && delivery != ToolDelivery.OfficialPageOnly;
        CanDownload = hasRelease && delivery != ToolDelivery.DownloadOnly;
        CanRemove = present && State.IsPortable;
        CanUpdate = newer && Definition.IsSigned && (Definition.IsPortable || Definition.IsInstaller);
        CanUseWinget = CanUseWinget && Definition.IsInstaller && !present;

        (StatusText, _primary) = delivery switch
        {
            ToolDelivery.OfficialPageOnly => ("Pas de lien direct", PrimaryAction.OpenPage),
            ToolDelivery.BuiltIn => (present ? "Fourni par Windows" : "Absent de ce Windows", present ? PrimaryAction.Launch : PrimaryAction.None),
            _ when !hasRelease && !present => ("Pas de lien vérifié", PrimaryAction.OpenPage),
            ToolDelivery.DownloadOnly => (present ? $"Installé{version}" : "À télécharger", hasRelease ? PrimaryAction.Download : PrimaryAction.OpenPage),
            ToolDelivery.PortableExe or ToolDelivery.PortableZip => present
                ? ($"Portable prêt{version}", PrimaryAction.Launch)
                : ("Non installé", PrimaryAction.InstallPortable),
            _ => present
                ? ($"Installé{version}", State.ExecutablePath is not null ? PrimaryAction.Launch : PrimaryAction.None)
                : ("Non installé", PrimaryAction.RunInstaller),
        };

        if (newer) StatusText += $" · {Release!.Version} disponible";

        PrimaryLabel = _primary switch
        {
            PrimaryAction.InstallPortable => "Installer (portable)",
            PrimaryAction.RunInstaller => "Installer",
            PrimaryAction.Launch => "Lancer",
            PrimaryAction.Download => "Télécharger",
            PrimaryAction.OpenPage => "Page officielle",
            _ => null,
        };
    }

    private Task RunPrimaryAsync()
    {
        switch (_primary)
        {
            case PrimaryAction.InstallPortable: return RunBusyAsync(InstallPortableAsync);
            case PrimaryAction.RunInstaller: return RunBusyAsync(RunInstallerAsync);
            case PrimaryAction.Download: return DownloadAsync();
            case PrimaryAction.Launch: Launch(); break;
            case PrimaryAction.OpenPage: OpenPage(); break;
        }

        return Task.CompletedTask;
    }

    private Task UpdateAsync() => Definition.IsPortable ? RunBusyAsync(InstallPortableAsync) : RunBusyAsync(RunInstallerAsync);

    private Task DownloadAsync() => RunBusyAsync(async token =>
    {
        ToolActionOutcome outcome = await _owner.Actions.DownloadForUserAsync(Definition, Progress(), token);
        ShowOutcome(outcome);
        if (outcome.Succeeded && outcome.Path is { } path) UnelevatedLauncher.TryShowInExplorer(path, out _);
    });

    private async Task InstallPortableAsync(CancellationToken token)
    {
        ToolActionOutcome outcome = await _owner.Actions.InstallPortableAsync(Definition, Progress(), token);
        ShowOutcome(outcome);
        await _owner.RefreshItemAsync(this);
    }

    private async Task RunInstallerAsync(CancellationToken token)
    {
        if (Release is not { } r) return;

        string from = r.Url.Host;
        bool confirmed = _owner.Confirm(
            $"Installer {Definition.Name} {r.Version} ?\n\n" +
            $"L'installeur officiel, signé par {Definition.ExpectedPublisher}, va être téléchargé depuis {from} ({ByteFormatter.Format(r.Size)}), " +
            "puis vérifié (empreinte et signature) avant d'être lancé. PCPerfSuite tourne en administrateur : l'installeur " +
            "s'ouvrira avec ces droits, sans nouvelle invite de Windows.\n\n" +
            "Une installation ne s'annule pas depuis PCPerfSuite : elle se désinstalle depuis Paramètres Windows › Applications.");
        if (!confirmed) return;

        ToolActionOutcome outcome = await _owner.Actions.RunInstallerAsync(Definition, Progress(), token);
        ShowOutcome(outcome);
        await _owner.RefreshItemAsync(this);
    }

    private void Launch()
    {
        ToolActionOutcome outcome = ToolboxActions.Launch(Definition, State);
        Message = outcome.Message;
    }

    private void Remove()
    {
        bool confirmed = _owner.Confirm(
            $"Supprimer {Definition.Name}{(State.Version is { } v ? $" {v}" : "")} ?\n\n" +
            $"Son dossier ({State.Location ?? ProgramDataFolder.RootPath}) est effacé, avec les réglages que l'outil y a enregistrés. " +
            "Il se réinstalle d'un clic.");
        if (!confirmed) return;

        Message = ToolboxActions.RemovePortable(Definition).Message;
        _ = _owner.RefreshItemAsync(this);
    }

    private void UseWinget()
    {
        bool confirmed = _owner.Confirm(
            $"Installer {Definition.Name} avec winget ?\n\n" +
            $"winget, le gestionnaire de paquets de Windows, va installer « {Definition.WingetId} » depuis sa propre source. " +
            "Il vérifie l'empreinte de son manifeste, mais PCPerfSuite ne contrôle pas ce fichier comme les siens. " +
            "winget s'ouvre dans sa propre fenêtre et peut te demander d'accepter des conditions.");
        if (!confirmed) return;

        Message = WingetFallback.Install(Definition).Message;
    }

    private void OpenPage()
    {
        if (Definition.OfficialPage is not { } page) return;
        if (!ExternalLink.TryOpen(page, out string? error)) Message = error;
    }

    private void CopyLink()
    {
        if (DirectLink is not { } link) return;
        Message = _owner.CopyToClipboard(link) ? "Lien copié dans le presse-papiers." : "Le presse-papiers est occupé, réessaie dans un instant.";
    }

    /// <summary>Un lien mort ou une empreinte qui a changé : la page officielle reste le recours (règle 3), et winget
    /// pour un installeur signé, s'il est là. Jamais de contournement.</summary>
    private void ShowOutcome(ToolActionOutcome outcome)
    {
        Message = outcome.Message;
        if (outcome.Succeeded || outcome.Failure is not (DownloadFailureKind.LinkUnavailable or DownloadFailureKind.Mismatch)) return;

        Message += " Passe par la page officielle de l'outil.";
        CanUseWinget = Definition.IsInstaller && Definition.IsSigned && WingetFallback.IsValidId(Definition.WingetId)
                       && _owner.HasWinget && !State.IsPresent;
        if (CanUseWinget) Message += " Ou essaie avec winget.";
    }

    private Progress<string> Progress() => new(text => Message = text);

    private async Task RunBusyAsync(Func<CancellationToken, Task> action)
    {
        _operation?.Dispose();
        _operation = new CancellationTokenSource();
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(_operation.Token, _owner.ShutdownToken);
        IsBusy = true;
        Message = null;
        try
        {
            await action(linked.Token);
        }
        catch (Exception ex)
        {
            // Les actions du Core ne lèvent pas ; une erreur d'interface ne doit pas fermer l'app pour autant.
            Message = $"Action impossible ({ex.Message}).";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
