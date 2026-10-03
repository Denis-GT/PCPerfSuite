using System.Diagnostics;
using PCPerfSuite.Core.Benchmark.Kernels;
using PCPerfSuite.Core.Benchmark.Protocol;
using PCPerfSuite.Core.Benchmark.Worker;

namespace PCPerfSuite.Core.Benchmark.Memory;

/// <summary>
/// Benchs mémoire, côté worker. Débit : un tampon unique découpé par thread, chaque passe enchaîne écriture, lecture
/// (somme vérifiée) et copie, balayages entiers jusqu'à la durée de passe ; Go/s décimaux (10⁹ octets). Latence : pointer
/// chasing mono-thread, nanosecondes par pas, passes qui doivent rendre le même indice final. Les tailles viennent de
/// l'app (<see cref="MemoryBenchSizing"/>). Durées expérimentales (règle 6).
/// </summary>
public sealed class MemoryBenchRunner
{
    public const string WriteKey = "ecriture";
    public const string ReadKey = "lecture";
    public const string CopyKey = "copie";
    public const string LatencyKey = "latence";
    public const string BandwidthUnit = "Go/s";
    public const string LatencyUnit = "ns";

    /// <summary>Pas par tronçon de latence : entre deux, annulation et avancement.</summary>
    private const long LatencyChunkSteps = 1_000_000;

    public BenchJobResult Run(BenchJobRequest request, Action<BenchProgress>? progress, CancellationToken cancel)
    {
        RamJobParameters p = request.Ram ?? new RamJobParameters();
        if (p.Passes < 1) return BenchJobResult.Failure(request.Id, request.Kind, "nombre de passes invalide");

        return BenchTestKinds.Parse(request.Kind) switch
        {
            BenchTestKind.RamBandwidth => RunBandwidth(request, p, progress, cancel),
            BenchTestKind.RamLatency => RunLatency(request, p, progress, cancel),
            _ => BenchJobResult.Failure(request.Id, request.Kind, $"test mémoire inconnu « {request.Kind} »"),
        };
    }

    private static BenchJobResult RunBandwidth(BenchJobRequest request, RamJobParameters p, Action<BenchProgress>? progress, CancellationToken cancel)
    {
        if (p.BandwidthBytes <= 0) return BenchJobResult.Failure(request.Id, request.Kind, "taille du tampon de débit absente");
        if (p.PassSeconds <= 0) return BenchJobResult.Failure(request.Id, request.Kind, "durée de passe invalide");
        int threads = Math.Max(1, p.ThreadCount);

        var result = new BenchJobResult { JobId = request.Id, Kind = request.Kind };
        var stopwatch = Stopwatch.StartNew();
        MemoryBandwidthKernel kernel;
        try
        {
            kernel = new MemoryBandwidthKernel(p.BandwidthBytes, threads, CpuKernelCatalog.DefaultSeed);
        }
        catch (OutOfMemoryException)
        {
            return BenchJobResult.Failure(request.Id, request.Kind, $"mémoire insuffisante pour un tampon de {p.BandwidthBytes / MemoryBenchSizing.Mebibyte} Mo");
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return BenchJobResult.Failure(request.Id, request.Kind, ex.Message);
        }

        MemoryOperation[] operations = [MemoryOperation.Write, MemoryOperation.Read, MemoryOperation.Copy];
        string[] keys = [WriteKey, ReadKey, CopyKey];
        string[] labels = ["Écriture", "Lecture", "Copie"];
        var rates = new double[operations.Length][];
        for (int o = 0; o < operations.Length; o++) rates[o] = new double[p.Passes];
        double plannedTotal = p.Passes * operations.Length * p.PassSeconds;
        double plannedDone = 0;
        int readMismatches = 0;

        void Report(string phase, string? detail, double? value)
            => progress?.Invoke(new BenchProgress
            {
                JobId = request.Id,
                Phase = phase,
                Percent = plannedTotal > 0 ? Math.Clamp(plannedDone / plannedTotal * 100, 0, 100) : 0,
                Detail = detail,
                Value = value,
                Unit = value is null ? null : BandwidthUnit,
            });

        try
        {
            using var team = new MemoryLoadTeam(kernel);
            Report("préparation", $"tampon de {kernel.Length / MemoryBenchSizing.Mebibyte} Mo, {threads} thread(s)", null);
            team.Start();
            team.Fill(cancel);
            team.Calibrate(cancel);

            for (int pass = 0; pass < p.Passes; pass++)
            {
                for (int o = 0; o < operations.Length; o++)
                {
                    cancel.ThrowIfCancellationRequested();
                    Report($"passe {pass + 1}/{p.Passes}", labels[o], null);
                    MemorySweepOutcome outcome = team.Measure(operations[o], TimeSpan.FromSeconds(p.PassSeconds), cancel);
                    readMismatches += outcome.Mismatches;
                    rates[o][pass] = outcome.BytesPerSecond / 1e9;
                    plannedDone += p.PassSeconds;
                    Report($"passe {pass + 1}/{p.Passes}", labels[o], rates[o][pass]);
                }
            }

            for (int o = 0; o < operations.Length; o++)
            {
                result.Measurements.Add(BenchMeasurement.From(keys[o], labels[o], BandwidthUnit, rates[o]));
            }
            result.Succeeded = true;
        }
        catch (OperationCanceledException)
        {
            result.Succeeded = false;
            result.Error = BenchJobDispatcher.CancelledError;
        }
        catch (Exception ex)
        {
            result.Succeeded = false;
            result.Error = $"{ex.GetType().Name} : {ex.Message}";
        }
        finally
        {
            result.DurationSeconds = stopwatch.Elapsed.TotalSeconds;
            result.ChecksumMismatch = readMismatches > 0;
            result.IsComparable = kernel.IsComparable;
            result.Notes["tampon-mo"] = (kernel.Length / MemoryBenchSizing.Mebibyte).ToString();
            result.Notes["threads"] = threads.ToString();
            result.Notes["jeu-instructions"] = kernel.InstructionSet;
            result.Notes["passes"] = $"{p.Passes} × {p.PassSeconds:0.##} s";
            result.Notes["erreurs-lecture"] = readMismatches.ToString();
            result.Notes["unite"] = "Go/s décimaux (10⁹ octets/s) ; la copie compte lus + écrits";
            kernel.Dispose();
        }

        return result;
    }

    private static BenchJobResult RunLatency(BenchJobRequest request, RamJobParameters p, Action<BenchProgress>? progress, CancellationToken cancel)
    {
        if (p.LatencyBytes <= 0) return BenchJobResult.Failure(request.Id, request.Kind, "taille du tampon de latence absente");
        if (p.LatencySteps <= 0) return BenchJobResult.Failure(request.Id, request.Kind, "nombre de pas invalide");

        var result = new BenchJobResult { JobId = request.Id, Kind = request.Kind };
        var stopwatch = Stopwatch.StartNew();
        double plannedTotal = p.Passes * p.LatencySteps;
        double plannedDone = 0;

        void Report(string phase, string? detail, double? value)
            => progress?.Invoke(new BenchProgress
            {
                JobId = request.Id,
                Phase = phase,
                Percent = plannedTotal > 0 ? Math.Clamp(plannedDone / plannedTotal * 100, 0, 100) : 0,
                Detail = detail,
                Value = value,
                Unit = value is null ? null : LatencyUnit,
            });

        MemoryLatencyKernel? kernel = null;
        var nanoseconds = new double[p.Passes];
        var finalIndices = new uint[p.Passes];
        bool permutationIntact = true;
        try
        {
            Report("préparation", $"tampon de {p.LatencyBytes / MemoryBenchSizing.Mebibyte} Mo, chaîne à cycle unique", null);
            try
            {
                kernel = new MemoryLatencyKernel(p.LatencyBytes, CpuKernelCatalog.DefaultSeed, _ => { }, cancel);
            }
            catch (OutOfMemoryException)
            {
                return BenchJobResult.Failure(request.Id, request.Kind, $"mémoire insuffisante pour un tampon de {p.LatencyBytes / MemoryBenchSizing.Mebibyte} Mo");
            }
            catch (ArgumentOutOfRangeException ex)
            {
                return BenchJobResult.Failure(request.Id, request.Kind, ex.Message);
            }

            // Préchauffe non comptée : la première chaîne paie la montée en fréquence et le remplissage de la TLB (vu à
            // l'essai : première passe 15 % plus lente que les suivantes).
            cancel.ThrowIfCancellationRequested();
            Report("préchauffe", "chaîne non comptée", null);
            kernel.Chase(0, Math.Min(LatencyChunkSteps, p.LatencySteps));

            for (int pass = 0; pass < p.Passes; pass++)
            {
                long remaining = p.LatencySteps;
                double seconds = 0;
                uint index = 0;
                while (remaining > 0)
                {
                    cancel.ThrowIfCancellationRequested();
                    long chunk = Math.Min(LatencyChunkSteps, remaining);
                    LatencyChaseOutcome outcome = kernel.Chase(index, chunk);
                    seconds += outcome.Seconds;
                    index = outcome.FinalIndex;
                    remaining -= chunk;
                    plannedDone += chunk;
                    Report($"passe {pass + 1}/{p.Passes}", $"{kernel.LineCount:N0} lignes", outcome.NanosecondsPerStep);
                }
                nanoseconds[pass] = seconds * 1e9 / p.LatencySteps;
                finalIndices[pass] = index;
            }

            permutationIntact = kernel.VerifyPermutationChecksum();
            result.Measurements.Add(BenchMeasurement.From(LatencyKey, "Latence (pointer chasing)", LatencyUnit, nanoseconds, higherIsBetter: false));
            result.Succeeded = true;
        }
        catch (OperationCanceledException)
        {
            result.Succeeded = false;
            result.Error = BenchJobDispatcher.CancelledError;
        }
        catch (Exception ex)
        {
            result.Succeeded = false;
            result.Error = $"{ex.GetType().Name} : {ex.Message}";
        }
        finally
        {
            result.DurationSeconds = stopwatch.Elapsed.TotalSeconds;
            bool passesAgree = finalIndices.Take(result.Succeeded ? p.Passes : 0).Distinct().Count() <= 1;
            result.ChecksumMismatch = !permutationIntact || !passesAgree;
            if (kernel is not null)
            {
                result.Notes["tampon-mo"] = (kernel.Length / MemoryBenchSizing.Mebibyte).ToString();
                result.Notes["lignes"] = kernel.LineCount.ToString();
            }
            result.Notes["pages"] = "4 Ko (grandes pages écartées : SeLockMemoryPrivilege)";
            result.Notes["pas-par-passe"] = p.LatencySteps.ToString();
            result.Notes["passes"] = p.Passes.ToString();
            result.Notes["verification"] = permutationIntact && passesAgree ? "permutation intacte, passes concordantes" : permutationIntact ? "indices finaux discordants" : "permutation altérée en mémoire";
            kernel?.Dispose();
        }

        return result;
    }
}

/// <summary>Issue d'une phase de mesure de débit : octets déplacés par seconde, tous threads confondus, et sommes de
/// lecture fausses.</summary>
public sealed record MemorySweepOutcome(double BytesPerSecond, int Mismatches);

/// <summary>
/// L'équipe de threads du test de débit : chacun balaie sa tranche, tous commencent et finissent une phase ensemble
/// (barrière). Une phase de mesure fait des balayages entiers jusqu'à la durée demandée ; le débit d'un thread est ses
/// octets sur son temps, le débit total leur somme.
/// </summary>
internal sealed class MemoryLoadTeam : IDisposable
{
    private readonly MemoryBandwidthKernel _kernel;
    private readonly int _count;
    private readonly Barrier _barrier;
    private readonly Thread[] _threads;
    private readonly ulong[] _expected;
    private readonly double[] _rates;
    private readonly int[] _mismatches;
    private readonly Exception?[] _failures;
    private volatile MemoryPhase _phase = new(MemoryPhaseKind.Exit, MemoryOperation.Read, TimeSpan.Zero);
    private CancellationToken _cancel;
    private bool _started;
    private bool _exited;

    public MemoryLoadTeam(MemoryBandwidthKernel kernel)
    {
        _kernel = kernel;
        _count = kernel.ThreadCount;
        _barrier = new Barrier(_count + 1);
        _threads = new Thread[_count];
        _expected = new ulong[_count];
        _rates = new double[_count];
        _mismatches = new int[_count];
        _failures = new Exception?[_count];
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
        for (int i = 0; i < _count; i++)
        {
            int index = i;
            _threads[i] = new Thread(() => ThreadMain(index), 1 << 18) { IsBackground = true, Name = $"PCPerfSuite bench RAM {index}" };
            _threads[i].Start();
        }
    }

    /// <summary>Écrit le motif partout (c'est aussi ce qui fait venir les pages avant le chronomètre).</summary>
    public void Fill(CancellationToken cancel) => RunPhase(new MemoryPhase(MemoryPhaseKind.Once, MemoryOperation.Write, TimeSpan.Zero), cancel);

    /// <summary>Lit une fois chaque tranche et retient sa somme comme étalon.</summary>
    public void Calibrate(CancellationToken cancel) => RunPhase(new MemoryPhase(MemoryPhaseKind.Calibrate, MemoryOperation.Read, TimeSpan.Zero), cancel);

    public MemorySweepOutcome Measure(MemoryOperation operation, TimeSpan duration, CancellationToken cancel)
    {
        RunPhase(new MemoryPhase(MemoryPhaseKind.Measure, operation, duration), cancel);
        return new MemorySweepOutcome(_rates.Sum(), _mismatches.Sum());
    }

    private void RunPhase(MemoryPhase phase, CancellationToken cancel)
    {
        if (!_started) throw new InvalidOperationException("Équipe non démarrée.");
        if (_exited) throw new InvalidOperationException("Équipe arrêtée.");
        cancel.ThrowIfCancellationRequested();

        _cancel = cancel;
        _phase = phase;
        Array.Clear(_rates);
        Array.Clear(_mismatches);
        _barrier.SignalAndWait();
        _barrier.SignalAndWait();

        Exception? failure = _failures.FirstOrDefault(f => f is not null);
        if (failure is not null) throw new InvalidOperationException($"thread de charge mémoire en échec : {failure.Message}", failure);
        cancel.ThrowIfCancellationRequested();
    }

    private void ThreadMain(int index)
    {
        try
        {
            while (true)
            {
                _barrier.SignalAndWait();
                MemoryPhase phase = _phase;
                if (phase.Kind == MemoryPhaseKind.Exit)
                {
                    _barrier.SignalAndWait();
                    return;
                }

                try
                {
                    RunOnThread(index, phase);
                }
                catch (Exception ex)
                {
                    _failures[index] = ex;
                }
                _barrier.SignalAndWait();
            }
        }
        catch (Exception ex)
        {
            _failures[index] = ex;
            try { _barrier.RemoveParticipant(); } catch (Exception) { /* déjà retiré */ }
        }
    }

    private void RunOnThread(int index, MemoryPhase phase)
    {
        switch (phase.Kind)
        {
            case MemoryPhaseKind.Once:
                _kernel.Sweep(phase.Operation, index);
                break;

            case MemoryPhaseKind.Calibrate:
                _expected[index] = _kernel.Sweep(MemoryOperation.Read, index);
                break;

            case MemoryPhaseKind.Measure:
            {
                long bytesPerSweep = _kernel.BytesPerSweep(phase.Operation, index);
                if (bytesPerSweep <= 0) return;
                long ticksToRun = (long)(phase.Duration.TotalSeconds * Stopwatch.Frequency);
                long start = Stopwatch.GetTimestamp();
                long sweeps = 0;
                int mismatches = 0;
                long elapsed;
                do
                {
                    ulong checksum = _kernel.Sweep(phase.Operation, index);
                    if (phase.Operation == MemoryOperation.Read && checksum != _expected[index]) mismatches++;
                    sweeps++;
                    elapsed = Stopwatch.GetTimestamp() - start;
                }
                while (elapsed < ticksToRun && !_cancel.IsCancellationRequested);

                double seconds = (double)elapsed / Stopwatch.Frequency;
                _rates[index] = seconds > 0 ? sweeps * (double)bytesPerSweep / seconds : 0;
                _mismatches[index] = mismatches;
                break;
            }
        }
    }

    public void Dispose()
    {
        if (_started && !_exited)
        {
            try
            {
                _phase = new MemoryPhase(MemoryPhaseKind.Exit, MemoryOperation.Read, TimeSpan.Zero);
                _cancel = new CancellationToken(true);
                _barrier.SignalAndWait(TimeSpan.FromSeconds(5));
                _barrier.SignalAndWait(TimeSpan.FromSeconds(5));
            }
            catch (Exception)
            {
                // Threads d'arrière-plan : ils meurent avec le processus.
            }
            _exited = true;
        }
        _barrier.Dispose();
    }
}

internal enum MemoryPhaseKind { Once, Calibrate, Measure, Exit }

internal sealed record MemoryPhase(MemoryPhaseKind Kind, MemoryOperation Operation, TimeSpan Duration);
