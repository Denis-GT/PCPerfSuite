using System.Globalization;

namespace PCPerfSuite.Core.Safety.Events;

/// <summary>Un incident lu dans le journal Système.</summary>
public enum IncidentKind
{
    /// <summary>Arrêt brutal : Kernel-Power 41 sans code d'arrêt ni bouton maintenu. Coupure de courant, alimentation
    /// qui décroche, PC qui s'éteint net (Microsoft cite une alimentation défaillante parmi les causes).</summary>
    PowerLoss,

    /// <summary>Arrêt forcé : Kernel-Power 41 avec le bouton d'alimentation maintenu.</summary>
    ForcedShutdown,

    /// <summary>Écran bleu : Kernel-Power 41 avec un code d'arrêt, ou WER 1001.</summary>
    BlueScreen,

    /// <summary>Arrêt inattendu : EventLog 6008 seul, sans Kernel-Power 41.</summary>
    UnexpectedShutdown,

    /// <summary>TDR : le pilote graphique a cessé de répondre (Display 4101).</summary>
    DisplayDriverReset,

    /// <summary>Erreur matérielle signalée par WHEA (corrigée ou fatale).</summary>
    HardwareError,

    /// <summary>Erreur de disque (bloc défectueux, pagination, E/S reprise).</summary>
    DiskError,
}

/// <summary>Un incident : son type, l'heure où Windows l'a journalisé, et la preuve (événements et codes, jamais de
/// texte de message). Pour un arrêt, l'heure est celle du démarrage suivant : la coupure a eu lieu avant.</summary>
public sealed record Incident(IncidentKind Kind, DateTimeOffset LoggedUtc, string Evidence);

/// <summary>Ce qui est arrivé pendant une opération du journal de session restée en cours.</summary>
public enum IncidentQualificationKind
{
    /// <summary>L'app s'est arrêtée (tuée, plantée), Windows non : pas de redémarrage depuis l'opération.</summary>
    Interrupted,

    /// <summary>Windows a été arrêté ou redémarré normalement pendant l'opération.</summary>
    CleanShutdown,

    /// <summary>Redémarrage, mais aucun événement ne dit comment (journaux vidés ou illisibles).</summary>
    Unknown,

    /// <summary>Arrêt inattendu (6008 seul).</summary>
    UnexpectedShutdown,

    /// <summary>Arrêt forcé par le bouton d'alimentation.</summary>
    ForcedShutdown,

    /// <summary>Arrêt brutal (41 sans code ni bouton).</summary>
    PowerLoss,

    /// <summary>Écran bleu.</summary>
    BlueScreen,
}

/// <summary>Qualification d'une opération interrompue : le type (le plus grave l'emporte), les incidents retenus (arrêt
/// et incidents survenus pendant l'opération, TDR compris), le texte affiché et, pour un arrêt, l'heure du démarrage
/// suivant, qui borne l'heure de l'arrêt par au-dessus.</summary>
public sealed record IncidentQualification(
    IncidentQualificationKind Kind,
    IReadOnlyList<Incident> Incidents,
    string Summary,
    DateTimeOffset? RestartedUtc = null)
{
    public bool HasIncident(IncidentKind kind) => Incidents.Any(incident => incident.Kind == kind);
}

/// <summary>
/// Classement pur des événements du journal Système, sans lecture ni horloge : testable, reproductible.
///
/// Windows ne journalise pas un arrêt au moment où il se produit, mais au démarrage suivant (Kernel-Power 41, 6008,
/// 1001, WHEA 18). Pour une opération commencée à T, soit B1 le premier démarrage après T et B2 le suivant : les
/// événements « en direct » (TDR, WHEA corrigées, disque) comptent dans [T, B1), les événements d'après coup dans
/// [B1, B2). Ainsi, une coupure survenue plus tard, sans rapport avec l'opération, ne lui est pas imputée.
/// </summary>
public static class IncidentClassifier
{
    /// <summary>Écart toléré entre deux heures de démarrage (réglage de l'horloge) pour les dire identiques.</summary>
    public static readonly TimeSpan SameBootTolerance = TimeSpan.FromMinutes(2);

    /// <summary>41, 6008 et 1001 d'un même démarrage sont journalisés à quelques secondes d'écart.</summary>
    private static readonly TimeSpan SameShutdownWindow = TimeSpan.FromMinutes(10);

    /// <summary>Marge avant un marqueur de démarrage : un événement d'après coup peut être horodaté juste avant lui.</summary>
    private static readonly TimeSpan BootMargin = TimeSpan.FromMinutes(1);

    /// <summary>Tous les incidents d'une liste d'événements (diagnostic des 30 derniers jours) : les 41, 6008 et 1001 d'un
    /// même arrêt n'en font qu'un.</summary>
    public static IReadOnlyList<Incident> ClassifyAll(IEnumerable<SystemEventRecord> events)
    {
        var sorted = events.OrderBy(e => e.TimeUtc).ToList();
        var incidents = new List<Incident>();

        var shutdown = new List<SystemEventRecord>();
        foreach (SystemEventRecord record in sorted.Where(IsShutdownEvidence))
        {
            if (shutdown.Count > 0 && record.TimeUtc - shutdown[0].TimeUtc > SameShutdownWindow)
            {
                incidents.Add(ShutdownIncident(shutdown));
                shutdown.Clear();
            }
            shutdown.Add(record);
        }
        if (shutdown.Count > 0) incidents.Add(ShutdownIncident(shutdown));

        foreach (SystemEventRecord record in sorted)
        {
            switch (record.Kind)
            {
                case SystemEventKind.DisplayDriverReset:
                    incidents.Add(new Incident(IncidentKind.DisplayDriverReset, record.TimeUtc,
                        $"Display 4101{(record.Detail is { } driver ? $" ({driver})" : "")}"));
                    break;
                case SystemEventKind.HardwareError:
                    incidents.Add(new Incident(IncidentKind.HardwareError, record.TimeUtc, $"WHEA-Logger {record.EventId} ({WheaLabel(record.EventId)})"));
                    break;
                case SystemEventKind.DiskError:
                    incidents.Add(new Incident(IncidentKind.DiskError, record.TimeUtc,
                        $"disk {record.EventId}{(record.Detail is { } disk ? $" ({disk})" : "")}"));
                    break;
            }
        }

        return incidents.OrderBy(i => i.LoggedUtc).ToList();
    }

    /// <summary>
    /// Qualifie une opération restée en cours. <paramref name="entryBootUtc"/> est le démarrage de Windows noté avec
    /// l'opération, <paramref name="currentBootUtc"/> celui d'aujourd'hui. <paramref name="events"/> : le journal Système
    /// depuis <paramref name="entryTimeUtc"/>, ou null s'il n'a pas pu être lu.
    /// </summary>
    public static IncidentQualification Qualify(DateTimeOffset entryTimeUtc, DateTimeOffset? entryBootUtc,
        DateTimeOffset currentBootUtc, IReadOnlyList<SystemEventRecord>? events, DateTimeOffset nowUtc)
    {
        // Sans démarrage noté (ligne d'ouverture perdue), Windows n'a pas redémarré si son démarrage précède l'opération.
        bool sameBoot = entryBootUtc is { } boot
            ? (boot - currentBootUtc).Duration() <= SameBootTolerance
            : currentBootUtc <= entryTimeUtc;

        if (sameBoot)
        {
            // Windows ne s'est pas arrêté : seule l'app l'a été. Un TDR ou une erreur WHEA pendant l'opération reste utile.
            IReadOnlyList<Incident> live = events is null ? [] : LiveIncidents(events, entryTimeUtc, nowUtc);
            return new IncidentQualification(IncidentQualificationKind.Interrupted, live,
                WithLive("interrompu (l'app s'est arrêtée, pas Windows)", live));
        }

        if (events is null)
        {
            return new IncidentQualification(IncidentQualificationKind.Unknown, [],
                "Windows a redémarré depuis ; cause inconnue (journal Système illisible)");
        }

        // Premier démarrage après l'opération : d'après le journal, sinon celui d'aujourd'hui s'il est bien postérieur.
        DateTimeOffset? firstBoot = events
            .Where(e => e.Kind == SystemEventKind.BootStarted && e.TimeUtc > entryTimeUtc)
            .Select(e => (DateTimeOffset?)e.TimeUtc)
            .Min();
        if (firstBoot is null && currentBootUtc > entryTimeUtc) firstBoot = currentBootUtc;
        if (firstBoot is not { } b1)
        {
            return new IncidentQualification(IncidentQualificationKind.Unknown, [],
                "Windows a redémarré depuis ; démarrage introuvable dans le journal Système");
        }

        DateTimeOffset b2 = events
            .Where(e => e.Kind == SystemEventKind.BootStarted && e.TimeUtc > b1 + BootMargin)
            .Select(e => e.TimeUtc)
            .DefaultIfEmpty(nowUtc)
            .Min();

        var incidents = new List<Incident>(LiveIncidents(events, entryTimeUtc, b1));

        List<SystemEventRecord> afterRestart = events
            .Where(e => e.IsPostMortem && e.TimeUtc >= b1 - BootMargin && e.TimeUtc < b2)
            .ToList();
        List<SystemEventRecord> shutdownEvidence = afterRestart.Where(IsShutdownEvidence).ToList();
        Incident? shutdown = shutdownEvidence.Count > 0 ? ShutdownIncident(shutdownEvidence) : null;
        if (shutdown is not null) incidents.Add(shutdown);

        foreach (SystemEventRecord fatal in afterRestart.Where(e => e.Kind == SystemEventKind.HardwareError))
        {
            incidents.Add(new Incident(IncidentKind.HardwareError, fatal.TimeUtc, $"WHEA-Logger {fatal.EventId} ({WheaLabel(fatal.EventId)})"));
        }

        incidents.Sort((x, y) => x.LoggedUtc.CompareTo(y.LoggedUtc));

        IncidentQualificationKind kind = shutdown?.Kind switch
        {
            IncidentKind.BlueScreen => IncidentQualificationKind.BlueScreen,
            IncidentKind.PowerLoss => IncidentQualificationKind.PowerLoss,
            IncidentKind.ForcedShutdown => IncidentQualificationKind.ForcedShutdown,
            IncidentKind.UnexpectedShutdown => IncidentQualificationKind.UnexpectedShutdown,
            _ => events.Any(e => e.Kind == SystemEventKind.CleanShutdown && e.TimeUtc >= entryTimeUtc && e.TimeUtc < b1)
                ? IncidentQualificationKind.CleanShutdown
                : IncidentQualificationKind.Unknown,
        };

        return new IncidentQualification(kind, incidents, WithLive(Describe(kind, shutdown), incidents.Where(i => i != shutdown).ToList()), b1);
    }

    /// <summary>Libellé court d'un incident, pour le diagnostic et les rapports.</summary>
    public static string Label(IncidentKind kind) => kind switch
    {
        IncidentKind.PowerLoss => "arrêt brutal",
        IncidentKind.ForcedShutdown => "arrêt forcé (bouton maintenu)",
        IncidentKind.BlueScreen => "écran bleu",
        IncidentKind.UnexpectedShutdown => "arrêt inattendu",
        IncidentKind.DisplayDriverReset => "pilote graphique relancé (TDR)",
        IncidentKind.HardwareError => "erreur matérielle (WHEA)",
        IncidentKind.DiskError => "erreur de disque",
        _ => kind.ToString(),
    };

    public static string Label(IncidentQualificationKind kind) => kind switch
    {
        IncidentQualificationKind.Interrupted => "interrompu",
        IncidentQualificationKind.CleanShutdown => "arrêt normal de Windows",
        IncidentQualificationKind.Unknown => "redémarrage de cause inconnue",
        IncidentQualificationKind.UnexpectedShutdown => "arrêt inattendu",
        IncidentQualificationKind.ForcedShutdown => "arrêt forcé",
        IncidentQualificationKind.PowerLoss => "arrêt brutal",
        IncidentQualificationKind.BlueScreen => "écran bleu",
        _ => kind.ToString(),
    };

    private static bool IsShutdownEvidence(SystemEventRecord record)
        => record.Kind is SystemEventKind.KernelPower41 or SystemEventKind.BugCheck or SystemEventKind.UnexpectedShutdown;

    private static IReadOnlyList<Incident> LiveIncidents(IEnumerable<SystemEventRecord> events, DateTimeOffset from, DateTimeOffset to)
        => ClassifyAll(events.Where(e => !e.IsPostMortem && e.TimeUtc >= from && e.TimeUtc < to))
            .Where(i => i.Kind is IncidentKind.DisplayDriverReset or IncidentKind.HardwareError or IncidentKind.DiskError)
            .ToList();

    /// <summary>Un arrêt d'après ses événements (même démarrage) : écran bleu dès qu'un code d'arrêt ou un 1001 existe,
    /// puis arrêt forcé, puis arrêt brutal, et arrêt inattendu pour un 6008 seul.</summary>
    private static Incident ShutdownIncident(IReadOnlyList<SystemEventRecord> records)
    {
        SystemEventRecord first = records[0];
        SystemEventRecord? kernel41 = records.FirstOrDefault(r => r.Kind == SystemEventKind.KernelPower41);
        SystemEventRecord? bugCheck = records.FirstOrDefault(r => r.Kind == SystemEventKind.BugCheck);

        if ((kernel41?.BugcheckCode is { } code && code != 0) || bugCheck is not null)
        {
            string? shown = bugCheck?.Detail
                ?? (kernel41?.BugcheckCode is { } raw && raw != 0 ? $"0x{raw.ToString("X8", CultureInfo.InvariantCulture)}" : null);
            string source = kernel41 is not null && bugCheck is not null ? "Kernel-Power 41, WER 1001"
                : kernel41 is not null ? "Kernel-Power 41" : "WER 1001";
            return new Incident(IncidentKind.BlueScreen, first.TimeUtc, $"{source}{(shown is null ? "" : $", code {shown}")}");
        }

        if (kernel41 is not null)
        {
            return kernel41.PowerButtonTimestamp is { } pressed && pressed != 0
                ? new Incident(IncidentKind.ForcedShutdown, first.TimeUtc, "Kernel-Power 41, bouton d'alimentation maintenu")
                : new Incident(IncidentKind.PowerLoss, first.TimeUtc, "Kernel-Power 41, sans code d'arrêt ni bouton");
        }

        return new Incident(IncidentKind.UnexpectedShutdown, first.TimeUtc, "EventLog 6008");
    }

    private static string Describe(IncidentQualificationKind kind, Incident? shutdown)
    {
        string label = Label(kind);
        return shutdown is null ? label : $"{label} ({shutdown.Evidence})";
    }

    private static string WithLive(string summary, IReadOnlyList<Incident> live)
    {
        if (live.Count == 0) return summary;
        string others = string.Join(", ", live.GroupBy(i => i.Kind).Select(g => g.Count() > 1 ? $"{Label(g.Key)} ×{g.Count()}" : Label(g.Key)));
        return $"{summary} ; pendant l'opération : {others}";
    }

    private static string WheaLabel(int eventId) => eventId switch
    {
        17 => "corrigée",
        18 => "fatale",
        19 => "corrigée, processeur",
        47 => "corrigée, mémoire",
        _ => "matérielle",
    };
}
