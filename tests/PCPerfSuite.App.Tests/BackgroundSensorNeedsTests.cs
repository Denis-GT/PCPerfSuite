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

    private static HashSet<SensorGroup> ForAutoSwitch(bool enabled, bool hasBattery = false, bool onBattery = false, bool game = false)
    {
        var groups = new HashSet<SensorGroup>();
        BackgroundSensorNeeds.AddForAutoSwitch(groups, enabled, hasBattery, onBattery, game);
        return groups;
    }

    [Fact]
    public void AutoSwitch_Disabled_NeedsNothing()
        => Assert.Empty(ForAutoSwitch(enabled: false, hasBattery: true, game: true));

    [Fact]
    public void AutoSwitch_OnDesktop_ReadsLoadsFpsAndGpu_NeverTheCostlyCpuGroup()
        => Assert.Equal(new[] { SensorGroup.CpuLoad, SensorGroup.Gpu, SensorGroup.Fps }, ForAutoSwitch(enabled: true).OrderBy(g => g));

    [Fact]
    public void AutoSwitch_OnBattery_LeavesTheGpuAloneUnlessAGameIsShown()
    {
        Assert.Equal(new[] { SensorGroup.CpuLoad, SensorGroup.Fps, SensorGroup.Battery },
            ForAutoSwitch(enabled: true, hasBattery: true, onBattery: true).OrderBy(g => g));
        Assert.Contains(SensorGroup.Gpu, ForAutoSwitch(enabled: true, hasBattery: true, onBattery: true, game: true));
        Assert.Contains(SensorGroup.Gpu, ForAutoSwitch(enabled: true, hasBattery: true, onBattery: false));
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
