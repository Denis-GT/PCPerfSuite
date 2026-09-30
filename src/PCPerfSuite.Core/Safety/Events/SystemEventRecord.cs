using PCPerfSuite.Core.Compatibility;

namespace PCPerfSuite.Core.Safety.Events;

/// <summary>Les événements du journal Système que l'app sait lire. Aucun autre n'est retenu.</summary>
public enum SystemEventKind
{
    /// <summary>Kernel-Power 41 : Windows a redémarré sans s'être arrêté proprement. Journalisé au démarrage suivant.</summary>
    KernelPower41,

    /// <summary>WER-SystemErrorReporting 1001 : un écran bleu a été enregistré (code d'arrêt). Au démarrage suivant.</summary>
    BugCheck,

    /// <summary>EventLog 6008 : l'arrêt précédent était inattendu. Au démarrage suivant.</summary>
    UnexpectedShutdown,

    /// <summary>Démarrage de Windows (Kernel-General 12, EventLog 6005).</summary>
    BootStarted,

    /// <summary>Arrêt propre de Windows (Kernel-General 13, EventLog 6006).</summary>
    CleanShutdown,

    /// <summary>WHEA-Logger 17, 18, 19 ou 47 : erreur matérielle corrigée (17, 19, 47) ou fatale (18, au démarrage
    /// suivant).</summary>
    HardwareError,

    /// <summary>Display 4101 : le pilote graphique a cessé de répondre et a été relancé (TDR).</summary>
    DisplayDriverReset,

    /// <summary>disk 7, 51 ou 153 : bloc défectueux, erreur de pagination, nouvel essai d'une E/S.</summary>
    DiskError,

    /// <summary>Kernel-Processor-Power 37 : la vitesse du processeur est limitée par le micrologiciel.</summary>
    FirmwareLimited,
}

/// <summary>
/// Un événement du journal Système, réduit à ce qui sert au diagnostic. Jamais le texte du message : il peut contenir
/// des noms d'applications. <paramref name="Detail"/> ne porte qu'une donnée technique choisie (code d'arrêt, nom du
/// pilote graphique, numéro de disque).
/// </summary>
public sealed record SystemEventRecord(SystemEventKind Kind, int EventId, DateTimeOffset TimeUtc)
{
    /// <summary>Kernel-Power 41 : code d'arrêt (0 si aucun écran bleu n'a été enregistré).</summary>
    public ulong? BugcheckCode { get; init; }

    /// <summary>Kernel-Power 41 : différent de 0 si le bouton d'alimentation a été maintenu.</summary>
    public ulong? PowerButtonTimestamp { get; init; }

    public string? Detail { get; init; }

    /// <summary>Événement journalisé au démarrage qui suit l'incident (et non au moment où il se produit).</summary>
    public bool IsPostMortem => Kind is SystemEventKind.KernelPower41 or SystemEventKind.BugCheck or SystemEventKind.UnexpectedShutdown
        || (Kind == SystemEventKind.HardwareError && EventId == 18);
}

/// <summary>Résultat d'une lecture du journal Système : les événements, ou null et la raison. <paramref name="Truncated"/> :
/// la lecture s'est arrêtée au plafond d'événements.</summary>
public sealed record SystemEventReadResult(IReadOnlyList<SystemEventRecord>? Events, Unavailable? Problem, bool Truncated = false)
{
    public static SystemEventReadResult Failed(UnavailableCause cause, string reason) => new(null, new Unavailable(cause, reason));
}
