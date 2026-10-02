namespace PCPerfSuite.Core.Profiles;

/// <summary>Qui applique, et comment.</summary>
/// <param name="RequesterId">Identifiant stable (<see cref="ProfileGroupRequesters.Manual"/>, « bascule-auto »…).</param>
/// <param name="RequesterLabel">Nom affiché si le bail est pris pour l'application (« la page Profils »).</param>
/// <param name="IsManual">Un clic de l'utilisateur : après une sécurité thermique, une hausse demandée à la main reste
/// permise, pas une hausse automatique.</param>
/// <param name="MakeStartupState">Faux : les valeurs ne deviennent pas l'état de démarrage des onglets, et sont rendues
/// d'origine à la fermeture (D7). Pour la bascule automatique (#9).</param>
/// <param name="Lease">La poignée du bail, quand le demandeur le tient déjà (bascule automatique, bench…).</param>
/// <param name="FansWait">Attente maximale du premier relevé des ventilateurs (lancement de l'app).</param>
public sealed record ProfileGroupApplyOptions(
    string RequesterId,
    string RequesterLabel,
    bool IsManual,
    bool MakeStartupState = true,
    TuningLeaseHandle? Lease = null,
    TimeSpan? FansWait = null)
{
    /// <summary>Les options de la bascule automatique : demandeur « bascule-auto » (la reprise au lancement reconnaît
    /// ainsi ses bascules), pas un clic de l'utilisateur (aucune hausse après une sécurité thermique), et sans en faire
    /// l'état de démarrage (D7 : rendu à la fermeture).</summary>
    public static ProfileGroupApplyOptions ForAutoSwitch()
        => new(AutoSwitchRequester.Id, AutoSwitchRequester.Label, IsManual: false, MakeStartupState: false);
}

/// <summary>Le rapport, et l'état retenu à mémoriser comme groupe actif (null si rien n'a été posé).</summary>
public sealed record ProfileGroupApplyResult(ProfileGroupReport Report, ProfileGroupActiveState? Active);

/// <summary>
/// Applique un groupe de profils, en logique d'orchestration testable : les onglets (cibles) font le travail et gardent
/// leurs sécurités ; ici se décident le bail, l'ordre, la prudence et le rapport consolidé. Déroulé :
/// <list type="number">
/// <item>copie du groupe (le modifier pendant l'attente n'a pas d'effet) ;</item>
/// <item>les écritures manuelles en attente partent tout de suite, pour ne pas écraser le groupe ensuite ;</item>
/// <item>bail : celui du demandeur, vérifié, ou pris le temps de l'application ; tenu par un autre, rien n'est écrit ;</item>
/// <item>les ventilateurs pas encore relevés sont attendus (délai borné), puis le bail est revérifié ;</item>
/// <item>chaque partie est planifiée sur l'état relu, puis l'ordre se décide sur la limite de puissance visée ;</item>
/// <item>une partie risquée (OC GPU, watts au-delà de l'origine) ouvre sa période probatoire avant toute écriture, et
/// n'est pas posée si la ligne du journal n'a pas pu être écrite ;</item>
/// <item>application dans l'ordre, rapport par dimension, état retenu.</item>
/// </list>
/// À appeler sur le fil d'interface (les onglets y vivent) : les attentes reprennent sur ce même fil.
/// </summary>
public sealed class ProfileGroupApplier
{
    public static readonly TimeSpan DefaultFansWait = TimeSpan.FromSeconds(20);

    public const string PermanentNote =
        "Réglages du plan d'alimentation : permanents, Windows les garde après la fermeture de PCPerfSuite. Leur origine est notée, et « Tout rétablir » la rend.";

    public const string TransientNote =
        "Appliqué sans en faire l'état de démarrage : ces réglages sont rendus d'origine à la fermeture de l'app, et le lancement suivant reprend l'état enregistré dans les onglets.";

    private readonly ICpuGroupTarget _cpu;
    private readonly IGpuGroupTarget _gpu;
    private readonly IFanGroupTarget _fans;
    private readonly TuningLease _lease;
    private readonly TimeProvider _time;

    public ProfileGroupApplier(
        ICpuGroupTarget cpu, IGpuGroupTarget gpu, IFanGroupTarget fans, TuningLease lease, ProfileGroupProbation probation, TimeProvider time)
    {
        _cpu = cpu;
        _gpu = gpu;
        _fans = fans;
        _lease = lease;
        Probation = probation;
        _time = time;
    }

    public ProfileGroupProbation Probation { get; }

    /// <summary>Un réglage relevé par l'app reste en place (watts, OC GPU).</summary>
    public bool AnyRaised => _cpu.IsRaised || _gpu.IsRaised;

    public async Task<ProfileGroupApplyResult> ApplyAsync(ProfileGroup source, ProfileGroupApplyOptions options, CancellationToken cancellationToken = default)
    {
        ProfileGroup group = source.Clone();
        if (group.IsEmpty) return Refused(group, "ce groupe ne touche à rien.");

        // Avant le bail : une écriture manuelle encore en attente part maintenant, au lieu d'écraser le groupe ensuite ou
        // d'être perdue sous le bail.
        _fans.FlushPendingManualWrites();
        _cpu.FlushPendingManualWrites();
        _gpu.FlushPendingManualWrites();

        TuningLeaseHandle? acquired = null;
        TuningLeaseHandle? handle = options.Lease;
        if (handle is not null)
        {
            if (!_lease.IsHeldBy(handle)) return Refused(group, _lease.RefusalText(handle) ?? "le bail de réglage n'est plus tenu.");
        }
        else
        {
            TuningLeaseResult result = _lease.TryAcquire(options.RequesterId, options.RequesterLabel, $"application du groupe « {group.Name} »");
            if (!result.Acquired) return Refused(group, _lease.RefusalText(null) ?? "réglages pilotés par un autre demandeur.");
            handle = acquired = result.Handle;
        }

        try
        {
            if (group.Fans is not null && !_fans.ReadState().IsReady)
            {
                try
                {
                    await _fans.WhenReady.WaitAsync(options.FansWait ?? DefaultFansWait, _time, cancellationToken);
                }
                catch (TimeoutException)
                {
                    // La partie ventilation dira qu'aucun relevé n'est arrivé ; le reste s'applique.
                }

                if (!_lease.IsHeldBy(handle))
                {
                    return Refused(group, _lease.RefusalText(handle) ?? "le bail de réglage a été perdu pendant l'attente des ventilateurs.");
                }
            }

            return Apply(group, options, handle);
        }
        finally
        {
            acquired?.Dispose();
        }
    }

    private ProfileGroupApplyResult Apply(ProfileGroup group, ProfileGroupApplyOptions options, TuningLeaseHandle? handle)
    {
        DateTimeOffset now = _time.GetUtcNow();
        var context = new ProfileGroupApplyContext(options.RequesterId, options.IsManual, options.MakeStartupState, handle);

        CpuGroupPlan? cpuPlan = group.Cpu is { } cpuPart ? CpuGroupPlanner.Plan(cpuPart, _cpu.ReadState(), options.IsManual) : null;
        GpuGroupPlan? gpuPlan = group.Gpu is { } gpuPart ? GpuGroupPlanner.Plan(gpuPart, _gpu.ReadState(), options.IsManual) : null;
        FanGroupPlan? fanPlan = group.Fans is { } fanPart ? FanGroupPlanner.Plan(fanPart, _fans.ReadState(), _fans.AbsenceReason) : null;

        var trends = new List<PowerTrend>();
        if (cpuPlan is not null) trends.Add(cpuPlan.Trend);
        if (gpuPlan is not null) trends.Add(gpuPlan.Trend);
        ApplyOrder order = ApplyDirection.Decide(trends);

        bool gpuRisk = gpuPlan?.Raises == true;
        bool wattsRisk = cpuPlan?.RaisesWatts == true;
        bool probationOpen = false;
        if (gpuRisk || wattsRisk)
        {
            // Ce que la période en cours surveille et que ce groupe ne règle pas reste en place : la nouvelle le reprend.
            ProbationCarry? carried = Probation.CarryOver(
                touchesGpu: gpuPlan?.TouchesCard == true,
                touchesWatts: cpuPlan is { TouchesWatts: true } or { RestoreWatts: true },
                gpuRaisedNow: _gpu.IsRaised,
                wattsRaisedNow: _cpu.IsRaised);
            probationOpen = Probation.Begin(group.Id, ProfileGroupProbation.ApplyAction, gpuRisk, wattsRisk, options.MakeStartupState,
                options.RequesterId, carried);
            if (!probationOpen)
            {
                // Règle du journal de session : une opération risquée ne commence pas sans sa ligne sur le disque.
                string reason = $"le journal de session n'a pas pu être écrit ({Probation.LastProblem}) : sans lui, la prudence au démarrage ne pourrait pas jouer";
                if (gpuRisk) gpuPlan = gpuPlan!.WithoutRequest(ReportItem.Refused(DimensionReport.Title(ProfileDimension.Gpu), $"overclock non posé : {reason}"));
                if (wattsRisk) cpuPlan = cpuPlan!.WithoutWatts(ReportItem.Refused(CpuGroupPlanner.WattsLabel, $"watts relevés non posés : {reason}"));
                gpuRisk = wattsRisk = false;
            }
        }

        var reports = new List<DimensionReport>();
        FanApplyOutcome? fanOutcome = order == ApplyOrder.FansFirst ? ApplyFans(fanPlan, context, reports) : null;
        CpuApplyOutcome? cpuOutcome = ApplyCpu(cpuPlan, context, reports);
        GpuApplyOutcome? gpuOutcome = ApplyGpu(gpuPlan, context, reports);
        if (order != ApplyOrder.FansFirst) fanOutcome = ApplyFans(fanPlan, context, reports);

        bool gpuRaisedNow = _gpu.IsRaised;
        bool wattsRaisedNow = _cpu.IsRaised;
        if (probationOpen) Probation.AfterApplication(gpuRaisedNow || wattsRaisedNow);
        else Probation.NoteReplaced(gpuRaisedNow || wattsRaisedNow);

        var notes = new List<string>();
        if (cpuOutcome?.Report.IsPermanent == true) notes.Add(PermanentNote);
        if (!options.MakeStartupState) notes.Add(TransientNote);

        var report = new ProfileGroupReport(group.Id, group.Name, now, order, reports, null, notes);
        if (!report.AnyLanded) return new ProfileGroupApplyResult(report, null);

        var active = new ProfileGroupActiveState
        {
            GroupId = group.Id,
            SinceUtc = now,
            Revision = group.Revision,
            MadeStartupState = options.MakeStartupState,
            RequesterId = options.RequesterId,
            Cpu = cpuOutcome?.Retained,
            Gpu = gpuOutcome?.Retained,
            Fans = fanOutcome?.Retained,
            GpuRaised = gpuRisk && gpuRaisedNow,
            WattsRaised = wattsRisk && wattsRaisedNow,
        };

        return new ProfileGroupApplyResult(report, active);
    }

    private FanApplyOutcome? ApplyFans(FanGroupPlan? plan, ProfileGroupApplyContext context, List<DimensionReport> reports)
    {
        if (plan is null) return null;
        FanApplyOutcome outcome = _fans.Apply(plan, context);
        reports.Add(outcome.Report);
        return outcome;
    }

    private CpuApplyOutcome? ApplyCpu(CpuGroupPlan? plan, ProfileGroupApplyContext context, List<DimensionReport> reports)
    {
        if (plan is null) return null;
        CpuApplyOutcome outcome = _cpu.Apply(plan, context);
        reports.Add(outcome.Report);
        return outcome;
    }

    private GpuApplyOutcome? ApplyGpu(GpuGroupPlan? plan, ProfileGroupApplyContext context, List<DimensionReport> reports)
    {
        if (plan is null) return null;
        GpuApplyOutcome outcome = _gpu.Apply(plan, context);
        reports.Add(outcome.Report);
        return outcome;
    }

    private ProfileGroupApplyResult Refused(ProfileGroup group, string reason)
        => new(ProfileGroupReport.Refused(group, _time.GetUtcNow(), reason), null);
}
