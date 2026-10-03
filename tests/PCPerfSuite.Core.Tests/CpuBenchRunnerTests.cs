using PCPerfSuite.Core.Benchmark;
using PCPerfSuite.Core.Benchmark.Cpu;
using PCPerfSuite.Core.Benchmark.Kernels;
using PCPerfSuite.Core.Benchmark.Protocol;
using PCPerfSuite.Core.Benchmark.Worker;

namespace PCPerfSuite.Core.Tests;

/// <summary>Le bench processeur côté worker, avec des durées de quelques centièmes de seconde : c'est le protocole
/// (phases, clés, notes, annulation) qu'on vérifie ici, pas la vitesse de la machine.</summary>
public class CpuBenchRunnerTests
{
    private static BenchJobRequest Mono(double sustained = 0, int passes = 2) => new()
    {
        Kind = BenchTestKinds.Key(BenchTestKind.CpuMono),
        Cpu = new CpuJobParameters { WarmupSeconds = 0.02, PassSeconds = 0.02, Passes = passes, SustainedSeconds = sustained, ThreadCount = 1 },
    };

    [Fact]
    public void Un_test_mono_court_rend_une_mesure_rafale_par_noyau()
    {
        var progress = new List<BenchProgress>();

        BenchJobResult result = new CpuBenchRunner().Run(Mono(), progress.Add, CancellationToken.None);

        Assert.True(result.Succeeded, result.Error);
        Assert.Null(result.Error);
        Assert.False(result.ChecksumMismatch);
        Assert.Equal(CpuKernelCatalog.Keys.Select(k => k + CpuBenchRunner.BurstSuffix), result.Measurements.Select(m => m.Key));
        Assert.All(result.Measurements, m =>
        {
            Assert.Equal(2, m.Values.Count);
            Assert.True(m.Median > 0);
            Assert.True(m.HigherIsBetter);
            Assert.NotEmpty(m.Unit);
        });
        Assert.Equal("1", result.Notes["threads"]);
        Assert.Equal("non épinglé", result.Notes["epinglage"]);
        Assert.Equal("0", result.Notes["erreurs-calcul"]);
        Assert.Equal("oui", result.Notes["etalons-identiques"]);
        Assert.Contains("entier", result.Notes["noyaux"]);
        Assert.True(result.DurationSeconds > 0);
        Assert.NotEmpty(progress);
        Assert.All(progress, p => Assert.InRange(p.Percent, 0, 100));
        Assert.Contains(progress, p => p.Phase.StartsWith("rafale", StringComparison.Ordinal) && p.Value is > 0);
    }

    [Fact]
    public void Un_test_multi_avec_phase_soutenue_rend_aussi_les_mesures_soutenu()
    {
        var request = new BenchJobRequest
        {
            Kind = BenchTestKinds.Key(BenchTestKind.CpuMulti),
            Cpu = new CpuJobParameters { WarmupSeconds = 0, PassSeconds = 0.02, Passes = 1, SustainedSeconds = 0.3, ThreadCount = 2 },
        };
        var phases = new List<string>();

        BenchJobResult result = new CpuBenchRunner().Run(request, p => phases.Add(p.Phase), CancellationToken.None);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal("2", result.Notes["threads"]);
        foreach (string key in CpuKernelCatalog.Keys)
        {
            Assert.NotNull(result.Find(key + CpuBenchRunner.BurstSuffix));
            Assert.NotNull(result.Find(key + CpuBenchRunner.SustainedSuffix));
        }
        Assert.Contains(phases, p => p.StartsWith("charge continue", StringComparison.Ordinal));
        Assert.Contains(phases, p => p.StartsWith("soutenu", StringComparison.Ordinal));
        Assert.InRange(result.DurationSeconds, 0.3, 20);
    }

    [Fact]
    public void Une_annulation_avant_le_depart_rend_un_echec_arrete()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        BenchJobResult result = new CpuBenchRunner().Run(Mono(), null, cancel.Token);

        Assert.False(result.Succeeded);
        Assert.Equal(BenchJobDispatcher.CancelledError, result.Error);
    }

    [Fact]
    public void Une_annulation_pendant_la_charge_continue_arrete_vite_le_test()
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        BenchJobResult result = new CpuBenchRunner().Run(Mono(sustained: 60), null, cancel.Token);

        Assert.False(result.Succeeded);
        Assert.Equal(BenchJobDispatcher.CancelledError, result.Error);
        Assert.InRange(result.DurationSeconds, 0, 10);
    }

    [Fact]
    public void Un_noyau_inconnu_ou_des_passes_invalides_sont_refuses_sans_lever()
    {
        BenchJobRequest unknown = Mono();
        unknown.Cpu!.Kernels = ["gpu"];
        BenchJobRequest noPass = Mono(passes: 0);

        Assert.Contains("noyau inconnu", new CpuBenchRunner().Run(unknown, null, CancellationToken.None).Error);
        Assert.Contains("passe", new CpuBenchRunner().Run(noPass, null, CancellationToken.None).Error);
    }

    [Fact]
    public void Le_distributeur_refuse_un_test_inconnu_et_date_la_duree()
    {
        var request = new BenchJobRequest { Kind = "inconnu" };

        BenchJobResult result = BenchJobDispatcher.Run(request, null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("test inconnu", result.Error);
        Assert.Equal(request.Id, result.JobId);
        Assert.True(result.DurationSeconds >= 0);
    }

    [Fact]
    public void Le_distributeur_passe_un_test_cpu_au_runner()
    {
        BenchJobResult result = BenchJobDispatcher.Run(Mono(), null, CancellationToken.None);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(3, result.Measurements.Count);
    }
}
