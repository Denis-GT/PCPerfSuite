using System.Runtime.InteropServices;

namespace PCPerfSuite.Core.Benchmark.Kernels;

/// <summary>
/// Générateur xoshiro256** à graine fixe : la même graine donne toujours la même suite, sur tout PC et toute version de
/// .NET, contrairement à <see cref="Random"/>. Sert à produire les données des noyaux et la permutation de Sattolo.
/// Struct : aucune allocation dans les boucles de charge.
/// </summary>
public struct SeededRandom
{
    private ulong _s0, _s1, _s2, _s3;

    public SeededRandom(ulong seed)
    {
        // splitmix64 pour étaler la graine sur les quatre mots d'état (jamais tous nuls).
        ulong state = seed;
        _s0 = SplitMix(ref state);
        _s1 = SplitMix(ref state);
        _s2 = SplitMix(ref state);
        _s3 = SplitMix(ref state);
    }

    private static ulong SplitMix(ref ulong state)
    {
        ulong z = state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    public ulong NextUInt64()
    {
        ulong result = RotateLeft(_s1 * 5, 7) * 9;
        ulong t = _s1 << 17;
        _s2 ^= _s0;
        _s3 ^= _s1;
        _s1 ^= _s2;
        _s0 ^= _s3;
        _s2 ^= t;
        _s3 = RotateLeft(_s3, 45);
        return result;
    }

    public uint NextUInt32() => (uint)(NextUInt64() >> 32);

    /// <summary>Entier dans [0, bound) sans rejet (réduction par multiplication, Lemire) : déterministe et sans boucle.</summary>
    public uint NextBelow(uint bound) => (uint)(((ulong)NextUInt32() * bound) >> 32);

    /// <summary>Flottant dans [-1, 1).</summary>
    public float NextUnitFloat() => (float)((NextUInt64() >> 40) * (1.0 / (1UL << 24))) * 2f - 1f;

    public void Fill(Span<uint> destination)
    {
        for (int i = 0; i < destination.Length; i++) destination[i] = NextUInt32();
    }

    public void Fill(Span<byte> destination)
    {
        Span<ulong> words = MemoryMarshal.Cast<byte, ulong>(destination);
        for (int i = 0; i < words.Length; i++) words[i] = NextUInt64();
        for (int i = words.Length * sizeof(ulong); i < destination.Length; i++) destination[i] = (byte)NextUInt64();
    }

    private static ulong RotateLeft(ulong value, int offset) => (value << offset) | (value >> (64 - offset));
}
