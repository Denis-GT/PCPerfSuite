using PCPerfSuite.Core.Hardware;
using PCPerfSuite.Core.Hardware.Cpu;
using PCPerfSuite.Core.Hardware.Fans;
using PCPerfSuite.Core.Profiles;
using PCPerfSuite.Core.Safety;
using static PCPerfSuite.Core.Tests.ProfileGroupTestData;

namespace PCPerfSuite.Core.Tests;

/// <summary>Les trois onglets, fictifs : ils notent l'ordre des appels et ce qu'ils reçoivent.</summary>
internal sealed class FakeTargets
{
    public List<string> Calls { get; } = new();
    public FakeCpu Cpu { get; }
    public FakeGpu Gpu { get; }
    public FakeFans Fans { get; }

    public FakeTargets()
    {
        Cpu = new FakeCpu(this);
        Gpu = new FakeGpu(this);
        Fans = new FakeFans(this);
    }

    internal sealed class FakeCpu(FakeTargets owner) : ICpuGroupTarget
    {
        public CpuTargetState State { get; set; } = ProfileGroupTestData.Cpu();
        public CpuGroupPlan? Received { get; private set; }
        public ProfileGroupApplyContext? Context { get; private set; }
        public Action? OnApply { get; set; }
        public ProfileDimension Dimension => ProfileDimension.Cpu;
        public bool IsRaised { get; set; }
        public void FlushPendingManualWrites() => owner.Calls.Add("flush-cpu");
        public CpuTargetState ReadState() => State;
        public string Describe(CpuProfile profile) => "cpu";

        public CpuApplyOutcome Apply(CpuGroupPlan plan, ProfileGroupApplyContext context)
        {
            owner.Calls.Add("cpu");
            OnApply?.Invoke();
            Received = plan;
            Context = context;
            if (plan.RaisesWatts) IsRaised = true;
            var items = plan.Items.ToList();
            items.AddRange(plan.PlanWrites.Select(w => ReportItem.Applied(w.Setting.Label, $"« {w.Setting.Label} » posé")));
            if (plan.Watts is { } watts) items.Add(ReportItem.Applied("watts", $"{watts.Sustained} W"));
            return new CpuApplyOutcome(new DimensionReport(ProfileDimension.Cpu, items, plan.Notes, plan.PlanWrites.Count > 0),
                new CpuProfile { SustainedWatts = plan.Watts?.Sustained });
        }
    }

    internal sealed class FakeGpu(FakeTargets owner) : IGpuGroupTarget
    {
        public GpuTargetState State { get; set; } = ProfileGroupTestData.Gpu();
        public GpuGroupPlan? Received { get; private set; }
        public Action? OnApply { get; set; }
        public bool RefuseEverything { get; set; }
        public ProfileDimension Dimension => ProfileDimension.Gpu;
        public bool IsRaised { get; set; }
        public void FlushPendingManualWrites() => owner.Calls.Add("flush-gpu");
        public GpuTargetState ReadState() => State;
        public string Describe(GpuOverclockProfile profile) => "gpu";

        public GpuApplyOutcome Apply(GpuGroupPlan plan, ProfileGroupApplyContext context)
        {
            owner.Calls.Add("gpu");
            OnApply?.Invoke();
            Received = plan;
            var items = plan.Items.ToList();
            if (plan.Request is { } request)
            {
                items.Add(RefuseEverything ? ReportItem.Refused("cœur", "refusé") : ReportItem.Applied("cœur", $"cœur {request.CoreOffsetMhz}"));
                if (plan.Raises && !RefuseEverything) IsRaised = true;
            }

            return new GpuApplyOutcome(new DimensionReport(ProfileDimension.Gpu, items, plan.Notes),
                new GpuRetainedValues { CoreOffsetMhz = plan.Request?.CoreOffsetMhz });
        }
    }

    internal sealed class FakeFans(FakeTargets owner) : IFanGroupTarget
    {
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Ready { get; set; } = true;
        public FanGroupPlan? Received { get; private set; }
        public ProfileDimension Dimension => ProfileDimension.Fans;
        public bool IsRaised => false;
        public Task WhenReady => Ready ? Task.CompletedTask : _ready.Task;
        public void FlushPendingManualWrites() => owner.Calls.Add("flush-fans");
        public FanTargetState ReadState() => ProfileGroupTestData.Fans(ready: Ready);
        public string AbsenceReason(string fanId) => "absent de ce PC";
        public string Describe(FanProfile profile) => "fans";

        public void BecomeReady()
        {
            Ready = true;
            _ready.TrySetResult();
        }

        public FanApplyOutcome Apply(FanGroupPlan plan, ProfileGroupApplyContext context)
        {
            owner.Calls.Add("fans");
            Received = plan;
            var items = plan.Items.ToList();
            items.AddRange(plan.Entries.Select(e => ReportItem.Applied(e.Name, $"« {e.Name} » réglé")));
            if (plan.RestoreAuto) items.Add(ReportItem.Applied("ventilation", "rendue au BIOS"));
            return new FanApplyOutcome(new DimensionReport(ProfileDimension.Fans, items, plan.Notes),
                new FanProfile { Fans = plan.Entries.Select(e => e.Config).ToList() });
        }
    }
}

/// <summary>L'orchestrateur : bail, ordre, prudence et rapport consolidé, sur des onglets fictifs.</summary>
public sealed class ProfileGroupApplierTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 20, 0, 0, TimeSpan.Zero);
    private readonly TempDirectory _dir = new();
    private readonly ManualClock _clock = new(T0);
    private readonly SessionJournal _journal;
    private readonly TuningLease _lease;
    private readonly FakeTargets _targets = new();
    private readonly ProfileGroupApplier _applier;

    public ProfileGroupApplierTests()
    {
        _journal = new SessionJournal(_dir.File("journal-session.jsonl"), _clock);
        _lease = new TuningLease(_clock);
        _applier = new ProfileGroupApplier(_targets.Cpu, _targets.Gpu, _targets.Fans, _lease, new ProfileGroupProbation(_journal, _clock), _clock);
    }

    public void Dispose() => _dir.Dispose();

    private static readonly ProfileGroupApplyOptions Manual = new(ProfileGroupRequesters.Manual, "la page Profils", IsManual: true);

    private static ProfileGroup Group(int gpuCore = 150, float? watts = null, uint? epp = null, bool fans = true)
    {
        var cpu = new CpuProfile { SustainedWatts = watts };
        if (epp is not null) cpu.PowerSettings["epp"] = new CpuProfilePowerValue { Ac = epp };
        return new ProfileGroup
        {
            Name = "Jeu",
            Cpu = watts is null && epp is null ? null : CpuPart(cpu),
            Gpu = GpuPart(new GpuOverclockProfile { CoreClockOffsetMhz = gpuCore }),
            Fans = fans ? new ProfileGroupFansPart { Values = new FanProfile { Fans = [Fan("cpu", FanControlMode.Curve)] } } : null,
        };
    }

    private List<string> Applied => _targets.Calls.Where(c => !c.StartsWith("flush")).ToList();

    [Fact]
    public async Task Un_bail_tenu_par_un_autre_refuse_le_groupe_sans_rien_ecrire()
    {
        _lease.TryAcquire("bench", "le bench", "mesure en cours");

        ProfileGroupApplyResult result = await _applier.ApplyAsync(Group(), Manual);

        Assert.True(result.Report.WasRefused);
        Assert.Contains("Réglages pilotés par le bench depuis", result.Report.Title);
        Assert.Empty(Applied);
        Assert.Null(result.Active);
        Assert.Empty(_journal.Read().Entries);
    }

    [Fact]
    public async Task Les_ecritures_manuelles_en_attente_partent_avant_le_groupe()
    {
        await _applier.ApplyAsync(Group(), Manual);

        Assert.Equal(["flush-fans", "flush-cpu", "flush-gpu"], _targets.Calls.Take(3));
    }

    [Fact]
    public async Task En_montee_la_ventilation_passe_d_abord()
    {
        ProfileGroupApplyResult result = await _applier.ApplyAsync(Group(gpuCore: 150, watts: 150), Manual);

        Assert.Equal(["fans", "cpu", "gpu"], Applied);
        Assert.Equal(ApplyOrder.FansFirst, result.Report.Order);
    }

    [Fact]
    public async Task En_descente_la_ventilation_passe_en_dernier()
    {
        _targets.Gpu.State = Gpu(Overclock(core: 150));

        ProfileGroupApplyResult result = await _applier.ApplyAsync(Group(gpuCore: 0, watts: 65), Manual);

        Assert.Equal(["cpu", "gpu", "fans"], Applied);
        Assert.Equal(ApplyOrder.FansLast, result.Report.Order);
    }

    [Fact]
    public async Task Le_choix_de_l_etat_de_demarrage_est_transmis_et_annonce()
    {
        ProfileGroupApplyResult result = await _applier.ApplyAsync(Group(watts: 65), Manual with { MakeStartupState = false });

        Assert.False(_targets.Cpu.Context!.MakeStartupState);
        Assert.Contains(ProfileGroupApplier.TransientNote, result.Report.Notes);
        Assert.False(result.Active!.MadeStartupState);
    }

    [Fact]
    public async Task La_ligne_du_journal_est_sur_le_disque_avant_la_premiere_ecriture_risquee()
    {
        SessionEntryState? before = null;
        _targets.Gpu.OnApply = () => before = _journal.Read().Entries.SingleOrDefault()?.State;

        ProfileGroupApplyResult result = await _applier.ApplyAsync(Group(gpuCore: 150), Manual);

        Assert.Equal(SessionEntryState.InProgress, before);
        Assert.Equal(SessionEntryState.InProgress, Assert.Single(_journal.Read().Entries).State);
        Assert.True(result.Active!.GpuRaised);
        Assert.NotNull(_applier.Probation.Current);
    }

    [Fact]
    public async Task Sans_journal_les_parties_risquees_ne_sont_pas_posees()
    {
        Directory.CreateDirectory(_dir.File("dossier"));
        var applier = new ProfileGroupApplier(_targets.Cpu, _targets.Gpu, _targets.Fans, _lease,
            new ProfileGroupProbation(new SessionJournal(_dir.File("dossier"), _clock), _clock), _clock);

        ProfileGroupApplyResult result = await applier.ApplyAsync(Group(gpuCore: 150, epp: 80), Manual);

        Assert.Null(_targets.Gpu.Received!.Request);
        Assert.Single(_targets.Cpu.Received!.PlanWrites);
        Assert.Contains("journal de session n'a pas pu être écrit", result.Report.Find(ProfileDimension.Gpu)!.Describe());
        Assert.False(result.Active!.GpuRaised);
    }

    [Fact]
    public async Task Rien_de_releve_apres_l_application_clot_la_periode()
    {
        _targets.Gpu.RefuseEverything = true;

        await _applier.ApplyAsync(Group(gpuCore: 150), Manual);

        Assert.Equal(SessionEntryState.Completed, Assert.Single(_journal.Read().Entries).State);
        Assert.Null(_applier.Probation.Current);
    }

    [Fact]
    public async Task Un_groupe_sans_risque_n_ouvre_pas_de_periode()
    {
        ProfileGroupApplyResult result = await _applier.ApplyAsync(Group(gpuCore: 0, epp: 80), Manual);

        Assert.Empty(_journal.Read().Entries);
        Assert.False(result.Active!.GpuRaised);
    }

    [Fact]
    public async Task Le_bail_pris_pour_l_application_est_rendu_ensuite()
    {
        TuningLeaseHolder? during = null;
        _targets.Gpu.OnApply = () => during = _lease.Holder;

        await _applier.ApplyAsync(Group(), Manual);

        Assert.Equal(ProfileGroupRequesters.Manual, during!.RequesterId);
        Assert.Null(_lease.Holder);
    }

    [Fact]
    public async Task Le_detenteur_du_bail_applique_sans_le_perdre()
    {
        TuningLeaseHandle handle = _lease.TryAcquire("bascule-auto", "la bascule automatique", "").Handle!;

        ProfileGroupApplyResult result = await _applier.ApplyAsync(Group(),
            new ProfileGroupApplyOptions("bascule-auto", "la bascule automatique", IsManual: false, Lease: handle));

        Assert.False(result.Report.WasRefused);
        Assert.True(_lease.IsHeldBy(handle));
    }

    [Fact]
    public async Task Une_poignee_perimee_est_refusee()
    {
        TuningLeaseHandle handle = _lease.TryAcquire("bascule-auto", "la bascule automatique", "").Handle!;
        handle.Dispose();

        ProfileGroupApplyResult result = await _applier.ApplyAsync(Group(), new ProfileGroupApplyOptions("bascule-auto", "", false, Lease: handle));

        Assert.True(result.Report.WasRefused);
        Assert.Empty(Applied);
    }

    [Fact]
    public async Task Au_lancement_les_ventilateurs_sont_attendus()
    {
        _targets.Fans.Ready = false;

        Task<ProfileGroupApplyResult> pending = _applier.ApplyAsync(Group(), Manual);
        Assert.False(pending.IsCompleted);

        _targets.Fans.BecomeReady();
        ProfileGroupApplyResult result = await pending;

        Assert.Single(_targets.Fans.Received!.Entries);
        Assert.False(result.Report.WasRefused);
    }

    [Fact]
    public async Task Sans_releve_des_ventilateurs_le_reste_s_applique_quand_meme()
    {
        _targets.Fans.Ready = false;

        ProfileGroupApplyResult result = await _applier.ApplyAsync(Group(), Manual with { FansWait = TimeSpan.FromMilliseconds(30) });

        Assert.Contains("aucun relevé des ventilateurs", result.Report.Find(ProfileDimension.Fans)!.Describe());
        Assert.Contains("gpu", Applied);
    }

    [Fact]
    public async Task Le_bail_perdu_pendant_l_attente_annule_tout()
    {
        _targets.Fans.Ready = false;
        TuningLeaseHandle handle = _lease.TryAcquire("bascule-auto", "la bascule automatique", "").Handle!;

        Task<ProfileGroupApplyResult> pending = _applier.ApplyAsync(Group(), new ProfileGroupApplyOptions("bascule-auto", "", false, Lease: handle));
        handle.Dispose();
        _lease.TryAcquire("bench", "le bench", "mesure");
        _targets.Fans.BecomeReady();
        ProfileGroupApplyResult result = await pending;

        Assert.True(result.Report.WasRefused);
        Assert.Empty(Applied);
    }

    [Fact]
    public async Task Modifier_le_groupe_pendant_l_attente_est_sans_effet()
    {
        _targets.Fans.Ready = false;
        ProfileGroup group = Group(gpuCore: 150);

        Task<ProfileGroupApplyResult> pending = _applier.ApplyAsync(group, Manual);
        group.Gpu!.Values!.CoreClockOffsetMhz = 250;
        _targets.Fans.BecomeReady();
        await pending;

        Assert.Equal(150, _targets.Gpu.Received!.Request!.CoreOffsetMhz);
    }

    [Fact]
    public async Task Le_plan_d_alimentation_touche_est_annonce_permanent()
    {
        ProfileGroupApplyResult result = await _applier.ApplyAsync(Group(gpuCore: 0, epp: 80), Manual);

        Assert.Contains(ProfileGroupApplier.PermanentNote, result.Report.Notes);
    }

    [Fact]
    public async Task Le_rapport_consolide_dit_chaque_dimension()
    {
        _targets.Cpu.State = Cpu(accepted: false);

        ProfileGroupApplyResult result = await _applier.ApplyAsync(Group(gpuCore: 150, watts: 150, epp: 80), Manual);

        string text = result.Report.Describe();
        Assert.Equal("Groupe « Jeu » appliqué en partie.", result.Report.Title);
        Assert.Contains("Processeur : ", text);
        Assert.Contains("watts ignorées : l'avertissement de l'onglet Processeur n'a pas été accepté", text);
        Assert.Contains("cœur 150", result.Report.Find(ProfileDimension.Gpu)!.Describe());
        Assert.Contains("Ventilation : « CPU Fan » réglé", text);
        Assert.Equal(150, result.Active!.Gpu!.CoreOffsetMhz);
    }

    [Fact]
    public async Task Un_groupe_qui_ne_pose_rien_ne_devient_pas_actif()
    {
        _targets.Gpu.State = Gpu(available: false);

        ProfileGroupApplyResult result = await _applier.ApplyAsync(Group(fans: false), Manual);

        Assert.Null(result.Active);
        Assert.Equal("Groupe « Jeu » non appliqué.", result.Report.Title);
    }

    [Fact]
    public async Task Un_groupe_vide_est_refuse()
    {
        ProfileGroupApplyResult result = await _applier.ApplyAsync(new ProfileGroup { Name = "Vide" }, Manual);

        Assert.True(result.Report.WasRefused);
        Assert.Empty(_targets.Calls);
    }

    [Fact]
    public async Task Une_hausse_automatique_apres_une_securite_thermique_n_est_pas_posee()
    {
        _targets.Gpu.State = Gpu(emergency: true);

        ProfileGroupApplyResult result = await _applier.ApplyAsync(Group(gpuCore: 150),
            new ProfileGroupApplyOptions("bascule-auto", "la bascule automatique", IsManual: false));

        Assert.Null(_targets.Gpu.Received!.Request);
        Assert.Empty(_journal.Read().Entries);
        Assert.Contains("sécurité thermique", result.Report.Describe());
    }
}
