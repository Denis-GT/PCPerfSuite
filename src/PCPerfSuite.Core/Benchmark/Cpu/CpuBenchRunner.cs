using System.Diagnostics;
using PCPerfSuite.Core.Benchmark.Kernels;
using PCPerfSuite.Core.Benchmark.Protocol;

namespace PCPerfSuite.Core.Benchmark.Cpu;

/// <summary>
/// Bench processeur, côté worker : une équipe de threads (un, épinglé, pour le mono-thread ; un par processeur logique
/// pour le multi), des noyaux à résultat vérifié, et le protocole commun : préchauffe non comptée, passes « rafale »
/// dans les premières secondes, charge continue (noyaux alternés, FMA plafonné) jusqu'au point « soutenu » puis passes
/// « soutenu ». L'écart rafale / soutenu est déjà un diagnostic (PL1/PL2, Tau, refroidissement). Durées et seuils
/// expérimentaux (règle 6).
/// </summary>
public sealed class CpuBenchRunner
{
    public const string BurstSuffix = ".rafale";
    public const string SustainedSuffix = ".soutenu";

    /// <summary>Durée d'un tronçon de charge continue : FMA jamais plus longtemps d'affilée que le plafond demandé.</summary>
    private static readonly TimeSpan LoadSliceCap = TimeSpan.FromSeconds(10);

    public BenchJobResult Run(BenchJobRequest request, Action<BenchProgress>? progress, CancellationToken cancel)
    {
        CpuJobParameters p = request.Cpu ?? new CpuJobParameters();
        IReadOnlyList<string> kernelKeys = p.Kernels is { Count: > 0 } ? p.Kernels : CpuKernelCatalog.Keys;
        foreach (string key in kernelKeys)
        {
            if (!CpuKernelCatalog.IsKnown(key)) return BenchJobResult.Failure(request.Id, request.Kind, $"noyau inconnu « {key} »");
        }
        if (p.Passes < 1 || p.PassSeconds <= 0) return BenchJobResult.Failure(request.Id, request.Kind, "paramètres de passe invalides");

        IReadOnlyList<LogicalProcessorTarget>? targets = p.Threads is { Count: > 0 } ? p.Threads : null;
        int threadCount = targets?.Count ?? Math.Max(1, p.ThreadCount);

        var result = new BenchJobResult { JobId = request.Id, Kind = request.Kind };
        var stopwatch = Stopwatch.StartNew();
        using var team = new CpuLoadTeam(threadCount, targets, kernelKeys, CpuKernelCatalog.DefaultSeed);

        double passSeconds = p.PassSeconds;
        double measureBlock = p.Passes * kernelKeys.Count * passSeconds;
        double sustainedWait = Math.Max(0, p.SustainedSeconds - measureBlock);
        double plannedTotal = p.WarmupSeconds + measureBlock + (p.SustainedSeconds > 0 ? sustainedWait + measureBlock : 0);
        double plannedDone = 0;

        void Report(string phase, string? detail = null, double? value = null, string? unit = null)
            => progress?.Invoke(new BenchProgress
            {
                JobId = request.Id,
                Phase = phase,
                Percent = plannedTotal > 0 ? Math.Clamp(plannedDone / plannedTotal * 100, 0, 100) : 0,
                Detail = detail,
                Value = value,
                Unit = unit,
            });

        try
        {
            Report("démarrage", $"{threadCount} thread(s)");
            team.Start();
            team.RunPhase(CpuLoadPhase.Calibrate(), cancel);
            if (!team.ChecksumsAgree) result.ChecksumMismatch = true;

            if (cancel.IsCancellationRequested) return Cancelled(result, stopwatch);
            if (p.WarmupSeconds > 0)
            {
                Report("préchauffe", $"{p.WarmupSeconds:0} s non comptées");
                team.RunPhase(CpuLoadPhase.Warmup(TimeSpan.FromSeconds(p.WarmupSeconds)), cancel);
                plannedDone += p.WarmupSeconds;
            }

            var burstStart = Stopwatch.StartNew();
            double[][] burst = MeasurePasses(team, kernelKeys, p, "rafale", cancel, Report, ref plannedDone, result);
            if (cancel.IsCancellationRequested) return Cancelled(result, stopwatch);
            AddMeasurements(result, team, kernelKeys, burst, BurstSuffix, "rafale");

            if (p.SustainedSeconds > 0)
            {
                int kernelIndex = 0;
                TimeSpan slice = TimeSpan.FromSeconds(Math.Min(LoadSliceCap.TotalSeconds, Math.Max(0.5, p.MaxFloatStretchSeconds)));
                while (burstStart.Elapsed.TotalSeconds < p.SustainedSeconds)
                {
                    if (cancel.IsCancellationRequested) return Cancelled(result, stopwatch);
                    double remaining = p.SustainedSeconds - burstStart.Elapsed.TotalSeconds;
                    TimeSpan duration = TimeSpan.FromSeconds(Math.Min(slice.TotalSeconds, remaining));
                    if (p.DutyLoadMs is > 0 && p.DutyRestMs is > 0)
                    {
                        duration = TimeSpan.FromMilliseconds(Math.Min(p.DutyLoadMs.Value, duration.TotalMilliseconds));
                    }
                    Report("charge continue", $"{remaining:0} s avant la mesure soutenue");
                    CpuLoadOutcome load = team.RunPhase(CpuLoadPhase.Measure(kernelIndex, duration), cancel);
                    if (load.Mismatches > 0) result.ChecksumMismatch = true;
                    plannedDone += duration.TotalSeconds;
                    if (p.DutyLoadMs is > 0 && p.DutyRestMs is > 0)
                    {
                        Thread.Sleep(p.DutyRestMs.Value);
                        plannedDone += p.DutyRestMs.Value / 1000.0;
                    }
                    kernelIndex = (kernelIndex + 1) % kernelKeys.Count;
                }

                double[][] sustained = MeasurePasses(team, kernelKeys, p, "soutenu", cancel, Report, ref plannedDone, result);
                if (cancel.IsCancellationRequested) return Cancelled(result, stopwatch);
                AddMeasurements(result, team, kernelKeys, sustained, SustainedSuffix, "soutenu");
            }

            team.RunPhase(CpuLoadPhase.Exit(), cancel);
            result.Succeeded = true;
        }
        catch (OperationCanceledException)
        {
            return Cancelled(result, stopwatch);
        }
        catch (Exception ex)
        {
            result.Succeeded = false;
            result.Error = $"{ex.GetType().Name} : {ex.Message}";
        }
        finally
        {
            result.DurationSeconds = stopwatch.Elapsed.TotalSeconds;
            FillNotes(result, team, kernelKeys, p, threadCount, targets is not null);
        }

        return result;
    }

    private static BenchJobResult Cancelled(BenchJobResult result, Stopwatch stopwatch)
    {
        result.Succeeded = false;
        result.Error = Worker.BenchJobDispatcher.CancelledError;
        result.DurationSeconds = stopwatch.Elapsed.TotalSeconds;
        return result;
    }

    private static double[][] MeasurePasses(CpuLoadTeam team, IReadOnlyList<string> keys, CpuJobParameters p, string label,
        CancellationToken cancel, Action<string, string?, double?, string?> report, ref double plannedDone, BenchJobResult result)
    {
        var rates = new double[keys.Count][];
        for (int k = 0; k < keys.Count; k++) rates[k] = new double[p.Passes];

        for (int pass = 0; pass < p.Passes; pass++)
        {
            for (int k = 0; k < keys.Count; k++)
            {
                if (cancel.IsCancellationRequested) return rates;
                report($"{label}, passe {pass + 1}/{p.Passes}", team.Label(k), null, null);
                CpuLoadOutcome outcome = team.RunPhase(CpuLoadPhase.Measure(k, TimeSpan.FromSeconds(p.PassSeconds)), cancel);
                if (outcome.Mismatches > 0) result.ChecksumMismatch = true;
                rates[k][pass] = outcome.Rate;
                plannedDone += p.PassSeconds;
                report($"{label}, passe {pass + 1}/{p.Passes}", team.Label(k), outcome.Rate, team.Unit(k));
            }
        }
        return rates;
    }

    private static void AddMeasurements(BenchJobResult result, CpuLoadTeam team, IReadOnlyList<string> keys, double[][] rates,
        string suffix, string phaseLabel)
    {
        for (int k = 0; k < keys.Count; k++)
        {
            result.Measurements.Add(BenchMeasurement.From(keys[k] + suffix, $"{team.Label(k)} ({phaseLabel})", team.Unit(k), rates[k]));
        }
    }

    private static void FillNotes(BenchJobResult result, CpuLoadTeam team, IReadOnlyList<string> keys, CpuJobParameters p, int threadCount, bool pinned)
    {
        result.Notes["threads"] = threadCount.ToString();
        result.Notes["epinglage"] = pinned ? team.DescribePlacement() : "non épinglé";
        result.Notes["jeu-instructions"] = team.DescribeInstructionSets();
        result.Notes["noyaux"] = string.Join(", ", keys);
        result.Notes["passes"] = $"{p.Passes} × {p.PassSeconds:0.##} s";
        result.Notes["prechauffe-s"] = p.WarmupSeconds.ToString("0.##");
        result.Notes["soutenu-s"] = p.SustainedSeconds.ToString("0");
        result.Notes["etalons-identiques"] = team.ChecksumsAgree ? "oui" : "non";
        result.Notes["erreurs-calcul"] = team.TotalMismatches.ToString();
        result.IsComparable = team.AllComparable;
    }
}

/// <summary>Une phase demandée à l'équipe de threads.</summary>
internal sealed record CpuLoadPhase(CpuLoadPhaseKind Kind, int KernelIndex, TimeSpan Duration)
{
    public static CpuLoadPhase Calibrate() => new(CpuLoadPhaseKind.Calibrate, 0, TimeSpan.Zero);
    public static CpuLoadPhase Warmup(TimeSpan duration) => new(CpuLoadPhaseKind.Warmup, 0, duration);
    public static CpuLoadPhase Measure(int kernelIndex, TimeSpan duration) => new(CpuLoadPhaseKind.Measure, kernelIndex, duration);
    public static CpuLoadPhase Exit() => new(CpuLoadPhaseKind.Exit, 0, TimeSpan.Zero);
}

internal enum CpuLoadPhaseKind { Calibrate, Warmup, Measure, Exit }

/// <summary>Issue d'une phase de mesure : débit cumulé des threads dans l'unité du noyau, et sommes fausses.</summary>
internal sealed record CpuLoadOutcome(double Rate, int Mismatches);

/// <summary>
/// L'équipe de threads de charge : chacun crée ses noyaux, s'épingle, étalonne, puis exécute les phases que le
/// coordinateur lui donne, en se synchronisant sur une barrière (tous commencent et finissent une passe ensemble). Une
/// exception dans un thread est rapportée au coordinateur, jamais perdue.
/// </summary>
internal sealed class CpuLoadTeam : IDisposable
{
    private readonly int _count;
    private readonly IReadOnlyList<LogicalProcessorTarget>? _targets;
    private readonly IReadOnlyList<string> _keys;
    private readonly ulong _seed;
    private readonly Barrier _barrier;
    private readonly Thread[] _threads;
    private readonly ulong[][] _checksums;
    private readonly ulong[] _expected;
    private readonly KernelPassOutcome?[] _outcomes;
    private readonly ThreadPlacementResult?[] _placements;
    private readonly string[] _labels;
    private readonly string[] _units;
    private readonly double[] _unitScales;
    private readonly string[][] _instructionSets;
    private readonly bool[] _comparable;
    private readonly Exception?[] _failures;
    private volatile CpuLoadPhase _phase = CpuLoadPhase.Calibrate();
    private CancellationToken _cancel;
    private bool _started;
    private bool _exited;

    public CpuLoadTeam(int count, IReadOnlyList<LogicalProcessorTarget>? targets, IReadOnlyList<string> keys, ulong seed)
    {
        _count = count;
        _targets = targets;
        _keys = keys;
        _seed = seed;
        _barrier = new Barrier(count + 1);
        _threads = new Thread[count];
        _checksums = new ulong[count][];
        _expected = new ulong[keys.Count];
        _outcomes = new KernelPassOutcome?[count];
        _placements = new ThreadPlacementResult?[count];
        _instructionSets = new string[count][];
        _comparable = new bool[count];
        _failures = new Exception?[count];
        _labels = new string[keys.Count];
        _units = new string[keys.Count];
        _unitScales = new double[keys.Count];
        for (int k = 0; k < keys.Count; k++)
        {
            using ICpuKernel sample = CpuKernelCatalog.Create(keys[k], seed);
            _labels[k] = sample.Label;
            _units[k] = sample.Unit;
            _unitScales[k] = sample.UnitScale;
        }
    }

    public bool ChecksumsAgree { get; private set; } = true;

    public int TotalMismatches { get; private set; }

    public bool AllComparable => _comparable.All(c => c);

    public string Label(int kernelIndex) => _labels[kernelIndex];

    public string Unit(int kernelIndex) => _units[kernelIndex];

    public void Start()
    {
        if (_started) return;
        _started = true;
        for (int i = 0; i < _count; i++)
        {
            int index = i;
            _threads[i] = new Thread(() => ThreadMain(index), 1 << 20)
            {
                IsBackground = true,
                Name = $"PCPerfSuite bench {index}",
                Priority = ThreadPriority.Normal,
            };
            _threads[i].Start();
        }
    }

    /// <summary>Lance une phase et attend sa fin. Pour une mesure, rend le débit cumulé des threads.</summary>
    public CpuLoadOutcome RunPhase(CpuLoadPhase phase, CancellationToken cancel)
    {
        if (!_started) throw new InvalidOperationException("Équipe non démarrée.");
        if (_exited) return new CpuLoadOutcome(0, 0);

        _cancel = cancel;
        _phase = phase;
        Array.Clear(_outcomes);
        _barrier.SignalAndWait();
        _barrier.SignalAndWait();
        if (phase.Kind == CpuLoadPhaseKind.Exit) _exited = true;

        Exception? failure = _failures.FirstOrDefault(f => f is not null);
        if (failure is not null) throw new InvalidOperationException($"thread de charge en échec : {failure.Message}", failure);

        if (phase.Kind == CpuLoadPhaseKind.Calibrate)
        {
            for (int k = 0; k < _keys.Count; k++) _expected[k] = _checksums[0][k];
            ChecksumsAgree = _checksums.All(c => c.AsSpan().SequenceEqual(_checksums[0]));
            return new CpuLoadOutcome(0, ChecksumsAgree ? 0 : 1);
        }

        if (phase.Kind != CpuLoadPhaseKind.Measure) return new CpuLoadOutcome(0, 0);

        double unitScale = _unitScales[phase.KernelIndex];
        double rate = 0;
        int mismatches = 0;
        foreach (KernelPassOutcome? outcome in _outcomes)
        {
            if (outcome is null) continue;
            if (outcome.Seconds > 0) rate += outcome.Operations / outcome.Seconds / unitScale;
            mismatches += outcome.Mismatches;
        }
        TotalMismatches += mismatches;
        return new CpuLoadOutcome(rate, mismatches);
    }

    public string DescribePlacement() => ThreadPlacementResult.Summarize(_placements);

    public string DescribeInstructionSets()
        => string.Join(", ", _instructionSets.Where(s => s is not null).SelectMany(s => s).Distinct());

    private void ThreadMain(int index)
    {
        ICpuKernel[] kernels = Array.Empty<ICpuKernel>();
        try
        {
            if (_targets is not null) _placements[index] = ThreadPlacement.PinCurrentThread(_targets[index]);
            kernels = _keys.Select(key => CpuKernelCatalog.Create(key, _seed)).ToArray();
            _instructionSets[index] = kernels.Select(k => $"{k.Key} : {k.InstructionSet}").ToArray();
            _comparable[index] = kernels.All(k => k.IsComparable);
            _checksums[index] = new ulong[kernels.Length];

            while (true)
            {
                _barrier.SignalAndWait();
                CpuLoadPhase phase = _phase;
                if (phase.Kind == CpuLoadPhaseKind.Exit)
                {
                    _barrier.SignalAndWait();
                    return;
                }

                try
                {
                    RunPhaseOnThread(index, kernels, phase);
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
            // Le coordinateur attend sur la barrière : on la retire pour ne pas le bloquer à jamais.
            try { _barrier.RemoveParticipant(); } catch (Exception) { /* déjà retiré */ }
        }
        finally
        {
            foreach (ICpuKernel kernel in kernels) kernel.Dispose();
        }
    }

    private void RunPhaseOnThread(int index, ICpuKernel[] kernels, CpuLoadPhase phase)
    {
        switch (phase.Kind)
        {
            case CpuLoadPhaseKind.Calibrate:
                for (int k = 0; k < kernels.Length; k++) _checksums[index][k] = KernelPass.Calibrate(kernels[k]);
                break;

            case CpuLoadPhaseKind.Warmup:
            {
                long end = Stopwatch.GetTimestamp() + (long)(phase.Duration.TotalSeconds * Stopwatch.Frequency);
                int k = 0;
                while (Stopwatch.GetTimestamp() < end && !_cancel.IsCancellationRequested)
                {
                    kernels[k].Run();
                    k = (k + 1) % kernels.Length;
                }
                break;
            }

            case CpuLoadPhaseKind.Measure:
                _outcomes[index] = KernelPass.RunFor(kernels[phase.KernelIndex], _expected[phase.KernelIndex], phase.Duration, _cancel);
                break;
        }
    }

    public void Dispose()
    {
        if (_started && !_exited)
        {
            try
            {
                _phase = CpuLoadPhase.Exit();
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
