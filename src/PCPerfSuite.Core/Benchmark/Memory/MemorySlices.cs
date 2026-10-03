namespace PCPerfSuite.Core.Benchmark.Memory;

/// <summary>Une tranche d'un tampon, en octets depuis son début.</summary>
public readonly record struct MemorySlice(long Offset, long Length)
{
    public long End => Offset + Length;
}

/// <summary>
/// Découpe un tampon en tranches contiguës, une par thread, dont les bornes sont alignées (4 Ko par défaut : une page,
/// donc aussi une ligne de cache et un vecteur). Logique pure : les dernières tranches peuvent être vides quand le tampon
/// est plus petit que <c>count</c> pages, l'appelant les ignore.
/// </summary>
public static class MemorySlices
{
    public const int PageBytes = 4096;

    public static IReadOnlyList<MemorySlice> Split(long length, int count, int alignment = PageBytes)
    {
        if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
        if (count < 1) throw new ArgumentOutOfRangeException(nameof(count));
        if (alignment <= 0 || (alignment & (alignment - 1)) != 0) throw new ArgumentOutOfRangeException(nameof(alignment));

        long usable = length - length % alignment;
        var slices = new MemorySlice[count];
        long previous = 0;
        for (int i = 0; i < count; i++)
        {
            long end = i == count - 1 ? usable : AlignDown(usable * (i + 1) / count, alignment);
            slices[i] = new MemorySlice(previous, end - previous);
            previous = end;
        }
        return slices;
    }

    public static long AlignDown(long value, long alignment) => value - value % alignment;
}
