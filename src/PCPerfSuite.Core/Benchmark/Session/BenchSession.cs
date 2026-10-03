using PCPerfSuite.Core.Benchmark.Protocol;
using PCPerfSuite.Core.Benchmark.Worker;
using PCPerfSuite.Core.Hardware;
using PCPerfSuite.Core.Profiles;
using PCPerfSuite.Core.Safety;

namespace PCPerfSuite.Core.Benchmark.Session;

/// <summary>Le worker vu par la session : ce que <see cref="BenchWorkerSession"/> offre, derrière une interface pour les tests.</summary>
public interface IBenchWorker : IDisposable
{
    bool IsAlive { get; }

    IReadOnlyList<string> WorkerNotes { get; }

    event Action<BenchProgress>? ProgressReported;

    Task<BenchJobResult> RunJobAsync(BenchJobRequest request, CancellationToken cancel);

    void Kill();
}

/// <summary>Un test à passer : son type, la demande pour le worker, et les valeurs de sa ligne au journal de session
/// (nombres ou mots seulement : la lettre du volume, jamais un chemin).</summary>
public sealed record BenchTestPlan(BenchTestKind Kind, BenchJobRequest Request, IReadOnlyDictionary<string, string> JournalValues);

/// <summary>Ce qu'une session va passer, dans l'ordre, avec sa politique thermique.</summary>
public sealed class BenchSessionPlan
{
    public required IReadOnlyList<BenchTestPlan> Tests { get; init; }

    public required BenchThermalLimits Thermal { get; init; }

    /// <summary>Retour au repos entre deux tests (faux pour les tests automatisés).</summary>
    public bool IdleReturnBetweenTests { get; init; } = true;
}

/// <summary>Les ports de la session vers le reste de l'app : tout est injectable, rien n'est obligatoire sauf le worker,
/// le bail et le journal.</summary>
public sealed class BenchSessionPorts
{
    public required Func<Action<string>?, CancellationToken, Task<IBenchWorker>> StartWorker { get; init; }

    public required TuningLease Lease { get; init; }

    public required SessionJournal Journal { get; init; }

    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>Abonnement aux relevés de capteurs ; null quand il n'y en a pas (tests).</summary>
    public Func<Action<HardwareSnapshot>, IDisposable>? SubscribeSnapshots { get; init; }

    /// <summary>Bail de cadence du relevé pendant la session (<c>HardwareMonitorService.RequestCadence</c>).</summary>
    public Func<IDisposable>? RequestCadence { get; init; }

    /// <summary>Priorité de l'app relevée pendant un test (<see cref="ProcessPriorityScope.Raise"/>).</summary>
    public Func<IDisposable>? RaisePriority { get; init; }

    /// <summary>Dernier relevé connu avant l'abonnement (<c>HardwareMonitorService.LastSnapshot</c>), pour la température
    /// de départ.</summary>
    public Func<HardwareSnapshot?>? LastSnapshot { get; init; }
}

/// <summary>Avancement d'une session, pour la page.</summary>
public sealed record BenchSessionProgress(int TestIndex, int TestCount, BenchTestKind? Kind, string Phase, double Percent, string? Detail, double? Value, string? Unit);

/// <summary>Issue d'un test dans la session.</summary>
public sealed record BenchTestOutcome(
    BenchTestKind Kind,
    BenchJobRequest Request,
    BenchJobResult Result,
    DateTimeOffset StartedUtc,
    DateTimeOffset EndedUtc,
    IReadOnlyList<SensorSeries> Series,
    RecordingCadence Cadence,
    ThrottleTally Throttle,
    float? MaxCpuTempC,
    BenchStopReason? StopReason,
    string? StopDetail,
    bool JournalDurable,
    string? IdleReturnNote);

/// <summary>Issue de la session.</summary>
public sealed record BenchSessionOutcome(
    DateTimeOffset StartedUtc,
    DateTimeOffset EndedUtc,
    IReadOnlyList<BenchTestOutcome> Tests,
    string? LeaseRefusal,
    IReadOnlyList<string> WorkerNotes,
    IReadOnlyList<string> Log,
    BenchStopReason? StoppedBy,
    bool Cancelled)
{
    public bool Started => LeaseRefusal is null;
}

/// <summary>
/// Orchestrateur d'une session de bench, côté app : prend le bail de réglage de #8 pour toute la session (ni groupe ni
/// bascule ne touche aux réglages pendant une mesure), le bail de cadence de #4, s'abonne aux capteurs, lance le worker,
/// puis pour chaque test : une ligne du journal de session (#4) ouverte avant et close après, la priorité de l'app
/// relevée, l'enregistrement des capteurs, la sécurité qui peut arrêter le test (annulation puis worker tué), et le
/// retour au repos avant le suivant. Logique d'orchestration testable : tout le matériel passe par
/// <see cref="BenchSessionPorts"/>.
/// </summary>
public sealed class BenchSession
{
    public const string LeaseLabel = "le bench";
    public const string LeaseReason = "mesure en cours";
    public const string JournalUnavailableError = "journal de session non écrit : le test ne commence pas";
    private static readonly TimeSpan SafetyTick = TimeSpan.FromMilliseconds(500);

    private readonly BenchSessionPorts _ports;

    public BenchSession(BenchSessionPorts ports) => _ports = ports;

    public async Task<BenchSessionOutcome> RunAsync(BenchSessionPlan plan, Action<BenchSessionProgress>? progress, CancellationToken cancel)
    {
        TimeProvider time = _ports.Time;
        DateTimeOffset started = time.GetUtcNow();
        var log = new List<string>();
        var outcomes = new List<BenchTestOutcome>();
        BenchStopReason? stoppedBy = null;
        bool cancelled = false;

        TuningLeaseResult lease = _ports.Lease.TryAcquire(BenchVersion.Requester, LeaseLabel, LeaseReason);
        if (!lease.Acquired)
        {
            string refusal = _ports.Lease.RefusalText(null) ?? "réglages tenus par une autre fonction";
            return new BenchSessionOutcome(started, time.GetUtcNow(), outcomes, refusal, [], log, null, false);
        }

        using TuningLeaseHandle leaseHandle = lease.Handle!;
        using IDisposable? cadence = TryPort(_ports.RequestCadence, "bail de cadence", log);

        var monitor = new BenchSafetyMonitor(plan.Thermal);
        var latest = new LatestSnapshot();
        using IDisposable? subscription = _ports.SubscribeSnapshots is { } subscribe
            ? TryPort(() => subscribe(latest.Set), "abonnement aux capteurs", log)
            : null;

        IBenchWorker? worker = null;
        IReadOnlyList<string> workerNotes = [];
        try
        {
            try
            {
                worker = await _ports.StartWorker(message => log.Add($"worker : {message}"), cancel).ConfigureAwait(false);
                workerNotes = worker.WorkerNotes;
            }
            catch (OperationCanceledException)
            {
                return new BenchSessionOutcome(started, time.GetUtcNow(), outcomes, null, workerNotes, log, null, true);
            }
            catch (Exception ex)
            {
                log.Add($"lancement du worker : {ex.Message}");
                foreach (BenchTestPlan test in plan.Tests)
                {
                    outcomes.Add(NotRun(test, time.GetUtcNow(), $"worker de charge non lancé : {ex.Message}"));
                }
                return new BenchSessionOutcome(started, time.GetUtcNow(), outcomes, null, workerNotes, log, null, false);
            }

            HardwareSnapshot? first = latest.Get() ?? TryRead(_ports.LastSnapshot, "dernier relevé", log);
            float? baselineC = first is null ? null : BenchSafetySample.From(first, time.GetUtcNow()).CpuTempC;
            log.Add(baselineC is { } b ? $"température de départ : {b:0} °C" : "température de départ non lue");

            for (int index = 0; index < plan.Tests.Count; index++)
            {
                BenchTestPlan test = plan.Tests[index];
                if (cancel.IsCancellationRequested)
                {
                    cancelled = true;
                    break;
                }

                string? idleNote = null;
                if (index > 0 && plan.IdleReturnBetweenTests)
                {
                    idleNote = await WaitIdleAsync(baselineC, latest, monitor, index, plan.Tests.Count, progress, cancel).ConfigureAwait(false);
                    if (cancel.IsCancellationRequested)
                    {
                        cancelled = true;
                        break;
                    }
                }

                if (!worker.IsAlive)
                {
                    log.Add("worker mort : relance");
                    try
                    {
                        worker.Dispose();
                        worker = await _ports.StartWorker(message => log.Add($"worker : {message}"), cancel).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // Arrêter pendant la relance : la session s'arrête, et les tests déjà passés restent à enregistrer.
                        cancelled = true;
                        break;
                    }
                    catch (Exception ex)
                    {
                        outcomes.Add(NotRun(test, time.GetUtcNow(), $"worker de charge non relancé : {ex.Message}"));
                        continue;
                    }
                }

                BenchTestOutcome outcome = await RunTestAsync(test, index, plan.Tests.Count, worker, monitor, latest, progress, log, cancel).ConfigureAwait(false);
                outcome = outcome with { IdleReturnNote = idleNote };
                outcomes.Add(outcome);

                if (outcome.StopReason is { } reason)
                {
                    stoppedBy = reason;
                    log.Add($"session arrêtée : {BenchSafetyMonitor.Label(reason)} ({outcome.StopDetail})");
                    foreach (BenchTestPlan remaining in plan.Tests.Skip(index + 1))
                    {
                        outcomes.Add(NotRun(remaining, time.GetUtcNow(), $"non passé : {BenchSafetyMonitor.Label(reason)}"));
                    }
                    break;
                }
                if (cancel.IsCancellationRequested)
                {
                    cancelled = true;
                    break;
                }
            }
        }
        finally
        {
            worker?.Dispose();
        }

        return new BenchSessionOutcome(started, time.GetUtcNow(), outcomes, null, workerNotes, log, stoppedBy, cancelled || cancel.IsCancellationRequested);
    }

    private async Task<BenchTestOutcome> RunTestAsync(BenchTestPlan test, int index, int count, IBenchWorker worker, BenchSafetyMonitor monitor,
        LatestSnapshot latest, Action<BenchSessionProgress>? progress, List<string> log, CancellationToken cancel)
    {
        TimeProvider time = _ports.Time;
        DateTimeOffset startedUtc = time.GetUtcNow();
        string actionKey = BenchTestKinds.Key(test.Kind);

        using SessionOperation operation = _ports.Journal.Begin(BenchVersion.Requester, actionKey, test.JournalValues);
        if (!operation.IsDurable)
        {
            log.Add($"{actionKey} : {JournalUnavailableError}");
            return NotRun(test, startedUtc, JournalUnavailableError) with { JournalDurable = false };
        }

        // Chaque relevé reçu est enregistré à son arrivée (250 ms), pas seulement ceux que voit la veille de sécurité
        // (500 ms) : sinon la cadence obtenue, que le résultat rapporte, serait mesurée à la moitié.
        var recording = new SensorRecording(startedUtc.UtcDateTime);
        latest.Record(recording);
        monitor.Reset();
        BenchStopReason? stopReason = null;
        string? stopDetail = null;
        using var testCancel = CancellationTokenSource.CreateLinkedTokenSource(cancel);

        void OnProgress(BenchProgress p) => progress?.Invoke(new BenchSessionProgress(index, count, test.Kind, p.Phase, p.Percent, p.Detail, p.Value, p.Unit));
        worker.ProgressReported += OnProgress;
        using IDisposable? priority = TryPort(_ports.RaisePriority, "priorité de l'app", log);

        // Sécurité : à chaque nouveau relevé, et à défaut toutes les 500 ms (perte de relevé).
        var safetyStop = new CancellationTokenSource();
        Task safety = Task.Run(async () =>
        {
            DateTime? lastSeen = null;
            while (!safetyStop.IsCancellationRequested)
            {
                HardwareSnapshot? snapshot = latest.Get();
                BenchSafetyVerdict verdict;
                if (snapshot is not null && snapshot.CapturedAtUtc != lastSeen)
                {
                    lastSeen = snapshot.CapturedAtUtc;
                    verdict = monitor.Note(BenchSafetySample.From(snapshot, CapturedAt(snapshot)), underLoad: true);
                }
                else
                {
                    verdict = monitor.NoteNoReading(time.GetUtcNow());
                }

                if (verdict.Stop is { } reason)
                {
                    stopReason = reason;
                    stopDetail = verdict.Detail;
                    testCancel.Cancel();
                    return;
                }
                try { await Task.Delay(SafetyTick, safetyStop.Token).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
            }
        }, CancellationToken.None);

        BenchJobResult result;
        try
        {
            result = await worker.RunJobAsync(test.Request, testCancel.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            result = BenchJobResult.Failure(test.Request.Id, test.Request.Kind, $"{ex.GetType().Name} : {ex.Message}");
        }
        finally
        {
            latest.Record(null);
            worker.ProgressReported -= OnProgress;
            safetyStop.Cancel();
            try { await safety.ConfigureAwait(false); } catch (Exception) { /* la veille n'intéresse plus */ }
            safetyStop.Dispose();
        }

        if (stopReason is { } stopped)
        {
            if (worker.IsAlive && result.Succeeded == false && result.Error != BenchJobDispatcher.CancelledError)
            {
                // Le worker n'a pas rendu la main sur l'arrêt : on ne prend aucun risque.
                worker.Kill();
            }
            result.Succeeded = false;
            result.Error = $"{BenchSafetyMonitor.Label(stopped)} : {stopDetail}";
            operation.Fail(result.Error);
        }
        else if (result.Succeeded)
        {
            operation.Complete();
        }
        else
        {
            operation.Fail(result.Error ?? "échec");
        }

        DateTimeOffset endedUtc = time.GetUtcNow();
        lock (recording)
        {
            return new BenchTestOutcome(test.Kind, test.Request, result, startedUtc, endedUtc, recording.Series, recording.Cadence(endedUtc.UtcDateTime),
                recording.Throttle, monitor.MaxCpuTempC, stopReason, stopDetail, true, null);
        }
    }

    private async Task<string> WaitIdleAsync(float? baselineC, LatestSnapshot latest, BenchSafetyMonitor monitor, int index, int count,
        Action<BenchSessionProgress>? progress, CancellationToken cancel)
    {
        TimeProvider time = _ports.Time;
        var idle = new IdleReturn(baselineC, time.GetUtcNow());
        while (true)
        {
            HardwareSnapshot? snapshot = latest.Get();
            float? current = snapshot is null ? null : BenchSafetySample.From(snapshot, time.GetUtcNow()).CpuTempC;
            if (snapshot is not null) monitor.Note(BenchSafetySample.From(snapshot, CapturedAt(snapshot)), underLoad: false);
            IdleReturnState state = idle.Check(time.GetUtcNow(), current);
            progress?.Invoke(new BenchSessionProgress(index, count, null, "retour au repos", 0, state.Reason, current, current is null ? null : "°C"));
            if (state.Done) return state.Reason;
            try { await Task.Delay(SafetyTick, cancel).ConfigureAwait(false); } catch (OperationCanceledException) { return "interrompu"; }
        }
    }

    /// <summary>L'heure d'un relevé est celle de sa capture (même horloge que la session en production) : un relevé en
    /// retard compte pour le moment où il a été pris, et deux relevés chauds espacés du délai déclenchent quel que soit
    /// le moment où la veille les voit.</summary>
    private static DateTimeOffset CapturedAt(HardwareSnapshot snapshot) => new(DateTime.SpecifyKind(snapshot.CapturedAtUtc, DateTimeKind.Utc));

    private static BenchTestOutcome NotRun(BenchTestPlan test, DateTimeOffset at, string error)
        => new(test.Kind, test.Request, BenchJobResult.Failure(test.Request.Id, test.Request.Kind, error), at, at, [],
            new RecordingCadence(0, 0, 0, null, null, 0), new ThrottleTally(0, 0, 0, 0, 0), null, null, null, true, null);

    private static T? TryRead<T>(Func<T?>? port, string what, List<string> log) where T : class
    {
        if (port is null) return null;
        try
        {
            return port();
        }
        catch (Exception ex)
        {
            log.Add($"{what} : {ex.Message}");
            return null;
        }
    }

    private static IDisposable? TryPort(Func<IDisposable>? port, string what, List<string> log)
    {
        if (port is null) return null;
        try
        {
            return port();
        }
        catch (Exception ex)
        {
            log.Add($"{what} : {ex.Message}");
            return null;
        }
    }

    /// <summary>Le dernier relevé reçu, lu par la veille de sécurité ; pendant un test, chaque relevé reçu est aussi
    /// ajouté à son enregistrement.</summary>
    private sealed class LatestSnapshot
    {
        private HardwareSnapshot? _snapshot;
        private SensorRecording? _recording;

        public void Set(HardwareSnapshot snapshot)
        {
            Volatile.Write(ref _snapshot, snapshot);
            if (Volatile.Read(ref _recording) is { } recording)
            {
                lock (recording) recording.Add(snapshot);
            }
        }

        public HardwareSnapshot? Get() => Volatile.Read(ref _snapshot);

        /// <summary>L'enregistrement du test en cours, null entre deux tests.</summary>
        public void Record(SensorRecording? recording) => Volatile.Write(ref _recording, recording);
    }
}
