using PCPerfSuite.App.ViewModels;
using PCPerfSuite.Core.Hardware;

namespace PCPerfSuite.App.Tests;

/// <summary>Ce que le mode éco garde en lecture pour l'overlay et les courbes de ventilateurs.</summary>
public class BackgroundSensorNeedsTests
{
    private static HashSet<SensorGroup> ForOverlay(bool enabled, params string[] ids)
    {
        var groups = new HashSet<SensorGroup>();
        BackgroundSensorNeeds.AddForOverlay(groups, enabled, TestData.Selection(ids).Select(m => m.ReadGroup));
        return groups;
    }

    private static HashSet<SensorGroup> ForFan(bool isGpuFan, FanTempSource source)
    {
        var groups = new HashSet<SensorGroup>();
        BackgroundSensorNeeds.AddForFan(groups, isGpuFan, source);
        return groups;
    }

    [Fact]
    public void Overlay_Disabled_NeedsNothing()
    {
        Assert.Empty(ForOverlay(enabled: false, "cpu.load", "gpu.temp.core"));
    }

    [Fact]
    public void Overlay_Enabled_NeedsTheGroupsOfItsMetrics()
    {
        Assert.Equal(new[] { SensorGroup.CpuLoad, SensorGroup.Gpu },
            ForOverlay(enabled: true, "cpu.load", "gpu.temp.core").OrderBy(g => g));
    }

    [Fact]
    public void Overlay_TimeOnly_NeedsNothing()
    {
        Assert.Empty(ForOverlay(enabled: true, "system.time"));
    }

    [Fact]
    public void Fan_OnCpuTemperature_NeedsMotherboardAndCpu()
    {
        Assert.Equal(new[] { SensorGroup.Cpu, SensorGroup.Motherboard },
            ForFan(isGpuFan: false, FanTempSource.CpuPackage).OrderBy(g => g));
    }

    [Fact]
    public void Fan_OnHottest_NeedsCpuAndGpu()
    {
        Assert.Equal(new[] { SensorGroup.Cpu, SensorGroup.Gpu, SensorGroup.Motherboard },
            ForFan(isGpuFan: false, FanTempSource.HottestOfCpuGpu).OrderBy(g => g));
    }

    [Fact]
    public void GpuFan_OnGpuTemperature_NeedsOnlyTheGpu()
    {
        Assert.Equal(new[] { SensorGroup.Gpu }, ForFan(isGpuFan: true, FanTempSource.GpuCore));
    }
}
