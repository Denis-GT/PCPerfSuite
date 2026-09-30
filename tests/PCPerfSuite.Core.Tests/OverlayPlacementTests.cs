using PCPerfSuite.Core.Hardware.Displays;
using PCPerfSuite.Core.Overlay;

namespace PCPerfSuite.Core.Tests;

/// <summary>Position de l'overlay fenêtre en pixels physiques : 9 ancrages, 100 et 150 %, écran à gauche en négatif.</summary>
public class OverlayPlacementTests
{
    private static readonly PixelRect FullHd = new(0, 0, 1920, 1080);

    // Fenêtre de 200×100 DIP, marges de 24 DIP.
    [Theory]
    [InlineData(OverlayAnchor.TopLeft, 1.0, 24, 24)]
    [InlineData(OverlayAnchor.TopCenter, 1.0, 860, 24)]
    [InlineData(OverlayAnchor.TopRight, 1.0, 1696, 24)]
    [InlineData(OverlayAnchor.MiddleLeft, 1.0, 24, 490)]
    [InlineData(OverlayAnchor.MiddleCenter, 1.0, 860, 490)]
    [InlineData(OverlayAnchor.MiddleRight, 1.0, 1696, 490)]
    [InlineData(OverlayAnchor.BottomLeft, 1.0, 24, 956)]
    [InlineData(OverlayAnchor.BottomCenter, 1.0, 860, 956)]
    [InlineData(OverlayAnchor.BottomRight, 1.0, 1696, 956)]
    [InlineData(OverlayAnchor.TopLeft, 1.5, 36, 36)]
    [InlineData(OverlayAnchor.TopCenter, 1.5, 810, 36)]
    [InlineData(OverlayAnchor.TopRight, 1.5, 1584, 36)]
    [InlineData(OverlayAnchor.MiddleLeft, 1.5, 36, 465)]
    [InlineData(OverlayAnchor.MiddleCenter, 1.5, 810, 465)]
    [InlineData(OverlayAnchor.MiddleRight, 1.5, 1584, 465)]
    [InlineData(OverlayAnchor.BottomLeft, 1.5, 36, 894)]
    [InlineData(OverlayAnchor.BottomCenter, 1.5, 810, 894)]
    [InlineData(OverlayAnchor.BottomRight, 1.5, 1584, 894)]
    public void EachAnchor_AtEachScale(OverlayAnchor anchor, double scale, int x, int y)
        => Assert.Equal(new PixelPoint(x, y), OverlayPlacement.Compute(anchor, 24, 24, FullHd, scale, 200, 100));

    [Theory]
    [InlineData(OverlayAnchor.TopLeft, -2524, 36)]
    [InlineData(OverlayAnchor.TopRight, -336, 36)]
    [InlineData(OverlayAnchor.BottomCenter, -1430, 1254)]
    public void ScreenOnTheLeftOfThePrimary_HasNegativeCoordinates(OverlayAnchor anchor, int x, int y)
    {
        // Écran 1440p à 150 % à gauche du principal, bords hauts alignés.
        var left = new PixelRect(-2560, 0, 0, 1440);
        Assert.Equal(new PixelPoint(x, y), OverlayPlacement.Compute(anchor, 24, 24, left, 1.5, 200, 100));
    }

    [Fact]
    public void ScreenAboveThePrimary_KeepsNegativeTop()
        => Assert.Equal(new PixelPoint(24, -1056),
            OverlayPlacement.Compute(OverlayAnchor.TopLeft, 24, 24, new PixelRect(0, -1080, 1920, 0), 1, 200, 100));

    [Fact]
    public void HugeMargins_StayInsideTheScreen()
        => Assert.Equal(new PixelPoint(0, 0),
            OverlayPlacement.Compute(OverlayAnchor.BottomRight, 600, 600, new PixelRect(0, 0, 900, 600), 1.5, 600, 400));

    [Fact]
    public void WindowBiggerThanTheScreen_SticksToTopLeft()
        => Assert.Equal(new PixelPoint(-1280, 0),
            OverlayPlacement.Compute(OverlayAnchor.MiddleRight, 24, 24, new PixelRect(-1280, 0, 0, 720), 2, 1000, 500));

    [Fact]
    public void InvalidScale_FallsBackTo100Percent()
        => Assert.Equal(new PixelPoint(24, 24), OverlayPlacement.Compute(OverlayAnchor.TopLeft, 24, 24, FullHd, 0, 200, 100));
}
