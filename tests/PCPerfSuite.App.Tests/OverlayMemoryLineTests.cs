using PCPerfSuite.App.Metrics;
using PCPerfSuite.App.Overlay;
using PCPerfSuite.Core.Hardware;

namespace PCPerfSuite.App.Tests;

/// <summary>Lignes VRAM (mémoire du GPU) et RAM : deux lignes distinctes, qui n'apparaissent que si ce PC fournit au
/// moins une de leurs valeurs.</summary>
public class OverlayMemoryLineTests
{
    private static List<OverlayLine> Build(MetricSample sample, params string[] ids)
    {
        var metrics = TestData.Selection(ids);
        return OverlayComposer.Build(
            metrics, oneLinePerMetric: false, TestData.Colors,
            unavailableMemoryIds: OverlayComposer.UnavailableMemoryIds(metrics, sample));
    }

    [Fact]
    public void VramAndRam_AreTwoLines_VramRightUnderGpu()
    {
        List<OverlayLine> lines = Build(TestData.Sample(), "gpu.load", "gpu.clock.memory", "gpu.vram.used", "ram.load", "ram.used");

        Assert.Equal(new[] { "gpu", "vram", "ram" }, lines.Select(l => l.Key));
        Assert.Equal(new[] { "GPU", "VRAM", "RAM" }, lines.Select(l => l.Label));
        Assert.Equal(new[] { "gpu.load" }, lines[0].Cells.Select(c => c.Metric.Id));
        Assert.Equal(new[] { "gpu.clock.memory", "gpu.vram.used" }, lines[1].Cells.Select(c => c.Metric.Id));
        Assert.Equal(new[] { "ram.load", "ram.used" }, lines[2].Cells.Select(c => c.Metric.Id));
        Assert.All(lines.SelectMany(l => l.Cells), c => Assert.Null(c.Prefix));
    }

    [Fact]
    public void VramLine_HasTheGpuColourAndAReadableTitle()
    {
        OverlayLine vram = Assert.Single(Build(TestData.Sample(), "gpu.vram.used"));

        Assert.Equal(MetricCatalog.Categories.Single(c => c.Key == "gpu").OverlayColor, vram.LabelColorHex);
        Assert.Equal("Mémoire du GPU (VRAM)", vram.Title);
    }

    [Fact]
    public void VramUnavailable_DropsTheVramLine()
    {
        // GPU présent, mais sans aucun capteur de mémoire lisible.
        MetricSample sample = TestData.Sample(TestData.Hardware(new GpuSnapshot(), TestData.Memory));

        List<OverlayLine> lines = Build(sample, "gpu.clock.memory", "gpu.vram.used", "ram.used");

        Assert.Equal(new[] { "ram" }, lines.Select(l => l.Key));
    }

    [Fact]
    public void NoDedicatedGpu_DropsTheVramLine()
    {
        MetricSample sample = TestData.Sample(TestData.Hardware(null, TestData.Memory));

        Assert.Equal(new[] { "ram" }, Build(sample, "gpu.vram.used", "ram.used").Select(l => l.Key));
    }

    [Fact]
    public void RamUnavailable_DropsTheRamLine()
    {
        MetricSample sample = TestData.Sample(TestData.Hardware(TestData.Gpu, new MemorySnapshot()));

        List<OverlayLine> lines = Build(sample, "gpu.clock.memory", "gpu.vram.used", "ram.used", "ram.load");

        Assert.Equal(new[] { "vram" }, lines.Select(l => l.Key));
    }

    [Fact]
    public void BothUnavailable_DropsBothLines_AndLeavesTheOthers()
    {
        // cpu.load vaut aussi « N/D » ici : seules les lignes VRAM et RAM disparaissent faute de valeur.
        MetricSample sample = TestData.Sample(TestData.Hardware(null, new MemorySnapshot()));

        List<OverlayLine> lines = Build(sample, "cpu.load", "gpu.vram.used", "ram.used");

        Assert.Equal(new[] { "cpu" }, lines.Select(l => l.Key));
    }

    [Fact]
    public void PartlyUnavailableLine_KeepsItsOtherValues_WithNdWhereMissing()
    {
        // La VRAM utilisée est lue, la fréquence mémoire non : la ligne garde les deux (le « N/D » explique le trou).
        MetricSample sample = TestData.Sample(TestData.Hardware(new GpuSnapshot { VramUsedMb = 8200 }, TestData.Memory));

        List<OverlayLine> lines = Build(sample, "gpu.clock.memory", "gpu.vram.used", "ram.used");
        OverlayComposer.Update(lines, sample);

        OverlayLine vram = Assert.Single(lines, l => l.Key == "vram");
        Assert.Equal(new[] { "gpu.clock.memory", "gpu.vram.used" }, vram.Cells.Select(c => c.Metric.Id));
        Assert.Equal("N/D", vram.Cells[0].Value);
    }

    [Fact]
    public void NotReadYet_RemovesNothing()
    {
        // Aucun groupe encore lu : les valeurs valent « -- » (en attente), pas « N/D » (ce PC ne les fournit pas).
        MetricSample sample = TestData.Sample(new HardwareSnapshot { GroupsEverRead = Array.Empty<SensorGroup>() });

        List<OverlayLine> lines = Build(sample, "gpu.vram.used", "ram.used");
        OverlayComposer.Update(lines, sample);

        Assert.Equal(new[] { "vram", "ram" }, lines.Select(l => l.Key));
        Assert.All(lines.SelectMany(l => l.Cells), c => Assert.Equal("--", c.Value));
    }

    [Fact]
    public void UnavailableMemoryIds_OnlyReportsMetricsOfTheVramAndRamLines()
    {
        // cpu.load vaut aussi « N/D » ici, mais ne fait partie ni de la ligne VRAM ni de la ligne RAM.
        MetricSample sample = TestData.Sample(TestData.Hardware(null, new MemorySnapshot()));

        HashSet<string> ids = OverlayComposer.UnavailableMemoryIds(
            TestData.Selection("cpu.load", "gpu.vram.used", "ram.used"), sample);

        Assert.Equal(new HashSet<string> { "gpu.vram.used", "ram.used" }, ids);
    }

    [Fact]
    public void WithoutAnUnavailableSet_NothingIsRemoved()
    {
        List<OverlayLine> lines = OverlayComposer.Build(
            TestData.Selection("gpu.vram.used", "ram.used"), oneLinePerMetric: false, TestData.Colors);

        Assert.Equal(new[] { "vram", "ram" }, lines.Select(l => l.Key));
    }

    [Fact]
    public void SavedOrderWithoutTheVramLine_PutsItRightUnderGpu()
    {
        // Ordre enregistré avant que la mémoire du GPU ait sa ligne : elle prend place sous GPU, où qu'il soit.
        List<string> order = OverlayLineOrder.Merge(new[] { "ram", "gpu", "cpu" }, OverlayComposer.CatalogCategoryOrder);

        Assert.Equal(order.IndexOf("gpu") + 1, order.IndexOf("vram"));
    }
}
