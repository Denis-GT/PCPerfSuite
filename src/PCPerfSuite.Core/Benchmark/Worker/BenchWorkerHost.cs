using System.Collections.Concurrent;
using System.IO.Pipes;
using PCPerfSuite.Core.Benchmark.Cpu;
using PCPerfSuite.Core.Benchmark.Protocol;

namespace PCPerfSuite.Core.Benchmark.Worker;

/// <summary>
/// Le worker de charge, côté processus secondaire (<c>PCPerfSuite.exe --bench-worker &lt;tube&gt;</c>) : se connecte au
/// tube de l'app, se présente, exécute les tests qu'elle lui demande, et surtout coupe toute charge dès qu'il n'a plus
/// de nouvelles d'elle pendant <see cref="AppSilenceTimeout"/> (app plantée, tuée, bloquée). Il n'ouvre aucune fenêtre
/// et ne lit aucun réglage. Codes de sortie dans les constantes ; un plantage inattendu est relevé par l'appelant.
/// </summary>
public static class BenchWorkerHost
{
    public const int ExitOk = 0;
    /// <summary>Exception non rattrapée dans le worker (relevée par l'hôte, journalisée).</summary>
    public const int ExitCrashed = 1;
    public const int ExitPipeUnavailable = 2;
    public const int ExitAppSilent = 3;

    public static readonly TimeSpan AppSilenceTimeout = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMilliseconds(500);
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan JobStopGrace = TimeSpan.FromSeconds(5);

    /// <summary>Point d'entrée du mode secondaire. <paramref name="applyProcessSetup"/> faux pour les tests, qui ne
    /// veulent pas passer leur propre processus en priorité High.</summary>
    public static int Run(string pipeName, Action<string>? log = null, bool applyProcessSetup = true)
    {
        IReadOnlyList<string> setupNotes = applyProcessSetup ? WorkerProcessSetup.Apply() : Array.Empty<string>();

        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            pipe.Connect((int)ConnectTimeout.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            log?.Invoke($"connexion au tube impossible : {ex.GetType().Name} : {ex.Message}");
            return ExitPipeUnavailable;
        }

        return Serve(pipe, setupNotes, log);
    }

    /// <summary>Boucle du worker sur un flux déjà connecté (testable sans processus).</summary>
    public static int Serve(Stream stream, IReadOnlyList<string> setupNotes, Action<string>? log = null)
    {
        using var channel = new BenchLineChannel(stream);
        var liveness = new LivenessWatch(AppSilenceTimeout, LivenessWatch.MonotonicNow());
        using var incoming = new BlockingCollection<BenchMessage>();

        BenchMessage hello = BenchMessage.Hello(Environment.ProcessId);
        hello.Notes = setupNotes.ToList();
        if (!channel.TrySend(hello)) return ExitPipeUnavailable;

        var reader = new Thread(() =>
        {
            try
            {
                while (channel.ReadLine() is { } line)
                {
                    BenchMessage? message = BenchMessageCodec.TryDecode(line, out string? problem);
                    if (message is null)
                    {
                        log?.Invoke($"message de l'app ignoré : {problem}");
                        continue;
                    }
                    liveness.Note(LivenessWatch.MonotonicNow());
                    incoming.Add(message);
                }
            }
            catch (Exception ex)
            {
                log?.Invoke($"lecture du tube interrompue : {ex.GetType().Name}");
            }
            finally
            {
                try { incoming.CompleteAdding(); } catch (ObjectDisposedException) { /* fin de service */ }
            }
        })
        { IsBackground = true, Name = "PCPerfSuite bench reader" };
        reader.Start();

        int exit = ExitOk;
        Task? job = null;
        CancellationTokenSource? jobCancel = null;
        DateTimeOffset lastHeartbeat = LivenessWatch.MonotonicNow();

        try
        {
            while (true)
            {
                if (incoming.TryTake(out BenchMessage? message, 100))
                {
                    switch (message.Type)
                    {
                        case BenchMessageTypes.Start when message.Job is { } request:
                            if (job is { IsCompleted: false })
                            {
                                channel.TrySend(BenchMessage.ErrorOf(request.Id, "un test est déjà en cours"));
                                break;
                            }
                            jobCancel?.Dispose();
                            jobCancel = new CancellationTokenSource();
                            CancellationToken token = jobCancel.Token;
                            job = Task.Run(() =>
                            {
                                BenchJobResult result = BenchJobDispatcher.Run(request, p => channel.TrySend(BenchMessage.ProgressOf(p)), token);
                                channel.TrySend(BenchMessage.ResultOf(result));
                            }, CancellationToken.None);
                            break;

                        case BenchMessageTypes.Start:
                            channel.TrySend(BenchMessage.ErrorOf(message.JobId, "demande sans paramètres"));
                            break;

                        case BenchMessageTypes.Stop:
                            jobCancel?.Cancel();
                            break;

                        case BenchMessageTypes.Heartbeat:
                            break;

                        default:
                            log?.Invoke($"message de l'app non attendu : {message.Type}");
                            break;
                    }
                }
                else if (incoming.IsAddingCompleted || channel.IsBroken)
                {
                    // Tube fermé : l'app est partie proprement (ou pas). Dans les deux cas, plus de charge.
                    break;
                }

                DateTimeOffset now = LivenessWatch.MonotonicNow();
                if (liveness.IsExpired(now))
                {
                    log?.Invoke($"plus de nouvelles de l'app depuis {liveness.Silence(now).TotalSeconds:0.0} s : arrêt de la charge");
                    exit = ExitAppSilent;
                    break;
                }

                if (now - lastHeartbeat >= HeartbeatInterval)
                {
                    channel.TrySend(BenchMessage.Heartbeat());
                    lastHeartbeat = now;
                }
            }
        }
        finally
        {
            jobCancel?.Cancel();
            try { job?.Wait(JobStopGrace); } catch (Exception) { /* le résultat n'intéresse plus personne */ }
            jobCancel?.Dispose();
            channel.Close();
            reader.Join(TimeSpan.FromSeconds(2));
        }

        return exit;
    }
}
