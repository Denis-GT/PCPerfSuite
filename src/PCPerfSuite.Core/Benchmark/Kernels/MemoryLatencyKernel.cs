using System.Diagnostics;
using System.Runtime.CompilerServices;
using PCPerfSuite.Core.Benchmark.Memory;

namespace PCPerfSuite.Core.Benchmark.Kernels;

/// <summary>Issue d'une série de pas de pointer chasing.</summary>
public sealed record LatencyChaseOutcome(long Steps, double Seconds, uint FinalIndex)
{
    public double NanosecondsPerStep => Steps > 0 ? Seconds * 1e9 / Steps : 0;
}

/// <summary>
/// Noyau de latence mémoire : des lignes de 64 octets dont la première contient l'indice de la suivante, selon une
/// permutation à cycle unique (<see cref="SattoloPermutation"/>) ; on suit la chaîne, chaque lecture dépendant de la
/// précédente, et on compte les nanosecondes par pas. Parcours imprévisible pour le préchargeur, tampon bien au-delà du
/// L3 : c'est la RAM (et la TLB, en pages de 4 Ko : les grandes pages exigent SeLockMemoryPrivilege) qu'on mesure. Le
/// nombre de lignes est ramené à une puissance de deux : un indice lu est toujours masqué, une corruption mémoire ne
/// peut pas sortir du tampon, et elle se voit à l'indice final (les passes doivent s'accorder) et à la somme des
/// indices (celle d'une permutation).
/// </summary>
public sealed unsafe class MemoryLatencyKernel : IDisposable
{
    public const int LineBytes = 64;
    public const long MinimumBytes = 1L << 20;

    private readonly AlignedBuffer _buffer;
    private readonly uint _mask;

    /// <summary>Alloue le tampon (puissance de deux de lignes, au plus <paramref name="bytes"/>) et y écrit la chaîne.
    /// Lève <see cref="OutOfMemoryException"/> si le système refuse.</summary>
    public MemoryLatencyKernel(long bytes, ulong seed, Action<double>? progress = null, CancellationToken cancel = default)
    {
        if (bytes < MinimumBytes) throw new ArgumentOutOfRangeException(nameof(bytes), bytes, $"Au moins {MinimumBytes} octets.");
        // Plus grande puissance de deux de lignes qui tient dans la taille demandée, au plus 2^30 lignes (64 Go).
        long lines = (long)System.Numerics.BitOperations.RoundUpToPowerOf2((ulong)(bytes / LineBytes) + 1) / 2;
        LineCount = (int)Math.Min(lines, 1L << 30);
        Length = (long)LineCount * LineBytes;
        _mask = (uint)(LineCount - 1);
        _buffer = AlignedBuffer.Allocate(Length, MemorySlices.PageBytes);

        try
        {
            var next = new uint[LineCount];
            SattoloPermutation.Fill(next, seed);
            cancel.ThrowIfCancellationRequested();
            progress?.Invoke(0.5);

            byte* pointer = _buffer.Pointer;
            for (long line = 0; line < LineCount; line++)
            {
                *(uint*)(pointer + line * LineBytes) = next[line];
            }
            progress?.Invoke(1);
        }
        catch (Exception)
        {
            _buffer.Dispose();
            throw;
        }
    }

    public int LineCount { get; }

    public long Length { get; }

    /// <summary>Suit la chaîne pendant <paramref name="steps"/> pas depuis <paramref name="start"/> ; quatre pas déroulés
    /// par itération pour que la boucle ne pèse rien devant une lecture en RAM.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public LatencyChaseOutcome Chase(uint start, long steps)
    {
        byte* pointer = _buffer.Pointer;
        uint mask = _mask;
        uint index = start & mask;
        long begin = Stopwatch.GetTimestamp();
        long remaining = steps;
        while (remaining >= 4)
        {
            index = *(uint*)(pointer + ((long)index << 6)) & mask;
            index = *(uint*)(pointer + ((long)index << 6)) & mask;
            index = *(uint*)(pointer + ((long)index << 6)) & mask;
            index = *(uint*)(pointer + ((long)index << 6)) & mask;
            remaining -= 4;
        }
        while (remaining > 0)
        {
            index = *(uint*)(pointer + ((long)index << 6)) & mask;
            remaining--;
        }
        double seconds = (double)(Stopwatch.GetTimestamp() - begin) / Stopwatch.Frequency;
        return new LatencyChaseOutcome(steps, seconds, index);
    }

    /// <summary>Vrai si les indices stockés forment encore une permutation (XOR de 0..n−1) : un bit retourné en RAM se
    /// verrait ici. Balayage séquentiel, quelques dizaines de millisecondes.</summary>
    public bool VerifyPermutationChecksum()
    {
        byte* pointer = _buffer.Pointer;
        uint expected = 0;
        uint actual = 0;
        for (long line = 0; line < LineCount; line++)
        {
            expected ^= (uint)line;
            actual ^= *(uint*)(pointer + line * LineBytes);
        }
        return expected == actual;
    }

    public void Dispose() => _buffer.Dispose();
}
