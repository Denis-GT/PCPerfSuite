using System.Diagnostics;
using PCPerfSuite.Core.Compatibility;
using PCPerfSuite.Core.Safety.Events;

namespace PCPerfSuite.Core.Safety;

/// <summary>
/// Étapes de la reprise au lancement, dans leur ordre fixe : on rend d'abord au matériel ses réglages d'origine, puis
/// seulement on décide de ce qui se relance. Un gestionnaire s'inscrit à l'étape de sa fonction ; à étape égale, dans
/// l'ordre d'inscription.
/// </summary>
public enum RecoveryStage
{
    /// <summary>Retour d'origine de l'OC (recherche d'OC #15, vérification matérielle #14), toujours en premier.</summary>
    OverclockRestore,

    /// <summary>Essai d'une fréquence d'écran (#17).</summary>
    DisplayTrial,

    /// <summary>Test combiné CPU + GPU (#11).</summary>
    CombinedTest,

    /// <summary>Bench CPU, RAM, disque (#10).</summary>
    Bench,

    /// <summary>Groupes de profils (#8).</summary>
    ProfileGroups,

    /// <summary>Bascule automatique de profils (#9).</summary>
    AutoSwitch,

    /// <summary>Le reste (limites par processus de #21…), en dernier.</summary>
    Other,
}

/// <summary>Une opération restée en cours, avec ce qui lui est arrivé.</summary>
public sealed record RecoveredEntry(SessionJournalEntry Entry, IncidentQualification Qualification);

/// <summary>
/// Gestionnaire de reprise d'une fonction : appelé au lancement, avant toute interface et avant que les pages ne
/// réappliquent leurs réglages, avec les opérations restées en cours des composants qu'il déclare. Il ne montre aucune
/// fenêtre (il tourne avant l'interface) : il agit, puis renvoie une note courte pour le diagnostic, sans nom
/// d'application ni de fichier. Il peut lever : l'erreur est journalisée et n'empêche ni les autres ni le lancement.
/// </summary>
public interface IStartupRecoveryHandler
{
    /// <summary>Nom stable, pour le journal des erreurs et le diagnostic (« oc-gpu-auto »).</summary>
    string Id { get; }

    RecoveryStage Stage { get; }

    /// <summary>Composants du journal de session dont il se charge (« test-combine »).</summary>
    IReadOnlyCollection<string> Components { get; }

    /// <summary>Appelé seulement s'il reste au moins une opération en cours pour l'un de ses composants.</summary>
    string? Handle(IReadOnlyList<RecoveredEntry> entries);
}

/// <summary>Ce qu'a fait un gestionnaire.</summary>
public sealed record RecoveryHandlerOutcome(string HandlerId, RecoveryStage Stage, int EntryCount, bool Succeeded, string? Note);

/// <summary>Bilan de la reprise au lancement, gardé pour le diagnostic et pour les pages qui doivent s'en souvenir
/// (ne pas réappliquer un OC après un arrêt brutal, par exemple).</summary>
public sealed record StartupRecoveryReport(
    IReadOnlyList<RecoveredEntry> Recovered,
    IReadOnlyList<SessionJournalEntry> SkippedLive,
    IReadOnlyList<RecoveryHandlerOutcome> Handlers,
    int IgnoredLines,
    string? JournalProblem,
    Unavailable? EventsProblem,
    bool Compacted)
{
    public static StartupRecoveryReport Failed(string problem) => new([], [], [], 0, problem, null, false);

    public bool HasInterruptedOperations => Recovered.Count > 0;
}

/// <summary>
/// Reprise au lancement, en logique d'orchestration testable (journal, lecture des événements et horloge injectés).
/// Déroulé : lire le journal de session ; laisser de côté une opération tenue par un processus PCPerfSuite encore
/// vivant (chien de garde d'une recherche d'OC) ; s'il reste des opérations en cours, lire le journal Système depuis la
/// plus ancienne (en tâche de fond, <see cref="EventReadTimeout"/> au plus) et qualifier chacune ; appeler les
/// gestionnaires par étape ; clore chaque opération reprise par une ligne « échouée » dont la cause est sa
/// qualification ; enfin compacter le journal. Ne lève jamais : un gestionnaire qui lève est journalisé et les autres
/// continuent (règle 2).
/// </summary>
public sealed class StartupRecovery
{
    /// <summary>Plafond de la lecture du journal Système avant l'interface : au-delà, la qualification est
    /// « inconnue » plutôt que de retarder le lancement.</summary>
    public static readonly TimeSpan EventReadTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Cause ajoutée à une opération reprise dont aucun gestionnaire ne s'est chargé.</summary>
    public const string NoHandlerNote = "aucun gestionnaire";

    /// <summary>Cause ajoutée quand le gestionnaire de l'opération a levé.</summary>
    public const string FailedHandlerNote = "gestionnaire en échec";

    private readonly SessionJournal _journal;
    private readonly IReadOnlyList<IStartupRecoveryHandler> _handlers;
    private readonly Func<DateTimeOffset, CancellationToken, SystemEventReadResult> _readEventsSince;
    private readonly TimeProvider _time;
    private readonly Action<Exception, string> _log;
    private readonly Func<SessionJournalEntry, bool> _isHeldByLiveProcess;
    private readonly TimeSpan _eventReadTimeout;

    public StartupRecovery(
        SessionJournal journal,
        IReadOnlyList<IStartupRecoveryHandler> handlers,
        Func<DateTimeOffset, CancellationToken, SystemEventReadResult> readEventsSince,
        TimeProvider time,
        Action<Exception, string> log,
        Func<SessionJournalEntry, bool>? isHeldByLiveProcess = null,
        TimeSpan? eventReadTimeout = null)
    {
        _journal = journal;
        _handlers = handlers;
        _readEventsSince = readEventsSince;
        _time = time;
        _log = log;
        _isHeldByLiveProcess = isHeldByLiveProcess ?? IsHeldByLivePcPerfSuite;
        _eventReadTimeout = eventReadTimeout ?? EventReadTimeout;
    }

    /// <summary>Gestionnaires dans l'ordre d'appel : par étape, puis dans l'ordre d'inscription.</summary>
    public static IReadOnlyList<IStartupRecoveryHandler> Order(IEnumerable<IStartupRecoveryHandler> handlers)
        => handlers.Select((handler, index) => (handler, index))
            .OrderBy(pair => SafeStage(pair.handler))
            .ThenBy(pair => pair.index)
            .Select(pair => pair.handler)
            .ToList();

    public StartupRecoveryReport Run()
    {
        DateTimeOffset now = _time.GetUtcNow();
        SessionJournalContent content = _journal.Read();

        var skipped = new List<SessionJournalEntry>();
        var pending = new List<SessionJournalEntry>();
        foreach (SessionJournalEntry entry in content.Pending)
        {
            if (SafeIsHeldByLiveProcess(entry)) skipped.Add(entry);
            else pending.Add(entry);
        }

        Unavailable? eventsProblem = null;
        var recovered = new List<RecoveredEntry>();
        var outcomes = new List<RecoveryHandlerOutcome>();

        if (pending.Count > 0)
        {
            DateTimeOffset since = pending.Min(entry => entry.StartedUtc) - TimeSpan.FromMinutes(1);
            (IReadOnlyList<SystemEventRecord>? events, eventsProblem) = ReadEvents(since);
            DateTimeOffset currentBoot = SessionJournal.BootTime(now);

            foreach (SessionJournalEntry entry in pending)
            {
                recovered.Add(new RecoveredEntry(entry,
                    IncidentClassifier.Qualify(entry.StartedUtc, entry.BootUtc, currentBoot, events, now)));
            }

            var handled = new HashSet<Guid>();
            var failed = new HashSet<Guid>();
            foreach (IStartupRecoveryHandler handler in Order(_handlers))
            {
                RecoveryHandlerOutcome? outcome = RunHandler(handler, recovered, handled, failed);
                if (outcome is not null) outcomes.Add(outcome);
            }

            foreach (RecoveredEntry item in recovered)
            {
                string cause = item.Qualification.Summary;
                if (failed.Contains(item.Entry.Id)) cause = $"{cause} ; {FailedHandlerNote}";
                else if (!handled.Contains(item.Entry.Id)) cause = $"{cause} ; {NoHandlerNote}";
                _journal.Close(item.Entry.Id, item.Entry.Component, SessionEntryState.Failed, cause);
            }
        }

        // Un processus encore vivant peut écrire pendant le compactage : on attend qu'il ait fini.
        bool compacted = skipped.Count == 0 && _journal.Compact(now);

        return new StartupRecoveryReport(recovered, skipped, outcomes, content.IgnoredLines, content.Problem,
            eventsProblem, compacted);
    }

    private (IReadOnlyList<SystemEventRecord>? Events, Unavailable? Problem) ReadEvents(DateTimeOffset since)
    {
        using var cancellation = new CancellationTokenSource();
        try
        {
            Task<SystemEventReadResult> read = Task.Run(() => _readEventsSince(since, cancellation.Token));
            if (!read.Wait(_eventReadTimeout))
            {
                cancellation.Cancel();
                return (null, new Unavailable(UnavailableCause.HardwareOrDriver,
                    $"journal Système non lu en {_eventReadTimeout.TotalSeconds:0} s au lancement"));
            }

            SystemEventReadResult result = read.Result;
            return (result.Events, result.Problem);
        }
        catch (Exception ex)
        {
            _log(ex, "reprise au démarrage : lecture du journal Système");
            return (null, new Unavailable(UnavailableCause.HardwareOrDriver, "lecture du journal Système en échec"));
        }
    }

    private RecoveryHandlerOutcome? RunHandler(IStartupRecoveryHandler handler, IReadOnlyList<RecoveredEntry> recovered,
        HashSet<Guid> handled, HashSet<Guid> failed)
    {
        string id = SafeId(handler);
        RecoveryStage stage = SafeStage(handler);
        List<RecoveredEntry> mine = [];
        try
        {
            IReadOnlyCollection<string> components = handler.Components;
            mine = recovered.Where(item => components.Contains(item.Entry.Component)).ToList();
            if (mine.Count == 0) return null;

            string? note = handler.Handle(mine);
            foreach (RecoveredEntry item in mine) handled.Add(item.Entry.Id);
            return new RecoveryHandlerOutcome(id, stage, mine.Count, Succeeded: true, SessionJournalText.Cause(note));
        }
        catch (Exception ex)
        {
            _log(ex, $"reprise au démarrage : {id}");
            foreach (RecoveredEntry item in mine) failed.Add(item.Entry.Id);
            return new RecoveryHandlerOutcome(id, stage, mine.Count, Succeeded: false, $"en échec ({ex.GetType().Name})");
        }
    }

    private bool SafeIsHeldByLiveProcess(SessionJournalEntry entry)
    {
        try
        {
            return _isHeldByLiveProcess(entry);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string SafeId(IStartupRecoveryHandler handler)
    {
        try
        {
            return handler.Id;
        }
        catch (Exception)
        {
            return handler.GetType().Name;
        }
    }

    private static RecoveryStage SafeStage(IStartupRecoveryHandler handler)
    {
        try
        {
            return handler.Stage;
        }
        catch (Exception)
        {
            return RecoveryStage.Other;
        }
    }

    /// <summary>L'opération est tenue par un processus PCPerfSuite encore en vie (un autre que celui-ci, lancé avant
    /// elle) : un numéro de processus réattribué depuis ne compte pas.</summary>
    internal static bool IsHeldByLivePcPerfSuite(SessionJournalEntry entry)
    {
        if (entry.ProcessId is not { } pid || pid == Environment.ProcessId) return false;

        try
        {
            using Process process = Process.GetProcessById(pid);
            using Process current = Process.GetCurrentProcess();
            if (process.HasExited) return false;
            if (!string.Equals(process.ProcessName, current.ProcessName, StringComparison.OrdinalIgnoreCase)) return false;
            return process.StartTime.ToUniversalTime() <= entry.StartedUtc.UtcDateTime;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
