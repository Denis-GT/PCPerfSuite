using System.Text.Json;
using System.Text.Json.Serialization;

namespace PCPerfSuite.Core.Benchmark.Protocol;

/// <summary>Une mesure d'un test : ses passes, leur médiane et leur coefficient de variation. L'unité est physique
/// (Mops/s, GFLOPS, Go/s, ns, Mo/s, IOPS) ; les points se calculent à part (<see cref="BenchScore"/>).</summary>
public sealed class BenchMeasurement
{
    /// <summary>Clé stable (« entier.rafale », « lecture », « seq1m-q8.lecture »).</summary>
    public string Key { get; set; } = "";

    public string Label { get; set; } = "";

    public string Unit { get; set; } = "";

    /// <summary>Faux pour une latence.</summary>
    public bool HigherIsBetter { get; set; } = true;

    public List<double> Values { get; set; } = new();

    public double Median { get; set; }

    /// <summary>Coefficient de variation des passes (écart-type / moyenne).</summary>
    public double Cv { get; set; }

    /// <summary>Passes trop dispersées (seuil <see cref="BenchStatistics.UnstableCvThreshold"/>) : activité en arrière-plan ?</summary>
    public bool IsUnstable { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <param name="unstableThreshold">Seuil du drapeau « instable » ; l'infini pour des valeurs dont la dispersion est
    /// attendue (tranches d'une phase disque).</param>
    public static BenchMeasurement From(string key, string label, string unit, IReadOnlyList<double> values, bool higherIsBetter = true,
        double unstableThreshold = BenchStatistics.UnstableCvThreshold)
    {
        PassSummary summary = BenchStatistics.Summarize(values, unstableThreshold);
        return new BenchMeasurement
        {
            Key = key,
            Label = label,
            Unit = unit,
            HigherIsBetter = higherIsBetter,
            Values = values.ToList(),
            Median = summary.Median,
            Cv = summary.CoefficientOfVariation,
            IsUnstable = summary.IsUnstable,
        };
    }
}

/// <summary>Résultat d'un test rendu par le worker. Un échec garde ce qui a pu être mesuré avant.</summary>
public sealed class BenchJobResult
{
    public string JobId { get; set; } = "";

    public string Kind { get; set; } = "";

    public bool Succeeded { get; set; }

    public string? Error { get; set; }

    /// <summary>Un noyau a rendu une somme de contrôle différente de l'étalon : erreur de calcul (matériel instable).</summary>
    public bool ChecksumMismatch { get; set; }

    public List<BenchMeasurement> Measurements { get; set; } = new();

    /// <summary>Faits utiles au diagnostic (jeu d'instructions, épinglage, threads, taille de tampon…).</summary>
    public Dictionary<string, string> Notes { get; set; } = new();

    /// <summary>Faux quand le chemin de calcul n'est pas celui de référence (repli sans AVX2+FMA, ARM64) : le score ne se
    /// compare pas à celui d'un autre PC.</summary>
    public bool IsComparable { get; set; } = true;

    public double DurationSeconds { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    public static BenchJobResult Failure(string jobId, string kind, string error)
        => new() { JobId = jobId, Kind = kind, Succeeded = false, Error = error };

    public BenchMeasurement? Find(string key) => Measurements.FirstOrDefault(m => m.Key == key);
}
