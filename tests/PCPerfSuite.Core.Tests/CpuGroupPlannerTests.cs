using PCPerfSuite.Core.Hardware.Cpu;
using PCPerfSuite.Core.Profiles;
using static PCPerfSuite.Core.Tests.ProfileGroupTestData;

namespace PCPerfSuite.Core.Tests;

/// <summary>Ce que la partie processeur d'un groupe peut poser sur ce PC, et pourquoi le reste est écarté.</summary>
public class CpuGroupPlannerTests
{
    private static CpuProfile Values(uint? boost = null, uint? epp = null, float? sustained = null, float? burst = null)
    {
        var profile = new CpuProfile { SustainedWatts = sustained, BurstWatts = burst };
        if (boost is not null) profile.PowerSettings["boost"] = new CpuProfilePowerValue { Ac = boost };
        if (epp is not null) profile.PowerSettings["epp"] = new CpuProfilePowerValue { Ac = epp };
        return profile;
    }

    private static string Texts(CpuGroupPlan plan) => string.Join(" | ", plan.Items.Select(i => i.Text));

    [Fact]
    public void Seuls_les_reglages_qui_changent_sont_ecrits()
    {
        CpuGroupPlan plan = CpuGroupPlanner.Plan(CpuPart(Values(boost: 1, epp: 80)), Cpu(boost: 1, epp: 50), isManual: true);

        CpuPlanWrite write = Assert.Single(plan.PlanWrites);
        Assert.Equal("epp", write.Setting.Id);
        Assert.Equal(80u, write.Ac);
        Assert.Equal(80u, write.Dc);
        Assert.Contains("1 réglage du plan déjà en place", Texts(plan));
        Assert.True(plan.TouchesPlan);
    }

    [Fact]
    public void Ce_que_le_groupe_regle_est_retenu_meme_deja_en_place()
    {
        CpuGroupPlan plan = CpuGroupPlanner.Plan(CpuPart(Values(boost: 1, epp: 80, sustained: 125, burst: 181)), Cpu(boost: 1, epp: 50), isManual: true);

        Assert.Equal(["boost", "epp"], plan.TouchedSettings!.Select(s => s.Id).Order());
        Assert.True(plan.TouchesWatts);
        Assert.Null(plan.Watts);
    }

    [Fact]
    public void Un_reglage_venu_d_un_autre_pc_est_ignore_et_compte()
    {
        CpuProfile values = Values(epp: 80);
        values.PowerSettings["max-frequency-perf"] = new CpuProfilePowerValue { Ac = 4000 };
        values.PowerSettings["futur"] = new CpuProfilePowerValue { Ac = 1 };

        CpuGroupPlan plan = CpuGroupPlanner.Plan(CpuPart(values), Cpu(), isManual: true);

        Assert.Single(plan.PlanWrites);
        Assert.Contains("2 réglages du plan absents de ce PC, ignorés", Texts(plan));
    }

    [Fact]
    public void Une_option_absente_de_ce_pc_est_refusee_plutot_que_rabotee()
    {
        CpuGroupPlan plan = CpuGroupPlanner.Plan(CpuPart(Values(boost: 5)), Cpu(), isManual: true);

        Assert.Empty(plan.PlanWrites);
        Assert.Contains("« Mode boost » : valeur 5 absente de ce PC, ignorée", Texts(plan));
    }

    [Fact]
    public void Une_valeur_numerique_est_bornee()
    {
        CpuGroupPlan plan = CpuGroupPlanner.Plan(CpuPart(Values(epp: 150)), Cpu(), isManual: true);

        Assert.Equal(100u, Assert.Single(plan.PlanWrites).Ac);
    }

    [Fact]
    public void Sur_un_portable_la_valeur_batterie_absente_reste_telle_quelle()
    {
        CpuGroupPlan plan = CpuGroupPlanner.Plan(CpuPart(Values(epp: 80)), Cpu(battery: true, eppDc: 70), isManual: true);

        CpuPlanWrite write = Assert.Single(plan.PlanWrites);
        Assert.Equal(80u, write.Ac);
        Assert.Equal(70u, write.Dc);
    }

    [Fact]
    public void Des_watts_sans_avertissement_accepte_sont_ignores()
    {
        CpuGroupPlan plan = CpuGroupPlanner.Plan(CpuPart(Values(sustained: 65)), Cpu(accepted: false), isManual: true);

        Assert.Null(plan.Watts);
        Assert.Contains("watts ignorées : l'avertissement de l'onglet Processeur n'a pas été accepté", Texts(plan));
    }

    [Fact]
    public void Des_watts_en_lecture_seule_donnent_la_raison_du_backend()
    {
        CpuGroupPlan plan = CpuGroupPlanner.Plan(CpuPart(Values(sustained: 65)), Cpu(writable: false), isManual: true);

        Assert.Null(plan.Watts);
        Assert.Contains("verrouillées par le BIOS", Texts(plan));
    }

    [Fact]
    public void Des_watts_d_un_autre_processeur_sont_ignores()
    {
        var other = I5 with { Name = "Intel(R) Core(TM) i5-13500T" };

        CpuGroupPlan plan = CpuGroupPlanner.Plan(CpuPart(Values(sustained: 65), other), Cpu(), isManual: true);

        Assert.Null(plan.Watts);
        Assert.Contains("relevées sur un autre processeur (Intel(R) Core(TM) i5-13500T)", Texts(plan));
    }

    [Fact]
    public void Des_watts_sans_processeur_d_origine_sont_ignores()
    {
        var part = new ProfileGroupCpuPart { Values = Values(sustained: 65), CapturedOn = null };

        CpuGroupPlan plan = CpuGroupPlanner.Plan(part, Cpu(), isManual: true);

        Assert.Null(plan.Watts);
        Assert.Contains("un processeur non identifié", Texts(plan));
    }

    [Fact]
    public void La_valeur_sans_limite_du_bios_n_est_jamais_reposee()
    {
        CpuGroupPlan plan = CpuGroupPlanner.Plan(CpuPart(Values(sustained: 4095)), Cpu(), isManual: true);

        Assert.Null(plan.Watts);
        Assert.Contains("« sans limite »", Texts(plan));
    }

    [Fact]
    public void Des_watts_hors_plage_sont_bornes_et_le_rapport_le_dit()
    {
        CpuGroupPlan plan = CpuGroupPlanner.Plan(CpuPart(Values(sustained: 5)), Cpu(), isManual: true);

        Assert.Equal(15, plan.Watts!.Sustained);
        Assert.Contains(plan.Items, i => i.Status == ReportItemStatus.Trimmed);
    }

    [Fact]
    public void La_pointe_ne_passe_jamais_sous_la_limite_soutenue()
    {
        CpuGroupPlan plan = CpuGroupPlanner.Plan(CpuPart(Values(sustained: 150, burst: 100)), Cpu(), isManual: true);

        Assert.Equal(150, plan.Watts!.Burst);
    }

    [Fact]
    public void Baisser_les_watts_est_une_descente_sans_risque()
    {
        CpuGroupPlan plan = CpuGroupPlanner.Plan(CpuPart(Values(sustained: 65, burst: 90)), Cpu(), isManual: true);

        Assert.Equal(PowerTrend.Down, plan.Trend);
        Assert.False(plan.RaisesWatts);
    }

    [Fact]
    public void Relever_les_watts_au_dela_de_l_origine_est_risque()
    {
        CpuGroupPlan plan = CpuGroupPlanner.Plan(CpuPart(Values(sustained: 150, burst: 200)), Cpu(), isManual: true);

        Assert.Equal(PowerTrend.Up, plan.Trend);
        Assert.True(plan.RaisesWatts);
    }

    [Fact]
    public void Remonter_vers_l_origine_n_est_pas_risque()
    {
        CpuGroupPlan plan = CpuGroupPlanner.Plan(CpuPart(Values(sustained: 125, burst: 181)), Cpu(watts: Watts(65, 90)), isManual: true);

        Assert.Equal(PowerTrend.Up, plan.Trend);
        Assert.False(plan.RaisesWatts);
    }

    [Fact]
    public void Un_watt_de_plus_que_l_origine_n_est_pas_une_hausse()
    {
        CpuGroupPlan plan = CpuGroupPlanner.Plan(CpuPart(Values(sustained: 126, burst: 181)), Cpu(), isManual: true);

        Assert.False(plan.RaisesWatts);
    }

    [Fact]
    public void Des_watts_deja_en_place_ne_sont_pas_reecrits()
    {
        CpuGroupPlan plan = CpuGroupPlanner.Plan(CpuPart(Values(sustained: 125, burst: 181)), Cpu(), isManual: true);

        Assert.Null(plan.Watts);
        Assert.Contains("limites déjà à 125 W", Texts(plan));
        Assert.Equal(PowerTrend.Same, plan.Trend);
    }

    [Fact]
    public void Apres_une_securite_thermique_un_pilote_automatique_ne_releve_pas_les_watts()
    {
        CpuGroupPlan automatic = CpuGroupPlanner.Plan(CpuPart(Values(sustained: 150)), Cpu(emergency: true), isManual: false);
        CpuGroupPlan manual = CpuGroupPlanner.Plan(CpuPart(Values(sustained: 150)), Cpu(emergency: true), isManual: true);
        CpuGroupPlan lowering = CpuGroupPlanner.Plan(CpuPart(Values(sustained: 65)), Cpu(emergency: true), isManual: false);

        Assert.Null(automatic.Watts);
        Assert.Contains(automatic.Items, i => i.Status == ReportItemStatus.Refused);
        Assert.NotNull(manual.Watts);
        Assert.NotNull(lowering.Watts);
    }

    [Fact]
    public void L_origine_rend_les_watts_et_le_plan()
    {
        CpuGroupPlan plan = CpuGroupPlanner.Plan(new ProfileGroupCpuPart { Kind = ProfilePartKinds.Origin }, Cpu(watts: Watts(150, 200)), isManual: false);

        Assert.True(plan.RestorePlanOrigin);
        Assert.True(plan.RestoreWatts);
        Assert.Equal(PowerTrend.Down, plan.Trend);
        Assert.False(plan.RaisesWatts);
    }

    [Fact]
    public void L_origine_sans_ecriture_possible_garde_le_plan_et_dit_pourquoi()
    {
        CpuGroupPlan plan = CpuGroupPlanner.Plan(new ProfileGroupCpuPart { Kind = ProfilePartKinds.Origin }, Cpu(writable: false), isManual: true);

        Assert.True(plan.RestorePlanOrigin);
        Assert.False(plan.RestoreWatts);
        Assert.Contains("verrouillées par le BIOS", Texts(plan));
    }

    [Fact]
    public void Une_partie_d_une_version_plus_recente_est_ignoree()
    {
        CpuGroupPlan plan = CpuGroupPlanner.Plan(new ProfileGroupCpuPart { Kind = "demi-mesure", Values = Values(epp: 10) }, Cpu(), isManual: true);

        Assert.False(plan.HasWork);
        Assert.Contains("version plus récente", Texts(plan));
    }

    [Fact]
    public void Retirer_les_watts_garde_le_plan()
    {
        CpuGroupPlan plan = CpuGroupPlanner.Plan(CpuPart(Values(epp: 80, sustained: 150)), Cpu(), isManual: true);

        CpuGroupPlan reduced = plan.WithoutWatts(ReportItem.Refused(CpuGroupPlanner.WattsLabel, "journal indisponible"));

        Assert.Null(reduced.Watts);
        Assert.False(reduced.RaisesWatts);
        Assert.Single(reduced.PlanWrites);
        Assert.Contains("journal indisponible", Texts(reduced));
    }
}
