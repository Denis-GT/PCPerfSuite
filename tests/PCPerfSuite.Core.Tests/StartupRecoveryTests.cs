using System.Text;
using PCPerfSuite.Core.Compatibility;
using PCPerfSuite.Core.Safety;
using PCPerfSuite.Core.Safety.Events;

namespace PCPerfSuite.Core.Tests;

/// <summary>Reprise au lancement : ordre fixe des étapes, gestionnaires isolés, qualification des opérations
/// interrompues, clôture dans le journal.</summary>
public class StartupRecoveryTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDirectory _dir = new();
    private readonly ManualClock _clock = new(T0);
    private readonly SessionJournal _journal;
    private readonly List<string> _calls = [];
    private readonly List<string> _logged = [];
    private int _eventReads;

    public StartupRecoveryTests() => _journal = new SessionJournal(_dir.File("journal-session.jsonl"), _clock);

    public void Dispose() => _dir.Dispose();

    private sealed class Handler(string id, RecoveryStage stage, string[] components, List<string> calls, bool throws = false)
        : IStartupRecoveryHandler
    {
        public string Id => id;
        public RecoveryStage Stage => stage;
        public IReadOnlyCollection<string> Components => components;
        public IReadOnlyList<RecoveredEntry> Received { get; private set; } = [];

        public string? Handle(IReadOnlyList<RecoveredEntry> entries)
        {
            calls.Add(id);
            Received = entries;
            if (throws) throw new InvalidOperationException("gestionnaire en panne");
            return "remis d'origine";
        }
    }

    private StartupRecovery Recovery(IReadOnlyList<IStartupRecoveryHandler> handlers,
        Func<DateTimeOffset, CancellationToken, SystemEventReadResult>? events = null,
        Func<SessionJournalEntry, bool>? isLive = null, TimeSpan? timeout = null)
        => new(_journal, handlers,
            events ?? ((_, _) => { Interlocked.Increment(ref _eventReads); return new SystemEventReadResult([], null); }),
            _clock,
            (ex, origin) => _logged.Add($"{origin} : {ex.Message}"),
            isLive ?? (_ => false),
            timeout);

    /// <summary>Opération écrite par une session d'avant un redémarrage : démarrage de Windows bien plus ancien.</summary>
    private Guid AppendFromAnEarlierBoot(string component, DateTimeOffset startedUtc)
    {
        var id = Guid.NewGuid();
        string line = $"\n{{\"v\":1,\"id\":\"{id}\",\"timeUtc\":\"{startedUtc:O}\",\"boot\":\"{startedUtc.AddDays(-3):O}\",\"pid\":4242,\"component\":\"{component}\",\"action\":\"debut\",\"state\":\"InProgress\"}}";
        File.AppendAllText(_journal.FilePath, line, new UTF8Encoding(false));
        return id;
    }

    [Fact]
    public void Order_FollowsTheStages_RegardlessOfRegistrationOrder()
    {
        IReadOnlyList<IStartupRecoveryHandler> ordered = StartupRecovery.Order(
        [
            new Handler("bascule", RecoveryStage.AutoSwitch, [], _calls),
            new Handler("bench", RecoveryStage.Bench, [], _calls),
            new Handler("oc", RecoveryStage.OverclockRestore, [], _calls),
            new Handler("ecran", RecoveryStage.DisplayTrial, [], _calls),
            new Handler("groupes", RecoveryStage.ProfileGroups, [], _calls),
            new Handler("combine", RecoveryStage.CombinedTest, [], _calls),
        ]);

        Assert.Equal(["oc", "ecran", "combine", "bench", "groupes", "bascule"], ordered.Select(h => h.Id));
    }

    [Fact]
    public void Run_CallsHandlersByStage_AndKeepsRegistrationOrderWithinAStage()
    {
        _journal.Begin("bench", "cpu");
        _journal.Begin("palier-oc", "essai");

        Recovery(
        [
            new Handler("bench", RecoveryStage.Bench, ["bench"], _calls),
            new Handler("oc-auto", RecoveryStage.OverclockRestore, ["palier-oc"], _calls),
            new Handler("oc-verification", RecoveryStage.OverclockRestore, ["palier-oc"], _calls),
        ]).Run();

        Assert.Equal(["oc-auto", "oc-verification", "bench"], _calls);
    }

    [Fact]
    public void HandlerOnlyReceivesItsComponents_WithTheirQualification()
    {
        SessionOperation mine = _journal.Begin("test-combine", "debut");
        _journal.Begin("bench", "cpu");
        var handler = new Handler("combine", RecoveryStage.CombinedTest, ["test-combine"], _calls);

        Recovery([handler]).Run();

        RecoveredEntry received = Assert.Single(handler.Received);
        Assert.Equal(mine.Id, received.Entry.Id);
        Assert.Equal(IncidentQualificationKind.Interrupted, received.Qualification.Kind);
    }

    [Fact]
    public void AThrowingHandler_IsLogged_AndTheOthersStillRun()
    {
        _journal.Begin("palier-oc", "essai");
        _journal.Begin("bench", "cpu");

        StartupRecoveryReport report = Recovery(
        [
            new Handler("oc-auto", RecoveryStage.OverclockRestore, ["palier-oc"], _calls, throws: true),
            new Handler("bench", RecoveryStage.Bench, ["bench"], _calls),
        ]).Run();

        Assert.Equal(["oc-auto", "bench"], _calls);
        Assert.Contains(_logged, line => line.Contains("oc-auto") && line.Contains("gestionnaire en panne"));
        Assert.False(report.Handlers.Single(h => h.HandlerId == "oc-auto").Succeeded);
        Assert.True(report.Handlers.Single(h => h.HandlerId == "bench").Succeeded);
        SessionJournalEntry failed = _journal.Read().Entries.Single(e => e.Component == "palier-oc");
        Assert.Contains(StartupRecovery.FailedHandlerNote, failed.Cause);
    }

    [Fact]
    public void AnEntryWithoutHandler_IsClosedFailed_WithItsQualification()
    {
        SessionOperation operation = _journal.Begin("limites-processus", "actives");

        StartupRecoveryReport report = Recovery([]).Run();

        Assert.Single(report.Recovered);
        SessionJournalEntry closed = Assert.Single(_journal.Read().Entries);
        Assert.Equal(operation.Id, closed.Id);
        Assert.Equal(SessionEntryState.Failed, closed.State);
        Assert.Contains("interrompu", closed.Cause);
        Assert.Contains(StartupRecovery.NoHandlerNote, closed.Cause);
    }

    [Fact]
    public void ASecondRun_FindsNothingLeft()
    {
        _journal.Begin("bench", "cpu");
        Recovery([]).Run();

        StartupRecoveryReport second = Recovery([]).Run();

        Assert.Empty(second.Recovered);
    }

    [Fact]
    public void NoPendingEntry_NeverReadsTheEventLog()
    {
        _journal.Begin("bench", "cpu").Complete();

        StartupRecoveryReport report = Recovery([new Handler("bench", RecoveryStage.Bench, ["bench"], _calls)]).Run();

        Assert.Equal(0, _eventReads);
        Assert.Empty(_calls);
        Assert.False(report.HasInterruptedOperations);
    }

    [Fact]
    public void AfterAReboot_ThePowerLossIsGivenToTheHandler()
    {
        AppendFromAnEarlierBoot("test-combine", T0);
        _clock.Now = T0.AddMinutes(30);
        var handler = new Handler("combine", RecoveryStage.CombinedTest, ["test-combine"], _calls);

        Recovery([handler], (_, _) => new SystemEventReadResult(
        [
            new SystemEventRecord(SystemEventKind.BootStarted, 12, T0.AddMinutes(5)),
            new SystemEventRecord(SystemEventKind.KernelPower41, 41, T0.AddMinutes(5.5)) { BugcheckCode = 0, PowerButtonTimestamp = 0 },
        ], null)).Run();

        Assert.Equal(IncidentQualificationKind.PowerLoss, Assert.Single(handler.Received).Qualification.Kind);
        Assert.Contains("arrêt brutal", _journal.Read().Entries.Single().Cause);
    }

    [Fact]
    public void ASlowEventLog_TimesOut_AndHandlersStillRun()
    {
        AppendFromAnEarlierBoot("bench", T0);
        _clock.Now = T0.AddMinutes(30);
        var handler = new Handler("bench", RecoveryStage.Bench, ["bench"], _calls);

        StartupRecoveryReport report = Recovery([handler], (_, token) =>
        {
            token.WaitHandle.WaitOne(TimeSpan.FromSeconds(5));
            return new SystemEventReadResult([], null);
        }, timeout: TimeSpan.FromMilliseconds(100)).Run();

        Assert.Equal(["bench"], _calls);
        Assert.Equal(IncidentQualificationKind.Unknown, handler.Received.Single().Qualification.Kind);
        Assert.NotNull(report.EventsProblem);
    }

    [Fact]
    public void AnEntryHeldByALivePcPerfSuite_IsLeftAlone_AndCompactionWaits()
    {
        SessionOperation held = _journal.Begin("palier-oc", "essai");

        StartupRecoveryReport report = Recovery([new Handler("oc", RecoveryStage.OverclockRestore, ["palier-oc"], _calls)],
            isLive: entry => entry.Id == held.Id).Run();

        Assert.Empty(_calls);
        Assert.Single(report.SkippedLive);
        Assert.False(report.Compacted);
        Assert.Equal(SessionEntryState.InProgress, _journal.Read().Entries.Single().State);
    }

    [Fact]
    public void Run_CompactsTheJournal()
    {
        _journal.Begin("bench", "cpu").Complete();
        _clock.Now = T0.AddDays(40);

        StartupRecoveryReport report = Recovery([]).Run();

        Assert.True(report.Compacted);
        Assert.Empty(_journal.Read().Entries);
    }

    [Fact]
    public void IsHeldByLivePcPerfSuite_RejectsThisProcessAndUnknownPids()
    {
        var entry = new SessionJournalEntry(Guid.NewGuid(), "bench", "cpu", new Dictionary<string, string>(),
            T0, T0, Environment.ProcessId, SessionEntryState.InProgress, null, T0);

        Assert.False(StartupRecovery.IsHeldByLivePcPerfSuite(entry));
        Assert.False(StartupRecovery.IsHeldByLivePcPerfSuite(entry with { ProcessId = null }));
        Assert.False(StartupRecovery.IsHeldByLivePcPerfSuite(entry with { ProcessId = int.MaxValue - 7 }));
    }

    [Fact]
    public void Row_WithoutInterruption_SaysNothingInterrupted()
    {
        var report = new StartupRecoveryReport([], [], [], 0, null, null, true);

        CompatibilityRow row = SessionJournalRowProvider.BuildRow(report, SessionJournalContent.Empty, writeError: null);

        Assert.True(row.IsSupported);
        Assert.Equal("Rien d'interrompu", row.Status);
    }

    [Fact]
    public void Row_ListsComponentsAndQualifications_NeverTheValues()
    {
        var entry = new SessionJournalEntry(Guid.NewGuid(), "test-combine", "debut",
            new Dictionary<string, string> { ["cpu-w"] = "253" }, T0, T0, 1, SessionEntryState.InProgress, null, T0);
        var qualification = new IncidentQualification(IncidentQualificationKind.PowerLoss, [], "arrêt brutal (Kernel-Power 41)");
        var report = new StartupRecoveryReport([new RecoveredEntry(entry, qualification)], [], [], 1, null, null, true);

        CompatibilityRow row = SessionJournalRowProvider.BuildRow(report, null, writeError: null);

        Assert.False(row.IsSupported);
        Assert.Equal("1 opération interrompue", row.Status);
        Assert.Contains("test-combine : arrêt brutal", row.Detail);
        Assert.DoesNotContain("253", row.Detail);
    }
}
