using PCPerfSuite.Core.Compatibility;
using PCPerfSuite.Core.Hardware.Displays;
using static PCPerfSuite.Core.Tests.DisplayTestData;

namespace PCPerfSuite.Core.Tests;

/// <summary>Ligne « Écrans » du diagnostic.</summary>
public class DisplaysRowProviderTests
{
    private static readonly DisplayMonitor Primary = Monitor(1, new PixelRect(0, 0, 2560, 1440), 144, primary: true,
        targets: Target("DELL U2720Q", @"\\?\DISPLAY#DEL4123#x#{y}", hz: 143.998));

    [Fact]
    public void NotReadYet_SaysSo()
        => Assert.Equal("Pas encore lu", Assert.Single(DisplaysRowProvider.BuildRows(null)).Status);

    [Fact]
    public void EachScreen_HasNameResolutionRefreshScaleOutputAndGpu()
    {
        IReadOnlyList<CompatibilityRow> rows = DisplaysRowProvider.BuildRows(Snapshot(Primary));

        Assert.Equal(2, rows.Count);
        Assert.Equal("1 écran", rows[0].Status);
        CompatibilityRow row = rows[1];
        Assert.Equal("DELL U2720Q", row.Status);
        Assert.Contains("2560×1440", row.Detail);
        Assert.Contains("144 Hz", row.Detail);
        Assert.Contains("échelle 150 %", row.Detail);
        Assert.Contains("sortie DisplayPort", row.Detail);
        Assert.Contains("piloté par NVIDIA GeForce RTX 4070", row.Detail);
        Assert.Contains("écran principal", row.Detail);
        Assert.True(row.IsSupported);
        Assert.DoesNotContain("Expérimental", row.Detail);
    }

    [Fact]
    public void ScreenWithoutEdid_IsExperimental()
    {
        DisplayMonitor dock = Monitor(2, new PixelRect(2560, 0, 4480, 1080), targets: Target(null, @"\\?\DISPLAY#Default_Monitor#x#{y}", null, null));
        CompatibilityRow row = DisplaysRowProvider.BuildRows(Snapshot(Primary, dock))[2];

        Assert.Equal("Écran 2 (1920×1080)", row.Status);
        Assert.Contains("Pas d'identifiants EDID", row.Detail);
        Assert.Contains("Expérimental", row.Detail);
    }

    [Fact]
    public void ClonedScreens_AreExperimental()
    {
        DisplayMonitor clone = Monitor(2, new PixelRect(2560, 0, 4480, 1080), targets: [Target("A", "a"), Target("B", "b", output: DisplayOutputKind.Hdmi)]);
        CompatibilityRow row = DisplaysRowProvider.BuildRows(Snapshot(Primary, clone))[2];

        Assert.Equal("A + B (dupliqués)", row.Status);
        Assert.Contains("sortie DisplayPort + HDMI", row.Detail);
        Assert.Contains("dupliqués", row.Detail);
        Assert.Contains("Expérimental", row.Detail);
    }

    [Fact]
    public void NoScreen_SaysWhy()
    {
        CompatibilityRow row = Assert.Single(DisplaysRowProvider.BuildRows(new DisplayTopologySnapshot(Array.Empty<DisplayMonitor>(), "Écrans illisibles.")));
        Assert.False(row.IsSupported);
        Assert.Contains("Écrans illisibles.", row.Detail);
    }

    [Fact]
    public void SingleScreens_NoIdenticalScreensRow()
        => Assert.DoesNotContain(DisplaysRowProvider.BuildRows(Snapshot(Primary)), r => r.Title == "Écrans identiques");

    [Fact]
    public void IdenticalScreens_SayHowManySerialsWereRead_WithoutShowingThem()
    {
        DisplayMonitor twin = Monitor(2, new PixelRect(2560, 0, 5120, 1440), targets: Target("DELL U2720Q", @"\\?\DISPLAY#DEL4123#z#{y}"));
        var hashes = new Dictionary<string, string> { [@"\\?\DISPLAY#DEL4123#x#{y}"] = "0123456789ABCDEF" };

        IReadOnlyList<CompatibilityRow> rows = DisplaysRowProvider.BuildRows(Snapshot(Primary, twin), hashes);
        CompatibilityRow row = Assert.Single(rows, r => r.Title == "Écrans identiques");

        Assert.Equal("Départagés en partie", row.Status);
        Assert.Contains("lu pour 1 d'entre eux", row.Detail);
        Assert.Contains("Expérimental", row.Detail);
        Assert.False(row.IsSupported);
        foreach (CompatibilityRow r in rows)
        {
            Assert.DoesNotContain("0123456789ABCDEF", r.Status + r.Detail);
            Assert.False(r.IsPersonal);
        }
    }

    [Fact]
    public void IdenticalScreens_AllSerialsRead_AreSupported()
    {
        DisplayMonitor twin = Monitor(2, new PixelRect(2560, 0, 5120, 1440), targets: Target("DELL U2720Q", @"\\?\DISPLAY#DEL4123#z#{y}"));
        var hashes = new Dictionary<string, string> { [@"\\?\DISPLAY#DEL4123#x#{y}"] = "A", [@"\\?\DISPLAY#DEL4123#z#{y}"] = "B" };

        CompatibilityRow row = Assert.Single(DisplaysRowProvider.BuildRows(Snapshot(Primary, twin), hashes), r => r.Title == "Écrans identiques");
        Assert.Equal("Départagés", row.Status);
        Assert.True(row.IsSupported);
    }
}
