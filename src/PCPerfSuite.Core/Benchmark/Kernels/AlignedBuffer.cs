using System.Runtime.InteropServices;

namespace PCPerfSuite.Core.Benchmark.Kernels;

/// <summary>
/// Tampon hors tas, aligné (64 octets par défaut : une ligne de cache ; 4 096 pour les E/S disque sans cache). Hors du
/// GC : ni compaction ni déplacement pendant la mesure. <see cref="Touch"/> écrit une fois dans chaque page avant le
/// chronomètre, pour que les défauts de page ne soient pas comptés.
/// </summary>
public sealed unsafe class AlignedBuffer : IDisposable
{
    private byte* _pointer;

    private AlignedBuffer(byte* pointer, long length, int alignment)
    {
        _pointer = pointer;
        Length = length;
        Alignment = alignment;
    }

    public long Length { get; }

    public int Alignment { get; }

    public byte* Pointer => _pointer == null ? throw new ObjectDisposedException(nameof(AlignedBuffer)) : _pointer;

    /// <summary>Adresse du tampon, pour vérifier l'alignement sans code unsafe.</summary>
    public nint Address => (nint)Pointer;

    /// <summary>Lève <see cref="OutOfMemoryException"/> si le système refuse : l'appelant a vérifié la RAM libre avant.</summary>
    public static AlignedBuffer Allocate(long bytes, int alignment = 64)
    {
        if (bytes <= 0) throw new ArgumentOutOfRangeException(nameof(bytes));
        if (alignment <= 0 || (alignment & (alignment - 1)) != 0) throw new ArgumentOutOfRangeException(nameof(alignment));
        byte* pointer = (byte*)NativeMemory.AlignedAlloc((nuint)bytes, (nuint)alignment);
        return new AlignedBuffer(pointer, bytes, alignment);
    }

    /// <summary>Un octet écrit par page de 4 Ko, puis le dernier : toutes les pages sont présentes.</summary>
    public void Touch()
    {
        byte* pointer = Pointer;
        const int page = 4096;
        for (long offset = 0; offset < Length; offset += page) pointer[offset] = (byte)offset;
        pointer[Length - 1] = 1;
    }

    /// <summary>Vue en tranche (au plus 2 Go par appel).</summary>
    public Span<T> AsSpan<T>(long byteOffset, int count) where T : unmanaged
    {
        long bytes = (long)count * sizeof(T);
        if (byteOffset < 0 || bytes < 0 || byteOffset + bytes > Length) throw new ArgumentOutOfRangeException(nameof(count));
        return new Span<T>(Pointer + byteOffset, count);
    }

    public void Dispose()
    {
        if (_pointer == null) return;
        NativeMemory.AlignedFree(_pointer);
        _pointer = null;
    }
}
