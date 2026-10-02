namespace PCPerfSuite.Core.Benchmark.Kernels;

/// <summary>
/// Noyau de charge processeur à résultat vérifié : un travail déterministe à graine fixe, sans allocation, dont la somme
/// de contrôle doit être identique à chaque exécution. Une somme différente, c'est une erreur de calcul (matériel
/// instable, tension trop basse) : c'est ce que l'OC automatique (#15, #16) attend du moteur. Une instance par thread.
/// </summary>
public interface ICpuKernel : IDisposable
{
    /// <summary>Clé stable (« entier », « flottant », « branches »).</summary>
    string Key { get; }

    string Label { get; }

    /// <summary>Unité du débit (« Mops/s », « GFLOPS »).</summary>
    string Unit { get; }

    /// <summary>Opérations élémentaires d'un <see cref="Run"/>, pour convertir un nombre d'exécutions en débit.</summary>
    double OperationsPerRun { get; }

    /// <summary>Diviseur de l'unité : 1e6 pour des Mops/s, 1e9 pour des GFLOPS.</summary>
    double UnitScale { get; }

    /// <summary>Jeu d'instructions effectivement utilisé (« AVX2+FMA », « Vector<T> », « scalaire »).</summary>
    string InstructionSet { get; }

    /// <summary>Faux quand ce n'est pas le chemin de référence : le score ne se compare pas à un autre PC.</summary>
    bool IsComparable { get; }

    /// <summary>Une unité de travail (quelques millisecondes) ; renvoie la somme de contrôle.</summary>
    ulong Run();
}

/// <summary>Issue d'une série d'exécutions chronométrées d'un noyau.</summary>
public sealed record KernelPassOutcome(double Seconds, long Runs, double Operations, int Mismatches)
{
    /// <summary>Débit dans l'unité du noyau.</summary>
    public double Rate(ICpuKernel kernel) => Seconds > 0 ? Operations / Seconds / kernel.UnitScale : 0;
}

public static class KernelPass
{
    /// <summary>Exécute le noyau en boucle pendant au moins <paramref name="duration"/> (la dernière exécution se termine),
    /// en comptant les sommes différentes de <paramref name="expectedChecksum"/>.</summary>
    public static KernelPassOutcome RunFor(ICpuKernel kernel, ulong expectedChecksum, TimeSpan duration, CancellationToken cancel)
    {
        long ticksToRun = (long)(duration.TotalSeconds * System.Diagnostics.Stopwatch.Frequency);
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        long runs = 0;
        int mismatches = 0;
        long elapsed;
        do
        {
            if (kernel.Run() != expectedChecksum) mismatches++;
            runs++;
            elapsed = System.Diagnostics.Stopwatch.GetTimestamp() - start;
        }
        while (elapsed < ticksToRun && !cancel.IsCancellationRequested);

        return new KernelPassOutcome((double)elapsed / System.Diagnostics.Stopwatch.Frequency, runs, runs * kernel.OperationsPerRun, mismatches);
    }

    /// <summary>Somme de contrôle étalon : la première exécution d'un noyau, dans ce processus, avec ce chemin de calcul.</summary>
    public static ulong Calibrate(ICpuKernel kernel) => kernel.Run();
}
