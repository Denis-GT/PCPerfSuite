using PCPerfSuite.Core.Compatibility;
using PCPerfSuite.Core.Hardware.Cpu;
using PCPerfSuite.Core.Hardware.Memory;

namespace PCPerfSuite.Core.Benchmark.Memory;

/// <summary>Tailles retenues pour les tests mémoire, ou pourquoi un test est indisponible sur ce PC (règle 3).</summary>
public sealed record MemoryBenchSizes(
    long? BandwidthBytes, Unavailable? BandwidthUnavailable,
    long? LatencyBytes, Unavailable? LatencyUnavailable,
    long? LargestL3Bytes, long? AvailableBytes);

/// <summary>
/// Dimensionnement des tampons des tests mémoire, en logique pure, d'après le plus grand L3 et la RAM libre :
/// débit = max(8 × L3, 128 Mo) plafonné à 25 % de la RAM libre (un seul tampon, la copie va d'une moitié à l'autre) ;
/// latence = 512 Mo, dans [256 Mo, 1 Go] et sous 25 % de la RAM libre, ramené à une puissance de deux. Sous le plancher,
/// le test est indisponible et dit pourquoi. Bornes expérimentales (règle 6).
/// </summary>
public static class MemoryBenchSizing
{
    public const long Mebibyte = 1L << 20;
    public const long BandwidthFloorBytes = 128 * Mebibyte;
    public const int L3Multiplier = 8;
    public const long LatencyDefaultBytes = 512 * Mebibyte;
    public const long LatencyMinimumBytes = 256 * Mebibyte;
    public const long LatencyMaximumBytes = 1024 * Mebibyte;
    public const double AvailableShare = 0.25;

    /// <summary>Tailles d'après le plus grand L3 (null : inconnu, plancher seul) et la RAM libre (null : inconnue, aucun
    /// plafond mais une note).</summary>
    public static MemoryBenchSizes Compute(long? largestL3Bytes, long? availableBytes)
    {
        long? cap = availableBytes is { } available ? (long)(available * AvailableShare) : null;

        long bandwidth = Math.Max(BandwidthFloorBytes, largestL3Bytes is > 0 ? L3Multiplier * largestL3Bytes.Value : 0);
        if (cap is { } c1 && c1 < bandwidth) bandwidth = c1;
        bandwidth = MemorySlices.AlignDown(bandwidth, 2 * Mebibyte);
        Unavailable? bandwidthProblem = bandwidth < BandwidthFloorBytes
            ? new Unavailable(UnavailableCause.HardwareOrDriver,
                $"pas assez de RAM libre : il faut {BandwidthFloorBytes / Mebibyte} Mo disponibles pour le tampon ({Describe(availableBytes)} libres)")
            : null;

        long latency = LatencyDefaultBytes;
        if (cap is { } c2 && c2 < latency) latency = c2;
        latency = RoundDownToPowerOfTwo(Math.Min(latency, LatencyMaximumBytes));
        Unavailable? latencyProblem = latency < LatencyMinimumBytes
            ? new Unavailable(UnavailableCause.HardwareOrDriver,
                $"pas assez de RAM libre : il faut {LatencyMinimumBytes / Mebibyte} Mo disponibles pour le tampon ({Describe(availableBytes)} libres)")
            : null;

        return new MemoryBenchSizes(
            bandwidthProblem is null ? bandwidth : null, bandwidthProblem,
            latencyProblem is null ? latency : null, latencyProblem,
            largestL3Bytes, availableBytes);
    }

    /// <summary>Tailles pour ce PC : L3 de la topologie (le plus grand groupe de cache) et RAM libre lue à l'instant.
    /// Best-effort : une lecture absente laisse la valeur à null.</summary>
    public static MemoryBenchSizes ReadCurrent(CpuTopology? topology)
    {
        long? l3 = topology?.Clusters.Select(c => c.L3Bytes).Where(b => b is > 0).Max();
        SystemMemoryReader.Reading? memory = SystemMemoryReader.Read();
        long? available = memory?.AvailableGb is { } gb ? (long)(gb * (1L << 30)) : null;
        return Compute(l3, available);
    }

    public static long RoundDownToPowerOfTwo(long value)
    {
        if (value <= 0) return 0;
        return 1L << (63 - System.Numerics.BitOperations.LeadingZeroCount((ulong)value));
    }

    private static string Describe(long? bytes) => bytes is { } b ? $"{b / Mebibyte} Mo" : "quantité inconnue";
}
