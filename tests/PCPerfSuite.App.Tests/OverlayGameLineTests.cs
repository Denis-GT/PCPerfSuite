using System.Globalization;
using PCPerfSuite.App.Metrics;
using PCPerfSuite.App.Overlay;
using PCPerfSuite.Core.Overlay;

namespace PCPerfSuite.App.Tests;

/// <summary>Ligne JEU : chaque FPS porte son libellé, et le 0.1% low est affichable comme les autres.</summary>
public class OverlayGameLineTests
{
    private static readonly string[] AllGameIds =
        { "game.fps", "game.fps.avg", "game.fps.low1", "game.fps.low01", "game.frametime" };

    private static OverlayLine GameLine(bool subLabels = true)
        => Assert.Single(OverlayComposer.Build(TestData.Selection(AllGameIds), oneLinePerMetric: false, TestData.Colors, subLabels));

    private static MetricSample InGame()
        => TestData.Sample(game: new RtssFrameStats(Fps: 144, FrameTimeMs: 7.0, AverageFps: 138, OnePercentLowFps: 95, PointOnePercentLowFps: 80, SampleCount: 1024));

    [Fact]
    public void EveryFpsMetric_HasALineLabel_ButTheFrameTimeHasNone()
    {
        var labels = MetricCatalog.All.Where(m => m.Category.Key == "game").ToDictionary(m => m.Id, m => m.LineLabel);

        Assert.Equal(
            new Dictionary<string, string?>
            {
                ["game.fps"] = "FPS", ["game.fps.avg"] = "MOY", ["game.fps.low1"] = "1%", ["game.fps.low01"] = "0.1%",
                ["game.frametime"] = null,
            },
            labels);
    }

    [Fact]
    public void OnlyMetricsWhoseUnitIsFpsCarryALineLabel()
    {
        // Le libellé tient lieu d'unité : le poser sur une autre métrique ferait disparaître son unité.
        Assert.All(MetricCatalog.All.Where(m => m.LineLabel is not null), m => Assert.Equal("FPS", m.Read(InGame()).Unit));
    }

    [Fact]
    public void PointOnePercentLow_IsADisplayableMetric()
    {
        Assert.Contains(MetricCatalog.All, m => m.Id == "game.fps.low01" && m.Label == "FPS 0.1% low");
        Assert.Equal("80", MetricCatalog.All.Single(m => m.Id == "game.fps.low01").Read(InGame()).Value);
    }

    [Fact]
    public void GameLine_PutsALabelBeforeEachFpsValue()
    {
        OverlayLine jeu = GameLine();

        Assert.Equal("JEU", jeu.Label);
        Assert.Equal(new[] { "FPS", "MOY", "1%", "0.1%", null }, jeu.Cells.Select(c => c.Prefix));
    }

    [Fact]
    public void GameLine_LabelsDoNotDependOnTheMemorySubLabelSwitch()
        => Assert.Equal(new[] { "FPS", "MOY", "1%", "0.1%", null }, GameLine(subLabels: false).Cells.Select(c => c.Prefix));

    [Fact]
    public void GameLine_DoesNotRepeatTheFpsUnit_ButKeepsMilliseconds()
    {
        OverlayLine jeu = GameLine();
        OverlayComposer.Update(new[] { jeu }, InGame());

        Assert.Equal(new[] { false, false, false, false, true }, jeu.Cells.Select(c => c.ShowUnit));
        Assert.Equal(new[] { "144", "138", "95", "80" }, jeu.Cells.Take(4).Select(c => c.Value));
        Assert.Equal(new[] { "", "", "", "", " ms" }, jeu.Cells.Select(c => c.Unit));
    }

    [Fact]
    public void GameLine_SeparatesTheUnlabelledFrameTimeFromTheLastLabel()
    {
        // Un label apporte ses deux espaces et une espace après lui ; le temps de frame, sans libellé, en prend deux.
        Assert.Equal(new[] { " ", " ", " ", " ", "  " }, GameLine().Cells.Select(c => c.Gap));
    }

    [Fact]
    public void GameLine_OutsideAGame_ShowsLabelsWithPlaceholders()
    {
        OverlayLine jeu = GameLine();
        OverlayComposer.Update(new[] { jeu }, TestData.Sample());

        // Hors jeu, « -- » (pas « N/D ») : ce PC n'est pas en cause, aucun jeu n'est lancé.
        Assert.All(jeu.Cells, c => Assert.Equal("--", c.Value));
        Assert.Equal(new[] { "FPS", "MOY", "1%", "0.1%" }, jeu.Cells.Take(4).Select(c => c.Prefix));
    }

    [Fact]
    public void RtssText_ShowsTheLabelsWithoutRepeatingTheUnit()
    {
        OverlayLine jeu = GameLine();
        OverlayComposer.Update(new[] { jeu }, InGame());

        string text = OverlayComposer.ToRtssText(new[] { jeu }, withColors: false, sizePercent: 100, new RtssColumnWidths());

        string frameTime = 7.0.ToString("0.0", CultureInfo.CurrentCulture);
        Assert.Equal(
            $"<A=3>JEU<A>  FPS <A=-3>144<A>  MOY <A=-3>138<A>  1% <A=-2>95<A>  0.1% <A=-2>80<A>  <A=-{frameTime.Length}>{frameTime}<A><A=3> ms<A>",
            text);
    }

    [Fact]
    public void OneLinePerMetric_IsUnchanged()
    {
        List<OverlayLine> lines = OverlayComposer.Build(
            TestData.Selection("game.fps.avg"), oneLinePerMetric: true, TestData.Colors);
        OverlayComposer.Update(lines, InGame());

        OverlayCell cell = Assert.Single(Assert.Single(lines).Cells);
        Assert.Equal("JEU moy", lines[0].Label);
        Assert.Null(cell.Prefix);
        Assert.True(cell.ShowUnit);
        Assert.Equal(" FPS", cell.Unit);
    }
}
