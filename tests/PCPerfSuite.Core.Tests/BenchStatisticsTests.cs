using PCPerfSuite.Core.Benchmark;
using PCPerfSuite.Core.Benchmark.Protocol;
using Xunit;

namespace PCPerfSuite.Core.Tests;

public class BenchStatisticsTests
{
    [Fact]
    public void La_mediane_ignore_une_passe_parasitee()
    {
        Assert.Equal(305, BenchStatistics.Median([300, 310, 305]));
        Assert.Equal(300, BenchStatistics.Median([300, 10, 310]));
        Assert.Equal(305, BenchStatistics.Median([300, 310]));
        Assert.True(double.IsNaN(BenchStatistics.Median([])));
    }

    [Fact]
    public void Des_passes_identiques_ont_un_cv_nul_et_sont_stables()
    {
        PassSummary summary = BenchStatistics.Summarize([100, 100, 100]);

        Assert.Equal(0, summary.CoefficientOfVariation);
        Assert.False(summary.IsUnstable);
        Assert.Equal(3, summary.Count);
    }

    [Fact]
    public void Au_dela_de_trois_pour_cent_la_mesure_est_instable()
    {
        Assert.False(BenchStatistics.Summarize([100, 102, 101]).IsUnstable);
        Assert.True(BenchStatistics.Summarize([100, 110, 90]).IsUnstable);
        Assert.Equal(0.1, BenchStatistics.CoefficientOfVariation([100, 110, 90]), 6);
    }

    [Fact]
    public void Une_seule_passe_ou_une_moyenne_nulle_ne_donne_pas_de_cv()
    {
        Assert.Equal(0, BenchStatistics.CoefficientOfVariation([42]));
        Assert.Equal(0, BenchStatistics.CoefficientOfVariation([0, 0]));
    }

    [Fact]
    public void La_moyenne_geometrique_refuse_une_valeur_non_positive()
    {
        Assert.Equal(2, BenchStatistics.GeometricMean([1, 4]), 9);
        Assert.True(double.IsNaN(BenchStatistics.GeometricMean([1, 0])));
        Assert.True(double.IsNaN(BenchStatistics.GeometricMean([])));
    }

    [Fact]
    public void Mille_points_a_la_reference_et_une_latence_inversee()
    {
        var references = new Dictionary<string, ScoreReference>
        {
            ["debit"] = new("debit", 100),
            ["latence"] = new("latence", 80, HigherIsBetter: false),
        };

        double? atReference = BenchScore.Points(
            [BenchMeasurement.From("debit", "", "", [100]), BenchMeasurement.From("latence", "", "", [80])], references);
        double? twiceAsFast = BenchScore.Points(
            [BenchMeasurement.From("debit", "", "", [200]), BenchMeasurement.From("latence", "", "", [40])], references);
        double? latencyOnly = BenchScore.Points([BenchMeasurement.From("latence", "", "", [160])], references);

        Assert.Equal(1000, atReference);
        Assert.Equal(2000, twiceAsFast);
        Assert.Equal(500, latencyOnly);
    }

    [Fact]
    public void Sans_reference_ou_sans_mesure_positive_pas_de_points()
    {
        var references = new Dictionary<string, ScoreReference> { ["debit"] = new("debit", 100) };

        Assert.Null(BenchScore.Points([BenchMeasurement.From("autre", "", "", [1])], references));
        Assert.Null(BenchScore.Points([BenchMeasurement.From("debit", "", "", [0])], references));
        Assert.Null(BenchScore.Points([], references));
    }

    [Fact]
    public void Chaque_test_a_ses_references_et_une_mesure_rafale_du_cpu_y_figure()
    {
        foreach (BenchTestKind kind in BenchTestKinds.All)
        {
            Assert.NotEmpty(BenchReferences.For(kind));
        }
        Assert.True(BenchReferences.For(BenchTestKind.CpuMono).ContainsKey("entier.rafale"));
        Assert.False(BenchReferences.For(BenchTestKind.RamLatency)["latence"].HigherIsBetter);
    }

    [Fact]
    public void Les_cles_des_tests_font_l_aller_retour()
    {
        foreach (BenchTestKind kind in BenchTestKinds.All)
        {
            Assert.Equal(kind, BenchTestKinds.Parse(BenchTestKinds.Key(kind)));
        }
        Assert.Null(BenchTestKinds.Parse("gpu"));
    }
}
