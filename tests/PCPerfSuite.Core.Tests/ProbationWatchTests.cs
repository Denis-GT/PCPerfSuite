using PCPerfSuite.Core.Compatibility;
using PCPerfSuite.Core.Profiles;
using PCPerfSuite.Core.Safety.Events;

namespace PCPerfSuite.Core.Tests;

/// <summary>La surveillance en session de la période probatoire : ce qu'elle fait de chaque lecture du journal Système.</summary>
public sealed class ProbationWatchTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 20, 0, 0, TimeSpan.Zero);

    private static ProbationInfo Period(bool gpu = true, ProbationCarry? carried = null)
        => new("g1", ProfileGroupProbation.ApplyAction, T0, T0 + ProfileGroupProbation.Window, gpu, !gpu, true, carried);

    private static readonly DateTimeOffset During = T0 + TimeSpan.FromMinutes(10);
    private static readonly DateTimeOffset AtTerm = T0 + ProfileGroupProbation.Window;

    private static SystemEventReadResult Events(params SystemEventRecord[] records) => new(records, null);

    private static SystemEventRecord Tdr(DateTimeOffset at) => new(SystemEventKind.DisplayDriverReset, 4101, at);

    [Fact]
    public void Un_tdr_pendant_la_periode_l_echoue()
    {
        ProbationTickDecision decision = ProbationWatch.Decide(Period(), During, Events(Tdr(T0 + TimeSpan.FromMinutes(8))));

        Assert.Equal(ProbationTickAction.Tdr, decision.Action);
        Assert.NotNull(decision.Tdr);
    }

    [Fact]
    public void Au_terme_sans_tdr_la_periode_est_terminee()
        => Assert.Equal(ProbationTickAction.Close, ProbationWatch.Decide(Period(), AtTerm, Events()).Action);

    [Fact]
    public void Une_lecture_ratee_n_est_jamais_prise_pour_aucun_tdr()
    {
        var failed = SystemEventReadResult.Failed(UnavailableCause.MissingRights, "lecture du journal Système refusée");

        Assert.Equal(ProbationTickAction.Wait, ProbationWatch.Decide(Period(), During, failed).Action);

        ProbationTickDecision atTerm = ProbationWatch.Decide(Period(), AtTerm, failed);
        Assert.Equal(ProbationTickAction.CloseUnverified, atTerm.Action);
        Assert.Equal("lecture du journal Système refusée", atTerm.Why);
    }

    [Fact]
    public void Une_lecture_qui_a_leve_est_une_lecture_ratee()
    {
        ProbationTickDecision decision = ProbationWatch.Decide(Period(), AtTerm, null);

        Assert.Equal(ProbationTickAction.CloseUnverified, decision.Action);
        Assert.Equal(ProbationWatch.ReadFailed, decision.Why);
    }

    [Fact]
    public void Sans_oc_gpu_seul_le_terme_compte()
    {
        Assert.False(ProbationWatch.NeedsEvents(Period(gpu: false)));
        Assert.Equal(ProbationTickAction.Wait, ProbationWatch.Decide(Period(gpu: false), During, null).Action);
        Assert.Equal(ProbationTickAction.Close, ProbationWatch.Decide(Period(gpu: false), AtTerm, null).Action);
    }

    [Fact]
    public void Sur_un_tdr_c_est_le_groupe_a_qui_revient_l_oc_qui_est_suspendu()
    {
        ProbationCarry own = ProbationWatch.TdrOwner(Period(gpu: true));
        ProbationCarry carried = ProbationWatch.TdrOwner(Period(gpu: false, carried: new ProbationCarry("oc", true, false, true, AutoSwitchRequester.Id)));

        Assert.Equal("g1", own.GroupId);
        Assert.True(own.GpuRaised);
        Assert.Equal("oc", carried.GroupId);
        Assert.Equal(AutoSwitchRequester.Id, carried.RequesterId);
    }

    [Fact]
    public void Un_oc_gpu_repris_d_un_groupe_precedent_est_surveille()
    {
        ProbationInfo period = Period(gpu: false, carried: new ProbationCarry("oc", true, false, true));

        Assert.True(ProbationWatch.NeedsEvents(period));
        Assert.Equal(ProbationTickAction.Tdr, ProbationWatch.Decide(period, During, Events(Tdr(During))).Action);
        Assert.Equal("oc", period.GpuOwner!.GroupId);
    }
}
