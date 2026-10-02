using PCPerfSuite.Core.Benchmark.Kernels;
using Xunit;

namespace PCPerfSuite.Core.Tests;

public class SattoloPermutationTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(10)]
    [InlineData(1000)]
    [InlineData(65536)]
    [InlineData(1 << 20)]
    public void La_permutation_est_un_cycle_unique(int size)
    {
        var next = new uint[size];

        SattoloPermutation.Fill(next, seed: 7);

        Assert.True(SattoloPermutation.IsSingleCycle(next));
        // Et c'est bien une permutation : chaque indice apparaît une fois.
        Assert.Equal(size, next.Distinct().Count());
    }

    [Fact]
    public void Meme_graine_meme_permutation_autre_graine_autre_permutation()
    {
        var first = new uint[4096];
        var second = new uint[4096];
        var other = new uint[4096];

        SattoloPermutation.Fill(first, 1);
        SattoloPermutation.Fill(second, 1);
        SattoloPermutation.Fill(other, 2);

        Assert.Equal(first, second);
        Assert.NotEqual(first, other);
    }

    [Fact]
    public void Un_cycle_court_ou_une_boucle_sur_soi_sont_refuses()
    {
        Assert.False(SattoloPermutation.IsSingleCycle([0, 2, 1]));      // 0 boucle sur lui-même
        Assert.False(SattoloPermutation.IsSingleCycle([1, 0, 3, 2]));   // deux cycles de 2
        Assert.False(SattoloPermutation.IsSingleCycle([]));
        Assert.True(SattoloPermutation.IsSingleCycle([1, 2, 0]));
    }

    [Fact]
    public void Le_generateur_a_graine_fixe_est_reproductible()
    {
        var a = new SeededRandom(42);
        var b = new SeededRandom(42);
        for (int i = 0; i < 1000; i++) Assert.Equal(a.NextUInt64(), b.NextUInt64());

        var c = new SeededRandom(42);
        for (int i = 0; i < 10000; i++)
        {
            Assert.InRange(c.NextBelow(10), 0u, 9u);
            float f = c.NextUnitFloat();
            Assert.InRange(f, -1f, 1f);
        }
    }
}
