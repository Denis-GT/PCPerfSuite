using System.Runtime.CompilerServices;

namespace PCPerfSuite.Core.Benchmark.Kernels;

/// <summary>
/// Noyau entier : tri (introsort maison, en place) d'un tampon de 65 536 entiers régénéré depuis la graine à chaque
/// exécution, puis hachage FNV-1a 64 bits du résultat. Sans instruction SHA ni AES, pour n'avantager aucune puce. Le
/// tampon (256 Ko) tient dans le L2 de n'importe quel cœur : c'est le processeur qu'on mesure, pas la mémoire.
/// Opérations comptées : n·log2(n) comparaisons-échanges + n pour le hachage.
/// </summary>
public sealed class IntegerKernel : ICpuKernel
{
    public const int ElementCount = 1 << 16;
    private const int InsertionThreshold = 16;

    private readonly uint[] _data = new uint[ElementCount];
    private readonly ulong _seed;

    public IntegerKernel(ulong seed) => _seed = seed;

    public string Key => "entier";

    public string Label => "Entier (tri et hachage)";

    public string Unit => "Mops/s";

    public double OperationsPerRun => ElementCount * 16.0 + ElementCount;

    public double UnitScale => 1e6;

    public string InstructionSet => "scalaire";

    public bool IsComparable => true;

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public ulong Run()
    {
        var random = new SeededRandom(_seed);
        random.Fill(_data);
        IntroSort(_data, 0, _data.Length - 1, 2 * Log2(_data.Length));
        return Fnv1a64(_data);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static ulong Fnv1a64(uint[] data)
    {
        ulong hash = 14695981039346656037UL;
        for (int i = 0; i < data.Length; i++)
        {
            hash ^= data[i];
            hash *= 1099511628211UL;
        }
        return hash;
    }

    private static int Log2(int n)
    {
        int log = 0;
        while ((n >>= 1) > 0) log++;
        return log;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void IntroSort(uint[] a, int lo, int hi, int depthLimit)
    {
        while (hi - lo > InsertionThreshold)
        {
            if (depthLimit == 0)
            {
                HeapSort(a, lo, hi);
                return;
            }
            depthLimit--;

            int pivotIndex = Partition(a, lo, hi);
            // La petite moitié en récursion, la grande en boucle : profondeur de pile bornée par log2(n).
            if (pivotIndex - lo < hi - pivotIndex)
            {
                IntroSort(a, lo, pivotIndex - 1, depthLimit);
                lo = pivotIndex + 1;
            }
            else
            {
                IntroSort(a, pivotIndex + 1, hi, depthLimit);
                hi = pivotIndex - 1;
            }
        }
        InsertionSort(a, lo, hi);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static int Partition(uint[] a, int lo, int hi)
    {
        int mid = lo + ((hi - lo) >> 1);
        // Médiane de trois en tête, pivot au milieu.
        if (a[mid] < a[lo]) Swap(a, mid, lo);
        if (a[hi] < a[lo]) Swap(a, hi, lo);
        if (a[hi] < a[mid]) Swap(a, hi, mid);
        uint pivot = a[mid];
        Swap(a, mid, hi - 1);

        int i = lo, j = hi - 1;
        while (true)
        {
            while (a[++i] < pivot) { }
            while (pivot < a[--j]) { }
            if (i >= j) break;
            Swap(a, i, j);
        }
        Swap(a, i, hi - 1);
        return i;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void InsertionSort(uint[] a, int lo, int hi)
    {
        for (int i = lo + 1; i <= hi; i++)
        {
            uint value = a[i];
            int j = i - 1;
            while (j >= lo && a[j] > value)
            {
                a[j + 1] = a[j];
                j--;
            }
            a[j + 1] = value;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void HeapSort(uint[] a, int lo, int hi)
    {
        int n = hi - lo + 1;
        for (int i = n / 2 - 1; i >= 0; i--) SiftDown(a, lo, i, n);
        for (int end = n - 1; end > 0; end--)
        {
            Swap(a, lo, lo + end);
            SiftDown(a, lo, 0, end);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void SiftDown(uint[] a, int lo, int root, int n)
    {
        while (true)
        {
            int child = 2 * root + 1;
            if (child >= n) return;
            if (child + 1 < n && a[lo + child] < a[lo + child + 1]) child++;
            if (a[lo + root] >= a[lo + child]) return;
            Swap(a, lo + root, lo + child);
            root = child;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Swap(uint[] a, int i, int j) => (a[i], a[j]) = (a[j], a[i]);

    public void Dispose()
    {
        // Rien à libérer : tableau géré, gardé pour la durée du test.
    }
}
