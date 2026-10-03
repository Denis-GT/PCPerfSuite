using PCPerfSuite.App.ViewModels;
using PCPerfSuite.Core.Benchmark;
using PCPerfSuite.Core.Benchmark.Protocol;
using PCPerfSuite.Core.Benchmark.Results;

namespace PCPerfSuite.App.Tests;

/// <summary>Les cartes et lignes de la page Bench : du texte pour l'utilisateur, à partir d'un résultat enregistré.</summary>
public class BenchViewModelsTests
{
    private static BenchTestResult Test(double? points = 1120, bool succeeded = true, bool comparable = true, bool unstable = false, string? stop = null)
    {
        var test = new BenchTestResult
        {
            Kind = "cpu-mono",
            Title = "Processeur, un cœur",
            Succeeded = succeeded,
            Error = succeeded ? null : "arrêté",
            Points = points,
            IsComparable = comparable,
            IsUnstable = unstable,
            DurationSeconds = 23,
            Cadence = "92 relevés en 23 s",
            Throttle = "aucun bridage sur 92 relevés",
            MaxCpuTempC = 71,
            StopReason = stop,
            StopDetail = stop is null ? null : "processeur à 99 °C pendant 10 s",
        };
        test.Measurements.Add(BenchMeasurement.From("entier.rafale", "Entier (rafale)", "Mops/s", unstable ? [300, 340, 310] : [320, 322, 318]));
        test.Measurements.Add(BenchMeasurement.From("latence", "Latence", "ns", [81.2, 80.9, 81.5], higherIsBetter: false));
        test.Notes["threads"] = "1";
        return test;
    }

    [Fact]
    public void La_carte_dit_les_points_la_duree_et_chaque_mesure_avec_son_unite()
    {
        var card = new BenchResultCardViewModel(Test());

        Assert.Equal("Processeur, un cœur", card.Title);
        Assert.EndsWith("pts", card.PointsText);
        Assert.Contains("1", card.PointsText);
        Assert.Equal("23 s", card.StatusText);
        Assert.Equal(2, card.Lines.Count);
        Assert.StartsWith("Entier (rafale) : 320 Mops/s (CV ", card.Lines[0]);
        Assert.Contains("ns", card.Lines[1]);
        Assert.Contains("71 °C", card.DetailText);
        Assert.Contains("threads : 1", card.DetailText);
        Assert.Null(card.Warning);
    }

    [Fact]
    public void La_carte_explique_l_absence_de_points_et_les_avertissements()
    {
        var fallback = new BenchResultCardViewModel(Test(points: null, comparable: false));
        var failed = new BenchResultCardViewModel(Test(points: null, succeeded: false));
        var unstable = new BenchResultCardViewModel(Test(unstable: true));
        var stopped = new BenchResultCardViewModel(Test(points: null, succeeded: false, stop: "Thermal"));

        Assert.Equal("sans points : chemin de calcul non comparable", fallback.PointsText);
        Assert.Equal("échec", failed.PointsText);
        Assert.Equal("arrêté", failed.StatusText);
        Assert.Contains("instables", unstable.StatusText);
        Assert.Contains("instable", unstable.Lines[0]);
        Assert.Contains("dispersées", unstable.Warning);
        Assert.Equal("processeur à 99 °C pendant 10 s", stopped.Warning);
    }

    [Fact]
    public void L_historique_date_la_session_et_signale_batterie_et_version()
    {
        var session = new BenchSessionResult { StartedUtc = new DateTimeOffset(2026, 10, 2, 10, 0, 0, TimeSpan.Zero), IsRepresentative = false, BenchVersion = BenchVersion.Bench + 1 };
        session.Tests.Add(Test());

        var item = new BenchHistoryItemViewModel(session);

        Assert.Contains("2026", item.Text);
        Assert.Contains("1 test", item.Text);
        Assert.Contains("batterie", item.Note);
        Assert.Contains($"v{BenchVersion.Bench + 1}", item.Note);
        Assert.Null(new BenchHistoryItemViewModel(new BenchSessionResult()).Note);
    }

    [Fact]
    public void Un_test_cochable_porte_sa_raison_et_previent_du_changement()
    {
        var item = new BenchTestItemViewModel(BenchTestKind.Disk);
        int changes = 0;
        item.CheckedChanged += () => changes++;

        item.IsChecked = true;
        item.IsAvailable = false;
        item.Reason = "aucun volume éligible";

        Assert.True(item.IsDisk);
        Assert.Equal("Disque", item.Title);
        Assert.Equal(1, changes);
        Assert.False(item.IsAvailable);
    }
}
