using PCPerfSuite.Core.Hardware;
using PCPerfSuite.Core.Hardware.Cpu;
using PCPerfSuite.Core.Hardware.Fans;
using PCPerfSuite.Core.Hardware.Gpu;
using PCPerfSuite.Core.Profiles;
using static PCPerfSuite.Core.Tests.ProfileGroupTestData;

namespace PCPerfSuite.Core.Tests;

/// <summary>Générateur des trois groupes : rien d'inventé, réglages de l'utilisateur respectés, groupes modifiés à la main
/// jamais écrasés.</summary>
public sealed class UsageProfileGeneratorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 20, 0, 0, TimeSpan.Zero);

    private static UsageGenerationInput Input(
        CpuTargetState? cpu = null, GpuTargetState? gpu = null, FanTargetState? fans = null,
        SavedTabTuning? saved = null, Func<string, UsageStats>? stats = null, bool includeBios = false, IUsageOverclockSource? overclock = null)
        => new(cpu ?? Cpu(), gpu ?? Gpu(), fans ?? Fans(true, false, Fan("cpu"), Fan("gpu:0")), saved ?? SavedTabTuning.None,
            stats ?? (u => UsageStats.Empty(u)), includeBios, overclock ?? EmptyUsageOverclockSource.Instance, Now);

    private static UsageGenerationResult Generate(UsageGenerationInput input, params ProfileGroup[] existing)
        => UsageProfileGenerator.Generate(existing, input);

    private static GeneratedUsageGroup For(UsageGenerationResult result, string usage)
        => result.Groups.Single(g => g.Usage == usage);

    [Fact]
    public void Trois_groupes_generes_un_par_usage()
    {
        UsageGenerationResult result = Generate(Input());

        Assert.Equal(ProfileGroupUsage.All, result.Groups.Select(g => g.Usage));
        Assert.All(result.Groups, g =>
        {
            Assert.True(g.Created);
            Assert.Equal(ProfileGroupOrigin.Generated, g.Group.Origin);
            Assert.False(g.Group.EditedByUser);
            Assert.EndsWith("(auto)", g.Group.Name);
            Assert.NotEmpty(g.Explanation);
        });
        Assert.Empty(result.Skipped);
    }

    [Fact]
    public void La_bureautique_remet_toujours_le_GPU_d_origine_meme_sans_carte_pilotable()
    {
        Assert.Equal(ProfilePartKind.Origin, For(Generate(Input()), ProfileGroupUsage.Office).Group.Gpu!.ParsedKind);
        Assert.Equal(ProfilePartKind.Origin, For(Generate(Input(gpu: Gpu(available: false))), ProfileGroupUsage.Office).Group.Gpu!.ParsedKind);
    }

    [Fact]
    public void Sans_overclock_enregistre_les_groupes_de_jeu_ne_touchent_pas_la_carte_et_n_inventent_rien()
    {
        UsageGenerationResult result = Generate(Input());

        Assert.Null(For(result, ProfileGroupUsage.LightGaming).Group.Gpu);
        Assert.Null(For(result, ProfileGroupUsage.HeavyGaming).Group.Gpu);
        Assert.Contains(For(result, ProfileGroupUsage.HeavyGaming).Explanation, l => l.Contains("emplacement de l'OC automatique"));
    }

    private static SavedTabTuning SavedOc(GpuIdentity? identity, bool atStartup = true, int core = 150)
        => new(null, null, false, new GpuOverclockProfile { CoreClockOffsetMhz = core, MemoryClockOffsetMhz = 500, PowerLimitPercent = 105 },
            identity, atStartup);

    [Fact]
    public void L_overclock_enregistre_est_repris_dans_les_groupes_de_jeu_sur_la_meme_carte()
    {
        UsageGenerationResult result = Generate(Input(saved: SavedOc(Rtx)));

        ProfileGroupGpuPart heavy = For(result, ProfileGroupUsage.HeavyGaming).Group.Gpu!;
        Assert.Equal(150, heavy.Values!.CoreClockOffsetMhz);
        Assert.Equal(500, heavy.Values.MemoryClockOffsetMhz);
        Assert.Equal(Rtx, heavy.CapturedOn);
        Assert.Equal(150, For(result, ProfileGroupUsage.LightGaming).Group.Gpu!.Values!.CoreClockOffsetMhz);
        Assert.Equal(ProfilePartKind.Origin, For(result, ProfileGroupUsage.Office).Group.Gpu!.ParsedKind);
        Assert.Contains(For(result, ProfileGroupUsage.HeavyGaming).Explanation, l => l.Contains("cœur +150 MHz"));
    }

    [Fact]
    public void L_overclock_d_une_autre_carte_ou_sans_demarrage_n_est_pas_repris()
    {
        var other = new GpuIdentity(GpuVendor.Nvidia, "NVIDIA GeForce RTX 4070", 0x10DE, 0x2786, 0x12345678);

        Assert.Null(For(Generate(Input(saved: SavedOc(other))), ProfileGroupUsage.HeavyGaming).Group.Gpu);
        Assert.Null(For(Generate(Input(saved: SavedOc(Rtx, atStartup: false))), ProfileGroupUsage.HeavyGaming).Group.Gpu);
        Assert.Null(For(Generate(Input(saved: SavedOc(null))), ProfileGroupUsage.HeavyGaming).Group.Gpu);
        Assert.Null(For(Generate(Input(saved: SavedOc(Rtx, core: 0) with
        {
            GpuOverclock = new GpuOverclockProfile { PowerLimitPercent = 90 },
        })), ProfileGroupUsage.HeavyGaming).Group.Gpu);
    }

    [Fact]
    public void L_emplacement_OC_l_emporte_quand_il_est_rempli()
    {
        var slot = new FakeOverclock(new UsageOverclock(ProfileGroupEditor.GpuValues(new GpuOverclockProfile { CoreClockOffsetMhz = 90 }, Rtx), "profil sûr de l'OC automatique"));

        UsageGenerationResult result = Generate(Input(saved: SavedOc(Rtx), overclock: slot));

        Assert.Equal(90, For(result, ProfileGroupUsage.HeavyGaming).Group.Gpu!.Values!.CoreClockOffsetMhz);
        Assert.Equal(ProfilePartKind.Origin, For(result, ProfileGroupUsage.Office).Group.Gpu!.ParsedKind);
    }

    private sealed class FakeOverclock(UsageOverclock value) : IUsageOverclockSource
    {
        public UsageOverclock? For(string usage, GpuTargetState gpu) => usage == ProfileGroupUsage.Office ? null : value;
    }

    // ---- Processeur ----

    [Fact]
    public void L_EPP_est_regle_par_usage_et_sur_batterie_aussi_quand_il_y_en_a_une()
    {
        UsageGenerationResult desktop = Generate(Input());
        UsageGenerationResult laptop = Generate(Input(cpu: Cpu(battery: true)));

        CpuProfilePowerValue office = For(desktop, ProfileGroupUsage.Office).Group.Cpu!.Values!.PowerSettings["epp"];
        Assert.Equal(50u, office.Ac);
        Assert.Null(office.Battery);
        Assert.Equal(0u, For(desktop, ProfileGroupUsage.HeavyGaming).Group.Cpu!.Values!.PowerSettings["epp"].Ac);
        Assert.Equal(70u, For(laptop, ProfileGroupUsage.Office).Group.Cpu!.Values!.PowerSettings["epp"].Battery);
        Assert.False(For(desktop, ProfileGroupUsage.Office).Group.Cpu!.Values!.PowerSettings.ContainsKey("boost"));
        Assert.Equal(I5, For(desktop, ProfileGroupUsage.Office).Group.Cpu!.CapturedOn);
    }

    [Fact]
    public void Les_watts_de_la_bureautique_sont_a_65_pourcent_de_l_origine_et_ceux_du_jeu_a_l_origine()
    {
        UsageGenerationResult result = Generate(Input());

        CpuProfile office = For(result, ProfileGroupUsage.Office).Group.Cpu!.Values!;
        CpuProfile heavy = For(result, ProfileGroupUsage.HeavyGaming).Group.Cpu!.Values!;
        Assert.Equal(81, office.SustainedWatts);
        Assert.Equal(118, office.BurstWatts);
        Assert.Equal(125, heavy.SustainedWatts);
        Assert.Equal(181, heavy.BurstWatts);
    }

    [Fact]
    public void Les_watts_enregistres_sont_repris_en_jeu_et_plafonnent_la_bureautique()
    {
        var saved = new SavedTabTuning(70, 90, true, null, null, false);

        UsageGenerationResult result = Generate(Input(saved: saved));

        Assert.Equal(70, For(result, ProfileGroupUsage.HeavyGaming).Group.Cpu!.Values!.SustainedWatts);
        Assert.Equal(90, For(result, ProfileGroupUsage.HeavyGaming).Group.Cpu!.Values!.BurstWatts);
        Assert.Equal(70, For(result, ProfileGroupUsage.Office).Group.Cpu!.Values!.SustainedWatts);
        Assert.True(For(result, ProfileGroupUsage.Office).Group.Cpu!.Values!.BurstWatts <= 90);
    }

    [Fact]
    public void Des_watts_enregistres_hors_bornes_ou_sans_demarrage_ne_sont_pas_repris()
    {
        Assert.Equal(125, For(Generate(Input(saved: new SavedTabTuning(400, null, true, null, null, false))), ProfileGroupUsage.HeavyGaming).Group.Cpu!.Values!.SustainedWatts);
        Assert.Equal(125, For(Generate(Input(saved: new SavedTabTuning(150, null, false, null, null, false))), ProfileGroupUsage.HeavyGaming).Group.Cpu!.Values!.SustainedWatts);
    }

    [Theory]
    [InlineData(false, true, 125f)]
    [InlineData(true, false, 125f)]
    [InlineData(true, true, 4095f)]
    public void Sans_ecriture_sans_accord_ou_avec_une_origine_illimitee_les_watts_ne_sont_pas_touches(bool writable, bool accepted, float origin)
    {
        CpuTargetState cpu = Cpu(writable: writable, accepted: accepted, watts: Watts(defaultSustained: origin, defaultBurst: origin));

        UsageGenerationResult result = Generate(Input(cpu: cpu));

        Assert.All(result.Groups, g => Assert.Null(g.Group.Cpu!.Values!.SustainedWatts));
        Assert.Contains(For(result, ProfileGroupUsage.Office).Explanation, l => l.Contains("watts non touchés", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Les_watts_ne_depassent_jamais_l_origine_sans_reglage_enregistre()
    {
        UsageGenerationResult result = Generate(Input(cpu: Cpu(watts: Watts(sustained: 200, defaultSustained: 125))));

        Assert.All(result.Groups, g => Assert.True(g.Group.Cpu!.Values!.SustainedWatts <= 125));
    }

    [Fact]
    public void Un_processeur_sans_reglage_pris_en_charge_n_a_pas_de_partie_processeur()
    {
        CpuTargetState cpu = Cpu(writable: false) with { Settings = [] };

        UsageGenerationResult result = Generate(Input(cpu: cpu));

        Assert.All(result.Groups, g => Assert.Null(g.Group.Cpu));
        Assert.Contains(For(result, ProfileGroupUsage.Office).Explanation, l => l.StartsWith("Processeur : non touché"));
    }

    // ---- Ventilation ----

    [Fact]
    public void Les_ventilateurs_au_BIOS_restent_au_BIOS_sans_la_case()
    {
        FanTargetState fans = Fans(true, false, Fan("cpu", FanControlMode.Auto), Fan("gpu:0", FanControlMode.Curve));

        UsageGenerationResult result = Generate(Input(fans: fans));

        FanProfile office = For(result, ProfileGroupUsage.Office).Group.Fans!.Values!;
        Assert.Equal(["gpu:0"], office.Fans.Select(f => f.ControlSensorId));
        Assert.Contains(For(result, ProfileGroupUsage.Office).Explanation, l => l.Contains("Laissés au BIOS : CPU Fan"));
    }

    [Fact]
    public void Tous_au_BIOS_sans_la_case_donne_une_ventilation_non_touchee()
    {
        UsageGenerationResult result = Generate(Input(fans: Fans(true, false, Fan("cpu", FanControlMode.Auto))));

        Assert.All(result.Groups, g => Assert.Null(g.Group.Fans));
        Assert.Contains(For(result, ProfileGroupUsage.Office).Explanation, l => l.Contains("tous les ventilateurs sont au BIOS"));
    }

    [Fact]
    public void Avec_la_case_les_ventilateurs_au_BIOS_suivent_le_GPU_ou_le_plus_chaud()
    {
        FanTargetState fans = Fans(true, false, Fan("cpu", FanControlMode.Auto), Fan("gpu:0", FanControlMode.Auto));

        FanProfile heavy = For(Generate(Input(fans: fans, includeBios: true)), ProfileGroupUsage.HeavyGaming).Group.Fans!.Values!;

        Assert.All(heavy.Fans, f => Assert.Equal(FanControlMode.Curve, f.Mode));
        Assert.Equal(FanTempSource.GpuCore, heavy.Fans.Single(f => f.ControlSensorId == "gpu:0").Source);
        Assert.Equal(FanTempSource.HottestOfCpuGpu, heavy.Fans.Single(f => f.ControlSensorId == "cpu").Source);
    }

    [Fact]
    public void La_source_choisie_par_l_utilisateur_est_gardee()
    {
        FanCurveConfig mine = Fan("cpu", FanControlMode.Curve);
        mine.Source = FanTempSource.MotherboardSystem;
        mine.MinPercent = 25;

        FanCurveConfig generated = For(Generate(Input(fans: Fans(true, false, mine))), ProfileGroupUsage.Office).Group.Fans!.Values!.Fans.Single();

        Assert.Equal(FanTempSource.MotherboardSystem, generated.Source);
        Assert.Equal(25, generated.MinPercent);
    }

    [Fact]
    public void Sur_un_portable_seul_le_ventilateur_de_la_carte_graphique_est_regle()
    {
        FanTargetState laptop = Fans(true, true, Fan("gpu:0", FanControlMode.Curve));

        GeneratedUsageGroup office = For(Generate(Input(fans: laptop)), ProfileGroupUsage.Office);

        Assert.Equal(["gpu:0"], office.Group.Fans!.Values!.Fans.Select(f => f.ControlSensorId));
        Assert.Contains(office.Explanation, l => l.Contains(FanGroupPlanner.LaptopNote));
    }

    [Fact]
    public void Sans_releve_des_ventilateurs_la_ventilation_n_est_pas_touchee()
        => Assert.All(Generate(Input(fans: Fans(ready: false))).Groups, g => Assert.Null(g.Group.Fans));

    [Fact]
    public void Chaque_courbe_atteint_100_pourcent_a_chaud()
    {
        UsageGenerationResult result = Generate(Input());

        Assert.All(result.Groups, g => Assert.All(g.Group.Fans!.Values!.Fans, f =>
        {
            Assert.Equal(100, f.Points[^1].Percent);
            Assert.True(f.Points[^1].TempC <= FanCurveShaper.FullSpeedBy);
        }));
    }

    private static UsageStats Hot(string usage, int cpuP95, int gpuP95)
        => new(usage, 7200, 2, 60, 80, cpuP95 - 10, cpuP95, gpuP95 - 10, gpuP95, 7200, 7200);

    [Fact]
    public void Des_temperatures_elevees_avancent_la_courbe_et_basses_la_reculent_un_peu()
    {
        UsageGenerationResult hot = Generate(Input(stats: u => Hot(u, 80, 80)));
        UsageGenerationResult cool = Generate(Input(stats: u => Hot(u, 35, 35)));

        float presetFirst = FanCurveMath.SilencieuxPoints()[0].TempC;
        Assert.Equal(presetFirst - 5, For(hot, ProfileGroupUsage.Office).Group.Fans!.Values!.Fans[0].Points[0].TempC);
        Assert.Equal(presetFirst + 3, For(cool, ProfileGroupUsage.Office).Group.Fans!.Values!.Fans[0].Points[0].TempC);
        Assert.Contains(For(hot, ProfileGroupUsage.Office).Explanation, l => l.Contains("p95 80 °C"));
    }

    [Fact]
    public void Sans_assez_d_historique_la_courbe_reste_le_prereglage()
    {
        var little = new UsageStats(ProfileGroupUsage.Office, 600, 1, null, null, 80, 90, 80, 90, 600, 600);

        FanCurveConfig fan = For(Generate(Input(stats: _ => little)), ProfileGroupUsage.Office).Group.Fans!.Values!.Fans[0];

        Assert.Equal(FanCurveMath.SilencieuxPoints()[0].TempC, fan.Points[0].TempC);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-5f)]
    [InlineData(3f)]
    [InlineData(40f)]
    public void Une_courbe_mise_en_forme_reste_dans_les_bornes_de_l_editeur(float shift)
    {
        List<FanCurvePoint> points = FanCurveShaper.Shape(FanCurveMath.SilencieuxPoints(), shift);

        Assert.All(points, p => Assert.InRange(p.TempC, FanCurveMath.MinTempC, FanCurveMath.MaxTempC));
        for (int i = 1; i < points.Count; i++)
        {
            Assert.True(points[i].TempC - points[i - 1].TempC >= FanCurveMath.MinTempGap - 0.001);
            Assert.True(points[i].Percent >= points[i - 1].Percent);
        }

        Assert.Equal(100, points[^1].Percent);
    }

    // ---- Régénérer ----

    [Fact]
    public void Regenerer_garde_l_identifiant_et_monte_la_revision_sans_marquer_modifie()
    {
        GeneratedUsageGroup first = For(Generate(Input()), ProfileGroupUsage.Office);

        // Un ventilateur de moins : le contenu change.
        GeneratedUsageGroup again = For(Generate(Input(fans: Fans(true, false, Fan("cpu"))), first.Group), ProfileGroupUsage.Office);

        Assert.False(again.Created);
        Assert.Equal(first.Group.Id, again.Group.Id);
        Assert.Equal(first.Group.Revision + 1, again.Group.Revision);
        Assert.False(again.Group.EditedByUser);
    }

    [Fact]
    public void Regenerer_sans_changement_garde_la_revision()
    {
        // Sinon la bascule reposerait le groupe en cours à l'identique (nouvelle période d'essai, bulle).
        GeneratedUsageGroup first = For(Generate(Input()), ProfileGroupUsage.Office);

        GeneratedUsageGroup again = For(Generate(Input(), first.Group), ProfileGroupUsage.Office);

        Assert.Equal(first.Group.Revision, again.Group.Revision);
        Assert.Equal(first.Group.UpdatedUtc, again.Group.UpdatedUtc);
        Assert.NotSame(first.Group, again.Group);
    }

    [Fact]
    public void Un_groupe_genere_modifie_a_la_main_n_est_jamais_ecrase()
    {
        ProfileGroup office = For(Generate(Input()), ProfileGroupUsage.Office).Group;
        ProfileGroupEditor.SetGpu(office, null, Now);
        Assert.True(office.EditedByUser);

        UsageGenerationResult result = Generate(Input(), office);

        Assert.DoesNotContain(result.Groups, g => g.Usage == ProfileGroupUsage.Office);
        Assert.Contains(result.Skipped, s => s.Usage == ProfileGroupUsage.Office && s.Reason.Contains("modifié à la main"));
        Assert.Null(office.Gpu);
    }

    [Fact]
    public void Un_usage_couvert_par_un_groupe_manuel_n_est_pas_recree()
    {
        var mine = new ProfileGroup { Name = "Mon jeu", Usage = ProfileGroupUsage.HeavyGaming, Origin = ProfileGroupOrigin.Manual };

        UsageGenerationResult result = Generate(Input(), mine);

        Assert.DoesNotContain(result.Groups, g => g.Usage == ProfileGroupUsage.HeavyGaming);
        Assert.Equal(2, result.Groups.Count);
    }

    [Fact]
    public void Un_groupe_genere_supprime_est_recree_sous_un_nom_libre()
    {
        var taken = new ProfileGroup { Name = "Bureautique (auto)", Usage = null };

        GeneratedUsageGroup office = For(Generate(Input(), taken), ProfileGroupUsage.Office);

        Assert.True(office.Created);
        Assert.Equal("Bureautique (auto) 2", office.Group.Name);
    }
}
