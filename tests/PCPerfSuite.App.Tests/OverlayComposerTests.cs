using PCPerfSuite.App.Overlay;

namespace PCPerfSuite.App.Tests;

public class OverlayComposerTests
{
    [Fact]
    public void MemoryLine_PutsGpuMemoryBeforeRam_WithSubLabels()
    {
        // Sélection dans l'ordre du catalogue : la RAM (catégorie 3) ne vient qu'après la mémoire du GPU.
        var metrics = TestData.Selection("gpu.clock.memory", "gpu.vram.used", "ram.used");

        List<OverlayLine> lines = OverlayComposer.Build(metrics, oneLinePerMetric: false, TestData.Colors);

        OverlayLine mem = Assert.Single(lines);
        Assert.Equal("MEM", mem.Label);
        Assert.Equal(new[] { "gpu.clock.memory", "gpu.vram.used", "ram.used" }, mem.Cells.Select(c => c.Metric.Id));
        Assert.Equal(new[] { "VRAM", null, "RAM" }, mem.Cells.Select(c => c.Prefix));
    }

    [Fact]
    public void MemoryLine_WithoutSubLabels_HasNoPrefix()
    {
        var metrics = TestData.Selection("gpu.vram.used", "ram.used");

        List<OverlayLine> lines = OverlayComposer.Build(metrics, oneLinePerMetric: false, TestData.Colors, memorySubLabels: false);

        Assert.All(Assert.Single(lines).Cells, cell => Assert.Null(cell.Prefix));
    }

    [Fact]
    public void Build_ByCategory_FollowsTheCatalogOrderByDefault()
    {
        var metrics = TestData.Selection("net.download", "ram.used", "gpu.load", "gpu.vram.used", "cpu.load");

        List<OverlayLine> lines = OverlayComposer.Build(metrics, oneLinePerMetric: false, TestData.Colors);

        // La mémoire du GPU quitte la ligne GPU pour la ligne MEM, rangée avec la RAM (sous GPU).
        Assert.Equal(new[] { "cpu", "gpu", "ram", "net" }, lines.Select(l => l.Key));
        Assert.Equal(new[] { "CPU", "GPU", "MEM", "NET" }, lines.Select(l => l.Label));
    }

    [Fact]
    public void Build_ByCategory_FollowsTheGivenOrder()
    {
        var metrics = TestData.Selection("cpu.load", "gpu.load", "gpu.vram.used", "net.download");

        List<OverlayLine> lines = OverlayComposer.Build(
            metrics, oneLinePerMetric: false, TestData.Colors, lineOrder: new[] { "net", "ram", "gpu", "cpu" });

        // MEM porte la clé de la RAM : c'est elle qui la place.
        Assert.Equal(new[] { "net", "ram", "gpu", "cpu" }, lines.Select(l => l.Key));
    }

    [Fact]
    public void Build_ByCategory_IgnoresOrderKeysThatAreNotDisplayed()
    {
        var metrics = TestData.Selection("cpu.load", "net.download");

        List<OverlayLine> lines = OverlayComposer.Build(
            metrics, oneLinePerMetric: false, TestData.Colors, lineOrder: new[] { "net", "gpu", "cpu" });

        Assert.Equal(new[] { "net", "cpu" }, lines.Select(l => l.Key));
    }

    [Fact]
    public void Build_PerMetric_FollowsTheGivenOrder()
    {
        var metrics = TestData.Selection("cpu.load", "gpu.load", "ram.load");

        List<OverlayLine> lines = OverlayComposer.Build(
            metrics, oneLinePerMetric: true, TestData.Colors, lineOrder: new[] { "ram.load", "cpu.load", "gpu.load" });

        Assert.Equal(new[] { "ram.load", "cpu.load", "gpu.load" }, lines.Select(l => l.Key));
        Assert.Equal("Charge totale", lines[1].Title);
    }

    [Fact]
    public void Build_PerMetric_KeepsTheSelectionOrderWithoutAGivenOrder()
    {
        var metrics = TestData.Selection("cpu.load", "gpu.load");

        List<OverlayLine> lines = OverlayComposer.Build(metrics, oneLinePerMetric: true, TestData.Colors);

        Assert.Equal(new[] { "cpu.load", "gpu.load" }, lines.Select(l => l.Key));
    }

    [Fact]
    public void Build_GivesEachLineAReadableTitle()
    {
        var metrics = TestData.Selection("cpu.load", "gpu.vram.used", "mb.temp.system");

        List<OverlayLine> lines = OverlayComposer.Build(metrics, oneLinePerMetric: false, TestData.Colors);

        Assert.Equal(new[] { "CPU", "Mémoire (GPU et RAM)", "Carte mère" }, lines.Select(l => l.Title));
    }

    [Fact]
    public void RtssColumnWidths_OnlyGrow()
    {
        var widths = new RtssColumnWidths();

        Assert.Equal(5, widths.Grow("value.cpu.load", 5));
        Assert.Equal(5, widths.Grow("value.cpu.load", 3));
        Assert.Equal(7, widths.Grow("value.cpu.load", 7));

        widths.Reset();
        Assert.Equal(2, widths.Grow("value.cpu.load", 2));
    }
}
