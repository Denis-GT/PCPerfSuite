using System.Diagnostics;
using System.IO.Pipes;

namespace PCPerfSuite.Core.Benchmark.Worker;

/// <summary>
/// Lance le worker : le même exe, par son chemin complet (jamais par le PATH), avec <c>--bench-worker &lt;tube&gt;</c>,
/// assigné à un Job Object qui le tue avec l'app, puis attend sa connexion sur un tube nommé au nom aléatoire réservé
/// à la session de l'utilisateur (CurrentUserOnly). Le processus hérite de l'élévation de l'app sans invite.
/// </summary>
public static class BenchWorkerLauncher
{
    public const string Argument = "--bench-worker";
    public const string PipePrefix = "PCPerfSuite.Bench.";
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Chemin de l'exe à lancer, ou null (avec la raison) quand l'app ne tourne pas depuis son propre exe
    /// (hôte dotnet d'un test, chemin inconnu).</summary>
    public static string? FindExecutable(out string? reason)
    {
        string? processPath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(processPath))
        {
            reason = "chemin de l'exe inconnu";
            return null;
        }
        if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            reason = "l'app tourne sous l'hôte dotnet, pas depuis son exe";
            return null;
        }
        reason = null;
        return processPath;
    }

    public static async Task<BenchWorkerSession> StartAsync(Action<string>? log, CancellationToken cancel)
    {
        string executable = FindExecutable(out string? reason) ?? throw new InvalidOperationException($"worker impossible à lancer : {reason}");
        string pipeName = PipePrefix + Guid.NewGuid().ToString("N");

        var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        JobObject? job = JobObject.TryCreateKillOnClose(out string? jobError);
        if (job is null) log?.Invoke($"Job Object : {jobError} (le battement de cœur reste le seul filet)");

        Process? process = null;
        try
        {
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(executable) ?? Environment.CurrentDirectory,
            };
            start.ArgumentList.Add(Argument);
            start.ArgumentList.Add(pipeName);
            process = Process.Start(start) ?? throw new InvalidOperationException("Process.Start n'a rendu aucun processus");

            if (job is not null && !job.TryAssign(process, out string? assignError))
            {
                log?.Invoke($"Job Object : {assignError} (le battement de cœur reste le seul filet)");
            }

            return await BenchWorkerSession.AcceptAsync(server, process, job, process.Id, ConnectTimeout, cancel).ConfigureAwait(false);
        }
        catch (Exception)
        {
            try { if (process is { HasExited: false }) process.Kill(); } catch (Exception) { /* déjà parti */ }
            job?.Dispose();
            server.Dispose();
            throw;
        }
    }
}
