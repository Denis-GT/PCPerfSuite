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
    public void RtssColumnWidths_OnlyGrow()
    {
        var widths = new RtssColumnWidths();

        Assert.Equal(5, widths.Grow("value0", 5));
        Assert.Equal(5, widths.Grow("value0", 3));
        Assert.Equal(7, widths.Grow("value0", 7));

        widths.Reset();
        Assert.Equal(2, widths.Grow("value0", 2));
    }
}
