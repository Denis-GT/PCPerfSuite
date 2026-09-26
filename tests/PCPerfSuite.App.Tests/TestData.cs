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

    /// <summary>GPU dédié dont tous les capteurs de mémoire répondent.</summary>
    public static GpuSnapshot Gpu => new() { VramUsedMb = 8200, VramTotalMb = 16000, MemoryClockMhz = 9501, MemoryJunctionTempC = 70 };

    /// <summary>RAM lue normalement.</summary>
    public static MemorySnapshot Memory => new() { UsedGb = 12.4f, TotalGb = 32, LoadPercent = 39 };

    /// <summary>Relevé d'un PC de bureau bien équipé : GPU dédié et RAM lus, tous les groupes de capteurs déjà lus une fois.</summary>
    public static MetricSample Sample(HardwareSnapshot? hardware = null, RtssFrameStats? game = null) => new()
    {
        Hardware = hardware ?? Hardware(Gpu, Memory),
        Game = game,
        LocalTime = new DateTime(2026, 9, 26, 12, 0, 0),
    };

    /// <summary>Un PC dont tous les groupes ont déjà été lus : une valeur nulle y vaut « N/D » (ce PC ne la fournit pas),
    /// pas « -- » (pas encore lue). <paramref name="gpu"/> nul : pas de GPU dédié.</summary>
    public static HardwareSnapshot Hardware(GpuSnapshot? gpu, MemorySnapshot memory) => new()
    {
        Gpu = gpu,
        Memory = memory,
        GroupsEverRead = Enum.GetValues<SensorGroup>(),
    };
}
