using System.Text.Json;
using PCPerfSuite.Core.Hardware.Displays;
using static PCPerfSuite.Core.Tests.DisplayTestData;

namespace PCPerfSuite.Core.Tests;

/// <summary>Retrouver l'écran enregistré : chemin, EDID, numéro de série.</summary>
public class DisplayIdentityResolverTests
{
    private const string PathA = @"\\?\DISPLAY#DEL4123#5&1a2b&0&UID4353#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
    private const string PathB = @"\\?\DISPLAY#DEL4123#5&1a2b&0&UID4354#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
    private const string PathMoved = @"\\?\DISPLAY#DEL4123#5&9f9f&0&UID8000#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";

    private static readonly DisplayMonitor Primary = Monitor(1, new PixelRect(0, 0, 1920, 1080), primary: true,
        targets: Target("Écran interne", @"\\?\DISPLAY#BOE0867#4&0&UID0#{x}", 0xE509, 0x0867, DisplayOutputKind.Internal));

    private static readonly DisplayMonitor DellA = Monitor(2, new PixelRect(1920, 0, 4480, 1440), targets: Target("DELL U2720Q", PathA));
    private static readonly DisplayMonitor DellB = Monitor(3, new PixelRect(4480, 0, 7040, 1440), targets: Target("DELL U2720Q", PathB));

    private static DisplayIdentity Saved(string? path, string? serialHash = null) => new()
    {
        DevicePath = path,
        EdidManufacturerId = Dell,
        EdidProductCodeId = 0x4123,
        FriendlyName = "DELL U2720Q",
        SerialHash = serialHash,
    };

    [Fact]
    public void ExactPath_Wins()
    {
        DisplayResolution result = DisplayIdentityResolver.Resolve(Saved(PathB), Snapshot(Primary, DellA, DellB), NoSerials);
        Assert.Equal(DisplayMatch.ExactPath, result.Match);
        Assert.Same(DellB, result.Monitor);
    }

    [Fact]
    public void ExactPath_IgnoresCase()
        => Assert.Same(DellA, DisplayIdentityResolver.Resolve(Saved(PathA.ToLowerInvariant()), Snapshot(Primary, DellA), NoSerials).Monitor);

    [Fact]
    public void OtherConnector_FoundByEdid()
    {
        DisplayMonitor moved = Monitor(2, DellA.Bounds, targets: Target("DELL U2720Q", PathMoved));
        DisplayResolution result = DisplayIdentityResolver.Resolve(Saved(PathA), Snapshot(Primary, moved), NoSerials);
        Assert.Equal(DisplayMatch.Edid, result.Match);
        Assert.Same(moved, result.Monitor);
    }

    [Fact]
    public void TwoIdenticalScreens_SplitBySerial()
    {
        DisplayMonitor movedA = Monitor(2, DellA.Bounds, targets: Target("DELL U2720Q", PathMoved));
        var serials = new Dictionary<string, string> { [PathMoved] = "AAAA", [PathB] = "BBBB" };

        DisplayResolution result = DisplayIdentityResolver.Resolve(Saved(PathA, "bbbb"), Snapshot(Primary, movedA, DellB), serials);

        Assert.Equal(DisplayMatch.EdidAndSerial, result.Match);
        Assert.Same(DellB, result.Monitor);
    }

    [Fact]
    public void TwoIdenticalScreens_WithoutSerial_AreAmbiguous()
    {
        DisplayMonitor movedA = Monitor(2, DellA.Bounds, targets: Target("DELL U2720Q", PathMoved));
        DisplayResolution result = DisplayIdentityResolver.Resolve(Saved("ailleurs"), Snapshot(Primary, movedA, DellB), NoSerials);
        Assert.Equal(DisplayMatch.Ambiguous, result.Match);
        Assert.Null(result.Monitor);
    }

    [Fact]
    public void Unplugged_IsNotFound()
    {
        DisplayResolution result = DisplayIdentityResolver.Resolve(Saved(PathA), Snapshot(Primary), NoSerials);
        Assert.Equal(DisplayMatch.NotFound, result.Match);
        Assert.Null(result.Monitor);
    }

    [Fact]
    public void ScreenWithoutEdid_OnlyFoundByPath()
    {
        var saved = new DisplayIdentity { DevicePath = PathA };
        Assert.Equal(DisplayMatch.NotFound, DisplayIdentityResolver.Resolve(saved, Snapshot(Primary, DellB), NoSerials).Match);
        Assert.Equal(DisplayMatch.ExactPath, DisplayIdentityResolver.Resolve(saved, Snapshot(Primary, DellA), NoSerials).Match);
    }

    [Fact]
    public void ClonedScreens_FoundThroughAnyTarget()
    {
        DisplayMonitor clone = Monitor(2, DellA.Bounds, targets: [Target("LG", @"\\?\DISPLAY#GSM5B09#x#{y}", 0x6D1E, 0x5B09), Target("DELL U2720Q", PathA)]);
        Assert.Same(clone, DisplayIdentityResolver.Resolve(Saved(PathA), Snapshot(Primary, clone), NoSerials).Monitor);
    }

    /// <summary>Deux écrans identiques, câbles échangés : le chemin enregistré désigne maintenant l'autre écran, et
    /// seul le numéro de série le dit.</summary>
    [Fact]
    public void SwappedCables_BetweenIdenticalScreens_FollowTheSerial()
    {
        DisplayMonitor aOnB = Monitor(2, DellA.Bounds, targets: Target("DELL U2720Q", PathB));
        DisplayMonitor bOnA = Monitor(3, DellB.Bounds, targets: Target("DELL U2720Q", PathA));
        var serials = new Dictionary<string, string> { [PathB] = "AAAA", [PathA] = "BBBB" };

        DisplayResolution result = DisplayIdentityResolver.Resolve(Saved(PathA, "aaaa"), Snapshot(Primary, aOnB, bOnA), serials);

        Assert.Equal(DisplayMatch.EdidAndSerial, result.Match);
        Assert.Same(aOnB, result.Monitor);
    }

    [Fact]
    public void SerialNotReadYet_KeepsTheExactPath()
        => Assert.Equal(DisplayMatch.ExactPath,
            DisplayIdentityResolver.Resolve(Saved(PathA, "aaaa"), Snapshot(Primary, DellA, DellB), NoSerials).Match);

    [Fact]
    public void HasIdenticalScreens()
    {
        Assert.True(DisplayIdentityResolver.HasIdenticalScreens(Snapshot(Primary, DellA, DellB)));
        Assert.False(DisplayIdentityResolver.HasIdenticalScreens(Snapshot(Primary, DellA)));

        // Sans EDID, deux écrans ne sont pas « identiques » : rien à départager par le numéro de série.
        DisplayMonitor noEdidA = Monitor(2, DellA.Bounds, targets: Target(null, "x", null, null));
        DisplayMonitor noEdidB = Monitor(3, DellB.Bounds, targets: Target(null, "y", null, null));
        Assert.False(DisplayIdentityResolver.HasIdenticalScreens(Snapshot(Primary, noEdidA, noEdidB)));
    }

    [Fact]
    public void SerialHashToRemember_OnlyWhenMissingAndFound()
    {
        var serials = new Dictionary<string, string> { [PathA] = "AAAA", [PathB] = "BBBB" };

        Assert.Equal("AAAA", DisplayIdentityResolver.SerialHashToRemember(Saved(PathA), Snapshot(Primary, DellA, DellB), serials));
        Assert.Null(DisplayIdentityResolver.SerialHashToRemember(Saved(PathA, "AAAA"), Snapshot(Primary, DellA, DellB), serials));
        Assert.Null(DisplayIdentityResolver.SerialHashToRemember(Saved(PathA), Snapshot(Primary), serials));
        Assert.Null(DisplayIdentityResolver.SerialHashToRemember(Saved(PathA), Snapshot(Primary, DellA), NoSerials));
    }

    [Fact]
    public void SerialHashToRemember_UsesTheRecognizedTarget_OfClonedScreens()
    {
        DisplayMonitor clone = Monitor(2, DellA.Bounds, targets: [Target("LG", @"\\?\DISPLAY#GSM5B09#x#{y}", 0x6D1E, 0x5B09), Target("DELL U2720Q", PathA)]);
        var serials = new Dictionary<string, string> { [@"\\?\DISPLAY#GSM5B09#x#{y}"] = "LGLG", [PathA] = "AAAA" };

        Assert.Equal("AAAA", DisplayIdentityResolver.SerialHashToRemember(Saved(PathA), Snapshot(Primary, clone), serials));
    }

    [Fact]
    public void FromMonitor_NeverKeepsTheGdiName()
    {
        var serials = new Dictionary<string, string> { [PathA] = "ABCD" };
        DisplayIdentity identity = DisplayIdentity.FromMonitor(DellA, serials);

        Assert.Equal(PathA, identity.DevicePath);
        Assert.Equal(Dell, identity.EdidManufacturerId);
        Assert.Equal("DELL U2720Q", identity.FriendlyName);
        Assert.Equal("ABCD", identity.SerialHash);
        Assert.DoesNotContain("DISPLAY2", JsonSerializer.Serialize(identity));
    }

    /// <summary>« Écran 2 (…) » change avec la disposition : un écran sans nom n'en enregistre aucun.</summary>
    [Fact]
    public void FromMonitor_WithoutName_KeepsNoNumberedName()
        => Assert.Null(DisplayIdentity.FromMonitor(Monitor(2, DellA.Bounds, targets: Target(" ", PathA)), NoSerials).FriendlyName);
}
