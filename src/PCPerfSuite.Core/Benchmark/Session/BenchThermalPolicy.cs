using PCPerfSuite.Core.Hardware.Cpu;
using PCPerfSuite.Core.Safety;

namespace PCPerfSuite.Core.Benchmark.Session;

/// <summary>Seuil thermique retenu pour le processeur pendant un bench, et d'où il vient.</summary>
public sealed record BenchThermalLimits(float CpuThresholdC, string Source, TimeSpan Delay, TimeSpan LossDelay)
{
    public const string SensorName = "processeur";

    /// <summary>Garde de #2 avec ce seuil : tenu <see cref="Delay"/> → arrêt ; plus aucune lecture pendant
    /// <see cref="LossDelay"/> → perte.</summary>
    public ThermalGuard CreateGuard() => new([new ThermalLimit(SensorName, CpuThresholdC, Delay)], LossDelay);

    public string Describe() => $"{CpuThresholdC:0} °C tenus {Delay.TotalSeconds:0} s ({Source})";
}

/// <summary>
/// Seuil d'arrêt thermique du bench par famille de processeur, en logique pure. Un processeur se protège lui-même ; la
/// garde n'est là que pour un refroidissement défaillant : un Intel à son TjMax (relevé par le socle de signaux, #4)
/// pendant 10 s, un Zen 4 ou Zen 5 à 98 °C (95 °C est son régime normal sous charge), un X3D à 93 °C (89 °C normal),
/// 98 °C quand on ne sait pas. Seuils expérimentaux (règle 6), à vérifier sur le portable Ryzen.
/// </summary>
public static class BenchThermalPolicy
{
    public const float DefaultThresholdC = 98;
    public const float Zen4Or5ThresholdC = 98;
    public const float X3DThresholdC = 93;
    public static readonly TimeSpan Delay = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan LossDelay = TimeSpan.FromSeconds(10);

    public static BenchThermalLimits Resolve(CpuPlatform? platform, CpuTopology? topology, int? tjMaxC)
    {
        if (platform?.Vendor == CpuVendor.Intel)
        {
            if (tjMaxC is > 60 and <= 115) return new BenchThermalLimits(tjMaxC.Value, "TjMax lu dans le processeur", Delay, LossDelay);
            return new BenchThermalLimits(DefaultThresholdC, "Intel sans TjMax lu : seuil prudent", Delay, LossDelay);
        }

        if (platform?.Vendor == CpuVendor.Amd)
        {
            bool x3d = topology?.HasMixedL3Sizes == true || platform.Name.Contains("X3D", StringComparison.OrdinalIgnoreCase);
            if (x3d) return new BenchThermalLimits(X3DThresholdC, "Ryzen X3D (89 °C normal sous charge)", Delay, LossDelay);
            bool zen4Or5 = (platform.Family == 0x19 && platform.Model >= 0x60) || platform.Family == 0x1A;
            if (zen4Or5) return new BenchThermalLimits(Zen4Or5ThresholdC, "Zen 4/5 (95 °C normal sous charge)", Delay, LossDelay);
            return new BenchThermalLimits(DefaultThresholdC, "AMD, famille non reconnue : seuil prudent", Delay, LossDelay);
        }

        return new BenchThermalLimits(DefaultThresholdC, "processeur non reconnu : seuil prudent", Delay, LossDelay);
    }
}
