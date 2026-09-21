namespace PCPerfSuite.Core.Processes;

/// <summary>
/// Famille d'un processus, dans l'esprit des trois sections du Gestionnaire des tâches. Windows n'expose
/// aucune API qui donne ce classement : il est déduit de signaux observables (fenêtre visible, emplacement
/// de l'exécutable, compte propriétaire) et reste donc une approximation assumée, pas une vérité système.
/// </summary>
public enum ProcessKind
{
    /// <summary>Le processus a une fenêtre principale visible : c'est ce que l'utilisateur appelle une appli.</summary>
    Application,

    /// <summary>Ni fenêtre, ni signe d'appartenance à Windows.</summary>
    Background,

    /// <summary>Composant de Windows : exécutable du dossier Windows et/ou compte système.</summary>
    Windows,
}

/// <summary>
/// Identité d'un processus. Le PID seul ne suffit pas : Windows réattribue les numéros dès qu'un processus
/// se termine, si bien qu'entre l'affichage d'une ligne et le clic dessus, le même PID peut désigner un
/// tout autre programme. L'heure de démarrage lève l'ambiguïté et interdit de terminer un innocent.
/// <see cref="StartTimeUtc"/> vaut null quand elle n'a pas pu être lue (processus protégé) : l'identité est
/// alors incomplète et toute action destructrice doit être refusée.
/// </summary>
public readonly record struct ProcessIdentity(int Pid, DateTime? StartTimeUtc)
{
    public bool IsComplete => StartTimeUtc is not null;
}

/// <summary>
/// Ce qu'un relevé sait d'un processus. Toute valeur que Windows n'a pas laissé lire est null et s'affiche
/// « -- » : un processus protégé garde sa ligne, avec des cellules vides, plutôt que de disparaître de la
/// liste ou d'afficher un zéro qui serait faux.
/// </summary>
public sealed class ProcessInfo
{
    public required ProcessIdentity Identity { get; init; }

    public int Pid => Identity.Pid;
    public DateTime? StartTimeUtc => Identity.StartTimeUtc;

    /// <summary>PID du processus parent tel que le donne l'instantané Toolhelp. Il peut désigner un processus
    /// déjà terminé, voire un autre processus qui a hérité de son numéro : à ne jamais suivre sans comparer
    /// les heures de démarrage.</summary>
    public int ParentPid { get; init; }

    /// <summary>Nom du fichier exécutable, seule dénomination toujours disponible.</summary>
    public required string Name { get; init; }

    /// <summary>Description lisible tirée des ressources de version de l'exécutable (« Google Chrome »).
    /// Null si le fichier n'en porte pas ou n'a pas pu être lu.</summary>
    public string? Description { get; init; }

    public string? Publisher { get; init; }
    public string? ExecutablePath { get; init; }

    /// <summary>Titre de la fenêtre principale, quand le processus en a une de visible.</summary>
    public string? WindowTitle { get; init; }

    /// <summary>Compte propriétaire (« DOMAINE\Utilisateur »). Null pour les processus dont le jeton n'est
    /// pas lisible — le cas de tous les processus système tant que SeDebugPrivilege n'est pas activé, ce que
    /// l'app s'interdit volontairement (voir ProcessService).</summary>
    public string? UserName { get; init; }

    public ProcessKind Kind { get; init; }

    /// <summary>Processus déclaré critique par Windows : l'arrêter met le système par terre. Renseigné par
    /// IsProcessCritical quand le handle a pu être ouvert, false sinon — donc jamais l'unique garde-fou.</summary>
    public bool IsCritical { get; init; }

    /// <summary>Part du temps processeur de la machine entière, de 0 à 100 : un processus mono-thread qui
    /// sature un cœur d'un i5-14600K (20 processeurs logiques) affiche 5 %, comme dans le Gestionnaire des
    /// tâches. Null au tout premier relevé, faute d'un relevé précédent avec quoi faire la différence.</summary>
    public double? CpuPercent { get; init; }

    /// <summary>Mémoire physique réellement à ce processus, l'équivalent de la colonne « Mémoire » du
    /// Gestionnaire des tâches (working set privé).</summary>
    public long? PrivateWorkingSetBytes { get; init; }

    /// <summary>Mémoire physique occupée, parties partagées comprises (working set complet).</summary>
    public long? WorkingSetBytes { get; init; }

    /// <summary>Mémoire validée auprès du système, qu'elle soit en RAM ou dans le fichier d'échange
    /// (« Private Bytes »). Ce n'est pas le working set privé, et les deux diffèrent beaucoup.</summary>
    public long? CommittedBytes { get; init; }

    /// <summary>Débit d'entrées/sorties, en octets par seconde. Windows compte ici TOUTES les E/S du
    /// processus — fichiers, réseau, tubes, console — et pas seulement le disque : c'est le même compteur
    /// que la colonne du Gestionnaire des tâches, d'où le libellé « E/S » et non « Disque ».</summary>
    public double? IoBytesPerSecond { get; init; }

    public int ThreadCount { get; init; }

    /// <summary>Faux quand Windows a refusé d'ouvrir le processus (Idle, System, CSRSS, processus protégés) :
    /// la ligne existe, mais ses mesures resteront vides et aucune action ne pourra l'atteindre.</summary>
    public bool IsAccessible { get; init; }
}

/// <summary>Ce que le relevé a réellement pu mesurer sur cette machine, pour masquer une colonne entière
/// plutôt que d'afficher « -- » sur quatre cents lignes.</summary>
public sealed class ProcessCapabilities
{
    /// <summary>Le working set privé est exposé par PROCESS_MEMORY_COUNTERS_EX2, apparu avec les mises à jour
    /// cumulatives de septembre 2023 ; sinon on retombe sur le working set complet.</summary>
    public bool HasPrivateWorkingSet { get; init; }

    public bool HasIoRate { get; init; }
}

/// <summary>D'où vient le facteur qui convertit le temps processeur brut en %CPU affiché. Sert au
/// diagnostic « Compatibilité de ce PC » : une colonne qui reproduit le Gestionnaire des tâches doit
/// pouvoir dire sur quoi elle s'est réglée.</summary>
public enum CpuScaleSource
{
    /// <summary>Temps processeur brut, sans pondération : aucun compteur Windows n'a répondu. Les valeurs
    /// sont alors sous-évaluées sur toute machine qui dépasse sa fréquence nominale.</summary>
    Raw,

    /// <summary>Replié sur « % Processor Performance » : la fréquence moyenne des cœurs. Approximation,
    /// utilisée seulement tant qu'aucune calibration n'a pu être faite.</summary>
    Performance,

    /// <summary>Mesuré : rapport entre « % Processor Utility » (la charge qu'affiche le Gestionnaire des
    /// tâches) et la charge brute de la même machine sur le même intervalle.</summary>
    Calibrated,
}

/// <summary>Photo de l'ensemble des processus à un instant donné.</summary>
public sealed class ProcessSnapshot
{
    public required IReadOnlyList<ProcessInfo> Processes { get; init; }
    public required ProcessCapabilities Capabilities { get; init; }

    /// <summary>Durée du relevé complet : affichée dans l'interface, parce qu'un onglet qui coûte cher doit
    /// le dire plutôt que de laisser deviner pourquoi la machine rame.</summary>
    public TimeSpan ReadDuration { get; init; }

    public DateTime CapturedAtLocal { get; init; } = DateTime.Now;

    /// <summary>Processus que Windows a refusé d'ouvrir : sert à expliquer les cellules vides.</summary>
    public int InaccessibleCount { get; init; }

    /// <summary>Vrai tant qu'aucun relevé précédent ne permet de calculer un écart : les %CPU et les débits
    /// d'E/S sont alors tous null, et surtout pas zéro.</summary>
    public bool IsFirstSample { get; init; }

    /// <summary>Facteur appliqué au temps processeur brut pour obtenir le %CPU affiché.</summary>
    public double CpuScaleFactor { get; init; } = 1.0;

    public CpuScaleSource CpuScaleSource { get; init; }
}

public enum TerminateFailure
{
    None,

    /// <summary>Windows refuse : processus protégé, antivirus, ou compte plus privilégié que l'app.</summary>
    AccessDenied,

    /// <summary>Le processus s'était déjà terminé de lui-même. Ce n'est pas une erreur.</summary>
    AlreadyExited,

    /// <summary>Le PID existe toujours mais appartient désormais à un autre processus : rien n'a été touché.</summary>
    IdentityMismatch,

    /// <summary>L'appel a réussi mais le processus était encore là après le délai d'attente.</summary>
    StillRunning,

    Other,
}

/// <summary>Résultat d'une demande de terminaison. <paramref name="Message"/> porte le détail technique
/// (message Win32) quand il y en a un, jamais une phrase déjà mise en forme pour l'utilisateur : c'est le
/// ViewModel qui décide de ce qui s'affiche.</summary>
public sealed record TerminateResult(TerminateFailure Failure, string? Message = null)
{
    public bool Success => Failure == TerminateFailure.None;

    public static TerminateResult Ok { get; } = new(TerminateFailure.None);
}
