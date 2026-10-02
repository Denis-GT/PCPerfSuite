namespace PCPerfSuite.Core.SystemInfo;

/// <summary>
/// Dossier de données de PCPerfSuite : réglages, journaux, témoins, journal de session, et demain bench et rapports.
///
/// Un seul endroit décide où vivent ces fichiers, pour que le mode portable (racine sur une clé USB) les déplace tous
/// d'un coup. La racine se choisit une fois, en tête du démarrage (<see cref="TryUseRoot"/>) ; sans choix, c'est
/// %LOCALAPPDATA%\PCPerfSuite. Aucun chemin n'est figé dans un champ statique : chaque appelant relit
/// <see cref="Current"/> au moment d'écrire.
///
/// Ajouter un fichier ou un sous-dossier : une propriété nommée ici, jamais un Path.Combine sur <see cref="Root"/>
/// ailleurs (noms réservés dans docs/decisions.md).
/// </summary>
public sealed class AppDataPaths
{
    public const string FolderName = "PCPerfSuite";

    private static readonly AppDataRootChoice Choice = new(() => new AppDataPaths(DefaultRoot));

    public AppDataPaths(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("Racine du dossier de données vide.", nameof(root));
        Root = Path.GetFullPath(root);
    }

    /// <summary>%LOCALAPPDATA%\PCPerfSuite : la racine tant que personne n'en a choisi une autre.</summary>
    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), FolderName);

    /// <summary>Dossier de données de cette session. Figé à la première lecture : ce qui a été écrit à un endroit
    /// doit être relu au même endroit jusqu'à la fermeture.</summary>
    public static AppDataPaths Current => Choice.Current;

    /// <summary>Choisit la racine de la session (mode portable). Refusé, et false, une fois <see cref="Current"/>
    /// lue : les fichiers seraient sinon éparpillés entre deux dossiers.</summary>
    public static bool TryUseRoot(string root) => Choice.TryChoose(new AppDataPaths(root));

    public string Root { get; }

    /// <summary>Réglages de l'app (AppSettingsStore).</summary>
    public string SettingsFile => InRoot("settings.json");

    /// <summary>Journal des erreurs inattendues (CrashLog).</summary>
    public string CrashLogFile => InRoot("erreurs.log");

    /// <summary>État de la puce des ventilateurs au démarrage et à la fermeture (FanChipDiagnostic).</summary>
    public string FanChipDiagnosticFile => InRoot("diagnostic-ventilateurs.log");

    /// <summary>Témoin du sondage ADLX (AdlxProbeGuard).</summary>
    public string AdlxSentinelFile => InRoot("adlx-plantage.temoin");

    /// <summary>Journal de session : opérations risquées en cours, reprises au lancement (SessionJournal, StartupRecovery).</summary>
    public string SessionJournalFile => InRoot("journal-session.jsonl");

    /// <summary>Historique d'usage de la bascule automatique (#9) : agrégats par jour, applications vues, journal des
    /// bascules (UsageHistory). Hors de settings.json, réécrit en entier à chaque enregistrement.</summary>
    public string UsageFile => InRoot("usage.json");

    private string InRoot(string name) => Path.Combine(Root, name);
}

/// <summary>Choix de la racine pour une session : libre jusqu'à la première lecture, figé ensuite. À part pour être
/// testé sans toucher à la racine de l'app.</summary>
internal sealed class AppDataRootChoice
{
    private readonly object _gate = new();
    private readonly Func<AppDataPaths> _fallback;
    private AppDataPaths? _chosen;
    private bool _frozen;

    public AppDataRootChoice(Func<AppDataPaths> fallback) => _fallback = fallback;

    public AppDataPaths Current
    {
        get
        {
            lock (_gate)
            {
                _frozen = true;
                return _chosen ??= _fallback();
            }
        }
    }

    public bool TryChoose(AppDataPaths paths)
    {
        lock (_gate)
        {
            if (_frozen) return false;
            _chosen = paths;
            return true;
        }
    }
}
