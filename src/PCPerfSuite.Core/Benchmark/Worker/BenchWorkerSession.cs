using System.Diagnostics;
using System.IO.Pipes;
using PCPerfSuite.Core.Benchmark.Protocol;

namespace PCPerfSuite.Core.Benchmark.Worker;

/// <summary>
/// Le worker vu depuis l'app : un tube connecté, le processus (et son Job Object) s'il a été lancé, un test à la fois.
/// Envoie un battement toutes les 500 ms ; sans nouvelles du worker pendant <see cref="WorkerSilenceTimeout"/>, le tue
/// et fait échouer le test en cours. Un arrêt demandé laisse <see cref="StopGracePeriod"/> au worker pour rendre son
/// résultat partiel, puis le tue.
/// </summary>
public sealed class BenchWorkerSession : Session.IBenchWorker
{
    public static readonly TimeSpan WorkerSilenceTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMilliseconds(500);
    public static readonly TimeSpan StopGracePeriod = TimeSpan.FromSeconds(3);

    private readonly BenchLineChannel _channel;
    private readonly Stream _stream;
    private readonly Process? _process;
    private readonly JobObject? _job;
    private readonly LivenessWatch _liveness;
    private readonly Thread _reader;
    private readonly Timer _heartbeat;
    private readonly object _gate = new();
    private TaskCompletionSource<BenchJobResult>? _pending;
    private string? _pendingJobId;
    private string? _pendingKind;
    private volatile bool _closed;
    private int _disposed;

    private BenchWorkerSession(BenchLineChannel channel, Stream stream, Process? process, JobObject? job, BenchMessage hello)
    {
        _channel = channel;
        _stream = stream;
        _process = process;
        _job = job;
        ProcessId = hello.ProcessId;
        WorkerNotes = hello.Notes?.AsReadOnly() ?? (IReadOnlyList<string>)Array.Empty<string>();
        _liveness = new LivenessWatch(WorkerSilenceTimeout, LivenessWatch.MonotonicNow());
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "PCPerfSuite bench session" };
        _reader.Start();
        _heartbeat = new Timer(_ => OnHeartbeatTick(), null, HeartbeatInterval, HeartbeatInterval);
    }

    public int? ProcessId { get; }

    /// <summary>Réglages du processus worker rapportés par son bonjour (EcoQoS, priorité).</summary>
    public IReadOnlyList<string> WorkerNotes { get; }

    /// <summary>Pour les tests : couper les battements et voir le worker s'arrêter de lui-même.</summary>
    public bool HeartbeatEnabled { get; set; } = true;

    public bool IsAlive => !_closed && !_channel.IsBroken && (_process is null || !ProcessHasExited());

    public bool IsBusy => _pending is not null;

    public event Action<BenchProgress>? ProgressReported;

    /// <summary>Attend la connexion du worker sur le tube, lit son bonjour, vérifie son pid et sa version du bench.</summary>
    public static async Task<BenchWorkerSession> AcceptAsync(NamedPipeServerStream server, Process? process, JobObject? job,
        int? expectedProcessId, TimeSpan timeout, CancellationToken cancel)
    {
        using var timeoutCancel = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeoutCancel.CancelAfter(timeout);
        // Le canal n'est créé qu'une fois le tube connecté : son écrivain vide son tampon dès la construction, ce qu'un
        // tube serveur encore en attente refuse.
        BenchLineChannel? channel = null;
        try
        {
            await server.WaitForConnectionAsync(timeoutCancel.Token).ConfigureAwait(false);
            channel = new BenchLineChannel(server);
            string? line = await channel.ReadLineAsync(timeoutCancel.Token).ConfigureAwait(false);
            BenchMessage? hello = BenchMessageCodec.TryDecode(line, out string? problem);
            if (hello is null) throw new InvalidOperationException($"bonjour du worker illisible : {problem}");
            if (hello.Type != BenchMessageTypes.Hello) throw new InvalidOperationException($"premier message « {hello.Type} » au lieu du bonjour");
            if (expectedProcessId is { } pid && hello.ProcessId != pid)
            {
                throw new InvalidOperationException($"le worker connecté n'est pas celui lancé (pid {hello.ProcessId} au lieu de {pid})");
            }
            if (hello.BenchVersion != BenchVersion.Bench)
            {
                throw new InvalidOperationException($"version du bench {hello.BenchVersion} au lieu de {BenchVersion.Bench}");
            }

            return new BenchWorkerSession(channel, server, process, job, hello);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            CloseQuietly(channel, server);
            KillQuietly(process, job);
            throw new TimeoutException($"le worker ne s'est pas connecté en {timeout.TotalSeconds:0} s");
        }
        catch (Exception)
        {
            CloseQuietly(channel, server);
            KillQuietly(process, job);
            throw;
        }
    }

    private static void CloseQuietly(BenchLineChannel? channel, Stream server)
    {
        if (channel is not null) channel.Close();
        else try { server.Dispose(); } catch (Exception) { /* déjà fermé */ }
    }

    /// <summary>Un test ; un seul à la fois. Une annulation demande l'arrêt au worker, puis le tue passé le délai de grâce.</summary>
    public async Task<BenchJobResult> RunJobAsync(BenchJobRequest request, CancellationToken cancel)
    {
        var completion = new TaskCompletionSource<BenchJobResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_pending is not null) throw new InvalidOperationException("Un test est déjà en cours sur ce worker.");
            _pending = completion;
            _pendingJobId = request.Id;
            _pendingKind = request.Kind;
        }

        try
        {
            if (!IsAlive)
            {
                return BenchJobResult.Failure(request.Id, request.Kind, "worker indisponible");
            }
            if (!_channel.TrySend(BenchMessage.Start(request)))
            {
                return BenchJobResult.Failure(request.Id, request.Kind, "tube fermé avant le démarrage");
            }

            using CancellationTokenRegistration registration = cancel.Register(() =>
            {
                _channel.TrySend(BenchMessage.Stop(request.Id));
                _ = Task.Delay(StopGracePeriod).ContinueWith(_ =>
                {
                    if (completion.Task.IsCompleted) return;
                    KillWith("arrêté (worker tué, pas de résultat partiel)");
                }, TaskScheduler.Default);
            });

            return await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _pending = null;
                _pendingJobId = null;
                _pendingKind = null;
            }
        }
    }

    /// <summary>Tue le worker sans ménagement (sécurité thermique, capteurs muets, fermeture).</summary>
    public void Kill() => KillWith("worker tué");

    /// <summary>La cause d'abord, le tube ensuite : fermé avant, il réveillerait le lecteur, qui ferait échouer le test
    /// en cours avec « arrêté sans rendre de résultat » à la place de la vraie cause.</summary>
    private void KillWith(string reason)
    {
        _closed = true;
        FailPending(reason);
        _channel.Close();
        KillQuietly(_process, _job);
    }

    private void ReadLoop()
    {
        try
        {
            while (_channel.ReadLine() is { } line)
            {
                BenchMessage? message = BenchMessageCodec.TryDecode(line, out _);
                if (message is null) continue;
                _liveness.Note(LivenessWatch.MonotonicNow());

                switch (message.Type)
                {
                    case BenchMessageTypes.Progress when message.Progress is { } progress:
                        try { ProgressReported?.Invoke(progress); } catch (Exception) { /* un abonné fautif n'arrête pas la lecture */ }
                        break;

                    case BenchMessageTypes.Result when message.Result is { } result:
                        CompletePending(message.JobId, result);
                        break;

                    case BenchMessageTypes.Error:
                        FailPending(message.Error ?? "erreur du worker", message.JobId);
                        break;
                }
            }
        }
        catch (Exception)
        {
            // Tube cassé : traité comme une fin de flux.
        }
        finally
        {
            _closed = true;
            if (_pending is not null) FailPending($"le worker s'est arrêté sans rendre de résultat{DescribeWorkerExit()}");
        }
    }

    /// <summary>Pourquoi le worker est parti de lui-même, d'après son code de sortie (attendu un instant : le tube se
    /// ferme juste avant la fin du processus).</summary>
    private string DescribeWorkerExit()
    {
        try
        {
            if (_process is null || !_process.WaitForExit(1000)) return "";
            return _process.ExitCode switch
            {
                BenchWorkerHost.ExitAppSilent => " (il n'avait plus de nouvelles de l'app : battements en retard)",
                BenchWorkerHost.ExitCrashed => " (plantage du worker, voir le journal des erreurs)",
                BenchWorkerHost.ExitOk => "",
                int code => $" (code de sortie {code})",
            };
        }
        catch (Exception)
        {
            return "";
        }
    }

    private void OnHeartbeatTick()
    {
        if (_closed) return;
        if (HeartbeatEnabled) _channel.TrySend(BenchMessage.Heartbeat());

        DateTimeOffset now = LivenessWatch.MonotonicNow();
        if (_liveness.IsExpired(now))
        {
            KillWith($"worker muet depuis {_liveness.Silence(now).TotalSeconds:0} s : tué");
        }
    }

    private void CompletePending(string? jobId, BenchJobResult result)
    {
        TaskCompletionSource<BenchJobResult>? pending;
        lock (_gate)
        {
            if (_pending is null || (jobId is not null && _pendingJobId is not null && jobId != _pendingJobId)) return;
            pending = _pending;
        }
        pending.TrySetResult(result);
    }

    private void FailPending(string error, string? jobId = null)
    {
        TaskCompletionSource<BenchJobResult>? pending;
        string? pendingJobId;
        string? pendingKind;
        lock (_gate)
        {
            if (_pending is null || (jobId is not null && _pendingJobId is not null && jobId != _pendingJobId)) return;
            pending = _pending;
            pendingJobId = _pendingJobId;
            pendingKind = _pendingKind;
        }
        pending.TrySetResult(BenchJobResult.Failure(pendingJobId ?? "", pendingKind ?? "", error));
    }

    private bool ProcessHasExited()
    {
        try { return _process!.HasExited; } catch (Exception) { return true; }
    }

    private static void KillQuietly(Process? process, JobObject? job)
    {
        try
        {
            if (process is not null && !process.HasExited) process.Kill();
        }
        catch (Exception)
        {
            // Déjà parti, ou refusé : le Job Object prend le relais.
        }
        job?.Dispose();
    }

    /// <summary>Ferme le tube (le worker s'arrête de lui-même), attend un peu, puis ferme le Job Object (qui le tue
    /// s'il traîne).</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _closed = true;
        _heartbeat.Dispose();
        if (_pending is not null) _channel.TrySend(BenchMessage.Stop(_pendingJobId));
        _channel.Close();
        try { _stream.Dispose(); } catch (Exception) { /* déjà fermé */ }
        FailPending("session fermée");
        try
        {
            if (_process is not null && !_process.HasExited) _process.WaitForExit(1500);
        }
        catch (Exception)
        {
            // Rien à faire de mieux.
        }
        KillQuietly(_process, _job);
        _process?.Dispose();
        _reader.Join(TimeSpan.FromSeconds(2));
    }
}
