using PCPerfSuite.Core.Hardware;
using PCPerfSuite.Core.PowerSettings;
using PCPerfSuite.Core.Profiles;
using PCPerfSuite.Core.Safety;
using PCPerfSuite.Core.Safety.Events;

namespace PCPerfSuite.Core.Tests;

/// <summary>
/// Prudence au démarrage de bout en bout : une bascule journalisée par la période probatoire de #8, un incident, puis la
/// reprise au lancement avec les deux gestionnaires réels (groupes, puis bascule), sur le même journal de session.
/// </summary>
public sealed class AutoSwitchStartupRecoveryTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 20, 0, 0, TimeSpan.Zero);
    private const int SavedCoreOffset = 150;

    private readonly TempDirectory _dir = new();
    private readonly ManualClock _clock = new(T0);
    private readonly SessionJournal _journal;
    private readonly AppSettings _settings = new();
    private readonly List<string> _logged = [];

    public AutoSwitchStartupRecoveryTests()
    {
        _journal = new SessionJournal(_dir.File("journal-session.jsonl"), _clock);

        // Les deux groupes de jeu générés reprennent l'OC enregistré dans l'onglet GPU, case « Appliquer au démarrage »
        // cochée par l'utilisateur.
        _settings.Gpu.CoreClockOffsetMhz = SavedCoreOffset;
        _settings.Gpu.ApplyOverclockAtStartup = true;
        _settings.ProfileGroups.Groups.Add(GamingGroup("leger", "Jeu léger (auto)", ProfileGroupUsage.LightGaming));
        _settings.ProfileGroups.Groups.Add(GamingGroup("exigeant", "Jeu exigeant (auto)", ProfileGroupUsage.HeavyGaming));
    }

    public void Dispose() => _dir.Dispose();

    private string UsagePath => _dir.File("usage.json");

    private static ProfileGroup GamingGroup(string id, string name, string usage) => new()
    {
        Id = id,
        Name = name,
        Usage = usage,
        Origin = ProfileGroupOrigin.Generated,
        Gpu = ProfileGroupEditor.GpuValues(new GpuOverclockProfile { CoreClockOffsetMhz = SavedCoreOffset }, ProfileGroupTestData.Rtx),
    };

    /// <summary>Ce que fait la bascule quand elle pose un groupe qui relève l'OC : l'orchestrateur, avec les options de la
    /// bascule, ouvre une période probatoire à son nom. Une régression dans ces options (demandeur « manuel », état de
    /// démarrage) casse ces tests.</summary>
    private async Task SwitchToAsync(string groupId)
    {
        var targets = new FakeTargets();
        var applier = new ProfileGroupApplier(targets.Cpu, targets.Gpu, targets.Fans, new TuningLease(_clock),
            new ProfileGroupProbation(_journal, _clock), _clock);

        ProfileGroupApplyResult result = await applier.ApplyAsync(_settings.ProfileGroups.Find(groupId)!, ProfileGroupApplyOptions.ForAutoSwitch());

        Assert.True(result.Active!.GpuRaised);
        Assert.False(result.Active.MadeStartupState);
    }

    /// <summary>Une ligne écrite avant #9, sans demandeur.</summary>
    private void OpenProbationWithoutRequester(string groupId)
        => Assert.True(new ProfileGroupProbation(_journal, _clock).Begin(groupId, ProfileGroupProbation.ApplyAction,
            gpuRaised: true, wattsRaised: false, madeStartupState: false));

    /// <summary>Lancement suivant, après un redémarrage de Windows à T0 + 5 min, avec les gestionnaires dans l'ordre
    /// inverse des étapes : c'est l'étape qui décide de l'ordre d'appel.</summary>
    private StartupRecoveryReport Relaunch(params SystemEventRecord[] events)
    {
        _clock.Now = T0.AddMinutes(30);
        IStartupRecoveryHandler[] handlers =
        [
            new AutoSwitchRecoveryHandler(() => UsagePath, id => _settings.ProfileGroups.Find(id)?.Name, _clock),
            new ProfileGroupRecoveryHandler(mutate => mutate(_settings), _clock),
        ];

        return new StartupRecovery(_journal, handlers, (_, _) => new SystemEventReadResult(events, null), _clock,
            (ex, origin) => _logged.Add($"{origin} : {ex.Message}"), _ => false, currentBoot: _ => T0.AddMinutes(5)).Run();
    }

    private static SystemEventRecord[] BlueScreenAfterTheSwitch() =>
    [
        new SystemEventRecord(SystemEventKind.BootStarted, 12, T0.AddMinutes(5)),
        new SystemEventRecord(SystemEventKind.KernelPower41, 41, T0.AddMinutes(5.5)) { BugcheckCode = 0x116, PowerButtonTimestamp = 0 },
    ];

    private UsageHistory Usage() => UsageHistory.FromFile(UsageHistoryStore.Read(UsagePath).File, _clock.GetUtcNow());

    [Fact]
    public async Task Un_ecran_bleu_apres_une_bascule_passe_par_les_groupes_puis_par_la_bascule()
    {
        await SwitchToAsync("exigeant");

        StartupRecoveryReport report = Relaunch(BlueScreenAfterTheSwitch());

        Assert.Empty(_logged);
        Assert.Equal([ProfileGroupProbation.Component, AutoSwitchRequester.Id], report.Handlers.Select(h => h.HandlerId));
        Assert.All(report.Handlers, h => Assert.True(h.Succeeded));

        // #8 : le groupe est suspendu, et la case qui reposerait le même OC au lancement est décochée.
        ProfileGroupSuspension suspension = _settings.ProfileGroups.Suspensions["exigeant"];
        Assert.Contains("écran bleu", suspension.Cause);
        Assert.True(suspension.GpuStartupUnchecked);
        Assert.False(_settings.Gpu.ApplyOverclockAtStartup);

        // #9 : l'incident est au journal des bascules, sous le nom du groupe.
        AutoSwitchJournalEntry incident = Assert.Single(Usage().UnacknowledgedIncidents());
        Assert.Equal("exigeant", incident.GroupId);
        Assert.Equal("Jeu exigeant (auto)", incident.GroupName);
        Assert.Contains("écran bleu", incident.Reason);

        // Le même OC ne revient ni par ce groupe ni par l'autre groupe de jeu.
        UsageGroupChoice heavy = UsageGroupResolver.Resolve(_settings.ProfileGroups, new UsageTarget(ProfileGroupUsage.HeavyGaming, null));
        UsageGroupChoice light = UsageGroupResolver.Resolve(_settings.ProfileGroups, new UsageTarget(ProfileGroupUsage.LightGaming, null));
        Assert.True(heavy.Suspended);
        Assert.True(light.Suspended);
        Assert.Equal("Jeu exigeant (auto)", light.SuspendedBy);

        // L'opération est close avec sa cause, une seule fois.
        SessionJournalEntry closed = Assert.Single(_journal.Read().Entries);
        Assert.Equal(SessionEntryState.Failed, closed.State);
        Assert.Contains("écran bleu", closed.Cause);
        Assert.DoesNotContain(StartupRecovery.NoHandlerNote, closed.Cause);
    }

    [Fact]
    public void Un_groupe_applique_a_la_main_est_suspendu_sans_ligne_au_journal_des_bascules()
    {
        OpenProbationWithoutRequester("exigeant");

        StartupRecoveryReport report = Relaunch(BlueScreenAfterTheSwitch());

        // Qui que soit le demandeur, l'OC qui a planté est celui que l'onglet reposerait : la case est décochée.
        Assert.Contains("exigeant", _settings.ProfileGroups.Suspensions.Keys);
        Assert.False(_settings.Gpu.ApplyOverclockAtStartup);
        Assert.Null(report.Handlers.Single(h => h.HandlerId == AutoSwitchRequester.Id).Note);
        Assert.False(File.Exists(UsagePath));
    }

    [Fact]
    public async Task Un_redemarrage_propre_ne_suspend_rien_et_n_ecrit_rien()
    {
        await SwitchToAsync("exigeant");

        Relaunch(
            new SystemEventRecord(SystemEventKind.CleanShutdown, 13, T0.AddMinutes(4)),
            new SystemEventRecord(SystemEventKind.BootStarted, 12, T0.AddMinutes(5)));

        Assert.Empty(_settings.ProfileGroups.Suspensions);
        Assert.True(_settings.Gpu.ApplyOverclockAtStartup);
        Assert.False(File.Exists(UsagePath));
        SessionJournalEntry closed = Assert.Single(_journal.Read().Entries);
        Assert.Equal(SessionEntryState.Failed, closed.State);
        Assert.Contains("arrêt normal de Windows", closed.Cause);
    }

    [Fact]
    public async Task Un_usage_json_verrouille_n_empeche_pas_la_suspension()
    {
        await SwitchToAsync("exigeant");
        File.WriteAllText(UsagePath, "{}");

        StartupRecoveryReport report;
        using (new FileStream(UsagePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            report = Relaunch(BlueScreenAfterTheSwitch());
        }

        Assert.Contains("exigeant", _settings.ProfileGroups.Suspensions.Keys);
        Assert.False(_settings.Gpu.ApplyOverclockAtStartup);
        Assert.Contains("non notée", report.Handlers.Single(h => h.HandlerId == AutoSwitchRequester.Id).Note);
    }
}
