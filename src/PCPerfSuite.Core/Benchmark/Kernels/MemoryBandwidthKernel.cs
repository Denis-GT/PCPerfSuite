using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using PCPerfSuite.Core.Benchmark.Memory;

namespace PCPerfSuite.Core.Benchmark.Kernels;

/// <summary>Les trois opérations du test de débit mémoire.</summary>
public enum MemoryOperation
{
    /// <summary>Écriture d'un motif par stockage non temporel (contourne le cache : c'est la RAM qu'on écrit).</summary>
    Write,

    /// <summary>Lecture vectorielle sommée (addition 64 bits par voie) : la somme vérifie que ce qui a été écrit est
    /// bien relu. Pas un XOR : un motif répété un nombre pair de fois s'y annulerait.</summary>
    Read,

    /// <summary>Copie de la première moitié du tampon vers la seconde ; lus + écrits comptés.</summary>
    Copy,
}

/// <summary>
/// Noyau de débit mémoire sur un tampon unique, aligné sur la page, découpé en tranches de 4 Ko par thread
/// (<see cref="MemorySlices"/>). Chemin de référence : AVX2 (lecture <c>LoadAlignedVector256</c>, écriture et copie
/// <c>StoreAlignedNonTemporal</c>) ; repli <see cref="Vector{T}"/> marqué non comparable. Le motif écrit dépend de la
/// graine et de la page : la somme de lecture est donc attendue, et une différence est une erreur mémoire ou une écriture
/// perdue. Débits en octets par seconde ; la copie compte un octet lu et un octet écrit.
/// </summary>
public sealed unsafe class MemoryBandwidthKernel : IDisposable
{
    private const int VectorBytes = 32;

    private readonly AlignedBuffer _buffer;
    private readonly IReadOnlyList<MemorySlice> _slices;
    private readonly IReadOnlyList<MemorySlice> _halfSlices;
    private readonly ulong _seed;
    private readonly bool _useAvx;

    /// <summary>Alloue <paramref name="bytes"/> (arrondis à la page, au moins deux pages par thread), hors tas. Lève
    /// <see cref="OutOfMemoryException"/> si le système refuse : l'appelant a dimensionné d'après la RAM libre.</summary>
    public MemoryBandwidthKernel(long bytes, int threadCount, ulong seed)
    {
        if (threadCount < 1) throw new ArgumentOutOfRangeException(nameof(threadCount));
        long minimum = 2L * MemorySlices.PageBytes * threadCount;
        if (bytes < minimum) throw new ArgumentOutOfRangeException(nameof(bytes), bytes, $"Au moins {minimum} octets pour {threadCount} thread(s).");

        Length = MemorySlices.AlignDown(bytes, 2L * MemorySlices.PageBytes);
        _buffer = AlignedBuffer.Allocate(Length, MemorySlices.PageBytes);
        _slices = MemorySlices.Split(Length, threadCount);
        _halfSlices = MemorySlices.Split(Length / 2, threadCount);
        _seed = seed;
        _useAvx = Avx2.IsSupported && Avx.IsSupported && Sse.IsSupported;
        ThreadCount = threadCount;
        InstructionSet = _useAvx ? "AVX2 (stockage non temporel)" : Vector.IsHardwareAccelerated ? $"Vector<T> ({Vector<byte>.Count} octets)" : "scalaire";
    }

    public long Length { get; }

    public int ThreadCount { get; }

    public string InstructionSet { get; }

    public bool IsComparable => _useAvx;

    /// <summary>Octets déplacés par un balayage de la tranche d'un thread pour une opération.</summary>
    public long BytesPerSweep(MemoryOperation operation, int thread)
        => operation == MemoryOperation.Copy ? 2 * _halfSlices[thread].Length : _slices[thread].Length;

    /// <summary>Un balayage de la tranche du thread ; rend la somme de lecture (0 pour une écriture ou une copie).</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public ulong Sweep(MemoryOperation operation, int thread)
    {
        byte* basePointer = _buffer.Pointer;
        switch (operation)
        {
            case MemoryOperation.Write:
            {
                MemorySlice slice = _slices[thread];
                if (_useAvx) WriteAvx(basePointer, slice, _seed);
                else WriteVector(basePointer, slice, _seed);
                return 0;
            }
            case MemoryOperation.Read:
            {
                MemorySlice slice = _slices[thread];
                return _useAvx ? ReadAvx(basePointer + slice.Offset, slice.Length) : ReadVector(basePointer + slice.Offset, slice.Length);
            }
            default:
            {
                MemorySlice slice = _halfSlices[thread];
                byte* source = basePointer + slice.Offset;
                byte* destination = basePointer + Length / 2 + slice.Offset;
                if (_useAvx) CopyAvx(source, destination, slice.Length);
                else CopyVector(source, destination, slice.Length);
                return 0;
            }
        }
    }

    /// <summary>Le motif d'une page : la graine mêlée au numéro de page, pour qu'aucune page ne ressemble à sa voisine
    /// (une compression ou une déduplication matérielle n'aurait rien à gagner).</summary>
    private static ulong PagePattern(ulong seed, long pageIndex)
    {
        ulong z = seed ^ ((ulong)pageIndex * 0x9E3779B97F4A7C15UL);
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        return z ^ (z >> 29);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void WriteAvx(byte* basePointer, MemorySlice slice, ulong seed)
    {
        byte* end = basePointer + slice.End;
        long page = slice.Offset / MemorySlices.PageBytes;
        for (byte* p = basePointer + slice.Offset; p < end; p += MemorySlices.PageBytes, page++)
        {
            Vector256<ulong> pattern = Vector256.Create(PagePattern(seed, page));
            for (int offset = 0; offset < MemorySlices.PageBytes; offset += 4 * VectorBytes)
            {
                Avx.StoreAlignedNonTemporal((ulong*)(p + offset), pattern);
                Avx.StoreAlignedNonTemporal((ulong*)(p + offset + VectorBytes), pattern);
                Avx.StoreAlignedNonTemporal((ulong*)(p + offset + 2 * VectorBytes), pattern);
                Avx.StoreAlignedNonTemporal((ulong*)(p + offset + 3 * VectorBytes), pattern);
            }
        }
        Sse.StoreFence();
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void WriteVector(byte* basePointer, MemorySlice slice, ulong seed)
    {
        int lanes = Vector<ulong>.Count;
        byte* end = basePointer + slice.End;
        long page = slice.Offset / MemorySlices.PageBytes;
        for (byte* p = basePointer + slice.Offset; p < end; p += MemorySlices.PageBytes, page++)
        {
            var pattern = new Vector<ulong>(PagePattern(seed, page));
            for (int offset = 0; offset < MemorySlices.PageBytes; offset += lanes * sizeof(ulong))
            {
                pattern.Store((ulong*)(p + offset));
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static ulong ReadAvx(byte* start, long length)
    {
        Vector256<ulong> acc0 = Vector256<ulong>.Zero, acc1 = Vector256<ulong>.Zero;
        Vector256<ulong> acc2 = Vector256<ulong>.Zero, acc3 = Vector256<ulong>.Zero;
        byte* end = start + length;
        for (byte* p = start; p < end; p += 4 * VectorBytes)
        {
            acc0 = Avx2.Add(acc0, Avx.LoadAlignedVector256((ulong*)p));
            acc1 = Avx2.Add(acc1, Avx.LoadAlignedVector256((ulong*)(p + VectorBytes)));
            acc2 = Avx2.Add(acc2, Avx.LoadAlignedVector256((ulong*)(p + 2 * VectorBytes)));
            acc3 = Avx2.Add(acc3, Avx.LoadAlignedVector256((ulong*)(p + 3 * VectorBytes)));
        }
        Vector256<ulong> acc = Avx2.Add(Avx2.Add(acc0, acc1), Avx2.Add(acc2, acc3));
        return unchecked(acc.GetElement(0) + acc.GetElement(1) + acc.GetElement(2) + acc.GetElement(3));
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static ulong ReadVector(byte* start, long length)
    {
        int lanes = Vector<ulong>.Count;
        Vector<ulong> acc = Vector<ulong>.Zero;
        byte* end = start + length;
        for (byte* p = start; p < end; p += lanes * sizeof(ulong))
        {
            acc += Vector.Load((ulong*)p);
        }
        ulong result = 0;
        for (int i = 0; i < lanes; i++) result = unchecked(result + acc[i]);
        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void CopyAvx(byte* source, byte* destination, long length)
    {
        byte* end = source + length;
        for (byte* s = source, d = destination; s < end; s += 4 * VectorBytes, d += 4 * VectorBytes)
        {
            Avx.StoreAlignedNonTemporal((ulong*)d, Avx.LoadAlignedVector256((ulong*)s));
            Avx.StoreAlignedNonTemporal((ulong*)(d + VectorBytes), Avx.LoadAlignedVector256((ulong*)(s + VectorBytes)));
            Avx.StoreAlignedNonTemporal((ulong*)(d + 2 * VectorBytes), Avx.LoadAlignedVector256((ulong*)(s + 2 * VectorBytes)));
            Avx.StoreAlignedNonTemporal((ulong*)(d + 3 * VectorBytes), Avx.LoadAlignedVector256((ulong*)(s + 3 * VectorBytes)));
        }
        Sse.StoreFence();
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void CopyVector(byte* source, byte* destination, long length)
    {
        int step = Vector<ulong>.Count * sizeof(ulong);
        byte* end = source + length;
        for (byte* s = source, d = destination; s < end; s += step, d += step)
        {
            Vector.Load((ulong*)s).Store((ulong*)d);
        }
    }

    /// <summary>Pour les tests : les octets d'une plage, copiés.</summary>
    public byte[] Snapshot(long offset, int count)
    {
        var bytes = new byte[count];
        _buffer.AsSpan<byte>(offset, count).CopyTo(bytes);
        return bytes;
    }

    public void Dispose() => _buffer.Dispose();
}
