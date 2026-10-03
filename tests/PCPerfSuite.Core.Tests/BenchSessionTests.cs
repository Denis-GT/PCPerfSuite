using PCPerfSuite.Core.Benchmark;
using PCPerfSuite.Core.Benchmark.Protocol;
using PCPerfSuite.Core.Benchmark.Session;
using PCPerfSuite.Core.Benchmark.Worker;
using PCPerfSuite.Core.Hardware;
using PCPerfSuite.Core.Profiles;
using PCPerfSuite.Core.Safety;

namespace PCPerfSuite.Core.Tests;

/// <summary>L'orchestrateur de session avec un faux worker, un faux flux de capteurs et une horloge réglée à la main :
/// bail, journal, sécurité, annulation, sans matériel.</summary>
public class BenchSessionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly BenchThermalLimits Limits = new(98, "test", TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));

    /// <summary>Un worker qui rend un résultat après un délai, « arrêté » si on l'annule avant.</summary>
    private sealed class FakeWorker : IBenchWorker
    {
        private readonly TimeSpan _jobDuration;

        public FakeWorker(TimeSpan? jobDuration = null) => _jobDuration = jobDuration ?? TimeSpan.FromMilliseconds(50);

        public List<BenchJobRequest> Requests { get; } = new();

        public bool IsAlive { get; private set; } = true;

        public bool Killed { get; private set; }

        public bool Disposed { get; private set; }

        /// <summary>Le worker meurt après son test (plantage) : la session doit le relancer.</summary>
        public bool DiesAfterJob { get; init; }

        public IReadOnlyList<string> WorkerNotes { get; } = ["EcoQoS : désactivé (faux)"];

        public event Action<BenchProgress>? ProgressReported;

        public async Task<BenchJobResult> RunJobAsync(BenchJobRequest request, CancellationToken cancel)
        {
            Requests.Add(request);
            ProgressReported?.Invoke(new BenchProgress { JobId = request.Id, Phase = "faux", Percent = 50 });
            try
            {
                await Task.Delay(_jobDuration, cancel);
            }
            catch (OperationCanceledException)
            {
                return BenchJobResult.Failure(request.Id, request.Kind, BenchJobDispatcher.CancelledError);
            }
            var result = new BenchJobResult { JobId = request.Id, Kind = request.Kind, Succeeded = true };
            result.Measurements.Add(BenchMeasurement.From("entier.rafale", "Entier", "Mops/s", [300, 302, 301]));
            if (DiesAfterJob) IsAlive = false;
            return result;
        }

        public void Kill()
        {
            Killed = true;
            IsAlive = false;
        }

        public void Dispose() => Disposed = true;
    }

    /// <summary>Flux de capteurs poussé à la main.</summary>
    private sealed class FakeSensors
    {
        private Action<HardwareSnapshot>? _consumer;

        public int Subscriptions { get; private set; }

        public int Unsubscriptions { get; private set; }

        public IDisposable Subscribe(Action<HardwareSnapshot> consumer)
        {
            _consumer = consumer;
            Subscriptions++;
            return new Release(this);
        }

        public void Push(HardwareSnapshot snapshot) => _consumer?.Invoke(snapshot);

        private sealed class Release(FakeSensors owner) : IDisposable
        {
            public void Dispose() => owner.Unsubscriptions++;
        }
    }

    private sealed class Counter : IDisposable
    {
        public int Opened;
        public int Closed;

        public IDisposable Open()
        {
            Opened++;
            return this;
        }

        public void Dispose() => Closed++;
    }

    private static BenchTestPlan Test(BenchTestKind kind, Dictionary<string, string>? values = null)
        => new(kind, new BenchJobRequest { Kind = BenchTestKinds.Key(kind) }, values ?? new Dictionary<string, string> { ["threads"] = "1" });

    private static BenchSessionPlan Plan(params BenchTestPlan[] tests) => new() { Tests = tests, Thermal = Limits, IdleReturnBetweenTests = false };

    [Fact]
    public async Task Deux_tests_passent_dans_l_ordre_avec_une_ligne_du_journal_chacun_et_le_bail_tenu()
    {
        using var temp = new TempDirectory();
        var clock = new ManualClock(T0);
        var journal = new SessionJournal(temp.File("journal.jsonl"), clock);
        var lease = new TuningLease(clock);
        var worker = new FakeWorker();
        var sensors = new FakeSensors();
        var cadence = new Counter();
        var priority = new Counter();
        bool leaseHeldDuringJob = false;
        worker.ProgressReported += _ => leaseHeldDuringJob = lease.Holder?.RequesterId == BenchVersion.Requester;
        var progress = new List<BenchSessionProgress>();
        var session = new BenchSession(new BenchSessionPorts
        {
            StartWorker = (_, _) => Task.FromResult<IBenchWorker>(worker),
            Lease = lease,
            Journal = journal,
            Time = clock,
            SubscribeSnapshots = sensors.Subscribe,
            RequestCadence = cadence.Open,
            RaisePriority = priority.Open,
            LastSnapshot = () => BenchSnapshots.At(0, cpuTemp: 45),
        });

        BenchSessionOutcome outcome = await session.RunAsync(
            Plan(Test(BenchTestKind.CpuMono), Test(BenchTestKind.Disk, new Dictionary<string, string> { ["volume"] = "C", ["systeme"] = "oui", ["taille-mo"] = "1024" })),
            progress.Add, CancellationToken.None);

        Assert.True(outcome.Started);
        Assert.Null(outcome.StoppedBy);
        Assert.False(outcome.Cancelled);
        Assert.Equal([BenchTestKind.CpuMono, BenchTestKind.Disk], outcome.Tests.Select(t => t.Kind));
        Assert.All(outcome.Tests, t => Assert.True(t.Result.Succeeded, t.Result.Error));
        Assert.All(outcome.Tests, t => Assert.True(t.JournalDurable));
        Assert.Equal(["cpu-mono", "disque"], worker.Requests.Select(r => r.Kind));
        Assert.True(leaseHeldDuringJob);
        Assert.Null(lease.Holder);
        Assert.Equal(1, cadence.Opened);
        Assert.Equal(1, cadence.Closed);
        Assert.Equal(2, priority.Opened);
        Assert.Equal(2, priority.Closed);
        Assert.Equal(1, sensors.Unsubscriptions);
        Assert.True(worker.Disposed);
        Assert.Contains(progress, p => p.Kind == BenchTestKind.Disk && p.Phase == "faux");
        Assert.Contains(outcome.Log, l => l.Contains("45 °C"));
        Assert.Equal(["EcoQoS : désactivé (faux)"], outcome.WorkerNotes);

        SessionJournalContent content = journal.Read();
        Assert.Equal(2, content.Entries.Count);
        Assert.All(content.Entries, e => Assert.Equal(SessionEntryState.Completed, e.State));
        Assert.All(content.Entries, e => Assert.Equal("bench", e.Component));
        Assert.Equal("disque", content.Entries[1].Action);
        Assert.Equal("C", content.Entries[1].Values["volume"]);
    }

    [Fact]
    public async Task Un_bail_tenu_par_une_autre_fonction_refuse_la_session_sans_rien_lancer()
    {
        using var temp = new TempDirectory();
        var lease = new TuningLease();
        using TuningLeaseHandle other = lease.TryAcquire("groupe", "un groupe", "application").Handle!;
        var worker = new FakeWorker();
        var session = new BenchSession(new BenchSessionPorts
        {
            StartWorker = (_, _) => Task.FromResult<IBenchWorker>(worker),
            Lease = lease,
            Journal = new SessionJournal(temp.File("journal.jsonl")),
        });

        BenchSessionOutcome outcome = await session.RunAsync(Plan(Test(BenchTestKind.CpuMono)), null, CancellationToken.None);

        Assert.False(outcome.Started);
        Assert.Contains("groupe", outcome.LeaseRefusal);
        Assert.Empty(outcome.Tests);
        Assert.Empty(worker.Requests);
    }

    [Fact]
    public async Task Un_arret_de_securite_thermique_annule_le_test_echoue_sa_ligne_et_saute_les_suivants()
    {
        using var temp = new TempDirectory();
        // Même origine que les relevés : l'heure d'un relevé est celle de sa capture, l'horloge de la session ne sert
        // qu'à la perte de relevé (jamais atteinte ici, elle n'avance pas).
        var clock = new ManualClock(new DateTimeOffset(BenchSnapshots.T0));
        var journal = new SessionJournal(temp.File("journal.jsonl"), clock);
        var worker = new FakeWorker(TimeSpan.FromSeconds(30));
        var sensors = new FakeSensors();
        var session = new BenchSession(new BenchSessionPorts
        {
            StartWorker = (_, _) => Task.FromResult<IBenchWorker>(worker),
            Lease = new TuningLease(clock),
            Journal = journal,
            Time = clock,
            SubscribeSnapshots = sensors.Subscribe,
        });

        Task<BenchSessionOutcome> run = session.RunAsync(Plan(Test(BenchTestKind.CpuMulti), Test(BenchTestKind.RamLatency)), null, CancellationToken.None);
        await Task.Delay(300);
        sensors.Push(BenchSnapshots.At(1, cpuTemp: 99));
        await Task.Delay(700);
        sensors.Push(BenchSnapshots.At(12, cpuTemp: 99)); // 11 s plus tard à la capture : seuil tenu
        BenchSessionOutcome outcome = await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(BenchStopReason.Thermal, outcome.StoppedBy);
        Assert.Equal(2, outcome.Tests.Count);
        BenchTestOutcome stopped = outcome.Tests[0];
        Assert.False(stopped.Result.Succeeded);
        Assert.Equal(BenchStopReason.Thermal, stopped.StopReason);
        Assert.Contains("arrêt de sécurité thermique", stopped.Result.Error);
        Assert.Equal(99, stopped.MaxCpuTempC);
        Assert.True(stopped.Series.First(s => s.Key == "cpu-temp").Points.Count >= 1);
        Assert.False(worker.Killed, "le worker a rendu la main sur l'annulation : pas tué");
        Assert.Contains("non passé", outcome.Tests[1].Result.Error);
        Assert.Single(worker.Requests);

        SessionJournalContent content = journal.Read();
        SessionJournalEntry entry = Assert.Single(content.Entries);
        Assert.Equal(SessionEntryState.Failed, entry.State);
        Assert.Contains("thermique", entry.Cause);
    }

    [Fact]
    public async Task Une_annulation_de_l_utilisateur_arrete_le_test_et_la_session()
    {
        using var temp = new TempDirectory();
        var worker = new FakeWorker(TimeSpan.FromSeconds(30));
        var session = new BenchSession(new BenchSessionPorts
        {
            StartWorker = (_, _) => Task.FromResult<IBenchWorker>(worker),
            Lease = new TuningLease(),
            Journal = new SessionJournal(temp.File("journal.jsonl")),
        });
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        BenchSessionOutcome outcome = await session.RunAsync(Plan(Test(BenchTestKind.CpuMono), Test(BenchTestKind.CpuMulti)), null, cancel.Token);

        Assert.True(outcome.Cancelled);
        Assert.Null(outcome.StoppedBy);
        BenchTestOutcome first = Assert.Single(outcome.Tests);
        Assert.Equal(BenchJobDispatcher.CancelledError, first.Result.Error);
        Assert.Null(first.StopReason);
    }

    [Fact]
    public async Task Sans_journal_ecrit_le_test_ne_commence_pas()
    {
        using var temp = new TempDirectory();
        var worker = new FakeWorker();
        var session = new BenchSession(new BenchSessionPorts
        {
            StartWorker = (_, _) => Task.FromResult<IBenchWorker>(worker),
            Lease = new TuningLease(),
            Journal = new SessionJournal(temp.Root), // un dossier à la place du fichier : rien ne peut s'y écrire
        });

        BenchSessionOutcome outcome = await session.RunAsync(Plan(Test(BenchTestKind.CpuMono)), null, CancellationToken.None);

        BenchTestOutcome test = Assert.Single(outcome.Tests);
        Assert.False(test.JournalDurable);
        Assert.Equal(BenchSession.JournalUnavailableError, test.Result.Error);
        Assert.Empty(worker.Requests);
    }

    [Fact]
    public async Task Un_worker_qui_ne_se_lance_pas_laisse_chaque_test_en_echec_explique()
    {
        using var temp = new TempDirectory();
        var session = new BenchSession(new BenchSessionPorts
        {
            StartWorker = (_, _) => throw new InvalidOperationException("l'app tourne sous l'hôte dotnet"),
            Lease = new TuningLease(),
            Journal = new SessionJournal(temp.File("journal.jsonl")),
        });

        BenchSessionOutcome outcome = await session.RunAsync(Plan(Test(BenchTestKind.CpuMono), Test(BenchTestKind.Disk)), null, CancellationToken.None);

        Assert.True(outcome.Started);
        Assert.Equal(2, outcome.Tests.Count);
        Assert.All(outcome.Tests, t => Assert.Contains("hôte dotnet", t.Result.Error));
        Assert.Contains(outcome.Log, l => l.Contains("lancement du worker"));
    }

    [Fact]
    public async Task Le_retour_au_repos_attend_la_temperature_entre_deux_tests()
    {
        using var temp = new TempDirectory();
        var clock = new ManualClock(T0);
        var worker = new FakeWorker();
        var sensors = new FakeSensors();
        var session = new BenchSession(new BenchSessionPorts
        {
            StartWorker = (_, _) => Task.FromResult<IBenchWorker>(worker),
            Lease = new TuningLease(clock),
            Journal = new SessionJournal(temp.File("journal.jsonl"), clock),
            Time = clock,
            SubscribeSnapshots = sensors.Subscribe,
            LastSnapshot = () => BenchSnapshots.At(0, cpuTemp: 40),
        });
        var plan = new BenchSessionPlan { Tests = [Test(BenchTestKind.CpuMono), Test(BenchTestKind.CpuMulti)], Thermal = Limits, IdleReturnBetweenTests = true };
        var phases = new List<BenchSessionProgress>();

        Task<BenchSessionOutcome> run = session.RunAsync(plan, phases.Add, CancellationToken.None);
        await Task.Delay(400);
        sensors.Push(BenchSnapshots.At(1, cpuTemp: 70)); // encore chaud : on attend
        await Task.Delay(600);
        Assert.Single(worker.Requests);
        sensors.Push(BenchSnapshots.At(2, cpuTemp: 42)); // revenu à 3 °C de la base
        BenchSessionOutcome outcome = await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, worker.Requests.Count);
        Assert.Contains(phases, p => p.Phase == "retour au repos");
        Assert.Contains("42 °C", outcome.Tests[1].IdleReturnNote);
        Assert.Null(outcome.Tests[0].IdleReturnNote);
    }

    [Fact]
    public async Task Chaque_releve_recu_pendant_un_test_est_enregistre_meme_plus_vite_que_la_veille()
    {
        using var temp = new TempDirectory();
        var clock = new ManualClock(new DateTimeOffset(BenchSnapshots.T0));
        var worker = new FakeWorker(TimeSpan.FromSeconds(2));
        var sensors = new FakeSensors();
        var session = new BenchSession(new BenchSessionPorts
        {
            StartWorker = (_, _) => Task.FromResult<IBenchWorker>(worker),
            Lease = new TuningLease(clock),
            Journal = new SessionJournal(temp.File("journal.jsonl"), clock),
            Time = clock,
            SubscribeSnapshots = sensors.Subscribe,
        });

        Task<BenchSessionOutcome> run = session.RunAsync(Plan(Test(BenchTestKind.CpuMono)), null, CancellationToken.None);
        await Task.Delay(300);
        // Six relevés à 250 ms d'écart à la capture, arrivés d'un coup : la veille (500 ms) n'en verrait qu'un.
        for (int i = 0; i < 6; i++) sensors.Push(BenchSnapshots.At(1 + i * 0.25, cpuTemp: 60 + i));
        BenchSessionOutcome outcome = await run.WaitAsync(TimeSpan.FromSeconds(10));

        RecordingCadence cadence = outcome.Tests[0].Cadence;
        Assert.Equal(6, cadence.Snapshots);
        Assert.Equal(250, cadence.MeanIntervalMs!.Value, 3);
        SensorSeries temperature = outcome.Tests[0].Series.First(s => s.Key == "cpu-temp");
        Assert.Equal([63f, 65f], temperature.Points.Select(p => p.Value)); // 1 Hz : le dernier relevé de chaque seconde
    }

    [Fact]
    public async Task Arreter_pendant_la_relance_du_worker_garde_les_tests_deja_passes()
    {
        using var temp = new TempDirectory();
        var first = new FakeWorker { DiesAfterJob = true };
        int starts = 0;
        var session = new BenchSession(new BenchSessionPorts
        {
            StartWorker = async (_, cancel) =>
            {
                if (Interlocked.Increment(ref starts) == 1) return first;
                await Task.Delay(Timeout.Infinite, cancel); // la relance attend la connexion du worker
                throw new InvalidOperationException("inatteignable");
            },
            Lease = new TuningLease(),
            Journal = new SessionJournal(temp.File("journal.jsonl")),
        });
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));

        BenchSessionOutcome outcome = await session.RunAsync(Plan(Test(BenchTestKind.CpuMono), Test(BenchTestKind.CpuMulti)), null, cancel.Token);

        Assert.True(outcome.Cancelled);
        BenchTestOutcome passed = Assert.Single(outcome.Tests);
        Assert.True(passed.Result.Succeeded, passed.Result.Error);
        Assert.Contains(outcome.Log, l => l.Contains("relance"));
        Assert.Equal(2, starts);
    }
}
