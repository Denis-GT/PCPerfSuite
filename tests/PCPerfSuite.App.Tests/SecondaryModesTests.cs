using PCPerfSuite.Core.Benchmark.Worker;
using PCPerfSuite.Core.SystemInfo;

namespace PCPerfSuite.App.Tests;

/// <summary>Liste fermée des arguments de l'exe : tout ce qui n'y est pas est refusé sans fenêtre.</summary>
public class SecondaryModesTests
{
    [Fact]
    public void Sans_argument_c_est_l_app_normale()
    {
        SecondaryMode mode = SecondaryModes.Parse([]);

        Assert.Equal(SecondaryModeKind.Normal, mode.Kind);
        Assert.False(mode.LaunchedByWindows);
        Assert.Null(mode.PipeName);
        Assert.Null(mode.Problem);
    }

    [Fact]
    public void Le_demarrage_par_windows_reste_inchange()
    {
        SecondaryMode mode = SecondaryModes.Parse([StartupTask.LaunchArgument]);

        Assert.Equal(SecondaryModeKind.Normal, mode.Kind);
        Assert.True(mode.LaunchedByWindows);
    }

    [Fact]
    public void Le_demarrage_par_windows_est_lu_sans_tenir_compte_de_la_casse()
    {
        Assert.True(SecondaryModes.Parse([StartupTask.LaunchArgument.ToUpperInvariant()]).LaunchedByWindows);
    }

    [Fact]
    public void Un_argument_inconnu_est_refuse_avec_le_code_de_sortie_2()
    {
        SecondaryMode mode = SecondaryModes.Parse(["--inconnu"]);

        Assert.Equal(SecondaryModeKind.Refused, mode.Kind);
        Assert.Contains("--inconnu", mode.Problem);
        Assert.Equal(2, SecondaryModes.RefusedExitCode);
    }

    [Fact]
    public void Un_argument_inconnu_apres_un_argument_connu_est_refuse_aussi()
    {
        Assert.Equal(SecondaryModeKind.Refused, SecondaryModes.Parse([StartupTask.LaunchArgument, "fichier.txt"]).Kind);
    }

    [Fact]
    public void Le_worker_de_bench_recoit_son_nom_de_tube()
    {
        string pipe = BenchWorkerLauncher.PipePrefix + Guid.NewGuid().ToString("N");

        SecondaryMode mode = SecondaryModes.Parse([SecondaryModes.BenchWorkerArgument, pipe]);

        Assert.Equal(SecondaryModeKind.BenchWorker, mode.Kind);
        Assert.Equal(pipe, mode.PipeName);
        Assert.False(mode.LaunchedByWindows);
    }

    [Fact]
    public void L_argument_du_worker_est_celui_que_le_lanceur_emploie()
    {
        Assert.Equal(BenchWorkerLauncher.Argument, SecondaryModes.BenchWorkerArgument);
    }

    [Theory]
    [InlineData(@"..\autre")]
    [InlineData("avec espace")]
    [InlineData("")]
    [InlineData("tube/chemin")]
    [InlineData("\\\\.\\pipe\\x")]
    public void Un_nom_de_tube_invalide_est_refuse(string pipe)
    {
        SecondaryMode mode = SecondaryModes.Parse([SecondaryModes.BenchWorkerArgument, pipe]);

        Assert.Equal(SecondaryModeKind.Refused, mode.Kind);
        Assert.Contains("tube", mode.Problem);
    }

    [Fact]
    public void Un_nom_de_tube_trop_long_est_refuse()
    {
        string pipe = new('a', 129);

        Assert.Equal(SecondaryModeKind.Refused, SecondaryModes.Parse([SecondaryModes.BenchWorkerArgument, pipe]).Kind);
        Assert.Equal(SecondaryModeKind.BenchWorker, SecondaryModes.Parse([SecondaryModes.BenchWorkerArgument, pipe[..128]]).Kind);
    }

    [Fact]
    public void Le_worker_sans_nom_de_tube_est_refuse()
    {
        SecondaryMode mode = SecondaryModes.Parse([SecondaryModes.BenchWorkerArgument]);

        Assert.Equal(SecondaryModeKind.Refused, mode.Kind);
        Assert.Contains("sans nom", mode.Problem);
    }

    [Fact]
    public void Le_worker_repete_est_refuse()
    {
        SecondaryMode mode = SecondaryModes.Parse([SecondaryModes.BenchWorkerArgument, "a", SecondaryModes.BenchWorkerArgument, "b"]);

        Assert.Equal(SecondaryModeKind.Refused, mode.Kind);
        Assert.Contains("répété", mode.Problem);
    }

    [Fact]
    public void Le_worker_et_le_demarrage_par_windows_sont_incompatibles()
    {
        SecondaryMode mode = SecondaryModes.Parse([SecondaryModes.BenchWorkerArgument, "tube", StartupTask.LaunchArgument]);

        Assert.Equal(SecondaryModeKind.Refused, mode.Kind);
        Assert.Contains("incompatibles", mode.Problem);
    }
}
