using PCPerfSuite.Core.Benchmark.Protocol;

namespace PCPerfSuite.Core.Benchmark;

/// <summary>Valeur de référence d'une mesure : ce que vaut 1000 points.</summary>
public sealed record ScoreReference(string MeasurementKey, double ReferenceValue, bool HigherIsBetter = true);

/// <summary>
/// Points d'un test (décision de Denis du 02/10/2026 : unités physiques et points). 1000 points = la machine de
/// référence ; les points d'un test sont 1000 × la moyenne géométrique des ratios mesure / référence (référence / mesure
/// pour une latence), sur les seules mesures qui ont une référence. Les références de <see cref="BenchReferences"/> sont
/// provisoires : relevées sur un Ryzen 7 5800H portable, à rebaser sur le i5-14600K de Denis. Expérimental (règle 6).
/// </summary>
public static class BenchScore
{
    public const double ReferencePoints = 1000;

    public static double? Points(IEnumerable<BenchMeasurement> measurements, IReadOnlyDictionary<string, ScoreReference> references)
    {
        var ratios = new List<double>();
        foreach (BenchMeasurement measurement in measurements)
        {
            if (!references.TryGetValue(measurement.Key, out ScoreReference? reference)) continue;
            if (!(reference.ReferenceValue > 0) || !(measurement.Median > 0)) continue;
            ratios.Add(reference.HigherIsBetter
                ? measurement.Median / reference.ReferenceValue
                : reference.ReferenceValue / measurement.Median);
        }

        if (ratios.Count == 0) return null;
        double mean = BenchStatistics.GeometricMean(ratios);
        return double.IsNaN(mean) ? null : Math.Round(mean * ReferencePoints);
    }
}

/// <summary>Références provisoires par test (voir <see cref="BenchScore"/>). Les clés sont celles des mesures
/// « rafale » (le soutenu est un diagnostic, pas un score) et des profils disque en Mo/s. Valeurs relevées le
/// 03/10/2026 sur le poste de reprise : AMD Ryzen 7 5800H (portable, 8 cœurs / 16 fils, Zen 3), 16 Go de DDR4-3200,
/// NVMe du système ; à rebaser sur le i5-14600K de Denis, puis à confronter au 13500T et au portable Ryzen. Changer
/// une référence oblige à incrémenter <see cref="BenchVersion.Bench"/>.</summary>
public static class BenchReferences
{
    private static readonly IReadOnlyDictionary<string, ScoreReference> CpuMono = Build(
        new ScoreReference("entier.rafale", 325),
        new ScoreReference("flottant.rafale", 48),
        new ScoreReference("branches.rafale", 395));

    private static readonly IReadOnlyDictionary<string, ScoreReference> CpuMulti = Build(
        new ScoreReference("entier.rafale", 4160),
        new ScoreReference("flottant.rafale", 300),
        new ScoreReference("branches.rafale", 5030));

    private static readonly IReadOnlyDictionary<string, ScoreReference> RamBandwidth = Build(
        new ScoreReference("lecture", 15.5),
        new ScoreReference("ecriture", 12.4),
        new ScoreReference("copie", 15.5));

    private static readonly IReadOnlyDictionary<string, ScoreReference> RamLatency = Build(
        new ScoreReference("latence", 131, HigherIsBetter: false));

    private static readonly IReadOnlyDictionary<string, ScoreReference> Disk = Build(
        new ScoreReference("seq1m-q8.lecture", 2910),
        new ScoreReference("seq1m-q8.ecriture", 1636),
        new ScoreReference("seq1m-q1.lecture", 2389),
        new ScoreReference("seq1m-q1.ecriture", 1631),
        new ScoreReference("alea4k-q32.lecture", 206),
        new ScoreReference("alea4k-q32.ecriture", 119),
        new ScoreReference("alea4k-q1.lecture", 43),
        new ScoreReference("alea4k-q1.ecriture", 77));

    public static IReadOnlyDictionary<string, ScoreReference> For(BenchTestKind kind) => kind switch
    {
        BenchTestKind.CpuMono => CpuMono,
        BenchTestKind.CpuMulti => CpuMulti,
        BenchTestKind.RamBandwidth => RamBandwidth,
        BenchTestKind.RamLatency => RamLatency,
        BenchTestKind.Disk => Disk,
        _ => new Dictionary<string, ScoreReference>(),
    };

    private static IReadOnlyDictionary<string, ScoreReference> Build(params ScoreReference[] references)
        => references.ToDictionary(r => r.MeasurementKey, r => r, StringComparer.Ordinal);
}
