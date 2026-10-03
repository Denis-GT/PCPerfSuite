using System.Diagnostics;
using PCPerfSuite.Core.Benchmark.Cpu;
using PCPerfSuite.Core.Benchmark.Memory;
using PCPerfSuite.Core.Benchmark.Protocol;

namespace PCPerfSuite.Core.Benchmark.Worker;

/// <summary>Exécute une demande de test dans le worker, selon son type. Ne lève jamais : un échec est un résultat.</summary>
public static class BenchJobDispatcher
{
    public const string CancelledError = "arrêté";

    public static BenchJobResult Run(BenchJobRequest request, Action<BenchProgress>? progress, CancellationToken cancel)
    {
        var stopwatch = Stopwatch.StartNew();
        BenchJobResult result;
        try
        {
            result = BenchTestKinds.Parse(request.Kind) switch
            {
                BenchTestKind.CpuMono or BenchTestKind.CpuMulti => new CpuBenchRunner().Run(request, progress, cancel),
                BenchTestKind.RamBandwidth or BenchTestKind.RamLatency => new MemoryBenchRunner().Run(request, progress, cancel),
                _ => BenchJobResult.Failure(request.Id, request.Kind, $"test inconnu « {request.Kind} »"),
            };
        }
        catch (OperationCanceledException)
        {
            result = BenchJobResult.Failure(request.Id, request.Kind, CancelledError);
        }
        catch (Exception ex)
        {
            result = BenchJobResult.Failure(request.Id, request.Kind, $"{ex.GetType().Name} : {ex.Message}");
        }

        if (result.DurationSeconds <= 0) result.DurationSeconds = stopwatch.Elapsed.TotalSeconds;
        return result;
    }
}
