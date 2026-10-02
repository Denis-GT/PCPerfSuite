using PCPerfSuite.Core.Hardware;
using PCPerfSuite.Core.Hardware.Gpu;
using PCPerfSuite.Core.Profiles;
using PCPerfSuite.Core.Safety;
using static PCPerfSuite.Core.Tests.ProfileGroupTestData;

namespace PCPerfSuite.Core.Tests;

/// <summary>Ce que la partie carte graphique d'un groupe peut poser sur cette carte.</summary>
public class GpuGroupPlannerTests
{
    private static GpuOverclockProfile Oc(int core = 150, int memory = 500, float? power = null, int? temperature = null,
        int? voltage = null, GpuVoltageUnit? unit = null)
        => new()
        {
            CoreClockOffsetMhz = core,
            MemoryClockOffsetMhz = memory,
            PowerLimitPercent = power,
            TemperatureLimitC = temperature,
            VoltageValue = voltage,
            VoltageUnit = unit,
        };

    private static string Texts(GpuGroupPlan plan) => string.Join(" | ", plan.Items.Select(i => i.Text).Concat(plan.Notes));

    [Fact]
    public void Sans_carte_pilotable_rien_n_est_pose()
    {
        GpuGroupPlan plan = GpuGroupPlanner.Plan(GpuPart(Oc()), Gpu(available: false), isManual: true);

        Assert.False(plan.HasWork);
        Assert.Contains("aucune carte pilotable : aucun pilote ne répond", Texts(plan));
    }

    [Fact]
    public void Sans_la_renonciation_intel_rien_n_est_pose()
    {
        GpuGroupPlan plan = GpuGroupPlanner.Plan(GpuPart(Oc()), Gpu(canOverclock: false), isManual: true);

        Assert.False(plan.HasWork);
        Assert.Contains("renonciation de garantie Intel", Texts(plan));
    }

    [Fact]
    public void Une_autre_carte_de_la_meme_marque_ne_recoit_rien()
    {
        var other = new GpuIdentity(GpuVendor.Nvidia, "NVIDIA GeForce RTX 4070", 0x10DE, 0x2786);

        GpuGroupPlan plan = GpuGroupPlanner.Plan(GpuPart(Oc(), other), Gpu(), isManual: true);

        Assert.False(plan.HasWork);
        Assert.Contains("relevés sur une autre carte (NVIDIA GeForce RTX 4070", Texts(plan));
    }

    [Fact]
    public void La_meme_puce_chez_un_autre_fabricant_ne_recoit_rien()
    {
        GpuGroupPlan plan = GpuGroupPlanner.Plan(GpuPart(Oc(), Rtx with { PciSubsystemId = 0x51721462 }), Gpu(), isManual: true);

        Assert.False(plan.HasWork);
    }

    [Fact]
    public void Une_carte_d_origine_inconnue_ne_recoit_rien()
    {
        GpuGroupPlan plan = GpuGroupPlanner.Plan(new ProfileGroupGpuPart { Values = Oc() }, Gpu(), isManual: true);

        Assert.False(plan.HasWork);
        Assert.Contains("une carte non identifiée", Texts(plan));
    }

    [Fact]
    public void Les_valeurs_sont_bornees_a_la_plage_de_la_carte()
    {
        GpuGroupPlan plan = GpuGroupPlanner.Plan(GpuPart(Oc(core: 400, power: 150)), Gpu(), isManual: true);

        Assert.Equal(250, plan.Request!.CoreOffsetMhz);
        Assert.Equal(110, plan.Request.PowerLimitPercent);
        Assert.Contains("cœur ramené de +400 MHz à +250 MHz", Texts(plan));
        Assert.Contains("puissance ramenée de 150 % à 110 %", Texts(plan));
    }

    [Fact]
    public void Une_tension_dans_une_autre_unite_est_ignoree()
    {
        GpuGroupPlan plan = GpuGroupPlanner.Plan(GpuPart(Oc(voltage: 50, unit: GpuVoltageUnit.Millivolts)), Gpu(), isManual: true);

        Assert.Null(plan.Request!.Voltage);
        Assert.Contains("enregistrée en mV, cette carte la règle en %", Texts(plan));
    }

    [Fact]
    public void Une_tension_dans_la_meme_unite_est_posee()
    {
        GpuGroupPlan plan = GpuGroupPlanner.Plan(GpuPart(Oc(voltage: 20, unit: GpuVoltageUnit.Percent)), Gpu(), isManual: true);

        Assert.Equal(20, plan.Request!.Voltage);
    }

    [Fact]
    public void Un_reglage_que_la_carte_n_expose_pas_est_signale()
    {
        var noMemory = new GpuOverclockSnapshot
        {
            CoreOffsetSupported = true,
            CoreOffsetMinMhz = -500,
            CoreOffsetMaxMhz = 250,
            MemoryOffsetSupported = false,
        };

        GpuGroupPlan plan = GpuGroupPlanner.Plan(GpuPart(Oc(core: 100, memory: 500, temperature: 85)), Gpu(overclock: noMemory), isManual: true);

        Assert.Null(plan.Request!.MemoryOffsetMhz);
        Assert.Contains("N/D sur cette carte, non posé : fréquence mémoire, limite de température", Texts(plan));
    }

    [Fact]
    public void Ce_qui_est_deja_en_place_n_est_pas_reecrit()
    {
        GpuGroupPlan plan = GpuGroupPlanner.Plan(GpuPart(Oc(core: 150, memory: 500, power: 100)), Gpu(Overclock(core: 150, memory: 500)), isManual: true);

        Assert.Null(plan.Request);
        Assert.False(plan.HasWork);
        Assert.Contains("déjà en place : cœur, mémoire, puissance", Texts(plan));
    }

    [Fact]
    public void L_etat_retenu_ne_garde_que_ce_que_le_groupe_regle()
    {
        GpuGroupPlan plan = GpuGroupPlanner.Plan(GpuPart(Oc(core: 150, memory: 0)), Gpu(), isManual: true);

        GpuRetainedValues retained = plan.Retain(new GpuRetainedValues { CoreOffsetMhz = 150, MemoryOffsetMhz = 0, PowerLimitPercent = 100, TemperatureLimitC = 83 });

        Assert.Equal(150, retained.CoreOffsetMhz);
        Assert.Equal(0, retained.MemoryOffsetMhz);
        Assert.Null(retained.PowerLimitPercent);
        Assert.Null(retained.TemperatureLimitC);
    }

    [Fact]
    public void Un_overclock_est_une_montee_risquee()
    {
        GpuGroupPlan plan = GpuGroupPlanner.Plan(GpuPart(Oc(core: 150)), Gpu(), isManual: true);

        Assert.Equal(PowerTrend.Up, plan.Trend);
        Assert.True(plan.Raises);
    }

    [Fact]
    public void Baisser_la_puissance_et_sous_cadencer_est_une_descente()
    {
        GpuGroupPlan plan = GpuGroupPlanner.Plan(GpuPart(Oc(core: -100, memory: 0, power: 80)), Gpu(), isManual: true);

        Assert.Equal(PowerTrend.Down, plan.Trend);
        Assert.False(plan.Raises);
    }

    [Fact]
    public void Apres_une_securite_thermique_un_pilote_automatique_ne_reposse_pas_l_overclock()
    {
        GpuGroupPlan automatic = GpuGroupPlanner.Plan(GpuPart(Oc(core: 150)), Gpu(emergency: true), isManual: false);
        GpuGroupPlan manual = GpuGroupPlanner.Plan(GpuPart(Oc(core: 150)), Gpu(emergency: true), isManual: true);

        Assert.Null(automatic.Request);
        Assert.Contains(automatic.Items, i => i.Status == ReportItemStatus.Refused);
        Assert.NotNull(manual.Request);
    }

    [Fact]
    public void L_origine_retire_l_overclock_en_descente()
    {
        GpuGroupPlan plan = GpuGroupPlanner.Plan(new ProfileGroupGpuPart { Kind = ProfilePartKinds.Origin }, Gpu(Overclock(core: 150), Power(110)), isManual: false);

        Assert.True(plan.RestoreOrigin);
        Assert.Equal(PowerTrend.Down, plan.Trend);
        Assert.False(plan.Raises);
    }

    [Fact]
    public void L_origine_sans_carte_ne_fait_rien()
    {
        GpuGroupPlan plan = GpuGroupPlanner.Plan(new ProfileGroupGpuPart { Kind = ProfilePartKinds.Origin }, Gpu(available: false), isManual: true);

        Assert.False(plan.HasWork);
    }

    [Fact]
    public void Une_demande_au_dessus_de_l_origine_est_relevee()
    {
        Assert.True(GpuOverclockRaise.IsRaising(new GpuOverclockRequest { CoreOffsetMhz = 1 }, Overclock(), Power()));
        Assert.True(GpuOverclockRaise.IsRaising(new GpuOverclockRequest { TemperatureLimitC = 88 }, Overclock(), Power()));
        Assert.True(GpuOverclockRaise.IsRaising(new GpuOverclockRequest { PowerLimitPercent = 105 }, Overclock(), null));
        Assert.False(GpuOverclockRaise.IsRaising(new GpuOverclockRequest { PowerLimitPercent = 100.3f, CoreOffsetMhz = -50 }, Overclock(), Power()));
        Assert.False(GpuOverclockRaise.IsRaising(new GpuOverclockRequest { TemperatureLimitC = 80 }, Overclock(), Power()));
    }

    // ---- Ce que l'onglet enregistre (D7) ----

    [Fact]
    public void Apres_un_groupe_transitoire_un_curseur_n_enregistre_que_son_reglage()
    {
        // La bascule a posé le GPU d'origine sans en faire l'état de démarrage, puis la puissance est montée à la main :
        // l'OC enregistré (cœur, mémoire) ne doit pas devenir +0.
        GpuTouched power = GpuTouched.None with { Power = true };

        GpuTouched saved = GpuTouched.ToSave(cardAvailable: true, transient: true, power);

        Assert.True(saved.Power);
        Assert.False(saved.Core);
        Assert.False(saved.Memory);
    }

    [Fact]
    public void Les_reglages_touches_depuis_le_groupe_transitoire_s_additionnent()
    {
        GpuTouched touched = (GpuTouched.None with { Power = true }).Union(GpuTouched.None with { Voltage = true });

        Assert.Equal(GpuTouched.None with { Power = true, Voltage = true }, GpuTouched.ToSave(true, true, touched));
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    public void Hors_etat_transitoire_tout_est_enregistre_si_la_carte_repond(bool available, bool transient, bool all)
        => Assert.Equal(all ? GpuTouched.All : GpuTouched.None, GpuTouched.ToSave(available, transient, GpuTouched.None));
}
