using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCPerfSuite.Core.Hardware.Cpu;
using PCPerfSuite.Core.Installations;
using PCPerfSuite.Core.Overlay;

namespace PCPerfSuite.App.ViewModels;

/// <summary>État du pilote PawnIO sur ce PC, tel que l'utilisateur doit le comprendre.</summary>
public enum PawnIoState
{
    /// <summary>Pas de processeur x64 : PawnIO n'existe pas pour ce PC, il n'y a rien à installer.</summary>
    Unsupported,
    NotInstalled,
    /// <summary>Installé et chargé par l'app.</summary>
    Ready,
    /// <summary>Installé depuis le lancement de l'app : il suffit de la relancer pour qu'elle l'utilise.</summary>
    RestartRequired,
    /// <summary>Présent sur le disque, mais sa bibliothèque ne se charge pas (trop ancienne, endommagée).</summary>
    Unusable,
}

/// <summary>
/// Un logiciel externe dont PCPerfSuite a besoin, avec son état et ses deux boutons : un principal (installer,
/// lancer) et un secondaire (page officielle). Un bouton dont le libellé est null n'existe pas dans l'état actuel.
///
/// Les sous-classes traduisent ce que la détection a relevé (<c>Apply</c>) ; cette classe porte ce que la vue
/// affiche. « Manquant » veut dire « pas installé » : un logiciel installé mais éteint (RTSS non lancé) n'est pas
/// manquant, l'app n'a rien à faire installer.
/// </summary>
public abstract partial class ExternalSoftwareViewModel : ObservableObject
{
    protected ExternalSoftwareViewModel()
    {
        PrimaryCommand = new AsyncRelayCommand(() => RunBusyAsync(RunPrimaryAsync), () => !IsBusy && PrimaryLabel is not null);
        SecondaryCommand = new RelayCommand(RunSecondary, () => !IsBusy && SecondaryLabel is not null);
    }

    public IAsyncRelayCommand PrimaryCommand { get; }
    public IRelayCommand SecondaryCommand { get; }

    /// <summary>Nom complet, en tête de ligne.</summary>
    public abstract string Name { get; }

    /// <summary>Nom court, pour l'info-bulle du bouton Paramètres.</summary>
    public abstract string ShortName { get; }

    /// <summary>À quoi il sert dans PCPerfSuite, en une ou deux phrases.</summary>
    public abstract string Purpose { get; }

    /// <summary>Ce qu'il apporte, en quelques mots, pour l'info-bulle (« FPS et overlay en jeu »).</summary>
    public abstract string MissingReason { get; }

    [ObservableProperty] private string statusText = "Vérification…";

    /// <summary>Précision sous la ligne d'état (« relance l'app », « n'existe pas pour ce processeur »).</summary>
    [ObservableProperty] private string? statusDetail;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsMissing))] private bool isInstalled;

    /// <summary>Faux quand ce PC ne peut pas utiliser ce logiciel : il n'est alors pas « manquant ».</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsMissing))] private bool isApplicable = true;

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(PrimaryCommand)), NotifyCanExecuteChangedFor(nameof(SecondaryCommand))]
    private bool isBusy;

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(PrimaryCommand))]
    private string? primaryLabel;

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(SecondaryCommand))]
    private string? secondaryLabel;

    /// <summary>Progression (« Téléchargement… 45 % »), puis résultat de la dernière action. Une relecture de l'état
    /// ne l'efface pas : il doit rester lisible après le retour de l'installeur.</summary>
    [ObservableProperty] private string? message;

    public bool IsMissing => IsApplicable && !IsInstalled;

    protected abstract Task RunPrimaryAsync();

    protected abstract void RunSecondary();

    private async Task RunBusyAsync(Func<Task> action)
    {
        IsBusy = true;
        Message = null;
        try
        {
            await action();
        }
        finally
        {
            IsBusy = false;
        }
    }
}

/// <summary>PawnIO : télécharge et lance l'installeur officiel signé (voir <see cref="OfficialInstaller"/>).</summary>
public sealed class PawnIoItemViewModel : ExternalSoftwareViewModel, IDisposable
{
    private readonly Func<Task> _refresh;
    private readonly CancellationTokenSource _cancellation = new();

    public PawnIoItemViewModel(Func<Task> refresh) => _refresh = refresh;

    public override string Name => "PawnIO";
    public override string ShortName => "PawnIO";
    public override string MissingReason => "températures CPU, limites de puissance";
    public override string Purpose =>
        "Pilote signé qui donne accès aux sondes bas niveau : températures et tensions du processeur, capteurs de la " +
        "carte mère, limites de puissance du CPU. Sans lui, ces valeurs s'affichent « N/D ».";

    public PawnIoState State { get; private set; }

    /// <summary>Classe ce que la détection a relevé. Statique et sans effet, pour que le diagnostic et l'onglet
    /// disent toujours la même chose.</summary>
    public static PawnIoState Classify(PawnIoInstallation installation)
    {
        if (!PawnIoDriver.IsSupportedPlatform) return PawnIoState.Unsupported;
        if (!installation.IsOnDisk) return PawnIoState.NotInstalled;
        if (PawnIoDriver.IsInstalled) return PawnIoState.Ready;

        // Sur le disque mais pas chargé : soit il n'y était pas au lancement (installé depuis), soit il y était et
        // sa bibliothèque a refusé de se charger.
        return PawnIoDriver.WasOnDiskAtProbe ? PawnIoState.Unusable : PawnIoState.RestartRequired;
    }

    public void Apply(PawnIoInstallation installation)
    {
        State = Classify(installation);
        string version = installation.Version is { } v ? $" ({v})" : "";

        IsApplicable = State != PawnIoState.Unsupported;
        IsInstalled = State is PawnIoState.Ready or PawnIoState.RestartRequired or PawnIoState.Unusable;

        switch (State)
        {
            case PawnIoState.Unsupported:
                StatusText = "Non disponible";
                StatusDetail = "PawnIO n'existe que pour les processeurs x64 : ce PC n'utilise pas ce pilote.";
                PrimaryLabel = null;
                SecondaryLabel = null;
                return;

            case PawnIoState.NotInstalled:
                StatusText = "Non installé";
                StatusDetail = null;
                PrimaryLabel = "Installer";
                break;

            case PawnIoState.Ready:
                StatusText = $"Installé{version}";
                StatusDetail = null;
                PrimaryLabel = "Mettre à jour";
                break;

            case PawnIoState.RestartRequired:
                StatusText = $"Installé{version}";
                StatusDetail = "Installé depuis le lancement de PCPerfSuite : relance l'app pour qu'elle l'utilise.";
                PrimaryLabel = "Mettre à jour";
                break;

            default:
                StatusText = $"Installé{version}, inutilisable";
                StatusDetail = PawnIoDriver.UnavailableReason ?? "Sa bibliothèque n'a pas pu être chargée.";
                PrimaryLabel = "Réinstaller";
                break;
        }

        SecondaryLabel = "Site officiel";
    }

    protected override async Task RunPrimaryAsync()
    {
        bool wasLoaded = PawnIoDriver.IsInstalled;
        var progress = new Progress<string>(text => Message = text);

        InstallOutcome outcome = await OfficialInstaller.DownloadAndRunAsync(
            PawnIoDriver.SetupSource, progress, _cancellation.Token);

        await _refresh();

        if (!outcome.Succeeded)
        {
            Message = $"{outcome.Message} Tu peux aussi l'installer depuis le site officiel (bouton « Site officiel »).";
        }
        else if (State is PawnIoState.NotInstalled)
        {
            // Un installeur qui se termine sans erreur mais ne laisse rien : mieux vaut le dire que d'afficher un succès.
            Message = "L'installeur s'est terminé, mais PawnIO n'apparaît pas installé. Essaie depuis le site officiel.";
        }
        else
        {
            Message = wasLoaded
                ? "Mise à jour terminée. Relance PCPerfSuite pour utiliser la nouvelle version."
                : outcome.Message;
        }
    }

    protected override void RunSecondary()
    {
        if (!PawnIoDriver.TryOpenDownloadPage(out string? error)) Message = error;
    }

    /// <summary>Annule un téléchargement en cours à la fermeture de l'app. Un installeur déjà lancé, lui, n'est
    /// jamais interrompu : arrêter l'installation d'un pilote à mi-chemin ferait plus de mal que de bien.</summary>
    public void Dispose() => _cancellation.Cancel();
}

/// <summary>RTSS : pas de lien direct stable, donc le bouton ouvre la page officielle ; une fois installé, il le lance.</summary>
public sealed class RtssItemViewModel : ExternalSoftwareViewModel
{
    /// <summary>RTSS met un instant à apparaître dans la liste des processus une fois lancé.</summary>
    private static readonly TimeSpan LaunchSettleTime = TimeSpan.FromMilliseconds(1500);

    private readonly Func<Task> _refresh;

    public RtssItemViewModel(Func<Task> refresh) => _refresh = refresh;

    public override string Name => "RTSS (RivaTuner Statistics Server)";
    public override string ShortName => "RTSS";
    public override string MissingReason => "FPS et overlay en jeu";
    public override string Purpose =>
        "Lit les FPS des jeux et affiche l'overlay de PCPerfSuite par-dessus, y compris en plein écran exclusif. " +
        "Gratuit ; RTSS doit être lancé pour que les FPS soient lus.";

    public RtssStatus Status { get; private set; } = new(false, null, null, false);

    public void Apply(RtssStatus status)
    {
        Status = status;
        IsApplicable = true;
        IsInstalled = status.IsInstalled;
        string version = status.Version is { } v ? $" ({v})" : "";

        if (!status.IsInstalled)
        {
            StatusText = "Non installé";
            StatusDetail = "PCPerfSuite ne peut pas le télécharger lui-même : Guru3D, l'éditeur de RTSS, ne propose pas de " +
                           "lien direct stable. Le bouton ouvre la page officielle ; l'état se met à jour dès ton retour ici.";
            PrimaryLabel = "Ouvrir la page de téléchargement";
            SecondaryLabel = null;
        }
        else if (status.IsRunning)
        {
            StatusText = $"Installé{version} · lancé";
            StatusDetail = null;
            PrimaryLabel = null;
            SecondaryLabel = "Page officielle";
        }
        else
        {
            StatusText = $"Installé{version} · non lancé";
            StatusDetail = "Les FPS et l'overlay en jeu ne fonctionnent que si RTSS tourne.";
            PrimaryLabel = status.ExecutablePath is not null ? "Lancer RTSS" : null;
            SecondaryLabel = "Page officielle";
        }
    }

    protected override async Task RunPrimaryAsync()
    {
        if (!Status.IsInstalled)
        {
            Message = ExternalLink.TryOpen(RtssInstallation.DownloadPageUrl, out string? openError)
                ? "Page de téléchargement ouverte dans ton navigateur. Une fois RTSS installé, reviens ici : l'état se met à jour tout seul."
                : openError;
            return;
        }

        if (!RtssInstallation.TryLaunch(Status.ExecutablePath, out string? launchError))
        {
            Message = launchError;
            return;
        }

        await Task.Delay(LaunchSettleTime);
        await _refresh();
    }

    protected override void RunSecondary()
    {
        if (!ExternalLink.TryOpen(RtssInstallation.DownloadPageUrl, out string? error)) Message = error;
    }
}

/// <summary>
/// Onglet « Installations » des Paramètres : les logiciels externes dont PCPerfSuite a besoin, leur état, et de quoi
/// les installer depuis leur source officielle. Regroupe aussi ce que l'app en tire ailleurs : le clignotement du
/// bouton Paramètres tant que l'un manque (<see cref="HasMissing"/>) et le diagnostic de compatibilité.
///
/// Pas de pilote graphique ici : NVAPI, ADLX et IGCL viennent avec le pilote de la carte, que seul son fabricant
/// ou Windows Update sait choisir, et l'onglet GPU explique déjà quand il manque.
/// </summary>
public sealed partial class InstallationsViewModel : ObservableObject, IDisposable
{
    private Task? _refreshTask;
    private bool _refreshAgain;

    public PawnIoItemViewModel PawnIo { get; }
    public RtssItemViewModel Rtss { get; }
    public IReadOnlyList<ExternalSoftwareViewModel> Items { get; }

    /// <summary>Vrai tant qu'au moins un logiciel n'est pas installé.</summary>
    [ObservableProperty] private bool hasMissing;

    /// <summary>Info-bulle courte : quoi installer et pourquoi. Null quand rien ne manque.</summary>
    [ObservableProperty] private string? missingSummary;

    public InstallationsViewModel()
    {
        PawnIo = new PawnIoItemViewModel(RefreshAsync);
        Rtss = new RtssItemViewModel(RefreshAsync);
        Items = new ExternalSoftwareViewModel[] { PawnIo, Rtss };

        RefreshNow();
    }

    /// <summary>Relit l'état sur le thread appelant : au lancement, pour que le premier affichage soit juste, et
    /// dans le diagnostic, qui se construit d'un bloc.</summary>
    public void RefreshNow() => Apply(PawnIoDriver.ReadInstallation(), RtssInstallation.Detect());

    /// <summary>Relit l'état hors du thread d'interface (le registre se parcourt en quelques millisecondes, mais
    /// l'app est relue à chaque retour de fenêtre). Une demande qui arrive pendant une relecture en déclenche une
    /// seconde juste après, et l'appelant attend qu'elle soit finie : un état lu avant la fin d'une installation ne
    /// doit pas rester affiché.</summary>
    [RelayCommand]
    public Task RefreshAsync()
    {
        if (_refreshTask is { IsCompleted: false })
        {
            _refreshAgain = true;
            return _refreshTask;
        }

        return _refreshTask = RunRefreshAsync();
    }

    private async Task RunRefreshAsync()
    {
        do
        {
            _refreshAgain = false;
            (PawnIoInstallation pawnIo, RtssStatus rtss) = await Task.Run(() => (PawnIoDriver.ReadInstallation(), RtssInstallation.Detect()));
            Apply(pawnIo, rtss);
        }
        while (_refreshAgain);
    }

    private void Apply(PawnIoInstallation pawnIo, RtssStatus rtss)
    {
        PawnIo.Apply(pawnIo);
        Rtss.Apply(rtss);

        List<ExternalSoftwareViewModel> missing = Items.Where(item => item.IsMissing).ToList();
        HasMissing = missing.Count > 0;
        MissingSummary = missing.Count == 0
            ? null
            : "À installer : " + string.Join(" · ", missing.Select(item => $"{item.ShortName} ({item.MissingReason})"));
    }

    public void Dispose() => PawnIo.Dispose();
}
