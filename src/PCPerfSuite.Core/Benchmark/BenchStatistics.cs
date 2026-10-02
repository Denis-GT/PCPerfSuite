namespace PCPerfSuite.Core.Benchmark;

/// <summary>Résumé des passes d'une mesure.</summary>
public sealed record PassSummary(double Median, double CoefficientOfVariation, bool IsUnstable, int Count);

/// <summary>
/// Statistiques du protocole de mesure : on garde la médiane des passes (insensible à une passe parasitée) et leur
/// coefficient de variation ; au-delà du seuil, la mesure est « instable », signe d'une activité en arrière-plan ou d'un
/// bridage qui oscille. Le seuil de 3 % est une proposition à calibrer sur de vraies machines (règle 6).
/// </summary>
public static class BenchStatistics
{
    /// <summary>Expérimental.</summary>
    public const double UnstableCvThreshold = 0.03;

    public static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return double.NaN;
        double[] sorted = values.OrderBy(v => v).ToArray();
        int middle = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }

    public static double Mean(IReadOnlyList<double> values) => values.Count == 0 ? double.NaN : values.Sum() / values.Count;

    /// <summary>Écart-type d'échantillon (n − 1) rapporté à la moyenne ; 0 pour moins de deux valeurs ou une moyenne nulle.</summary>
    public static double CoefficientOfVariation(IReadOnlyList<double> values)
    {
        if (values.Count < 2) return 0;
        double mean = Mean(values);
        if (mean == 0 || double.IsNaN(mean)) return 0;
        double sumOfSquares = values.Sum(v => (v - mean) * (v - mean));
        return Math.Sqrt(sumOfSquares / (values.Count - 1)) / Math.Abs(mean);
    }

    public static PassSummary Summarize(IReadOnlyList<double> values, double unstableThreshold = UnstableCvThreshold)
    {
        double cv = CoefficientOfVariation(values);
        return new PassSummary(Median(values), cv, cv > unstableThreshold, values.Count);
    }

    /// <summary>Moyenne géométrique de valeurs strictement positives ; NaN sans valeur ou avec une valeur non positive.</summary>
    public static double GeometricMean(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return double.NaN;
        double logSum = 0;
        foreach (double value in values)
        {
            if (!(value > 0)) return double.NaN;
            logSum += Math.Log(value);
        }
        return Math.Exp(logSum / values.Count);
    }
}
