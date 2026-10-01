using PCPerfSuite.Core.Hardware;
using PCPerfSuite.Core.Hardware.Cpu;
using PCPerfSuite.Core.Hardware.Fans;
using PCPerfSuite.Core.Profiles;
using static PCPerfSuite.Core.Tests.ProfileGroupTestData;

namespace PCPerfSuite.Core.Tests;

/// <summary>Ordre d'application, ventilation, conformité, modifications des groupes et état de démarrage des
/// ventilateurs.</summary>
public class ProfileGroupLogicTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    // ---- Ordre ----

    [Theory]
    [InlineData(new[] { PowerTrend.Up, PowerTrend.Down }, ApplyOrder.FansFirst)]
    [InlineData(new[] { PowerTrend.Down, PowerTrend.Down }, ApplyOrder.FansLast)]
    [InlineData(new[] { PowerTrend.Down, PowerTrend.Same }, ApplyOrder.FansLast)]
    [InlineData(new[] { PowerTrend.Down, PowerTrend.Unknown }, ApplyOrder.FansFirst)]
    [InlineData(new[] { PowerTrend.Same, PowerTrend.Same }, ApplyOrder.FansFirst)]
    [InlineData(new PowerTrend[0], ApplyOrder.FansFirst)]
    public void Une_hausse_ou_un_doute_met_la_ventilation_d_abord(PowerTrend[] trends, ApplyOrder expected)
        => Assert.Equal(expected, ApplyDirection.Decide(trends));

    [Fact]
    public void Une_limite_visee_mais_illisible_est_inconnue()
    {
        Assert.Equal(PowerTrend.Unknown, ApplyDirection.Of(65, null, 1));
        Assert.Equal(PowerTrend.Same, ApplyDirection.Of(null, 125, 1));
        Assert.Equal(PowerTrend.Up, ApplyDirection.Combine([PowerTrend.Down, PowerTrend.Up, PowerTrend.Unknown]));
    }

    // ---- Ventilation ----

    private static ProfileGroupFansPart FansPart(params FanCurveConfig[] fans)
        => new() { Values = new FanProfile { Fans = fans.ToList(), FanNames = { ["sys"] = "Boîtier avant" } } };

    private static string Texts(FanGroupPlan plan) => string.Join(" | ", plan.Items.Select(i => i.Text).Concat(plan.Notes));

    [Fact]
    public void Avant_le_premier_releve_aucun_ventilateur_n_est_regle()
    {
        FanGroupPlan plan = FanGroupPlanner.Plan(FansPart(Fan("cpu")), Fans(ready: false), _ => "");

        Assert.False(plan.HasWork);
        Assert.Contains("aucun relevé des ventilateurs pour l'instant", Texts(plan));
    }

    [Fact]
    public void Un_ventilateur_absent_est_nomme_avec_la_raison()
    {
        FanGroupPlan plan = FanGroupPlanner.Plan(FansPart(Fan("cpu"), Fan("sys")), Fans(), id => $"absent de ce PC ({id})");

        Assert.Equal("cpu", Assert.Single(plan.Entries).FanId);
        Assert.Contains("« Boîtier avant » non réglé (absent de ce PC (sys))", Texts(plan));
        Assert.Contains("pas dans ce groupe, laissés tels quels : « GPU »", Texts(plan));
    }

    [Fact]
    public void Un_ventilateur_deja_regle_ainsi_n_est_pas_reecrit()
    {
        FanGroupPlan plan = FanGroupPlanner.Plan(FansPart(Fan("cpu", FanControlMode.Auto)), Fans(), _ => "");

        Assert.Empty(plan.Entries);
        Assert.Contains("déjà en place : « CPU Fan »", Texts(plan));
    }

    [Fact]
    public void Une_courbe_inutilisable_n_est_pas_posee()
    {
        FanCurveConfig broken = Fan("cpu");
        broken.Points = [new FanCurvePoint { TempC = 40, Percent = 30 }];

        FanGroupPlan plan = FanGroupPlanner.Plan(FansPart(broken), Fans(), _ => "");

        Assert.Empty(plan.Entries);
        Assert.Contains("courbe inutilisable", Texts(plan));
    }

    [Fact]
    public void Sur_un_portable_le_rapport_dit_que_seule_la_carte_graphique_se_regle()
    {
        FanGroupPlan plan = FanGroupPlanner.Plan(FansPart(Fan("gpu:0")), Fans(laptop: true, current: [Fan("gpu:0", FanControlMode.Auto)]), _ => "");

        Assert.Single(plan.Entries);
        Assert.Contains(FanGroupPlanner.LaptopNote, plan.Notes);
    }

    [Fact]
    public void L_origine_rend_les_ventilateurs_au_bios()
        => Assert.True(FanGroupPlanner.Plan(new ProfileGroupFansPart { Kind = ProfilePartKinds.Origin }, Fans(), _ => "").RestoreAuto);

    [Fact]
    public void Sans_ventilateur_pilotable_la_raison_est_donnee()
    {
        var none = new FanTargetState(true, [], false, "aucune puce de pilotage reconnue", new FanProfile());

        FanGroupPlan plan = FanGroupPlanner.Plan(FansPart(Fan("cpu")), none, _ => "");

        Assert.Contains("aucun ventilateur pilotable : aucune puce de pilotage reconnue", Texts(plan));
    }

    // ---- Conformité ----

    [Fact]
    public void Le_processeur_est_conforme_tant_que_rien_n_a_bouge()
    {
        var retained = new CpuProfile { SustainedWatts = 65, PowerSettings = { ["epp"] = new CpuProfilePowerValue { Ac = 80 } } };

        Assert.Equal(ConformityState.Conform, ProfileGroupConformity.CompareCpu(retained, Cpu(epp: 80, watts: Watts(65.4f))).State);
    }

    [Fact]
    public void Le_firmware_qui_retablit_ses_limites_rend_le_groupe_non_conforme()
    {
        var retained = new CpuProfile { SustainedWatts = 65 };

        DimensionConformity conformity = ProfileGroupConformity.CompareCpu(retained, Cpu(watts: Watts(125)));

        Assert.Equal(ConformityState.Modified, conformity.State);
        Assert.Contains("limite soutenue 125 W au lieu de 65 W", conformity.Describe());
    }

    [Fact]
    public void Un_reglage_du_plan_change_a_la_main_se_voit()
    {
        var retained = new CpuProfile { PowerSettings = { ["boost"] = new CpuProfilePowerValue { Ac = 0 } } };

        DimensionConformity conformity = ProfileGroupConformity.CompareCpu(retained, Cpu(boost: 2));

        Assert.Contains("« Mode boost » : Agressif au lieu de Désactivé", conformity.Describe());
    }

    [Fact]
    public void La_carte_est_conforme_a_ce_qu_elle_a_retenu_pas_a_la_demande()
    {
        var retained = new GpuRetainedValues { CoreOffsetMhz = 250, PowerLimitPercent = 110 };

        Assert.Equal(ConformityState.Conform, ProfileGroupConformity.CompareGpu(retained, Gpu(Overclock(core: 250), Power(110))).State);
        Assert.Equal(ConformityState.Modified, ProfileGroupConformity.CompareGpu(retained, Gpu(Overclock(core: 0), Power(110))).State);
    }

    [Fact]
    public void Une_carte_devenue_muette_n_est_pas_dite_conforme()
        => Assert.Equal(ConformityState.Unverifiable,
            ProfileGroupConformity.CompareGpu(new GpuRetainedValues { CoreOffsetMhz = 100 }, Gpu(available: false)).State);

    [Fact]
    public void Un_ventilateur_repasse_en_auto_se_voit()
    {
        var retained = new FanProfile { Fans = [Fan("cpu", FanControlMode.Curve)] };

        DimensionConformity conformity = ProfileGroupConformity.CompareFans(retained, Fans(current: [Fan("cpu", FanControlMode.Auto), Fan("gpu:0")]));

        Assert.Contains("« CPU Fan » : mode Auto au lieu de Courbe", conformity.Describe());
        Assert.Equal(ConformityState.Unverifiable, ProfileGroupConformity.CompareFans(retained, Fans(ready: false)).State);
    }

    // ---- Modifications ----

    [Fact]
    public void Modifier_un_groupe_genere_le_marque_regle_a_la_main()
    {
        var generated = new ProfileGroup { Origin = ProfileGroupOrigin.Generated };
        var manual = new ProfileGroup();

        ProfileGroupEditor.SetFans(generated, ProfileGroupEditor.FanOrigin(), T0);
        ProfileGroupEditor.Rename(manual, "Jeu", T0);

        Assert.True(generated.EditedByUser);
        Assert.Equal(1, generated.Revision);
        Assert.False(manual.EditedByUser);
        Assert.Equal(1, manual.Revision);
    }

    [Fact]
    public void Renommer_sans_changer_le_nom_n_est_pas_une_modification()
    {
        var generated = new ProfileGroup { Name = "Bureautique", Origin = ProfileGroupOrigin.Generated };

        ProfileGroupEditor.Rename(generated, "Bureautique", T0);
        ProfileGroupEditor.SetUsage(generated, null, T0);

        Assert.False(generated.EditedByUser);
        Assert.Equal(0, generated.Revision);
    }

    [Fact]
    public void Dupliquer_donne_un_groupe_manuel_sans_usage()
    {
        var generated = new ProfileGroup
        {
            Name = "Jeu",
            Origin = ProfileGroupOrigin.Generated,
            Usage = ProfileGroupUsage.HeavyGaming,
            EditedByUser = true,
            Revision = 4,
            Gpu = ProfileGroupEditor.GpuOrigin(),
        };

        ProfileGroup copy = ProfileGroupEditor.Duplicate(generated, ["Jeu", "Jeu (copie)"], T0);

        Assert.NotEqual(generated.Id, copy.Id);
        Assert.Equal("Jeu (copie) 2", copy.Name);
        Assert.Equal(ProfileGroupOrigin.Manual, copy.Origin);
        Assert.Null(copy.Usage);
        Assert.False(copy.EditedByUser);
        Assert.Equal(0, copy.Revision);
        Assert.Equal(ProfilePartKind.Origin, copy.Gpu!.ParsedKind);
    }

    [Fact]
    public void Supprimer_retire_l_etat_actif_et_la_suspension()
    {
        var group = new ProfileGroup();
        var settings = new ProfileGroupsSettings
        {
            Groups = [group],
            Active = new ProfileGroupActiveState { GroupId = group.Id },
            Suspensions = { [group.Id] = new ProfileGroupSuspension { Cause = "écran bleu" } },
        };

        Assert.True(ProfileGroupEditor.Delete(settings, group.Id));

        Assert.Empty(settings.Groups);
        Assert.Null(settings.Active);
        Assert.Empty(settings.Suspensions);
    }

    [Theory]
    [InlineData("  Jeu  ", "Jeu")]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void Un_nom_se_nettoie(string? raw, string? expected)
        => Assert.Equal(expected, ProfileGroupEditor.CleanName(raw));

    [Fact]
    public void Une_partie_importee_est_une_copie()
    {
        var profile = new CpuProfile { SustainedWatts = 65 };

        ProfileGroupCpuPart part = ProfileGroupEditor.CpuValues(profile, I5);
        profile.SustainedWatts = 200;

        Assert.Equal(65, part.Values!.SustainedWatts);
        Assert.Equal(I5, part.CapturedOn);
    }

    // ---- État de démarrage des ventilateurs ----

    [Fact]
    public void Un_etat_transitoire_ne_devient_pas_celui_du_demarrage()
    {
        var overrides = new FanStartupOverrides();
        FanCurveConfig startup = Fan("cpu", FanControlMode.Manual, 40);
        FanCurveConfig live = Fan("cpu", FanControlMode.Manual, 80);

        overrides.Hold("cpu", startup);
        overrides.Hold("cpu", Fan("cpu", FanControlMode.Manual, 60));
        startup.ManualPercent = 99;

        List<FanCurveConfig> saved = overrides.Resolve([live, Fan("gpu:0")]);

        Assert.Equal(40, saved[0].ManualPercent);
        Assert.Equal("gpu:0", saved[1].ControlSensorId);
    }

    [Fact]
    public void Une_modification_a_la_main_redevient_l_etat_de_demarrage()
    {
        var overrides = new FanStartupOverrides();
        overrides.Hold("cpu", Fan("cpu", FanControlMode.Manual, 40));

        Assert.True(overrides.Release("cpu"));

        Assert.Equal(80, overrides.Resolve([Fan("cpu", FanControlMode.Manual, 80)])[0].ManualPercent);
        Assert.True(overrides.IsEmpty);
    }
}
