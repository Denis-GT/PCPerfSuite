using PCPerfSuite.Core.Hardware;
using PCPerfSuite.Core.Hardware.Cpu;
using PCPerfSuite.Core.Profiles;
using static PCPerfSuite.Core.Tests.ProfileGroupTestData;

namespace PCPerfSuite.Core.Tests;

/// <summary>
/// « Enregistrer l'état actuel » et « Mettre à jour le groupe » : ce que les onglets capturent porte des règles de
/// sécurité (jamais la valeur « sans limite » du BIOS, pas de watts sans l'avertissement accepté, rien d'un réglage que la
/// carte n'expose pas). Et le compte rendu d'une écriture du plan d'alimentation par un groupe.
/// </summary>
public sealed class ProfileGroupCaptureTests
{
    // ---- Processeur ----

    [Theory]
    [InlineData(true, true, 125f, 125f)]
    [InlineData(false, true, 125f, null)]
    [InlineData(true, false, 125f, null)]
    [InlineData(true, true, CpuMaxWattsResolver.UnlimitedWatts, null)]
    public void L_etat_capture_n_a_de_watts_qu_ecrivables_acceptes_et_bornes(bool accepted, bool writable, float sustained, float? expected)
    {
        CpuTargetState state = Cpu(watts: Watts(sustained: sustained), accepted: accepted, writable: writable);

        CpuProfile profile = state.ToProfile("Jeu");

        Assert.Equal(expected, profile.SustainedWatts);
        Assert.Equal("Jeu", profile.Name);
        Assert.Equal(2, profile.PowerSettings.Count);
    }

    [Fact]
    public void Une_limite_courte_sans_limite_n_est_pas_capturee()
    {
        CpuProfile profile = Cpu(watts: Watts(sustained: 125, burst: CpuMaxWattsResolver.UnlimitedWatts)).ToProfile("Jeu");

        Assert.Equal(125f, profile.SustainedWatts);
        Assert.Null(profile.BurstWatts);
    }

    [Fact]
    public void La_valeur_sur_batterie_n_est_gardee_que_sur_un_portable()
    {
        CpuProfile desktop = Cpu(battery: false).ToProfile("Bureau");
        CpuProfile laptop = Cpu(battery: true, eppDc: 70).ToProfile("Portable");

        Assert.Null(desktop.PowerSettings["epp"].Battery);
        Assert.Equal(70u, laptop.PowerSettings["epp"].Battery);
    }

    // ---- Carte graphique ----

    [Fact]
    public void Un_reglage_que_la_carte_n_expose_pas_est_capture_a_zero_et_retenu_nul()
    {
        var overclock = new GpuOverclockSnapshot { CoreOffsetSupported = false, CoreOffsetMhz = 250, MemoryOffsetSupported = true, MemoryOffsetMhz = 500 };
        GpuTargetState state = Gpu(overclock: overclock, power: Power(110));

        GpuOverclockProfile profile = state.ToProfile("Jeu");
        GpuRetainedValues retained = state.ToRetained();

        Assert.Equal(0, profile.CoreClockOffsetMhz);
        Assert.Equal(500, profile.MemoryClockOffsetMhz);
        Assert.Equal(110f, profile.PowerLimitPercent);
        Assert.Null(profile.TemperatureLimitC);
        Assert.Null(profile.VoltageValue);
        Assert.Null(retained.CoreOffsetMhz);
        Assert.Equal(500, retained.MemoryOffsetMhz);
    }

    // ---- Identité et diagnostic ----

    [Fact]
    public void Sans_nom_de_processeur_la_ligne_du_diagnostic_dit_que_les_watts_ne_se_posent_pas()
    {
        var unnamed = new CpuIdentity { Vendor = "Intel", Family = 6, Model = 183 };

        Assert.False(unnamed.HasName);
        Assert.False(CpuIdentityRowProvider.BuildRow(unnamed).IsSupported);
        Assert.Contains("aucune limite en watts", CpuIdentityRowProvider.BuildRow(unnamed).Detail);
        Assert.True(CpuIdentityRowProvider.BuildRow(I5).IsSupported);
    }

    [Fact]
    public void Sans_nom_de_processeur_le_refus_des_watts_ne_parle_pas_d_un_autre_processeur()
    {
        var unnamed = new CpuIdentity { Vendor = "Intel", Family = 6, Model = 183 };
        CpuTargetState state = Cpu(identity: unnamed);

        CpuGroupPlan plan = CpuGroupPlanner.Plan(CpuPart(new CpuProfile { SustainedWatts = 150 }, capturedOn: unnamed), state, isManual: true);

        string refusal = Assert.Single(plan.Items, i => i.Label == CpuGroupPlanner.WattsLabel).Text;
        Assert.Contains("registre de Windows", refusal);
        Assert.DoesNotContain("autre processeur", refusal);
    }

    // ---- Compte rendu du plan d'alimentation ----

    private static string Describe(CpuPowerSetting setting, uint ac, uint? dc) => dc is { } d && d != ac ? $"{ac}/{d}" : $"{ac}";

    [Fact]
    public void Le_compte_rendu_du_plan_dit_retenu_rogne_non_relu_et_refuse()
    {
        var result = new PowerPlanWriteResult(true,
        [
            new PowerPlanSettingResult(Epp, 80, 80, 80, 80),
            new PowerPlanSettingResult(Boost, 2, 2, 1, 1),
            new PowerPlanSettingResult(Epp, 60, 60, null, null),
        ], null, "Équilibré");

        List<ReportItem> items = CpuPlanReport.Items(result, restoring: false, Describe);

        Assert.Equal([ReportItemStatus.Applied, ReportItemStatus.Trimmed, ReportItemStatus.NotReadBack], items.Select(i => i.Status));
        Assert.Contains("Windows a retenu 1 au lieu de 2", items[1].Text);
        Assert.True(CpuPlanReport.IsPermanent(result));
    }

    [Fact]
    public void Un_refus_de_windows_sans_rien_de_retenu_n_est_pas_permanent()
    {
        var refused = new PowerPlanWriteResult(false, [new PowerPlanSettingResult(Epp, 80, 80, 50, 50)], "Windows a refusé le réglage", "Équilibré");
        var nothing = new PowerPlanWriteResult(false, [], "aucun plan actif", null);

        Assert.False(CpuPlanReport.IsPermanent(refused));
        Assert.Equal(ReportItemStatus.Refused, Assert.Single(CpuPlanReport.Items(refused, false, Describe)).Status);
        Assert.Contains("aucun plan actif", Assert.Single(CpuPlanReport.Items(nothing, false, Describe)).Text);
    }

    [Fact]
    public void Un_retour_a_l_origine_le_dit()
    {
        var result = new PowerPlanWriteResult(true, [new PowerPlanSettingResult(Epp, 50, 50, 50, 50)], null, "Équilibré");

        Assert.Contains("rendu à son origine (50)", Assert.Single(CpuPlanReport.Items(result, restoring: true, Describe)).Text);
    }
}
