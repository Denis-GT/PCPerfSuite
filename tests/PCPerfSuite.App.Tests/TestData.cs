using PCPerfSuite.App.Metrics;
using PCPerfSuite.App.Overlay;
using PCPerfSuite.Core.Hardware;
using PCPerfSuite.Core.Overlay;

namespace PCPerfSuite.App.Tests;

/// <summary>Métriques et relevés réels pour les tests : les définitions viennent du vrai catalogue, les relevés
/// se construisent avec les vrais snapshots — aucun faux objet, donc rien qui puisse diverger du code de l'app.</summary>
internal static class TestData
{
    /// <summary>Les métriques du catalogue portant ces identifiants, dans l'ordre demandé.</summary>
    public static List<MetricDefinition> Metrics(params string[] ids)
        => ids.Select(id => MetricCatalog.All.Single(m => m.Id == id)).ToList();

    /// <summary>Même ordre que la sélection de l'app : celui du catalogue, quel que soit l'ordre des identifiants.</summary>
    public static List<MetricDefinition> Selection(params string[] ids)
        => MetricCatalog.All.Where(m => ids.Contains(m.Id)).ToList();

    public static OverlayColorScheme Colors { get; } = new()
    {
        CategoryColor = category => category.OverlayColor,
        ValueColor = "#FFFFFF",
    };

    /// <summary>Relevé d'un PC de bureau bien équipé : GPU dédié, RAM, disques lus.</summary>
    public static MetricSample Sample(HardwareSnapshot? hardware = null, RtssFrameStats? game = null) => new()
    {
        Hardware = hardware ?? Hardware(),
        Game = game,
        LocalTime = new DateTime(2026, 9, 26, 12, 0, 0),
    };

    public static HardwareSnapshot Hardware(GpuSnapshot? gpu = null, MemorySnapshot? memory = null) => new()
    {
        Gpu = gpu ?? new GpuSnapshot { VramUsedMb = 8200, VramTotalMb = 16000, MemoryClockMhz = 9501, MemoryJunctionTempC = 70 },
        Memory = memory ?? new MemorySnapshot { UsedGb = 12.4f, TotalGb = 32, LoadPercent = 39 },
        GroupsEverRead = Enum.GetValues<SensorGroup>(),
    };
}
