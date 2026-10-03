using PCPerfSuite.Core.Benchmark.Disk;
using PCPerfSuite.Core.Benchmark.Memory;
using PCPerfSuite.Core.Benchmark.Protocol;
using PCPerfSuite.Core.Hardware.Cpu;

namespace PCPerfSuite.Core.Benchmark.Session;

/// <summary>Choix de l'utilisateur qui pèsent sur un plan (page Bench).</summary>
public sealed record BenchPlanOptions(bool SustainedEnabled, long DiskFileBytes, bool DiskIsRotational = false);

/// <summary>
/// Construit les demandes de test à partir de la machine, en logique pure : le mono-thread épinglé sur le premier
/// fil du deuxième cœur de la plus haute classe d'efficacité (un cœur P sur un hybride, le CCD au grand L3 sur un X3D),
/// le multi sur tous les processeurs logiques, la mémoire épinglée sur les cœurs physiques de la plus haute classe
/// (8 au plus : la RAM sature avant ; la latence sur le cœur du mono), les tailles de <see cref="MemoryBenchSizing"/>, le
/// disque d'après son volume. Donne aussi la durée estimée de chaque test et les valeurs de sa ligne au journal de session.
/// </summary>
public static class BenchPlanner
{
    public const double CpuWarmupSeconds = 5;
    public const double CpuPassSeconds = 2;
    public const int CpuPasses = 3;
    public const double CpuSustainedSeconds = 180;
    public const int MaxMemoryThreads = 8;
    public const double DiskPhaseSeconds = DiskBenchPlan.DefaultPhaseSeconds;

    /// <summary>Le processeur logique du mono-thread : le premier fil du **deuxième** cœur de la plus haute classe
    /// d'efficacité (le premier s'il n'y en a qu'un), dans le groupe de cache au grand L3 s'il y en a un. Pas le cœur 0 :
    /// Windows y range par défaut les interruptions et les DPC, et les passes y sont plus dispersées (vu à l'essai sur un
    /// Ryzen 7 5800H : CV 2 à 9 % sur le cœur 0, 1 à 4 % sur le cœur 1). Null si la topologie manque (thread non épinglé).</summary>
    public static LogicalProcessorTarget? MonoTarget(CpuTopology? topology)
    {
        if (topology is null) return null;
        CacheCluster? cluster = topology.Clusters.FirstOrDefault(c => c.HasLargerL3) ?? topology.Clusters.FirstOrDefault();
        IReadOnlyList<PhysicalCore>? cores = cluster?.Classes.OrderByDescending(c => c.EfficiencyClass).FirstOrDefault()?.Cores;
        PhysicalCore? core = cores is { Count: > 1 } ? cores[1] : cores?.FirstOrDefault();
        LogicalProcessor? thread = core?.Threads.FirstOrDefault();
        return thread is null ? null : Target(thread);
    }

    /// <summary>Un thread par processeur logique, épinglé ; vide sans topologie (threads libres).</summary>
    public static IReadOnlyList<LogicalProcessorTarget> MultiTargets(CpuTopology? topology)
        => topology?.LogicalProcessors.Select(Target).ToList() ?? [];

    public static int MemoryThreads(CpuTopology? topology, int fallbackProcessorCount)
    {
        int pinned = MemoryTargets(topology).Count;
        return pinned > 0 ? pinned : Math.Clamp(fallbackProcessorCount / 2, 1, MaxMemoryThreads);
    }

    /// <summary>Les threads du test de débit mémoire : le premier fil de chaque cœur de la plus haute classe (un cœur P
    /// sur un hybride), <see cref="MaxMemoryThreads"/> au plus, pris tour à tour dans chaque groupe de cache (sur deux CCD,
    /// chacun a son propre lien vers la mémoire). Vide sans topologie (threads libres).</summary>
    public static IReadOnlyList<LogicalProcessorTarget> MemoryTargets(CpuTopology? topology)
    {
        if (topology is null) return [];
        int top = topology.TopEfficiencyClass;
        List<Queue<PhysicalCore>> perCluster = topology.Clusters
            .Select(c => new Queue<PhysicalCore>(c.Classes.Where(k => k.EfficiencyClass == top).SelectMany(k => k.Cores)))
            .Where(q => q.Count > 0)
            .ToList();
        var targets = new List<LogicalProcessorTarget>();
        while (targets.Count < MaxMemoryThreads && perCluster.Any(q => q.Count > 0))
        {
            foreach (Queue<PhysicalCore> cores in perCluster)
            {
                if (targets.Count >= MaxMemoryThreads || !cores.TryDequeue(out PhysicalCore? core)) continue;
                if (core.Threads.FirstOrDefault() is { } thread) targets.Add(Target(thread));
            }
        }
        return targets;
    }

    public static BenchJobRequest CpuRequest(BenchTestKind kind, CpuTopology? topology, bool sustained, int fallbackProcessorCount)
    {
        var parameters = new CpuJobParameters
        {
            WarmupSeconds = CpuWarmupSeconds,
            PassSeconds = CpuPassSeconds,
            Passes = CpuPasses,
            SustainedSeconds = sustained ? CpuSustainedSeconds : 0,
        };
        if (kind == BenchTestKind.CpuMono)
        {
            LogicalProcessorTarget? target = MonoTarget(topology);
            parameters.Threads = target is null ? null : [target];
            parameters.ThreadCount = 1;
        }
        else
        {
            IReadOnlyList<LogicalProcessorTarget> targets = MultiTargets(topology);
            parameters.Threads = targets.Count > 0 ? targets.ToList() : null;
            parameters.ThreadCount = targets.Count > 0 ? targets.Count : Math.Max(1, fallbackProcessorCount);
        }
        return new BenchJobRequest { Kind = BenchTestKinds.Key(kind), Cpu = parameters };
    }

    /// <summary>Débit : un thread épinglé par cœur de <see cref="MemoryTargets"/> ; latence : un thread, épinglé comme le
    /// mono-thread. Sans topologie, des threads libres (<see cref="MemoryThreads"/>).</summary>
    public static BenchJobRequest RamRequest(BenchTestKind kind, MemoryBenchSizes sizes, CpuTopology? topology, int fallbackProcessorCount)
    {
        IReadOnlyList<LogicalProcessorTarget> targets = kind == BenchTestKind.RamBandwidth
            ? MemoryTargets(topology)
            : MonoTarget(topology) is { } mono ? [mono] : [];
        return new BenchJobRequest
        {
            Kind = BenchTestKinds.Key(kind),
            Ram = new RamJobParameters
            {
                BandwidthBytes = sizes.BandwidthBytes ?? 0,
                LatencyBytes = sizes.LatencyBytes ?? 0,
                ThreadCount = kind == BenchTestKind.RamBandwidth ? MemoryThreads(topology, fallbackProcessorCount) : 1,
                Threads = targets.Count > 0 ? targets.ToList() : null,
            },
        };
    }

    public static BenchJobRequest DiskRequest(string path, BenchVolume volume, long fileBytes)
        => new()
        {
            Kind = BenchTestKinds.Key(BenchTestKind.Disk),
            Disk = new DiskJobParameters
            {
                Path = path,
                FileSizeBytes = fileBytes,
                SectorBytes = volume.SectorBytes,
                PhaseSeconds = DiskPhaseSeconds,
                IsRotational = volume.IsRotational,
                WriteBudgetBytes = DiskBenchPlan.DefaultWriteBudgetFactor * fileBytes,
            },
        };

    /// <summary>Durée estimée d'un test, préchauffe et préparation comprises. Expérimental : à comparer aux durées vraies.</summary>
    public static double EstimateSeconds(BenchTestKind kind, BenchPlanOptions options)
    {
        double cpuBlock = CpuPasses * Kernels.CpuKernelCatalog.Keys.Count * CpuPassSeconds;
        switch (kind)
        {
            case BenchTestKind.CpuMono:
            case BenchTestKind.CpuMulti:
                return CpuWarmupSeconds + cpuBlock + (options.SustainedEnabled ? CpuSustainedSeconds + cpuBlock : 0);
            case BenchTestKind.RamBandwidth:
                return 3 + 3 * 3 * 1;
            case BenchTestKind.RamLatency:
                return 4 + 3 * 2.5;
            case BenchTestKind.Disk:
            {
                int phases = options.DiskIsRotational ? 4 : 8;
                double prefillMBps = options.DiskIsRotational ? 150 : 800;
                double prefill = options.DiskFileBytes / 1e6 / prefillMBps;
                return 3 + prefill + phases * DiskPhaseSeconds;
            }
            default:
                return 0;
        }
    }

    /// <summary>« 2 min 30 s », « 45 s », « 1 h 05 min ».</summary>
    public static string DescribeDuration(double seconds)
    {
        if (seconds < 60) return $"{Math.Ceiling(seconds):0} s";
        var span = TimeSpan.FromSeconds(Math.Ceiling(seconds / 5) * 5);
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours} h {span.Minutes:00} min";
        return span.Seconds == 0 ? $"{span.Minutes} min" : $"{span.Minutes} min {span.Seconds:00} s";
    }

    /// <summary>Valeurs de la ligne du journal de session : nombres ou mots, la lettre du volume sans deux-points.</summary>
    public static IReadOnlyDictionary<string, string> JournalValues(BenchJobRequest request, BenchVolume? volume = null)
    {
        var values = new Dictionary<string, string>();
        if (request.Cpu is { } cpu) values["threads"] = (cpu.Threads?.Count ?? cpu.ThreadCount).ToString();
        if (request.Ram is { } ram)
        {
            long bytes = request.Kind == BenchTestKinds.Key(BenchTestKind.RamLatency) ? ram.LatencyBytes : ram.BandwidthBytes;
            values["tampon-mo"] = (bytes / MemoryBenchSizing.Mebibyte).ToString();
        }
        if (request.Disk is { } disk)
        {
            values[BenchRecoveryHandler.FileSizeKey] = (disk.FileSizeBytes / DiskBenchPlan.Mebibyte).ToString();
            if (volume is not null)
            {
                values[BenchRecoveryHandler.VolumeKey] = volume.DriveLetter.TrimEnd(':');
                values[BenchRecoveryHandler.SystemVolumeKey] = volume.IsSystem ? "oui" : "non";
            }
        }
        return values;
    }

    private static LogicalProcessorTarget Target(LogicalProcessor processor)
        => new() { Group = processor.Id.Group, Index = processor.Id.Index, CpuSetId = processor.CpuSetId };
}
