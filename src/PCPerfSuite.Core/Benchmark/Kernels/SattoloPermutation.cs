namespace PCPerfSuite.Core.Benchmark.Kernels;

/// <summary>
/// Permutation à cycle unique (algorithme de Sattolo) : partir de n'importe quel indice et suivre <c>next</c> revient au
/// départ après exactement n pas, en passant par tous les éléments. C'est ce qu'il faut au pointer chasing du test de
/// latence : un parcours imprévisible pour le préchargeur, sans boucle courte qui resterait en cache.
/// </summary>
public static class SattoloPermutation
{
    public static void Fill(Span<uint> next, ulong seed)
    {
        for (int i = 0; i < next.Length; i++) next[i] = (uint)i;

        var random = new SeededRandom(seed);
        for (uint i = (uint)next.Length - 1; i > 0; i--)
        {
            uint j = random.NextBelow(i); // j < i, jamais i lui-même : c'est ce qui garantit le cycle unique
            (next[(int)i], next[(int)j]) = (next[(int)j], next[(int)i]);
        }
    }

    /// <summary>Vrai si suivre <c>next</c> depuis 0 revient à 0 après exactement n pas sans repasser deux fois au même endroit.</summary>
    public static bool IsSingleCycle(ReadOnlySpan<uint> next)
    {
        if (next.Length == 0) return false;
        uint current = 0;
        for (int step = 0; step < next.Length; step++)
        {
            if (current >= next.Length) return false;
            current = next[(int)current];
            if (current == 0) return step == next.Length - 1;
        }
        return false;
    }
}
