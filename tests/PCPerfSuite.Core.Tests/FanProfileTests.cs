using System.Text.Json;
using PCPerfSuite.Core.Hardware;
using PCPerfSuite.Core.Hardware.Fans;
using PCPerfSuite.Core.PowerSettings;

namespace PCPerfSuite.Core.Tests;

public class FanProfileTests
{
    private static FanCurveConfig Config(string id, FanControlMode mode = FanControlMode.Curve) => new()
    {
        ControlSensorId = id,
        Mode = mode,
        Points = FanCurveMath.EquilibrePoints(),
    };

    private static FanCurvePoint Point(float temp, float percent) => new() { TempC = temp, Percent = percent };

    // ---- Rapprochement avec les ventilateurs de ce PC ----

    [Fact]
    public void Un_profil_se_partage_entre_ventilateurs_presents_et_absents()
    {
        var profile = new FanProfile { Fans = { Config("a"), Config("b"), Config("c") } };

        FanProfileMatch match = FanProfileMatcher.Match(profile, new[] { "a", "c", "d" });

        Assert.Equal(new[] { "a", "c" }, match.Applicable.Select(f => f.ControlSensorId));
        Assert.Equal(new[] { "b" }, match.Missing.Select(f => f.ControlSensorId));
        Assert.Equal(new[] { "d" }, match.NotInProfile);
    }

    [Fact]
    public void Un_profil_vide_ne_touche_a_aucun_ventilateur()
    {
        FanProfileMatch match = FanProfileMatcher.Match(new FanProfile(), new[] { "a", "b" });

        Assert.Empty(match.Applicable);
        Assert.Empty(match.Missing);
        Assert.Equal(new[] { "a", "b" }, match.NotInProfile);
    }

    [Fact]
    public void Un_ventilateur_en_double_ou_sans_identifiant_est_ignore()
    {
        FanCurveConfig first = Config("a");
        FanCurveConfig second = Config("a", FanControlMode.Manual);
        var profile = new FanProfile { Fans = { first, second, Config("") } };

        FanProfileMatch match = FanProfileMatcher.Match(profile, new[] { "a" });

        Assert.Same(first, Assert.Single(match.Applicable));
        Assert.Empty(match.Missing);
    }

    [Fact]
    public void Un_profil_dont_aucun_ventilateur_existe_ici_les_signale_tous()
    {
        var profile = new FanProfile { Fans = { Config("x"), Config("y") } };

        FanProfileMatch match = FanProfileMatcher.Match(profile, new[] { "a" });

        Assert.Empty(match.Applicable);
        Assert.Equal(2, match.Missing.Count);
    }

    // ---- Valeurs venues d'un fichier ou d'une autre machine ----

    [Fact]
    public void Une_entree_valide_ne_produit_aucune_remarque()
    {
        SanitizedFanCurve result = FanProfileMatcher.Sanitize(Config("a"));

        Assert.True(result.IsUsable);
        Assert.Empty(result.Notes);
        Assert.Equal(FanCurveMath.EquilibrePoints().Count, result.Config!.Points.Count);
    }

    [Fact]
    public void Un_mode_inconnu_retombe_sur_auto()
    {
        FanCurveConfig entry = Config("a", (FanControlMode)7);

        SanitizedFanCurve result = FanProfileMatcher.Sanitize(entry);

        Assert.Equal(FanControlMode.Auto, result.Config!.Mode);
        Assert.Contains(result.Notes, n => n.Contains("mode inconnu"));
    }

    [Fact]
    public void Une_temperature_suivie_inconnue_retombe_sur_le_cpu()
    {
        FanCurveConfig entry = Config("a");
        entry.Source = (FanTempSource)42;

        SanitizedFanCurve result = FanProfileMatcher.Sanitize(entry);

        Assert.Equal(FanTempSource.CpuPackage, result.Config!.Source);
    }

    [Fact]
    public void Les_vitesses_sont_ramenees_entre_0_et_100_et_min_ne_depasse_pas_max()
    {
        FanCurveConfig entry = Config("a");
        entry.ManualPercent = 150;
        entry.MinPercent = 80;
        entry.MaxPercent = 20;

        FanCurveConfig config = FanProfileMatcher.Sanitize(entry).Config!;

        Assert.Equal(100, config.ManualPercent);
        Assert.Equal(80, config.MinPercent);
        Assert.Equal(80, config.MaxPercent);
    }

    [Fact]
    public void Une_valeur_non_numerique_prend_la_valeur_par_defaut()
    {
        FanCurveConfig entry = Config("a");
        entry.ManualPercent = float.NaN;
        entry.HysteresisC = float.PositiveInfinity;

        FanCurveConfig config = FanProfileMatcher.Sanitize(entry).Config!;

        Assert.Equal(50, config.ManualPercent);
        Assert.Equal(3, config.HysteresisC);
    }

    [Fact]
    public void Hysteresis_et_arret_a_froid_sont_ramenes_dans_leurs_plages()
    {
        FanCurveConfig entry = Config("a");
        entry.HysteresisC = 50;
        entry.StopBelowTempC = 5;

        FanCurveConfig config = FanProfileMatcher.Sanitize(entry).Config!;

        Assert.Equal(FanProfileMatcher.MaxHysteresisC, config.HysteresisC);
        Assert.Equal(FanProfileMatcher.MinStopTempC, config.StopBelowTempC);
    }

    [Fact]
    public void Sans_arret_a_froid_il_reste_sans()
    {
        Assert.Null(FanProfileMatcher.Sanitize(Config("a")).Config!.StopBelowTempC);
    }

    [Fact]
    public void Les_points_sont_tries_bornes_et_espaces()
    {
        FanCurveConfig entry = Config("a");
        entry.Points = new List<FanCurvePoint>
        {
            Point(60, 80),
            Point(-10, 20),   // sous la plage : ramené à 20 °C
            Point(21, 30),    // à moins de 2 °C du précédent : écarté
            Point(40, 140),   // % hors plage : ramené à 100
            Point(200, 90),   // au-dessus de la plage : ramené à 85 °C
        };

        SanitizedFanCurve result = FanProfileMatcher.Sanitize(entry);

        Assert.Equal(new float[] { 20, 40, 60, 85 }, result.Config!.Points.Select(p => p.TempC));
        Assert.Equal(new float[] { 20, 100, 80, 90 }, result.Config.Points.Select(p => p.Percent));
        Assert.Contains(result.Notes, n => n.Contains("points"));
    }

    [Fact]
    public void Un_point_non_numerique_ou_nul_est_ecarte()
    {
        FanCurveConfig entry = Config("a");
        entry.Points = new List<FanCurvePoint>
        {
            Point(30, 30), Point(float.NaN, 50), null!, Point(50, float.PositiveInfinity), Point(70, 90),
        };

        FanCurveConfig config = FanProfileMatcher.Sanitize(entry).Config!;

        Assert.Equal(new float[] { 30, 70 }, config.Points.Select(p => p.TempC));
    }

    [Fact]
    public void Trop_de_points_sont_limites_au_maximum()
    {
        FanCurveConfig entry = Config("a");
        entry.Points = Enumerable.Range(0, 40).Select(i => Point(20 + i * 1.6f, 50)).ToList();

        FanCurveConfig config = FanProfileMatcher.Sanitize(entry).Config!;

        Assert.True(config.Points.Count <= FanCurveMath.MaxPoints);
        Assert.True(config.Points.Count >= FanCurveMath.MinPoints);
    }

    [Fact]
    public void Une_courbe_de_moins_de_deux_points_est_refusee_en_mode_courbe()
    {
        FanCurveConfig entry = Config("a", FanControlMode.Curve);
        entry.Points = new List<FanCurvePoint> { Point(40, 50) };

        SanitizedFanCurve result = FanProfileMatcher.Sanitize(entry);

        Assert.False(result.IsUsable);
        Assert.Contains(result.Notes, n => n.Contains("inutilisable"));
    }

    [Theory]
    [InlineData(FanControlMode.Auto)]
    [InlineData(FanControlMode.Manual)]
    public void Hors_mode_courbe_une_courbe_absente_est_remplacee_par_equilibre(FanControlMode mode)
    {
        FanCurveConfig entry = Config("a", mode);
        entry.Points = new List<FanCurvePoint>();

        SanitizedFanCurve result = FanProfileMatcher.Sanitize(entry);

        Assert.True(result.IsUsable);
        Assert.Equal(mode, result.Config!.Mode);
        Assert.Equal(FanCurveMath.EquilibrePoints().Count, result.Config.Points.Count);
    }

    [Fact]
    public void Une_liste_de_points_absente_du_fichier_ne_fait_pas_lever()
    {
        FanCurveConfig entry = Config("a", FanControlMode.Auto);
        entry.Points = null!;

        Assert.True(FanProfileMatcher.Sanitize(entry).IsUsable);
    }

    [Fact]
    public void La_validation_ne_modifie_pas_l_entree_d_origine()
    {
        FanCurveConfig entry = Config("a", (FanControlMode)7);
        entry.ManualPercent = 150;
        entry.Points = new List<FanCurvePoint> { Point(70, 80), Point(30, 20) };

        FanProfileMatcher.Sanitize(entry);

        Assert.Equal((FanControlMode)7, entry.Mode);
        Assert.Equal(150, entry.ManualPercent);
        Assert.Equal(new float[] { 70, 30 }, entry.Points.Select(p => p.TempC));
    }

    // ---- Copie et persistance ----

    [Fact]
    public void Un_clone_est_independant_de_l_original()
    {
        FanCurveConfig original = Config("a");
        original.StopBelowTempC = 40;

        FanCurveConfig clone = original.Clone();
        clone.Points[0].Percent = 99;
        clone.Points.RemoveAt(1);
        clone.Mode = FanControlMode.Auto;

        Assert.Equal(FanCurveMath.EquilibrePoints()[0].Percent, original.Points[0].Percent);
        Assert.Equal(FanCurveMath.EquilibrePoints().Count, original.Points.Count);
        Assert.Equal(FanControlMode.Curve, original.Mode);
        Assert.Equal("a", clone.ControlSensorId);
        Assert.Equal(40, clone.StopBelowTempC);
    }

    [Fact]
    public void Les_profils_survivent_a_un_aller_retour_dans_le_fichier_de_reglages()
    {
        FanCurveConfig fan = Config("/lpc/nct6798d/0/control/5");
        fan.Source = FanTempSource.HottestOfCpuGpu;
        fan.StopBelowTempC = 35;

        var settings = new AppSettings();
        settings.FanProfiles.Add(new FanProfile
        {
            Name = "Nuit",
            Fans = { fan },
            FanNames = { [fan.ControlSensorId] = "Boîtier avant" },
        });

        string json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        AppSettings? read = JsonSerializer.Deserialize<AppSettings>(json);

        FanProfile profile = Assert.Single(read!.FanProfiles);
        Assert.Equal("Nuit", profile.Name);
        Assert.Equal("Boîtier avant", profile.FanNames[fan.ControlSensorId]);

        FanCurveConfig readFan = Assert.Single(profile.Fans);
        Assert.Equal(fan.ControlSensorId, readFan.ControlSensorId);
        Assert.Equal(FanTempSource.HottestOfCpuGpu, readFan.Source);
        Assert.Equal(35, readFan.StopBelowTempC);
        Assert.Equal(fan.Points.Count, readFan.Points.Count);
    }

    [Fact]
    public void Un_ancien_fichier_sans_profils_se_relit_avec_une_liste_vide()
    {
        AppSettings? read = JsonSerializer.Deserialize<AppSettings>("""{ "MonitoringRefreshMs": 500 }""");

        Assert.NotNull(read);
        Assert.Empty(read!.FanProfiles);
    }

    [Fact]
    public void Un_profil_edite_a_la_main_avec_un_mode_absurde_se_relit_puis_se_corrige()
    {
        const string json = """
            { "FanProfiles": [ { "Name": "Bidouillé", "Fans": [
                { "ControlSensorId": "/lpc/inconnu/0/control/9", "Mode": 7, "Source": 12, "ManualPercent": 500,
                  "Points": [ { "TempC": 70, "Percent": 80 }, { "TempC": 30, "Percent": 20 } ] } ] } ] }
            """;

        AppSettings? read = JsonSerializer.Deserialize<AppSettings>(json);
        FanProfile profile = Assert.Single(read!.FanProfiles);

        // Ce ventilateur n'existe pas ici : il est signalé, pas appliqué.
        FanProfileMatch match = FanProfileMatcher.Match(profile, new[] { "/lpc/nct6798d/0/control/1" });
        Assert.Single(match.Missing);

        // Et s'il existait, ses valeurs seraient ramenées dans les limites plutôt que posées telles quelles.
        FanCurveConfig config = FanProfileMatcher.Sanitize(profile.Fans[0]).Config!;
        Assert.Equal(FanControlMode.Auto, config.Mode);
        Assert.Equal(FanTempSource.CpuPackage, config.Source);
        Assert.Equal(100, config.ManualPercent);
        Assert.Equal(new float[] { 30, 70 }, config.Points.Select(p => p.TempC));
    }
}
