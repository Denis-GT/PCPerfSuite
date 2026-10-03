using PCPerfSuite.Core.Benchmark;
using PCPerfSuite.Core.Benchmark.Kernels;
using PCPerfSuite.Core.Benchmark.Memory;
using PCPerfSuite.Core.Benchmark.Protocol;
using PCPerfSuite.Core.Benchmark.Worker;
using PCPerfSuite.Core.Compatibility;

namespace PCPerfSuite.Core.Tests;

public class MemorySlicesTests
{
    [Fact]
    public void Les_tranches_sont_alignees_contigues_et_couvrent_tout_le_tampon_aligne()
    {
        IReadOnlyList<MemorySlice> slices = MemorySlices.Split(10 * 4096 + 100, 3);

        Assert.Equal(3, slices.Count);
        Assert.Equal(0, slices[0].Offset);
        Assert.All(slices, s => Assert.Equal(0, s.Offset % 4096));
        Assert.All(slices, s => Assert.Equal(0, s.Length % 4096));
        for (int i = 1; i < slices.Count; i++) Assert.Equal(slices[i - 1].End, slices[i].Offset);
        Assert.Equal(10 * 4096, slices[^1].End);
        Assert.Equal(10 * 4096, slices.Sum(s => s.Length));
    }

    [Fact]
    public void Un_tampon_plus_petit_que_le_nombre_de_threads_laisse_des_tranches_vides()
    {
        IReadOnlyList<MemorySlice> slices = MemorySlices.Split(2 * 4096, 4);

        Assert.Equal(2 * 4096, slices.Sum(s => s.Length));
        Assert.Contains(slices, s => s.Length == 0);
    }

    [Fact]
    public void Les_arguments_invalides_sont_refuses()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MemorySlices.Split(-1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => MemorySlices.Split(4096, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => MemorySlices.Split(4096, 1, 3000));
        Assert.Equal(8192, MemorySlices.AlignDown(8200, 4096));
    }
}

public class MemoryBenchSizingTests
{
    private const long Mo = 1L << 20;

    [Fact]
    public void Le_debit_prend_huit_fois_le_l3_avec_un_plancher_de_128_mo()
    {
        Assert.Equal(128 * Mo, MemoryBenchSizing.Compute(largestL3Bytes: 12 * Mo, availableBytes: 16_000 * Mo).BandwidthBytes);
        Assert.Equal(128 * Mo, MemoryBenchSizing.Compute(largestL3Bytes: null, availableBytes: 16_000 * Mo).BandwidthBytes);
        Assert.Equal(768 * Mo, MemoryBenchSizing.Compute(largestL3Bytes: 96 * Mo, availableBytes: 16_000 * Mo).BandwidthBytes);
        Assert.Equal(288 * Mo, MemoryBenchSizing.Compute(largestL3Bytes: 36 * Mo, availableBytes: 16_000 * Mo).BandwidthBytes);
    }

    [Fact]
    public void Le_debit_est_plafonne_au_quart_de_la_ram_libre_et_indisponible_sous_le_plancher()
    {
        MemoryBenchSizes capped = MemoryBenchSizing.Compute(96 * Mo, 2_000 * Mo);
        Assert.Equal(500 * Mo, capped.BandwidthBytes);
        Assert.Null(capped.BandwidthUnavailable);

        MemoryBenchSizes starved = MemoryBenchSizing.Compute(12 * Mo, 400 * Mo);
        Assert.Null(starved.BandwidthBytes);
        Assert.Equal(UnavailableCause.HardwareOrDriver, starved.BandwidthUnavailable!.Cause);
        Assert.Contains("128 Mo", starved.BandwidthUnavailable.Reason);
        Assert.Contains("400 Mo", starved.BandwidthUnavailable.Reason);
    }

    [Fact]
    public void La_latence_vaut_512_mo_bornee_et_en_puissance_de_deux()
    {
        Assert.Equal(512 * Mo, MemoryBenchSizing.Compute(null, 16_000 * Mo).LatencyBytes);
        Assert.Equal(512 * Mo, MemoryBenchSizing.Compute(null, null).LatencyBytes);
        Assert.Equal(256 * Mo, MemoryBenchSizing.Compute(null, 1_500 * Mo).LatencyBytes); // 375 Mo → 256 Mo
        Assert.Equal(256 * Mo, MemoryBenchSizing.Compute(null, 1_024 * Mo).LatencyBytes);

        MemoryBenchSizes starved = MemoryBenchSizing.Compute(null, 1_000 * Mo); // 250 Mo → 128 Mo < 256 Mo
        Assert.Null(starved.LatencyBytes);
        Assert.Contains("256 Mo", starved.LatencyUnavailable!.Reason);
    }

    [Fact]
    public void Sans_lecture_de_la_ram_libre_il_n_y_a_pas_de_plafond()
    {
        MemoryBenchSizes sizes = MemoryBenchSizing.Compute(96 * Mo, null);

        Assert.Equal(768 * Mo, sizes.BandwidthBytes);
        Assert.Null(sizes.AvailableBytes);
    }

    [Fact]
    public void L_arrondi_a_la_puissance_de_deux_descend()
    {
        Assert.Equal(512, MemoryBenchSizing.RoundDownToPowerOfTwo(1000));
        Assert.Equal(1024, MemoryBenchSizing.RoundDownToPowerOfTwo(1024));
        Assert.Equal(0, MemoryBenchSizing.RoundDownToPowerOfTwo(0));
    }

    [Fact]
    public void La_lecture_sur_ce_pc_ne_plante_pas()
    {
        MemoryBenchSizes sizes = MemoryBenchSizing.ReadCurrent(null);

        Assert.True(sizes.BandwidthBytes is not null || sizes.BandwidthUnavailable is not null);
    }
}

public class MemoryKernelsTests
{
    [Fact]
    public void L_ecriture_laisse_une_somme_de_lecture_attendue_et_la_copie_recopie_la_premiere_moitie()
    {
        using var kernel = new MemoryBandwidthKernel(2 * 1024 * 1024, threadCount: 2, seed: 7);

        kernel.Sweep(MemoryOperation.Write, 0);
        kernel.Sweep(MemoryOperation.Write, 1);
        ulong first0 = kernel.Sweep(MemoryOperation.Read, 0);
        ulong first1 = kernel.Sweep(MemoryOperation.Read, 1);
        Assert.Equal(first0, kernel.Sweep(MemoryOperation.Read, 0));
        Assert.NotEqual(first0, first1);
        Assert.Equal(0u, kernel.Sweep(MemoryOperation.Write, 0));

        kernel.Sweep(MemoryOperation.Copy, 0);
        kernel.Sweep(MemoryOperation.Copy, 1);
        long half = kernel.Length / 2;
        Assert.Equal(kernel.Snapshot(0, 4096), kernel.Snapshot(half, 4096));
        Assert.Equal(kernel.Snapshot(half - 4096, 4096), kernel.Snapshot(kernel.Length - 4096, 4096));
        Assert.Equal(kernel.Length, kernel.BytesPerSweep(MemoryOperation.Read, 0) + kernel.BytesPerSweep(MemoryOperation.Read, 1));
        Assert.Equal(kernel.Length, kernel.BytesPerSweep(MemoryOperation.Copy, 0) + kernel.BytesPerSweep(MemoryOperation.Copy, 1));
        Assert.NotEmpty(kernel.InstructionSet);
    }

    [Fact]
    public void Deux_noyaux_de_meme_graine_donnent_la_meme_somme_de_lecture()
    {
        using var a = new MemoryBandwidthKernel(1024 * 1024, 1, 3);
        using var b = new MemoryBandwidthKernel(1024 * 1024, 1, 3);
        a.Sweep(MemoryOperation.Write, 0);
        b.Sweep(MemoryOperation.Write, 0);

        Assert.Equal(a.Sweep(MemoryOperation.Read, 0), b.Sweep(MemoryOperation.Read, 0));
    }

    [Fact]
    public void Un_tampon_trop_petit_est_refuse()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MemoryBandwidthKernel(4096, 4, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MemoryLatencyKernel(1000, 1));
    }

    [Fact]
    public void La_chaine_de_latence_est_un_cycle_unique_masque_et_verifiable()
    {
        var progress = new List<double>();
        using var kernel = new MemoryLatencyKernel(3 * 1024 * 1024 / 2, seed: 11, progress.Add);

        Assert.Equal(1 << 14, kernel.LineCount); // 1,5 Mo → 16 384 lignes (1 Mo), puissance de deux
        Assert.Equal(1024 * 1024, kernel.Length);
        Assert.Equal([0.5, 1], progress);

        LatencyChaseOutcome full = kernel.Chase(0, kernel.LineCount);
        Assert.Equal(0u, full.FinalIndex);
        Assert.True(full.NanosecondsPerStep > 0);

        LatencyChaseOutcome part = kernel.Chase(0, 1000);
        Assert.NotEqual(0u, part.FinalIndex);
        Assert.Equal(part.FinalIndex, kernel.Chase(0, 1000).FinalIndex);
        Assert.Equal(full.FinalIndex, kernel.Chase(part.FinalIndex, kernel.LineCount - 1000).FinalIndex);
        Assert.True(kernel.VerifyPermutationChecksum());
    }
}

public class MemoryBenchRunnerTests
{
    private static BenchJobRequest Bandwidth(long bytes = 4 * 1024 * 1024, int threads = 2) => new()
    {
        Kind = BenchTestKinds.Key(BenchTestKind.RamBandwidth),
        Ram = new RamJobParameters { BandwidthBytes = bytes, ThreadCount = threads, Passes = 2, PassSeconds = 0.02 },
    };

    private static BenchJobRequest Latency(long bytes = 1024 * 1024, long steps = 200_000) => new()
    {
        Kind = BenchTestKinds.Key(BenchTestKind.RamLatency),
        Ram = new RamJobParameters { LatencyBytes = bytes, LatencySteps = steps, Passes = 2 },
    };

    [Fact]
    public void Le_test_de_debit_rend_ecriture_lecture_et_copie_sans_erreur_de_lecture()
    {
        var progress = new List<BenchProgress>();

        BenchJobResult result = new MemoryBenchRunner().Run(Bandwidth(), progress.Add, CancellationToken.None);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal([MemoryBenchRunner.WriteKey, MemoryBenchRunner.ReadKey, MemoryBenchRunner.CopyKey], result.Measurements.Select(m => m.Key));
        Assert.All(result.Measurements, m =>
        {
            Assert.Equal(2, m.Values.Count);
            Assert.True(m.Median > 0);
            Assert.Equal(MemoryBenchRunner.BandwidthUnit, m.Unit);
        });
        Assert.False(result.ChecksumMismatch);
        Assert.Equal("0", result.Notes["erreurs-lecture"]);
        Assert.Equal("2", result.Notes["threads"]);
        Assert.Equal("4", result.Notes["tampon-mo"]);
        Assert.Contains(progress, p => p.Phase.StartsWith("passe", StringComparison.Ordinal) && p.Value is > 0);
        Assert.All(progress, p => Assert.InRange(p.Percent, 0, 100));
    }

    [Fact]
    public void Le_test_de_latence_rend_des_nanosecondes_par_pas_et_des_passes_concordantes()
    {
        BenchJobResult result = new MemoryBenchRunner().Run(Latency(), null, CancellationToken.None);

        Assert.True(result.Succeeded, result.Error);
        BenchMeasurement latency = Assert.Single(result.Measurements);
        Assert.Equal(MemoryBenchRunner.LatencyKey, latency.Key);
        Assert.False(latency.HigherIsBetter);
        Assert.Equal(MemoryBenchRunner.LatencyUnit, latency.Unit);
        Assert.Equal(2, latency.Values.Count);
        Assert.InRange(latency.Median, 0.1, 1000);
        Assert.False(result.ChecksumMismatch);
        Assert.Equal("permutation intacte, passes concordantes", result.Notes["verification"]);
        Assert.Equal("16384", result.Notes["lignes"]);
    }

    [Fact]
    public void Une_taille_absente_ou_un_test_inconnu_sont_refuses_sans_lever()
    {
        BenchJobRequest noSize = Bandwidth(bytes: 0);
        BenchJobRequest noLatency = Latency(bytes: 0);
        var cpu = new BenchJobRequest { Kind = BenchTestKinds.Key(BenchTestKind.CpuMono) };

        Assert.Contains("taille", new MemoryBenchRunner().Run(noSize, null, CancellationToken.None).Error);
        Assert.Contains("taille", new MemoryBenchRunner().Run(noLatency, null, CancellationToken.None).Error);
        Assert.Contains("inconnu", new MemoryBenchRunner().Run(cpu, null, CancellationToken.None).Error);
    }

    [Fact]
    public void Une_annulation_arrete_les_deux_tests()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        Assert.Equal(BenchJobDispatcher.CancelledError, new MemoryBenchRunner().Run(Bandwidth(), null, cancel.Token).Error);
        Assert.Equal(BenchJobDispatcher.CancelledError, new MemoryBenchRunner().Run(Latency(steps: 50_000_000), null, cancel.Token).Error);
    }

    [Fact]
    public void Les_threads_du_debit_et_de_la_latence_s_epinglent_sur_leur_cible()
    {
        BenchJobRequest bandwidth = Bandwidth(threads: 1);
        bandwidth.Ram!.Threads = [new LogicalProcessorTarget { Group = 0, Index = 0 }];
        BenchJobRequest latency = Latency();
        latency.Ram!.Threads = [new LogicalProcessorTarget { Group = 0, Index = 0 }];

        BenchJobResult bandwidthResult = new MemoryBenchRunner().Run(bandwidth, null, CancellationToken.None);
        BenchJobResult latencyResult = new MemoryBenchRunner().Run(latency, null, CancellationToken.None);

        Assert.True(bandwidthResult.Succeeded, bandwidthResult.Error);
        Assert.Equal("1", bandwidthResult.Notes["threads"]);
        Assert.Contains("1/1 vérifié", bandwidthResult.Notes["epinglage"]);
        Assert.True(latencyResult.Succeeded, latencyResult.Error);
        Assert.Contains("affinite", latencyResult.Notes["epinglage"]);
        Assert.Equal("non épinglé", new MemoryBenchRunner().Run(Latency(), null, CancellationToken.None).Notes["epinglage"]);
    }

    [Fact]
    public void Le_distributeur_connait_les_tests_memoire()
    {
        BenchJobResult result = BenchJobDispatcher.Run(Latency(), null, CancellationToken.None);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(BenchTestKinds.Key(BenchTestKind.RamLatency), result.Kind);
    }
}
