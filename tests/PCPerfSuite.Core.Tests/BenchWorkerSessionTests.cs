using System.IO.Pipes;
using PCPerfSuite.Core.Benchmark;
using PCPerfSuite.Core.Benchmark.Protocol;
using PCPerfSuite.Core.Benchmark.Worker;

namespace PCPerfSuite.Core.Tests;

/// <summary>L'app et son worker reliés par un vrai tube nommé, le worker tournant dans un thread du processus de test
/// (pas de second processus : <c>applyProcessSetup</c> faux, pid attendu = le nôtre).</summary>
public class BenchWorkerSessionTests
{
    private sealed class WorkerThread : IDisposable
    {
        private readonly Thread _thread;
        private int _exitCode = int.MinValue;

        public WorkerThread(string pipeName)
        {
            _thread = new Thread(() => _exitCode = BenchWorkerHost.Run(pipeName, Log.Add, applyProcessSetup: false)) { IsBackground = true };
            _thread.Start();
        }

        public List<string> Log { get; } = new();

        public int? WaitForExit(TimeSpan timeout) => _thread.Join(timeout) ? _exitCode : null;

        public void Dispose() => _thread.Join(TimeSpan.FromSeconds(10));
    }

    private static NamedPipeServerStream NewServer(out string pipeName)
    {
        pipeName = BenchWorkerLauncher.PipePrefix + "test." + Guid.NewGuid().ToString("N");
        return new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    }

    private static BenchJobRequest ShortCpuMono(double sustained = 0) => new()
    {
        Kind = BenchTestKinds.Key(BenchTestKind.CpuMono),
        Cpu = new CpuJobParameters { WarmupSeconds = 0.02, PassSeconds = 0.02, Passes = 2, SustainedSeconds = sustained, ThreadCount = 1 },
    };

    [Fact]
    public async Task Le_worker_se_presente_execute_un_test_et_s_arrete_quand_le_tube_se_ferme()
    {
        NamedPipeServerStream server = NewServer(out string pipeName);
        using var worker = new WorkerThread(pipeName);
        var progress = new List<BenchProgress>();

        int? exitCode;
        using (BenchWorkerSession session = await BenchWorkerSession.AcceptAsync(server, null, null, Environment.ProcessId, TimeSpan.FromSeconds(10), CancellationToken.None))
        {
            Assert.Equal(Environment.ProcessId, session.ProcessId);
            Assert.True(session.IsAlive);
            Assert.Empty(session.WorkerNotes);
            session.ProgressReported += progress.Add;

            BenchJobResult result = await session.RunJobAsync(ShortCpuMono(), CancellationToken.None);

            Assert.True(result.Succeeded, result.Error);
            Assert.Equal(3, result.Measurements.Count);
            Assert.False(session.IsBusy);
            Assert.NotEmpty(progress);
        }

        exitCode = worker.WaitForExit(TimeSpan.FromSeconds(10));
        Assert.Equal(BenchWorkerHost.ExitOk, exitCode);
    }

    [Fact]
    public async Task Sans_battement_de_l_app_le_worker_coupe_tout_et_sort_de_lui_meme()
    {
        NamedPipeServerStream server = NewServer(out string pipeName);
        using var worker = new WorkerThread(pipeName);

        using BenchWorkerSession session = await BenchWorkerSession.AcceptAsync(server, null, null, Environment.ProcessId, TimeSpan.FromSeconds(10), CancellationToken.None);
        session.HeartbeatEnabled = false;

        int? exitCode = worker.WaitForExit(BenchWorkerHost.AppSilenceTimeout + TimeSpan.FromSeconds(5));

        Assert.Equal(BenchWorkerHost.ExitAppSilent, exitCode);
        Assert.Contains(worker.Log, line => line.Contains("plus de nouvelles de l'app", StringComparison.Ordinal));
        // Le tube s'est fermé du côté du worker : la session le sait.
        await Task.Delay(300);
        Assert.False(session.IsAlive);
    }

    [Fact]
    public async Task Une_annulation_arrete_le_test_en_cours_et_rend_un_echec_arrete()
    {
        NamedPipeServerStream server = NewServer(out string pipeName);
        using var worker = new WorkerThread(pipeName);
        using BenchWorkerSession session = await BenchWorkerSession.AcceptAsync(server, null, null, Environment.ProcessId, TimeSpan.FromSeconds(10), CancellationToken.None);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        BenchJobResult result = await session.RunJobAsync(ShortCpuMono(sustained: 60), cancel.Token);

        Assert.False(result.Succeeded);
        Assert.Equal(BenchJobDispatcher.CancelledError, result.Error);
        Assert.True(session.IsAlive, "le worker a rendu son résultat partiel : il n'avait pas à être tué");
    }

    [Fact]
    public async Task Un_second_test_pendant_le_premier_est_refuse()
    {
        NamedPipeServerStream server = NewServer(out string pipeName);
        using var worker = new WorkerThread(pipeName);
        using BenchWorkerSession session = await BenchWorkerSession.AcceptAsync(server, null, null, Environment.ProcessId, TimeSpan.FromSeconds(10), CancellationToken.None);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        Task<BenchJobResult> first = session.RunJobAsync(ShortCpuMono(sustained: 60), cancel.Token);
        await Task.Delay(100);

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.RunJobAsync(ShortCpuMono(), CancellationToken.None));
        Assert.True(session.IsBusy);
        BenchJobResult result = await first;
        Assert.Equal(BenchJobDispatcher.CancelledError, result.Error);
    }

    [Fact]
    public async Task Un_worker_au_mauvais_pid_est_refuse_et_le_tube_ferme()
    {
        NamedPipeServerStream server = NewServer(out string pipeName);
        using var worker = new WorkerThread(pipeName);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BenchWorkerSession.AcceptAsync(server, null, null, Environment.ProcessId + 1, TimeSpan.FromSeconds(10), CancellationToken.None));

        Assert.Contains("n'est pas celui lancé", ex.Message);
        int? exitCode = worker.WaitForExit(TimeSpan.FromSeconds(10));
        Assert.NotNull(exitCode);
        Assert.NotEqual(BenchWorkerHost.ExitAppSilent, exitCode);
    }

    [Fact]
    public async Task Sans_worker_l_attente_expire_proprement()
    {
        NamedPipeServerStream server = NewServer(out _);

        await Assert.ThrowsAsync<TimeoutException>(() =>
            BenchWorkerSession.AcceptAsync(server, null, null, null, TimeSpan.FromMilliseconds(300), CancellationToken.None));
    }

    [Fact]
    public void L_exe_du_lanceur_n_est_pas_l_hote_dotnet_des_tests()
    {
        string? executable = BenchWorkerLauncher.FindExecutable(out string? reason);

        if (executable is null) Assert.NotNull(reason);
        else Assert.NotEqual("dotnet", Path.GetFileNameWithoutExtension(executable), StringComparer.OrdinalIgnoreCase);
    }
}
