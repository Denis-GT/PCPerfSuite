using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace PCPerfSuite.Core.Benchmark.Kernels;

/// <summary>
/// Noyau flottant : produits de matrices 128 × 128 en simple précision, en <see cref="Vector256{T}"/> + FMA (chemin de
/// référence), avec repli <see cref="Vector{T}"/> sans FMA (SSE, ARM64 AdvSimd) marqué non comparable. Les trois
/// matrices (192 Ko) tiennent dans le L2 de n'importe quel cœur. Somme de contrôle : hachage bit à bit du résultat,
/// déterministe pour un chemin de calcul donné. Opérations : 2·n³ flops par produit.
/// </summary>
public sealed unsafe class FloatKernel : ICpuKernel
{
    public const int Size = 128;
    public const int ProductsPerRun = 16;

    private readonly AlignedBuffer _a = AlignedBuffer.Allocate(Size * Size * sizeof(float));
    private readonly AlignedBuffer _b = AlignedBuffer.Allocate(Size * Size * sizeof(float));
    private readonly AlignedBuffer _c = AlignedBuffer.Allocate(Size * Size * sizeof(float));
    private readonly bool _useFma;

    public FloatKernel(ulong seed)
    {
        _useFma = Fma.IsSupported && Avx.IsSupported;
        InstructionSet = _useFma ? "AVX2+FMA" : Vector.IsHardwareAccelerated ? $"Vector<T> ({Vector<float>.Count} lanes)" : "scalaire";

        var random = new SeededRandom(seed);
        Span<float> a = _a.AsSpan<float>(0, Size * Size);
        Span<float> b = _b.AsSpan<float>(0, Size * Size);
        for (int i = 0; i < a.Length; i++) a[i] = random.NextUnitFloat();
        for (int i = 0; i < b.Length; i++) b[i] = random.NextUnitFloat();
        _c.AsSpan<float>(0, Size * Size).Clear();
    }

    public string Key => "flottant";

    public string Label => "Flottant (produit matriciel)";

    public string Unit => "GFLOPS";

    public double OperationsPerRun => 2.0 * Size * Size * Size * ProductsPerRun;

    public double UnitScale => 1e9;

    public string InstructionSet { get; }

    public bool IsComparable => _useFma;

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public ulong Run()
    {
        float* a = (float*)_a.Pointer;
        float* b = (float*)_b.Pointer;
        float* c = (float*)_c.Pointer;
        for (int product = 0; product < ProductsPerRun; product++)
        {
            if (_useFma) MultiplyFma(a, b, c);
            else MultiplyVector(a, b, c);
        }
        return Hash(c, Size * Size);
    }

    /// <summary>Pour chaque ligne de C, quatre accumulateurs de 8 lanes (32 colonnes) parcourent k : 4 chargements et 4 FMA
    /// par k, les accumulateurs restent en registres.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void MultiplyFma(float* a, float* b, float* c)
    {
        for (int i = 0; i < Size; i++)
        {
            float* aRow = a + i * Size;
            float* cRow = c + i * Size;
            for (int j = 0; j < Size; j += 32)
            {
                Vector256<float> acc0 = Vector256<float>.Zero, acc1 = Vector256<float>.Zero;
                Vector256<float> acc2 = Vector256<float>.Zero, acc3 = Vector256<float>.Zero;
                for (int k = 0; k < Size; k++)
                {
                    Vector256<float> aik = Vector256.Create(aRow[k]);
                    float* bRow = b + k * Size + j;
                    acc0 = Fma.MultiplyAdd(aik, Avx.LoadAlignedVector256(bRow), acc0);
                    acc1 = Fma.MultiplyAdd(aik, Avx.LoadAlignedVector256(bRow + 8), acc1);
                    acc2 = Fma.MultiplyAdd(aik, Avx.LoadAlignedVector256(bRow + 16), acc2);
                    acc3 = Fma.MultiplyAdd(aik, Avx.LoadAlignedVector256(bRow + 24), acc3);
                }
                Avx.StoreAligned(cRow + j, acc0);
                Avx.StoreAligned(cRow + j + 8, acc1);
                Avx.StoreAligned(cRow + j + 16, acc2);
                Avx.StoreAligned(cRow + j + 24, acc3);
            }
        }
    }

    /// <summary>Repli portable : même découpage, multiplication puis addition séparées (pas de FMA, arrondi différent).</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void MultiplyVector(float* a, float* b, float* c)
    {
        int lanes = Vector<float>.Count;
        for (int i = 0; i < Size; i++)
        {
            float* aRow = a + i * Size;
            float* cRow = c + i * Size;
            for (int j = 0; j < Size; j += lanes)
            {
                Vector<float> acc = Vector<float>.Zero;
                for (int k = 0; k < Size; k++)
                {
                    var aik = new Vector<float>(aRow[k]);
                    acc += aik * Vector.Load(b + k * Size + j);
                }
                acc.Store(cRow + j);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static ulong Hash(float* values, int count)
    {
        uint* bits = (uint*)values;
        ulong hash = 14695981039346656037UL;
        for (int i = 0; i < count; i++)
        {
            hash ^= bits[i];
            hash *= 1099511628211UL;
        }
        return hash;
    }

    public void Dispose()
    {
        _a.Dispose();
        _b.Dispose();
        _c.Dispose();
    }
}
