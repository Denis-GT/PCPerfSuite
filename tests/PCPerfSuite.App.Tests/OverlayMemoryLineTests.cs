using PCPerfSuite.App.Metrics;
using PCPerfSuite.App.Overlay;
using PCPerfSuite.Core.Hardware;

namespace PCPerfSuite.App.Tests;

/// <summary>La ligne MEM (mémoire du GPU + RAM) ne montre que ce que ce PC fournit.</summary>
public class OverlayMemoryLineTests
{
    private static List<OverlayLine> Build(MetricSample sample, bool subLabels, params string[] ids)
    {
        var metrics = TestData.Selection(ids);
        return OverlayComposer.Build(
            metrics, oneLinePerMetric: false, TestData.Colors, subLabels,
            unavailableMemoryIds: OverlayComposer.UnavailableMemoryIds(metrics, sample));
    }

    private static OverlayLine MemoryLine(List<OverlayLine> lines) => Assert.Single(lines, l => l.Label == "MEM");

    [Fact]
    public void VramUnavailable_DropsTheVramGroupAndItsSubLabel()
    {
        // GPU présent, mais sans aucun capteur de mémoire lisible.
        MetricSample sample = TestData.Sample(TestData.Hardware(new GpuSnapshot(), TestData.Memory));

        OverlayLine mem = MemoryLine(Build(sample, true, "gpu.clock.memory", "gpu.vram.used", "ram.used"));

        Assert.Equal(new[] { "ram.used" }, mem.Cells.Select(c => c.Metric.Id));
        Assert.Equal(new[] { "RAM" }, mem.Cells.Select(c => c.Prefix));
    }

    [Fact]
    public void NoDedicatedGpu_DropsTheVramGroup()
    {
        MetricSample sample = TestData.Sample(TestData.Hardware(null, TestData.Memory));

        OverlayLine mem = MemoryLine(Build(sample, true, "gpu.vram.used", "ram.used"));

        Assert.Equal(new[] { "ram.used" }, mem.Cells.Select(c => c.Metric.Id));
    }

    [Fact]
    public void RamUnavailable_DropsTheRamGroupAndItsSubLabel()
    {
        MetricSample sample = TestData.Sample(TestData.Hardware(TestData.Gpu, new MemorySnapshot()));

        OverlayLine mem = MemoryLine(Build(sample, true, "gpu.clock.memory", "gpu.vram.used", "ram.used", "ram.load"));

        Assert.Equal(new[] { "gpu.clock.memory", "gpu.vram.used" }, mem.Cells.Select(c => c.Metric.Id));
        Assert.Equal(new[] { "VRAM", null }, mem.Cells.Select(c => c.Prefix));
    }

    [Fact]
    public void BothUnavailable_DropsTheWholeLine_MemLabelIncluded()
    {
        MetricSample sample = TestData.Sample(TestData.Hardware(null, new MemorySnapshot()));

        List<OverlayLine> lines = Build(sample, true, "cpu.load", "gpu.vram.used", "ram.used");

        // Les autres lignes ne sont pas touchées.
        Assert.Equal(new[] { "cpu" }, lines.Select(l => l.Key));
        Assert.DoesNotContain(lines, l => l.Label == "MEM");
    }

    [Fact]
    public void PartlyUnavailableGroup_KeepsItsOtherValues_WithNdWhereMissing()
    {
        // La VRAM utilisée est lue, la fréquence mémoire non : le groupe garde les deux (le « N/D » explique le trou).
        MetricSample sample = TestData.Sample(TestData.Hardware(new GpuSnapshot { VramUsedMb = 8200 }, TestData.Memory));

        List<OverlayLine> lines = Build(sample, true, "gpu.clock.memory", "gpu.vram.used", "ram.used");
        OverlayLine mem = MemoryLine(lines);
        OverlayComposer.Update(lines, sample);

        Assert.Equal(new[] { "gpu.clock.memory", "gpu.vram.used", "ram.used" }, mem.Cells.Select(c => c.Metric.Id));
        Assert.Equal(new[] { "VRAM", null, "RAM" }, mem.Cells.Select(c => c.Prefix));
        Assert.Equal("N/D", mem.Cells[0].Value);
    }

    [Fact]
    public void NotReadYet_RemovesNothing()
    {
        // Aucun groupe encore lu : les valeurs valent « -- » (en attente), pas « N/D » (ce PC ne les fournit pas).
        MetricSample sample = TestData.Sample(new HardwareSnapshot { GroupsEverRead = Array.Empty<SensorGroup>() });

        List<OverlayLine> lines = Build(sample, true, "gpu.vram.used", "ram.used");
        OverlayComposer.Update(lines, sample);

        OverlayLine mem = MemoryLine(lines);
        Assert.Equal(new[] { "VRAM", "RAM" }, mem.Cells.Select(c => c.Prefix));
        Assert.All(mem.Cells, c => Assert.Equal("--", c.Value));
    }

    [Fact]
    public void WithoutSubLabels_StillDropsUnavailableGroups()
    {
        MetricSample sample = TestData.Sample(TestData.Hardware(null, TestData.Memory));

        OverlayLine mem = MemoryLine(Build(sample, false, "gpu.vram.used", "ram.used"));

        Assert.Equal(new[] { "ram.used" }, mem.Cells.Select(c => c.Metric.Id));
        Assert.All(mem.Cells, c => Assert.Null(c.Prefix));
    }

    [Fact]
    public void OnlyOneGroupSelected_KeepsItsSubLabel()
    {
        MetricSample sample = TestData.Sample();

        Assert.Equal(new[] { "RAM" }, MemoryLine(Build(sample, true, "ram.used")).Cells.Select(c => c.Prefix));
        Assert.Equal(new[] { "VRAM" }, MemoryLine(Build(sample, true, "gpu.vram.used")).Cells.Select(c => c.Prefix));
    }

    [Fact]
    public void UnavailableMemoryIds_OnlyReportsMetricsOfTheMemoryLine()
    {
        // cpu.load vaut aussi « N/D » ici, mais ne fait pas partie de la ligne MEM.
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

        Assert.Equal(2, MemoryLine(lines).Cells.Count);
    }
}
