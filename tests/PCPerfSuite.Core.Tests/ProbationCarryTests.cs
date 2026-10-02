using PCPerfSuite.Core.Hardware;
using PCPerfSuite.Core.Hardware.Cpu;
using PCPerfSuite.Core.PowerSettings;
using PCPerfSuite.Core.Profiles;
using PCPerfSuite.Core.Safety;
using PCPerfSuite.Core.Safety.Events;
using static PCPerfSuite.Core.Tests.ProfileGroupTestData;

namespace PCPerfSuite.Core.Tests;

/// <summary>
/// Deux applications de groupe qui se suivent : ce que le premier a relevé et qui reste en place n'échappe pas à la
/// prudence quand le second ouvre sa période (en session comme au lancement).
/// </summary>
public sealed class ProbationCarryTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 20, 0, 0, TimeSpan.Zero);
    private static readonly ProfileGroupApplyOptions Manual = new(ProfileGroupRequesters.Manual, "la page Profils", IsManual: true);

    private readonly TempDirectory _dir = new();
    private readonly ManualClock _clock = new(T0);
    private readonly SessionJournal _journal;
    private readonly FakeTargets _targets = new();
    private readonly ProfileGroupApplier _applier;

    public ProbationCarryTests()
    {
        _journal = new SessionJournal(_dir.File("journal-session.jsonl"), _clock);
        _applier = new ProfileGroupApplier(_targets.Cpu, _targets.Gpu, _targets.Fans, new TuningLease(_clock),
            new ProfileGroupProbation(_journal, _clock), _clock);
    }

    public void Dispose() => _dir.Dispose();

    private static ProfileGroup GpuGroup(string id = "oc", int core = 150)
        => new() { Id = id, Name = "OC", Gpu = GpuPart(new GpuOverclockProfile { CoreClockOffsetMhz = core }) };

    private static ProfileGroup WattsGroup(string id = "watts", float watts = 150)
        => new() { Id = id, Name = "Watts", Cpu = CpuPart(new CpuProfile { SustainedWatts = watts }) };

    private SessionJournalEntry Open() => Assert.Single(_journal.Read().Entries, e => e.State == SessionEntryState.InProgress);

    // ---- Application ----

    [Fact]
    public async Task Un_groupe_qui_releve_les_watts_reprend_l_oc_gpu_laisse_par_le_precedent()
    {
        await _applier.ApplyAsync(GpuGroup(), Manual);
        _clock.Now += TimeSpan.FromMinutes(10);

        await _applier.ApplyAsync(WattsGroup(), Manual);

        SessionJournalEntry open = Open();
        Assert.Equal("watts", open.Values[ProfileGroupProbation.GroupKey]);
        Assert.Equal("non", open.Values[ProfileGroupProbation.GpuKey]);
        Assert.Equal("oc", open.Values[ProfileGroupProbation.CarriedGroupKey]);
        Assert.Equal("oui", open.Values[ProfileGroupProbation.CarriedGpuKey]);
        Assert.Equal("oui", open.Values[ProfileGroupProbation.CarriedStartupStateKey]);

        ProbationInfo current = _applier.Probation.Current!;
        Assert.True(current.WatchesGpu);
        Assert.Equal("oc", current.GpuOwner!.GroupId);
    }

    [Fact]
    public async Task Un_groupe_qui_regle_la_carte_ne_reprend_pas_son_oc()
    {
        await _applier.ApplyAsync(GpuGroup(), Manual);

        var both = new ProfileGroup { Id = "both", Name = "Les deux", Gpu = GpuPart(new GpuOverclockProfile()), Cpu = WattsGroup().Cpu };
        await _applier.ApplyAsync(both, Manual);

        Assert.DoesNotContain(ProfileGroupProbation.CarriedGroupKey, Open().Values.Keys);
    }

    [Fact]
    public async Task Rien_n_est_repris_quand_l_oc_n_est_plus_en_place()
    {
        await _applier.ApplyAsync(GpuGroup(), Manual);
        _targets.Gpu.IsRaised = false;

        await _applier.ApplyAsync(WattsGroup(), Manual);

        Assert.Null(_applier.Probation.Current!.Carried);
    }

    [Fact]
    public void Une_periode_qui_ne_peut_pas_etre_ecrite_laisse_la_precedente_ouverte()
    {
        string path = _dir.File("journal.jsonl");
        var probation = new ProfileGroupProbation(new SessionJournal(() => path, _clock), _clock);
        Assert.True(probation.Begin("oc", ProfileGroupProbation.ApplyAction, true, false, true));

        // Le journal devient inaccessible (disque retiré, plein…) : la nouvelle ligne ne peut pas être écrite.
        path = _dir.File("dossier");
        Directory.CreateDirectory(path);

        Assert.False(probation.Begin("watts", ProfileGroupProbation.ApplyAction, false, true, true));
        Assert.Equal("oc", probation.Current!.GroupId);
        Assert.True(probation.Current.GpuRaised);
    }

    // ---- Reprise au lancement ----

    private static RecoveredEntry Recovered(Dictionary<string, string> values)
    {
        var entry = new SessionJournalEntry(Guid.NewGuid(), ProfileGroupProbation.Component, ProfileGroupProbation.ApplyAction, values,
            T0, T0, 4242, SessionEntryState.InProgress, null, T0);
        return new RecoveredEntry(entry,
            new IncidentQualification(IncidentQualificationKind.BlueScreen, [], IncidentClassifier.Label(IncidentQualificationKind.BlueScreen)));
    }

    private static Dictionary<string, string> WattsLineCarryingOc(string carriedStartup = "oui") => new()
    {
        [ProfileGroupProbation.GroupKey] = "watts",
        [ProfileGroupProbation.GpuKey] = "non",
        [ProfileGroupProbation.WattsKey] = "oui",
        [ProfileGroupProbation.StartupStateKey] = "non",
        [ProfileGroupProbation.CarriedGroupKey] = "oc",
        [ProfileGroupProbation.CarriedGpuKey] = "oui",
        [ProfileGroupProbation.CarriedWattsKey] = "non",
        [ProfileGroupProbation.CarriedStartupStateKey] = carriedStartup,
    };

    private static AppSettings TwoGroups()
    {
        var settings = new AppSettings();
        settings.Gpu.ApplyOverclockAtStartup = true;
        settings.Cpu.ApplyAtStartup = true;
        settings.ProfileGroups.Groups.Add(new ProfileGroup { Id = "oc", Name = "OC" });
        settings.ProfileGroups.Groups.Add(new ProfileGroup { Id = "watts", Name = "Watts" });
        return settings;
    }

    [Fact]
    public void Apres_un_ecran_bleu_le_groupe_dont_l_oc_etait_repris_est_suspendu_et_la_case_gpu_decochee()
    {
        AppSettings settings = TwoGroups();
        var handler = new ProfileGroupRecoveryHandler(mutate => mutate(settings), _clock);

        string? note = handler.Handle([Recovered(WattsLineCarryingOc())]);

        Assert.False(settings.Gpu.ApplyOverclockAtStartup);
        Assert.True(settings.Cpu.ApplyAtStartup); // les watts du second groupe n'étaient pas l'état de démarrage
        Assert.True(settings.ProfileGroups.Suspensions["oc"].GpuStartupUnchecked);
        Assert.Contains("watts", settings.ProfileGroups.Suspensions.Keys);
        Assert.Contains("2 groupe(s) suspendu(s)", note);
        Assert.StartsWith("1 application(s)", note);
    }

    [Fact]
    public void Une_bascule_reprise_par_un_groupe_applique_a_la_main_est_notee_au_journal_des_bascules()
    {
        // La bascule a posé l'OC de « oc » ; « watts » a été appliqué à la main ensuite, et un écran bleu a suivi.
        Dictionary<string, string> values = WattsLineCarryingOc();
        values[ProfileGroupProbation.RequesterKey] = ProfileGroupRequesters.Manual;
        values[ProfileGroupProbation.CarriedRequesterKey] = AutoSwitchRequester.Id;
        string usage = _dir.File("usage.json");
        var handler = new AutoSwitchRecoveryHandler(() => usage, id => id == "oc" ? "OC (auto)" : "Watts", _clock);

        handler.Handle([Recovered(values)]);

        AutoSwitchJournalEntry incident = Assert.Single(
            UsageHistory.FromFile(UsageHistoryStore.Read(usage).File, T0).UnacknowledgedIncidents());
        Assert.Equal("oc", incident.GroupId);
    }

    [Fact]
    public async Task Le_demandeur_du_groupe_repris_est_note()
    {
        await _applier.ApplyAsync(GpuGroup(), ProfileGroupApplyOptions.ForAutoSwitch());
        await _applier.ApplyAsync(WattsGroup(), Manual);

        Assert.Equal(AutoSwitchRequester.Id, Open().Values[ProfileGroupProbation.CarriedRequesterKey]);
    }

    [Fact]
    public void Un_groupe_pose_par_la_bascule_puis_reapplique_a_la_main_reste_une_bascule()
    {
        Dictionary<string, string> values = WattsLineCarryingOc();
        values[ProfileGroupProbation.CarriedGroupKey] = "watts";
        values[ProfileGroupProbation.RequesterKey] = ProfileGroupRequesters.Manual;
        values[ProfileGroupProbation.CarriedRequesterKey] = AutoSwitchRequester.Id;

        ProfileGroupIncidentDecision merged = Assert.Single(ProfileGroupIncidentPolicy.EvaluateAll(Recovered(values)));

        Assert.Equal(AutoSwitchRequester.Id, merged.RequesterId);
        Assert.True(merged.GpuRaised);
        Assert.True(merged.WattsRaised);
    }

    [Fact]
    public void Une_ligne_d_avant_sans_reprise_ne_vise_que_son_groupe()
    {
        Dictionary<string, string> values = WattsLineCarryingOc();
        foreach (string key in values.Keys.Where(k => k.StartsWith("repris-")).ToList()) values.Remove(key);

        IReadOnlyList<ProfileGroupIncidentDecision> decisions = ProfileGroupIncidentPolicy.EvaluateAll(Recovered(values));

        Assert.Equal("watts", Assert.Single(decisions).GroupId);
    }

    [Fact]
    public void Un_bloc_des_groupes_illisible_n_empeche_pas_de_decocher_la_case()
    {
        var settings = new AppSettings { ProfileGroups = new ProfileGroupsSettings { Groups = null!, Suspensions = null! } };
        settings.Gpu.ApplyOverclockAtStartup = true;
        var handler = new ProfileGroupRecoveryHandler(mutate => mutate(settings), _clock);

        handler.Handle([Recovered(WattsLineCarryingOc())]);

        Assert.False(settings.Gpu.ApplyOverclockAtStartup);
    }
}
