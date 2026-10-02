namespace PCPerfSuite.Core.Benchmark.Kernels;

/// <summary>Les noyaux processeur du bench, par clé, dans l'ordre où les passes les enchaînent.</summary>
public static class CpuKernelCatalog
{
    public const string Integer = "entier";
    public const string Float = "flottant";
    public const string Branches = "branches";

    public static readonly IReadOnlyList<string> Keys = [Integer, Float, Branches];

    /// <summary>Graine commune à tous les threads et à toutes les passes : même travail, même somme attendue.</summary>
    public const ulong DefaultSeed = 0x50435065726653UL; // « PCPerfS »

    public static ICpuKernel Create(string key, ulong seed = DefaultSeed) => key switch
    {
        Integer => new IntegerKernel(seed),
        Float => new FloatKernel(seed),
        Branches => new BranchKernel(seed),
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, "Noyau inconnu."),
    };

    public static bool IsKnown(string? key) => key is not null && Keys.Contains(key);
}
