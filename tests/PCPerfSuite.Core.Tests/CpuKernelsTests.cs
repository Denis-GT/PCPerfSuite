using PCPerfSuite.Core.Benchmark.Kernels;
using Xunit;

namespace PCPerfSuite.Core.Tests;

public class CpuKernelsTests
{
    public static IEnumerable<object[]> Keys => CpuKernelCatalog.Keys.Select(k => new object[] { k });

    [Theory]
    [MemberData(nameof(Keys))]
    public void Deux_executions_donnent_la_meme_somme(string key)
    {
        using ICpuKernel kernel = CpuKernelCatalog.Create(key);

        ulong first = kernel.Run();
        ulong second = kernel.Run();

        Assert.Equal(first, second);
        Assert.True(kernel.OperationsPerRun > 0);
        Assert.NotEmpty(kernel.InstructionSet);
        Assert.NotEmpty(kernel.Unit);
    }

    [Theory]
    [MemberData(nameof(Keys))]
    public void Deux_instances_de_meme_graine_donnent_la_meme_somme(string key)
    {
        using ICpuKernel a = CpuKernelCatalog.Create(key, 99);
        using ICpuKernel b = CpuKernelCatalog.Create(key, 99);

        Assert.Equal(a.Run(), b.Run());
    }

    [Theory]
    [MemberData(nameof(Keys))]
    public void Une_autre_graine_donne_une_autre_somme(string key)
    {
        using ICpuKernel a = CpuKernelCatalog.Create(key, 1);
        using ICpuKernel b = CpuKernelCatalog.Create(key, 2);

        Assert.NotEqual(a.Run(), b.Run());
    }

    [Fact]
    public void Le_noyau_entier_trie_vraiment()
    {
        // Même graine que le noyau, même taille : la somme attendue est celle du tableau trié.
        var expected = new uint[IntegerKernel.ElementCount];
        new SeededRandom(5).Fill(expected);
        Array.Sort(expected);
        ulong hash = 14695981039346656037UL;
        foreach (uint value in expected) { hash ^= value; hash *= 1099511628211UL; }

        using var kernel = new IntegerKernel(5);

        Assert.Equal(hash, kernel.Run());
    }

    [Fact]
    public void Une_passe_chronometree_compte_les_executions_et_les_ecarts()
    {
        using ICpuKernel kernel = CpuKernelCatalog.Create(CpuKernelCatalog.Branches);
        ulong expected = KernelPass.Calibrate(kernel);

        KernelPassOutcome ok = KernelPass.RunFor(kernel, expected, TimeSpan.FromMilliseconds(50), CancellationToken.None);
        KernelPassOutcome wrong = KernelPass.RunFor(kernel, expected ^ 1, TimeSpan.FromMilliseconds(10), CancellationToken.None);

        Assert.True(ok.Runs >= 1);
        Assert.Equal(0, ok.Mismatches);
        Assert.True(ok.Seconds >= 0.05);
        Assert.True(ok.Rate(kernel) > 0);
        Assert.Equal(wrong.Runs, wrong.Mismatches);
    }

    [Fact]
    public void Une_annulation_arrete_la_passe_apres_l_execution_en_cours()
    {
        using ICpuKernel kernel = CpuKernelCatalog.Create(CpuKernelCatalog.Integer);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        KernelPassOutcome outcome = KernelPass.RunFor(kernel, kernel.Run(), TimeSpan.FromSeconds(10), cancel.Token);

        Assert.Equal(1, outcome.Runs);
    }

    [Fact]
    public void Un_noyau_inconnu_est_refuse()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CpuKernelCatalog.Create("gpu"));
        Assert.False(CpuKernelCatalog.IsKnown("gpu"));
        Assert.True(CpuKernelCatalog.IsKnown(CpuKernelCatalog.Float));
    }

    [Fact]
    public void Un_tampon_aligne_est_bien_aligne_et_touchable()
    {
        using AlignedBuffer buffer = AlignedBuffer.Allocate(3 * 4096 + 100, 4096);

        buffer.Touch();
        buffer.AsSpan<byte>(0, 16)[0] = 7;

        Assert.Equal(0, (long)buffer.Address % 4096);
        Assert.Equal(7, buffer.AsSpan<byte>(0, 1)[0]);
        Assert.Equal(3 * 4096 + 100, buffer.Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.AsSpan<byte>(0, 100_000));
    }
}
