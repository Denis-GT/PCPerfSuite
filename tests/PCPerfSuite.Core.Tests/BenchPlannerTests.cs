using PCPerfSuite.Core.Benchmark;
using PCPerfSuite.Core.Benchmark.Disk;
using PCPerfSuite.Core.Benchmark.Memory;
using PCPerfSuite.Core.Benchmark.Protocol;
using PCPerfSuite.Core.Benchmark.Session;
using PCPerfSuite.Core.Hardware.Cpu;

namespace PCPerfSuite.Core.Tests;

public class BenchPlannerTests
{
    private static CpuTopology Hybrid() => CpuTopology.Build(CpuTopology.ParseCpuSets(CpuSetBuffers.Hybrid6P8E()), [])!;

    private static CpuTopology X3D()
    {
        (byte[] cpuSets, byte[] caches) = CpuSetBuffers.DualCcdX3D();
        return CpuTopology.Build(CpuTopology.ParseCpuSets(cpuSets), CpuTopology.ParseCaches(caches))!;
    }

    [Fact]
    public void Le_mono_vise_le_premier_fil_du_deuxieme_coeur_performant_avec_son_cpu_set()
    {
        // Hybride 6P+8E : les P-cores (classe 1) occupent les processeurs 0 à 11, deux fils par cœur. Le deuxième cœur
        // commence au processeur 2 ; le cœur 0 reçoit les interruptions de Windows.
        LogicalProcessorTarget? target = BenchPlanner.MonoTarget(Hybrid());

        Assert.NotNull(target);
        Assert.Equal(0, target.Group);
        Assert.Equal(2, target.Index);
        Assert.Equal(258u, target.CpuSetId);
        Assert.Null(BenchPlanner.MonoTarget(null));
    }

    [Fact]
    public void Avec_un_seul_coeur_le_mono_le_prend()
    {
        CpuTopology single = CpuTopology.Build(CpuTopology.ParseCpuSets(CpuSetBuffers.Concat([CpuSetBuffers.Entry(0, 0, 0, 0), CpuSetBuffers.Entry(1, 0, 0, 0)])), [])!;

        LogicalProcessorTarget? target = BenchPlanner.MonoTarget(single);

        Assert.Equal(0, target!.Index);
    }

    [Fact]
    public void Sur_un_x3d_le_mono_vise_le_ccd_au_grand_l3()
    {
        CpuTopology topology = X3D();
        CacheCluster vcache = topology.Clusters.First(c => c.HasLargerL3);

        LogicalProcessorTarget? target = BenchPlanner.MonoTarget(topology);

        Assert.NotNull(target);
        Assert.Contains(vcache.Cores.SelectMany(c => c.Threads), t => t.Id.Index == target.Index && t.Id.Group == target.Group);
    }

    [Fact]
    public void Le_multi_vise_tous_les_processeurs_logiques_et_la_memoire_les_coeurs_performants()
    {
        CpuTopology hybrid = Hybrid();

        Assert.Equal(20, BenchPlanner.MultiTargets(hybrid).Count);
        Assert.Empty(BenchPlanner.MultiTargets(null));
        Assert.Equal(6, BenchPlanner.MemoryThreads(hybrid, 20));
        Assert.Equal(8, BenchPlanner.MemoryThreads(X3D(), 32));
        Assert.Equal(4, BenchPlanner.MemoryThreads(null, 8));
        Assert.Equal(1, BenchPlanner.MemoryThreads(null, 1));
    }

    [Fact]
    public void Sur_deux_ccd_les_threads_memoire_alternent_entre_les_groupes_de_cache()
    {
        CpuTopology topology = X3D();

        IReadOnlyList<LogicalProcessorTarget> targets = BenchPlanner.MemoryTargets(topology);

        Assert.Equal(BenchPlanner.MaxMemoryThreads, targets.Count);
        int ClusterOf(LogicalProcessorTarget t) => topology.Clusters
            .Select((cluster, index) => (cluster, index))
            .First(c => c.cluster.Cores.SelectMany(k => k.Threads).Any(p => p.Id.Index == t.Index && p.Id.Group == t.Group)).index;
        Assert.Equal(4, targets.Count(t => ClusterOf(t) == 0));
        Assert.Equal(4, targets.Count(t => ClusterOf(t) == 1));
        Assert.Empty(BenchPlanner.MemoryTargets(null));
    }

    [Fact]
    public void Les_demandes_processeur_portent_les_durees_et_les_cibles()
    {
        BenchJobRequest mono = BenchPlanner.CpuRequest(BenchTestKind.CpuMono, Hybrid(), sustained: false, fallbackProcessorCount: 20);
        BenchJobRequest multi = BenchPlanner.CpuRequest(BenchTestKind.CpuMulti, Hybrid(), sustained: true, fallbackProcessorCount: 20);
        BenchJobRequest blind = BenchPlanner.CpuRequest(BenchTestKind.CpuMulti, null, sustained: false, fallbackProcessorCount: 12);

        Assert.Equal("cpu-mono", mono.Kind);
        Assert.Single(mono.Cpu!.Threads!);
        Assert.Equal(0, mono.Cpu.SustainedSeconds);
        Assert.Equal(BenchPlanner.CpuWarmupSeconds, mono.Cpu.WarmupSeconds);
        Assert.Equal(20, multi.Cpu!.Threads!.Count);
        Assert.Equal(BenchPlanner.CpuSustainedSeconds, multi.Cpu.SustainedSeconds);
        Assert.Null(blind.Cpu!.Threads);
        Assert.Equal(12, blind.Cpu.ThreadCount);
    }

    [Fact]
    public void Les_demandes_memoire_et_disque_reprennent_les_tailles_et_le_volume()
    {
        MemoryBenchSizes sizes = MemoryBenchSizing.Compute(16L << 20, 16_000L << 20);
        var volume = new BenchVolume("D:", null, "NTFS", DriveType.Fixed, 1L << 40, 1L << 39, false,
            new VolumeDeviceInfo(512, 4096, 1, true, null), null, null, null);

        BenchJobRequest bandwidth = BenchPlanner.RamRequest(BenchTestKind.RamBandwidth, sizes, Hybrid(), 20);
        BenchJobRequest latency = BenchPlanner.RamRequest(BenchTestKind.RamLatency, sizes, Hybrid(), 20);
        BenchJobRequest blind = BenchPlanner.RamRequest(BenchTestKind.RamBandwidth, sizes, null, 8);
        BenchJobRequest disk = BenchPlanner.DiskRequest(@"D:\PCPerfSuite.Bench\test-disque.bin", volume, 1L << 30);

        Assert.Equal(sizes.BandwidthBytes, bandwidth.Ram!.BandwidthBytes);
        Assert.Equal(6, bandwidth.Ram.ThreadCount);
        // Un thread par cœur P (premier fil de chacun), jamais sur un cœur E ; la latence sur le cœur du mono.
        Assert.Equal([0, 2, 4, 6, 8, 10], bandwidth.Ram.Threads!.Select(t => t.Index));
        Assert.Equal(1, latency.Ram!.ThreadCount);
        Assert.Equal(BenchPlanner.MonoTarget(Hybrid())!.Index, Assert.Single(latency.Ram.Threads!).Index);
        Assert.Null(blind.Ram!.Threads);
        Assert.Equal(4, blind.Ram.ThreadCount);
        Assert.Equal(sizes.LatencyBytes, latency.Ram.LatencyBytes);
        Assert.Equal(4096, disk.Disk!.SectorBytes);
        Assert.True(disk.Disk.IsRotational);
        Assert.Equal(4L << 30, disk.Disk.WriteBudgetBytes);
        Assert.Equal(BenchPlanner.DiskPhaseSeconds, disk.Disk.PhaseSeconds);
    }

    [Fact]
    public void La_duree_estimee_suit_les_options()
    {
        var quick = new BenchPlanOptions(SustainedEnabled: false, DiskFileBytes: 1L << 30);
        var full = new BenchPlanOptions(SustainedEnabled: true, DiskFileBytes: 1L << 30);
        var hdd = new BenchPlanOptions(SustainedEnabled: false, DiskFileBytes: 1L << 30, DiskIsRotational: true);

        Assert.Equal(23, BenchPlanner.EstimateSeconds(BenchTestKind.CpuMono, quick));
        Assert.Equal(221, BenchPlanner.EstimateSeconds(BenchTestKind.CpuMulti, full));
        Assert.InRange(BenchPlanner.EstimateSeconds(BenchTestKind.Disk, quick), 43, 46);
        Assert.True(BenchPlanner.EstimateSeconds(BenchTestKind.Disk, hdd) < BenchPlanner.EstimateSeconds(BenchTestKind.Disk, quick));
        Assert.True(BenchPlanner.EstimateSeconds(BenchTestKind.RamBandwidth, quick) > 0);
    }

    [Theory]
    [InlineData(45, "45 s")]
    [InlineData(120, "2 min")]
    [InlineData(221, "3 min 45 s")]
    [InlineData(3600, "1 h 00 min")]
    public void La_duree_se_dit_en_francais(double seconds, string expected)
    {
        Assert.Equal(expected, BenchPlanner.DescribeDuration(seconds));
    }

    [Fact]
    public void Les_valeurs_du_journal_sont_des_nombres_ou_des_mots_jamais_un_chemin()
    {
        var volume = new BenchVolume("D:", null, "NTFS", DriveType.Fixed, 1L << 40, 1L << 39, false,
            new VolumeDeviceInfo(512, 4096, 1, false, null), null, null, null);
        BenchJobRequest cpu = BenchPlanner.CpuRequest(BenchTestKind.CpuMulti, Hybrid(), false, 20);
        BenchJobRequest latency = BenchPlanner.RamRequest(BenchTestKind.RamLatency, MemoryBenchSizing.Compute(null, null), null, 1);
        BenchJobRequest disk = BenchPlanner.DiskRequest(@"D:\PCPerfSuite.Bench\test-disque.bin", volume, 1L << 30);

        IReadOnlyDictionary<string, string> cpuValues = BenchPlanner.JournalValues(cpu);
        IReadOnlyDictionary<string, string> ramValues = BenchPlanner.JournalValues(latency);
        IReadOnlyDictionary<string, string> diskValues = BenchPlanner.JournalValues(disk, volume);

        Assert.Equal("20", cpuValues["threads"]);
        Assert.Equal("512", ramValues["tampon-mo"]);
        Assert.Equal("1024", diskValues["taille-mo"]);
        Assert.Equal("D", diskValues["volume"]);
        Assert.Equal("non", diskValues["systeme"]);
        Assert.All(diskValues.Values, v => Assert.DoesNotContain(@"\", v));
    }
}
