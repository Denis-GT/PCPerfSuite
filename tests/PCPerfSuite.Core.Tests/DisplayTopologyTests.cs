using System.Runtime.InteropServices;
using PCPerfSuite.Core.Hardware.Displays;
using static PCPerfSuite.Core.Hardware.Displays.DisplayConfigNative;
using static PCPerfSuite.Core.Tests.DisplayTestData;

namespace PCPerfSuite.Core.Tests;

/// <summary>Filtre des fenêtres du shell, ordre et noms des écrans, EDID, disposition des structures natives.</summary>
public class DisplayTopologyTests
{
    private const uint Own = 4242;

    [Theory]
    [InlineData("Shell_TrayWnd")]
    [InlineData("Shell_SecondaryTrayWnd")]
    [InlineData("Progman")]
    [InlineData("WorkerW")]
    [InlineData("NotifyIconOverflowWindow")]
    [InlineData("TopLevelWindowForOverflowXamlIsland")]
    [InlineData("MultitaskingViewFrame")]
    [InlineData("XamlExplorerHostIslandWindow")]
    [InlineData("shell_traywnd")]
    public void ShellWindows_AreIgnored(string className)
        => Assert.True(DisplayTopology.IsIgnoredForegroundWindow(className, "explorer", 100, Own));

    [Theory]
    [InlineData("StartMenuExperienceHost")]
    [InlineData("SearchHost")]
    [InlineData("SearchApp")]
    [InlineData("ShellExperienceHost")]
    public void StartMenuAndSearch_AreIgnored(string process)
        => Assert.True(DisplayTopology.IsIgnoredForegroundWindow("Windows.UI.Core.CoreWindow", process, 100, Own));

    [Fact]
    public void StoreApp_IsNotIgnored()
        => Assert.False(DisplayTopology.IsIgnoredForegroundWindow("Windows.UI.Core.CoreWindow", "Minecraft.Windows", 100, Own));

    [Fact]
    public void OwnWindows_AreIgnored()
        => Assert.True(DisplayTopology.IsIgnoredForegroundWindow("HwndWrapper[PCPerfSuite;;abc]", "PCPerfSuite", Own, Own));

    [Theory]
    [InlineData("UnrealWindow")]
    [InlineData("UnityWndClass")]
    [InlineData("SDL_app")]
    [InlineData("Chrome_WidgetWin_1")]
    public void Games_AreNotIgnored(string className)
        => Assert.False(DisplayTopology.IsIgnoredForegroundWindow(className, "game", 100, Own));

    [Fact]
    public void UnreadableWindow_IsIgnored()
        => Assert.True(DisplayTopology.IsIgnoredForegroundWindow(null, null, 100, Own));

    [Fact]
    public void Order_PrimaryFirstThenLeftToRight()
    {
        DisplayMonitor right = Monitor(3, new PixelRect(1920, 0, 3840, 1080));
        DisplayMonitor left = Monitor(2, new PixelRect(-2560, 0, 0, 1440));
        DisplayMonitor primary = Monitor(1, new PixelRect(0, 0, 1920, 1080), primary: true);

        Assert.Equal(new[] { primary, left, right }, DisplayTopology.Order([right, left, primary]));
    }

    [Fact]
    public void FindMonitor_ByHandle()
    {
        DisplayMonitor a = Monitor(1, new PixelRect(0, 0, 1920, 1080), primary: true);
        DisplayTopologySnapshot snapshot = Snapshot(a);
        Assert.Same(a, DisplayTopology.FindMonitor(snapshot, new IntPtr(1)));
        Assert.Null(DisplayTopology.FindMonitor(snapshot, new IntPtr(9)));
        Assert.Null(DisplayTopology.FindMonitor(snapshot, IntPtr.Zero));
    }

    [Fact]
    public void Label_UsesTheFriendlyName()
        => Assert.Equal("DELL U2720Q", DisplayNames.Label(Monitor(2, new PixelRect(0, 0, 2560, 1440), targets: Target("DELL U2720Q", "p")), 2));

    [Fact]
    public void Label_WithoutName_GivesNumberAndResolution()
        => Assert.Equal("Écran 2 (1920×1080)", DisplayNames.Label(Monitor(2, new PixelRect(1920, 0, 3840, 1080), targets: Target("  ", "p", null, null)), 2));

    [Fact]
    public void Label_WithoutAnyTarget_GivesNumberAndResolution()
        => Assert.Equal("Écran 1 (1920×1080)", DisplayNames.Label(Monitor(1, new PixelRect(0, 0, 1920, 1080)), 1));

    [Fact]
    public void Label_ClonedScreens()
        => Assert.Equal("DELL U2720Q + écran sans nom (dupliqués)",
            DisplayNames.Label(Monitor(1, new PixelRect(0, 0, 1920, 1080), targets: [Target("DELL U2720Q", "a"), Target(null, "b")]), 1));

    [Theory]
    [InlineData((ushort)0xAC10, "DEL")]
    [InlineData((ushort)0x2D4C, "SAM")]
    [InlineData((ushort)0x6D1E, "GSM")]
    [InlineData((ushort)0x0000, null)]
    public void DecodeManufacturer(ushort raw, string? expected)
        => Assert.Equal(expected, DisplayNames.DecodeManufacturer(raw));

    [Theory]
    [InlineData(5u, DisplayOutputKind.Hdmi)]
    [InlineData(10u, DisplayOutputKind.DisplayPort)]
    [InlineData(11u, DisplayOutputKind.Internal)]
    [InlineData(0x80000000u, DisplayOutputKind.Internal)]
    [InlineData(18u, DisplayOutputKind.UsbDisplayPort)]
    [InlineData(0xFFFFFFFFu, DisplayOutputKind.Other)]
    public void OutputTechnology(uint technology, DisplayOutputKind expected)
        => Assert.Equal(expected, DisplayNames.FromOutputTechnology(technology));

    /// <summary>Tailles de wingdi.h / winuser.h en x64 : une structure décalée ferait lire des noms et des EDID faux
    /// sans la moindre erreur.</summary>
    [Fact]
    public void NativeStructures_HaveTheWindowsLayout()
    {
        Assert.Equal(8, Marshal.SizeOf<LUID>());
        Assert.Equal(20, Marshal.SizeOf<DISPLAYCONFIG_PATH_SOURCE_INFO>());
        Assert.Equal(48, Marshal.SizeOf<DISPLAYCONFIG_PATH_TARGET_INFO>());
        Assert.Equal(72, Marshal.SizeOf<DISPLAYCONFIG_PATH_INFO>());
        Assert.Equal(64, Marshal.SizeOf<DISPLAYCONFIG_MODE_INFO>());
        Assert.Equal(20, Marshal.SizeOf<DISPLAYCONFIG_DEVICE_INFO_HEADER>());
        Assert.Equal(84, Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>());
        Assert.Equal(420, Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>());
        Assert.Equal(276, Marshal.SizeOf<DISPLAYCONFIG_ADAPTER_NAME>());
        Assert.Equal(104, Marshal.SizeOf<MONITORINFOEXW>());
        Assert.Equal(840, Marshal.SizeOf<DISPLAY_DEVICEW>());
    }

    [Fact]
    public void TargetName_EdidFieldsAtTheirOffsets()
    {
        Assert.Equal(28, (int)Marshal.OffsetOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>(nameof(DISPLAYCONFIG_TARGET_DEVICE_NAME.EdidManufactureId)));
        Assert.Equal(36, (int)Marshal.OffsetOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>(nameof(DISPLAYCONFIG_TARGET_DEVICE_NAME.MonitorFriendlyDeviceName)));
        Assert.Equal(164, (int)Marshal.OffsetOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>(nameof(DISPLAYCONFIG_TARGET_DEVICE_NAME.MonitorDevicePath)));
    }

    /// <summary>Sur le PC qui lance les tests (avec ou sans session graphique), la lecture ne lève pas.</summary>
    [Fact]
    public void Read_NeverThrows()
        => Assert.NotNull(DisplayTopology.Read());
}
