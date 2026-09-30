using PCPerfSuite.Core.Compatibility;
using PCPerfSuite.Core.Hardware;
using PCPerfSuite.Core.Safety;

namespace PCPerfSuite.Core.Tests;

/// <summary>Sécurité thermique GPU : armement sur un réglage relevé, retour d'origine, relecture et message.</summary>
public class GpuThermalSafetyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset At(double seconds) => T0.AddSeconds(seconds);

    private static GpuOverclockSnapshot Oc(int core = 0, int memory = 0, int temperature = 83, int voltage = 0) => new()
    {
        CoreOffsetSupported = true,
        MemoryOffsetSupported = true,
        CoreOffsetMhz = core,
        MemoryOffsetMhz = memory,
        TemperatureLimitSupported = true,
        TemperatureLimitC = temperature,
        TemperatureLimitDefaultC = 83,
        VoltageSupported = true,
        Voltage = voltage,
        VoltageDefault = 0,
    };

    private static GpuControlSnapshot Power(float percent = 100) => new()
    {
        Name = "GPU",
        PowerLimitSupported = true,
        PowerLimitPercent = percent,
        PowerLimitDefaultPercent = 100,
    };

    /// <summary>Carte factice : ce que la restauration laisse est réglable, pour simuler un retour qui échoue.</summary>
    private sealed class FakeCard : IGpuOverclockTarget
    {
        public GpuOverclockSnapshot? Overclock { get; set; } = Oc(core: 150);
        public GpuControlSnapshot? PowerSnapshot { get; set; } = Power();
        public GpuOverclockSnapshot? AfterRestore { get; set; } = Oc();
        public int Restores { get; private set; }

        public void RestoreOverclockDefaults()
        {
            Restores++;
            Overclock = AfterRestore;
        }

        public GpuOverclockSnapshot? GetOverclock() => Overclock;

        public GpuControlSnapshot? GetSnapshot() => PowerSnapshot;
    }

    [Fact]
    public void NotArmed_NeverRestores()
    {
        var card = new FakeCard();
        var safety = new GpuThermalSafety(card);

        safety.Note(At(0), 99f, null);
        safety.Note(At(60), 99f, null);

        Assert.Equal(0, card.Restores);
        Assert.True(safety.CoreEverRead);
    }

    [Fact]
    public void Armed_HotForFifteenSeconds_RestoresReadsBackAndWarns()
    {
        var card = new FakeCard();
        var safety = new GpuThermalSafety(card);
        string? warned = null;
        safety.EmergencyRestored += message => warned = message;
        safety.UpdateArming(true);

        safety.Note(At(0), 91f, null);
        Assert.Equal(0, card.Restores);
        safety.Note(At(15), 91f, null);

        Assert.Equal(1, card.Restores);
        Assert.False(safety.IsArmed);
        Assert.NotNull(warned);
        Assert.Contains("91 °C (cœur)", warned);
        Assert.Contains("rétablis", warned);
        Assert.Contains("cœur +0 MHz", warned);
        Assert.Equal(warned, safety.LastTripMessage);
        Assert.Equal(At(15), safety.LastTripAt);
    }

    [Fact]
    public void Armed_HotSpotOverItsThreshold_Restores()
    {
        var card = new FakeCard();
        var safety = new GpuThermalSafety(card);
        safety.UpdateArming(true);

        safety.Note(At(0), 80f, 106f);
        safety.Note(At(15), 80f, 106f);

        Assert.Equal(1, card.Restores);
    }

    [Fact]
    public void Armed_RestoreThatDoesNotHold_SaysItFailed()
    {
        var card = new FakeCard { AfterRestore = Oc(core: 150) };
        var safety = new GpuThermalSafety(card);
        string? warned = null;
        safety.EmergencyRestored += message => warned = message;
        safety.UpdateArming(true);

        safety.Note(At(0), 95f, null);
        safety.Note(At(15), 95f, null);

        Assert.NotNull(warned);
        Assert.Contains("a échoué", warned);
        Assert.Contains("cœur +150 MHz", warned);
    }

    [Fact]
    public void Armed_CardUnreadableAfterRestore_SaysSo()
    {
        var card = new FakeCard { AfterRestore = null, PowerSnapshot = null };
        var safety = new GpuThermalSafety(card);
        string? warned = null;
        safety.EmergencyRestored += message => warned = message;
        safety.UpdateArming(true);

        safety.Note(At(0), 95f, null);
        safety.Note(At(15), 95f, null);

        Assert.Contains("n'a pas pu être relue", warned);
    }

    [Fact]
    public void Armed_TemperatureLostAfterBeingRead_Restores()
    {
        var card = new FakeCard();
        var safety = new GpuThermalSafety(card);
        string? warned = null;
        safety.EmergencyRestored += message => warned = message;
        safety.UpdateArming(true);

        safety.Note(At(0), 70f, null);
        safety.Note(At(15), null, null);

        Assert.Equal(1, card.Restores);
        Assert.Contains("n'est plus lue", warned);
    }

    [Fact]
    public void Armed_TemperatureNeverRead_DoesNothing()
    {
        // Ce PC ne publie pas la température du GPU : la sécurité ne peut rien surveiller, et le dit ailleurs.
        var card = new FakeCard();
        var safety = new GpuThermalSafety(card);
        safety.UpdateArming(true);

        safety.Note(At(0), null, null);
        safety.Note(At(60), null, null);

        Assert.Equal(0, card.Restores);
        Assert.Equal(ThermalState.NotMonitorable, safety.State);
    }

    [Fact]
    public void Rearming_ForgetsEarlierHeat()
    {
        var card = new FakeCard();
        var safety = new GpuThermalSafety(card);
        safety.UpdateArming(true);
        safety.Note(At(0), 95f, null);

        safety.UpdateArming(false);
        safety.UpdateArming(true);
        safety.Note(At(15), 95f, null);

        Assert.Equal(0, card.Restores);
    }

    [Theory]
    [InlineData(150, 0, 83, 0, 100f, true)]
    [InlineData(0, 500, 83, 0, 100f, true)]
    [InlineData(0, 0, 88, 0, 100f, true)]
    [InlineData(0, 0, 83, 25, 100f, true)]
    [InlineData(0, 0, 83, 0, 110f, true)]
    [InlineData(0, 0, 83, 0, 100.4f, false)]
    [InlineData(-100, -200, 75, -50, 80f, false)]
    [InlineData(0, 0, 83, 0, 100f, false)]
    public void IsRaised_OnlyAboveTheOrigin(int core, int memory, int temperature, int voltage, float power, bool raised)
        => Assert.Equal(raised, GpuOverclockRaise.IsRaised(Oc(core, memory, temperature, voltage), Power(power)));

    [Fact]
    public void IsRaised_IgnoresWhatTheCardDoesNotExpose()
    {
        var overclock = new GpuOverclockSnapshot { CoreOffsetSupported = false, CoreOffsetMhz = 300 };
        var power = new GpuControlSnapshot { Name = "GPU", PowerLimitSupported = false, PowerLimitPercent = 150 };

        Assert.False(GpuOverclockRaise.IsRaised(overclock, power));
        Assert.False(GpuOverclockRaise.IsRaised(null, null));
    }

    [Fact]
    public void Diagnostic_Armed_ShowsThresholdsAndFollowedTemperatures()
    {
        var safety = new GpuThermalSafety(new FakeCard());
        safety.UpdateArming(true);
        safety.Note(At(0), 64f, null);

        CompatibilityRow row = GpuThermalSafetyRowProvider.BuildRow(true, safety);

        Assert.Equal("Sécurité thermique GPU", row.Title);
        Assert.Equal("Armée", row.Status);
        Assert.True(row.IsSupported);
        Assert.Contains("90 °C pendant 15 s", row.Detail);
        Assert.Contains("105 °C", row.Detail);
        Assert.Contains("cœur 64 °C", row.Detail);
        Assert.Contains("point chaud N/D", row.Detail);
    }

    [Fact]
    public void Diagnostic_ArmedWithoutTemperature_IsAProblem()
    {
        var safety = new GpuThermalSafety(new FakeCard());
        safety.UpdateArming(true);

        CompatibilityRow row = GpuThermalSafetyRowProvider.BuildRow(true, safety);

        Assert.False(row.IsSupported);
        Assert.Contains("ne peut rien surveiller", row.Detail);
    }

    [Fact]
    public void Diagnostic_NoControllableGpu_IsNotApplicable()
        => Assert.Equal("Sans objet", GpuThermalSafetyRowProvider.BuildRow(false, new GpuThermalSafety(new FakeCard())).Status);
}
