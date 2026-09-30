using PCPerfSuite.Core.Hardware.Displays;
using PCPerfSuite.Core.Overlay;
using static PCPerfSuite.Core.Tests.DisplayTestData;

namespace PCPerfSuite.Core.Tests;

/// <summary>Choix de l'écran de l'overlay : modes, repli sur l'écran principal, retour de l'écran choisi.</summary>
public class OverlayScreenChoiceTests
{
    private const string DellPath = @"\\?\DISPLAY#DEL4123#5&1a2b&0&UID4353#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";

    private static readonly DisplayMonitor Primary = Monitor(1, new PixelRect(0, 0, 1920, 1080), primary: true,
        targets: Target("Écran interne", @"\\?\DISPLAY#BOE0867#x#{y}", 0xE509, 0x0867, DisplayOutputKind.Internal));

    private static readonly DisplayMonitor Dell = Monitor(2, new PixelRect(-2560, 0, 0, 1440), 144, targets: Target("DELL U2720Q", DellPath));

    private static readonly DisplayIdentity SavedDell = new()
    {
        DevicePath = DellPath, EdidManufacturerId = DisplayTestData.Dell, EdidProductCodeId = 0x4123, FriendlyName = "DELL U2720Q",
    };

    [Theory]
    [InlineData(null, OverlayScreenMode.Primary)]
    [InlineData("", OverlayScreenMode.Primary)]
    [InlineData("inconnu", OverlayScreenMode.Primary)]
    [InlineData("fixed", OverlayScreenMode.Fixed)]
    [InlineData(" GAME ", OverlayScreenMode.Game)]
    public void ParseMode_OldFilesMeanPrimary(string? stored, OverlayScreenMode expected)
        => Assert.Equal(expected, OverlayScreenChoice.ParseMode(stored));

    [Theory]
    [InlineData(OverlayScreenMode.Primary)]
    [InlineData(OverlayScreenMode.Fixed)]
    [InlineData(OverlayScreenMode.Game)]
    public void Mode_RoundTrips(OverlayScreenMode mode)
        => Assert.Equal(mode, OverlayScreenChoice.ParseMode(OverlayScreenChoice.ToSetting(mode)));

    [Fact]
    public void Primary_IsNotStored()
        => Assert.Null(OverlayScreenChoice.ToSetting(OverlayScreenMode.Primary));

    [Fact]
    public void Primary_GoesOnThePrimary()
    {
        OverlayScreenTarget target = OverlayScreenChoice.Resolve(OverlayScreenMode.Primary, SavedDell, Snapshot(Dell, Primary), NoSerials, Dell);
        Assert.Same(Primary, target.Monitor);
        Assert.Null(target.Message);
    }

    [Fact]
    public void Fixed_GoesOnTheChosenScreen()
    {
        OverlayScreenTarget target = OverlayScreenChoice.Resolve(OverlayScreenMode.Fixed, SavedDell, Snapshot(Primary, Dell), NoSerials, null);
        Assert.Same(Dell, target.Monitor);
        Assert.Null(target.Message);
    }

    [Fact]
    public void Fixed_Unplugged_FallsBackWithAMessage_ThenComesBack()
    {
        OverlayScreenTarget unplugged = OverlayScreenChoice.Resolve(OverlayScreenMode.Fixed, SavedDell, Snapshot(Primary), NoSerials, null);
        Assert.Same(Primary, unplugged.Monitor);
        Assert.Contains("« DELL U2720Q » n'est pas branché", unplugged.Message);

        OverlayScreenTarget back = OverlayScreenChoice.Resolve(OverlayScreenMode.Fixed, SavedDell, Snapshot(Primary, Dell), NoSerials, null);
        Assert.Same(Dell, back.Monitor);
        Assert.Null(back.Message);
    }

    [Fact]
    public void Fixed_Ambiguous_SaysSo()
    {
        DisplayMonitor twinA = Monitor(2, new PixelRect(1920, 0, 4480, 1440), targets: Target("DELL U2720Q", "a"));
        DisplayMonitor twinB = Monitor(3, new PixelRect(4480, 0, 7040, 1440), targets: Target("DELL U2720Q", "b"));

        OverlayScreenTarget target = OverlayScreenChoice.Resolve(OverlayScreenMode.Fixed, SavedDell, Snapshot(Primary, twinA, twinB), NoSerials, null);

        Assert.Same(Primary, target.Monitor);
        Assert.Contains("Plusieurs écrans identiques", target.Message);
    }

    [Fact]
    public void Fixed_NothingChosenYet_AsksToChoose()
    {
        OverlayScreenTarget target = OverlayScreenChoice.Resolve(OverlayScreenMode.Fixed, null, Snapshot(Primary, Dell), NoSerials, null);
        Assert.Same(Primary, target.Monitor);
        Assert.NotNull(target.Message);
    }

    [Fact]
    public void Game_FollowsTheGameScreen_OrThePrimaryBeforeAnyGame()
    {
        Assert.Same(Dell, OverlayScreenChoice.Resolve(OverlayScreenMode.Game, null, Snapshot(Primary, Dell), NoSerials, Dell).Monitor);
        Assert.Same(Primary, OverlayScreenChoice.Resolve(OverlayScreenMode.Game, null, Snapshot(Primary, Dell), NoSerials, null).Monitor);
    }

    [Fact]
    public void NoScreenRead_NoMonitorAndAReason()
    {
        OverlayScreenTarget target = OverlayScreenChoice.Resolve(OverlayScreenMode.Fixed, SavedDell,
            new DisplayTopologySnapshot(Array.Empty<DisplayMonitor>(), "EnumDisplayMonitors a échoué."), NoSerials, null);

        Assert.Null(target.Monitor);
        Assert.Contains("EnumDisplayMonitors a échoué.", target.Message);
    }
}
