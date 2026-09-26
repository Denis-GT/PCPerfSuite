using PCPerfSuite.App.Metrics;
using PCPerfSuite.App.Overlay;
using PCPerfSuite.Core.Hardware;
using PCPerfSuite.Core.PowerSettings;

namespace PCPerfSuite.App.Tests;

/// <summary>Espacements d'une ligne : resserrés, sauf autour des débits disque et réseau et entre deux groupes.</summary>
public class OverlaySpacingTests
{
    private const string Wide = "  ";
    private const string Tight = " ";

    private static OverlayLine SingleLine(params string[] ids)
        => Assert.Single(OverlayComposer.Build(TestData.Selection(ids), oneLinePerMetric: false, TestData.Colors));

    private static string[] Gaps(OverlayLine line) => line.Cells.Select(c => c.Gap).ToArray();

    [Fact]
    public void OnlyDiskAndNetworkRatesAreFlaggedAsRates()
    {
        Assert.Equal(
            new[] { "net.download", "net.upload", "storage.read", "storage.write" },
            MetricCatalog.All.Where(m => m.IsRate).Select(m => m.Id).Order());
    }

    [Fact]
    public void PlainValues_AreOneSpaceApart_AfterTwoSpacesFromTheLabel()
    {
        OverlayLine cpu = SingleLine("cpu.load", "cpu.temp.package", "cpu.power", "cpu.clock.max");

        Assert.Equal(new[] { Wide, Tight, Tight, Tight }, Gaps(cpu));
    }

    [Fact]
    public void Rates_KeepTwoSpacesOnBothSides()
    {
        // Charge, lecture, écriture, température : un débit, le suivant, puis la valeur qui les suit.
        OverlayLine disk = SingleLine("storage.load", "storage.read", "storage.write", "storage.temp.max");

        Assert.Equal(new[] { Wide, Wide, Wide, Wide }, Gaps(disk));
    }

    [Fact]
    public void ValueBeforeARate_StaysCloseToItsNeighbours()
    {
        OverlayLine disk = SingleLine("storage.load", "storage.temp.max");

        Assert.Equal(new[] { Wide, Tight }, Gaps(disk));
    }

    [Fact]
    public void NetworkRates_AreTwoSpacesApart()
    {
        OverlayLine net = SingleLine("net.upload", "net.download");

        Assert.Equal(new[] { Wide, Wide }, Gaps(net));
    }

    [Fact]
    public void VramAndRamLines_AreSpacedLikeAnyOtherLine()
    {
        List<OverlayLine> lines = OverlayComposer.Build(
            TestData.Selection("gpu.clock.memory", "gpu.vram.used", "ram.load", "ram.used"), oneLinePerMetric: false, TestData.Colors);

        Assert.Equal(new[] { "vram", "ram" }, lines.Select(l => l.Key));
        Assert.All(lines, line => Assert.Equal(new[] { Wide, Tight }, Gaps(line)));
    }

    [Fact]
    public void OneLinePerMetric_KeepsTwoSpacesAfterTheLabel()
    {
        List<OverlayLine> lines = OverlayComposer.Build(
            TestData.Selection("cpu.load", "net.download"), oneLinePerMetric: true, TestData.Colors);

        Assert.All(lines, line => Assert.Equal(new[] { Wide }, Gaps(line)));
    }

    [Fact]
    public void RtssText_UsesTheSameSpacing()
    {
        var hardware = new HardwareSnapshot
        {
            Cpu = new CpuSnapshot { LoadPercent = 45, PackageTempC = 62, PowerWatts = 180 },
            GroupsEverRead = Enum.GetValues<SensorGroup>(),
        };
        MetricSample sample = TestData.Sample(hardware);
        List<OverlayLine> lines = OverlayComposer.Build(
            TestData.Selection("cpu.load", "cpu.temp.package", "cpu.power"), oneLinePerMetric: false, TestData.Colors);

        OverlayComposer.Update(lines, sample);
        string text = OverlayComposer.ToRtssText(lines, withColors: false, sizePercent: 100, new RtssColumnWidths());

        Assert.Equal("<A=3>CPU<A>  <A=-2>45<A><A=1>%<A> <A=-2>62<A><A=2>°C<A> <A=-3>180<A><A=2> W<A>", text);
    }

    [Fact]
    public void RtssText_ValuesDoNotTakeTheWidthOfTheOtherLines()
    {
        var hardware = new HardwareSnapshot
        {
            Cpu = new CpuSnapshot { LoadPercent = 45, PackageTempC = 62 },
            Gpu = new GpuSnapshot { CoreClockMhz = 1950, PowerWatts = 250 },
            GroupsEverRead = Enum.GetValues<SensorGroup>(),
        };
        MetricSample sample = TestData.Sample(hardware);
        List<OverlayLine> lines = OverlayComposer.Build(
            TestData.Selection("cpu.load", "cpu.temp.package", "gpu.clock.core", "gpu.power"), oneLinePerMetric: false, TestData.Colors);
        var widths = new RtssColumnWidths();

        OverlayComposer.Update(lines, sample);
        OverlayComposer.ToRtssText(lines, withColors: false, sizePercent: 100, widths);
        string text = OverlayComposer.ToRtssText(lines, withColors: false, sizePercent: 100, widths);

        // Au rendu suivant, les valeurs CPU (« 45% », « 62°C ») ne s'élargissent pas à celles du GPU placées en
        // dessous (« 250 W », « 1950 MHz ») : seuls les libellés de ligne partagent leur largeur.
        Assert.Equal(
            "<A=3>CPU<A>  <A=-2>45<A><A=1>%<A> <A=-2>62<A><A=2>°C<A>\n" +
            "<A=3>GPU<A>  <A=-3>250<A><A=2> W<A> <A=-4>1950<A><A=4> MHz<A>",
            text);
    }

    /// <summary>Réglage volontairement éloigné des valeurs par défaut : 3 espaces entre les valeurs, 5 aux séparations.</summary>
    private static readonly OverlaySpacing Custom = new(ValueSpaces: 3, SeparatorSpaces: 5);

    private static OverlayLine SingleLine(OverlaySpacing spacing, params string[] ids)
        => Assert.Single(OverlayComposer.Build(TestData.Selection(ids), oneLinePerMetric: false, TestData.Colors, spacing: spacing));

    [Fact]
    public void DefaultSpacing_IsOneBetweenValuesAndTwoForSeparators_AsInTheSettings()
    {
        var settings = new OverlayAppearanceSettings();

        Assert.Equal(Tight, OverlaySpacing.Default.ValueGap);
        Assert.Equal(Wide, OverlaySpacing.Default.SeparatorGap);
        Assert.Equal(OverlaySpacing.Default, new OverlaySpacing(settings.ValueSpacing, settings.SeparatorSpacing));
    }

    [Fact]
    public void CustomSpacing_AppliesAfterTheLabelAndBetweenValues()
    {
        OverlayLine cpu = SingleLine(Custom, "cpu.load", "cpu.temp.package", "cpu.power");

        Assert.Equal(new[] { "     ", "   ", "   " }, Gaps(cpu));
    }

    [Fact]
    public void CustomSpacing_AppliesAroundRates()
    {
        OverlayLine disk = SingleLine(Custom, "storage.load", "storage.read", "storage.temp.max");

        Assert.Equal(new[] { "     ", "     ", "     " }, Gaps(disk));
    }

    [Fact]
    public void CustomSeparator_PrecedesEachGameLabel_WhichStaysOneSpaceFromItsValue()
    {
        OverlayLine jeu = SingleLine(Custom, "game.fps", "game.fps.avg");

        Assert.Equal(new[] { "     FPS", "     MOY" }, jeu.Cells.Select(c => c.PrefixText));
        Assert.Equal(new[] { " ", " " }, Gaps(jeu));
    }

    [Fact]
    public void RtssText_UsesTheCustomSpacing()
    {
        var hardware = new HardwareSnapshot
        {
            Cpu = new CpuSnapshot { LoadPercent = 45, PackageTempC = 62, PowerWatts = 180 },
            GroupsEverRead = Enum.GetValues<SensorGroup>(),
        };
        List<OverlayLine> lines = OverlayComposer.Build(
            TestData.Selection("cpu.load", "cpu.temp.package", "cpu.power"), oneLinePerMetric: false, TestData.Colors, spacing: Custom);

        OverlayComposer.Update(lines, TestData.Sample(hardware));
        string text = OverlayComposer.ToRtssText(lines, withColors: false, sizePercent: 100, new RtssColumnWidths());

        Assert.Equal("<A=3>CPU<A>     <A=-2>45<A><A=1>%<A>   <A=-2>62<A><A=2>°C<A>   <A=-3>180<A><A=2> W<A>", text);
    }

    [Fact]
    public void Spacing_StaysWithinItsBounds()
    {
        // Un fichier de réglages modifié à la main ne doit ni coller deux valeurs, ni étirer une ligne hors de l'écran.
        var spacing = new OverlaySpacing(ValueSpaces: 0, SeparatorSpaces: 50);

        Assert.Equal(OverlaySpacing.MinSpaces, spacing.ValueGap.Length);
        Assert.Equal(OverlaySpacing.MaxSpaces, spacing.SeparatorGap.Length);
    }

    [Fact]
    public void RtssText_PutsVramAndRamOnTwoLines()
    {
        MetricSample sample = TestData.Sample();
        List<OverlayLine> lines = OverlayComposer.Build(
            TestData.Selection("gpu.vram.used", "ram.load"), oneLinePerMetric: false, TestData.Colors);

        OverlayComposer.Update(lines, sample);
        string text = OverlayComposer.ToRtssText(lines, withColors: false, sizePercent: 100, new RtssColumnWidths());

        Assert.Equal("<A=4>VRAM<A>  <A=-4>8200<A><A=3> Mo<A>\n<A=4>RAM<A>  <A=-2>39<A><A=1>%<A>", text);
    }
}
