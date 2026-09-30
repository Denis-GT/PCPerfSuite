using PCPerfSuite.Core.Compatibility;
using PCPerfSuite.Core.Hardware;
using PCPerfSuite.Core.Safety;

namespace PCPerfSuite.Core.Tests;

/// <summary>Sécurité thermique GPU : armement sur un réglage relevé, retour d'origine, relecture et message.</summary>
public class GpuThermalSafetyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset At(double seconds) => T0.AddSeconds(seconds);

    /// <summary>Heure murale du déclenchement, distincte de l'heure (monotone) des délais.</summary>
    private static readonly DateTimeOffset WallTime = new(2026, 9, 30, 14, 5, 0, TimeSpan.FromHours(2));

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.CreateCustomTimeZone("test", now.Offset, "test", "test");
    }

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

        /// <summary>Comme GpuControlService : la restauration réarme la sécurité d'après ce qu'elle relit.</summary>
        public Action? AfterRestoreCallback { get; set; }

        public void RestoreOverclockDefaults()
        {
            Restores++;
            Overclock = AfterRestore;
            AfterRestoreCallback?.Invoke();
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
        var safety = new GpuThermalSafety(card, new FixedClock(WallTime));
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
        Assert.Equal(WallTime, safety.LastTripAt);
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
    public void FailedRestore_RearmsAndRetriesAfterTheDelay()
    {
        // Le pilote refuse le retour d'origine : la cible réarme (le cœur relu est toujours relevé), et la sécurité
        // réessaie au bout d'un nouveau délai au lieu de rester désarmée sur une carte encore overclockée.
        var card = new FakeCard { AfterRestore = Oc(core: 150) };
        var safety = new GpuThermalSafety(card);
        card.AfterRestoreCallback = () => safety.UpdateArming(GpuOverclockRaise.IsRaised(card.Overclock, card.PowerSnapshot));
        safety.UpdateArming(true);

        safety.Note(At(0), 95f, null);
        safety.Note(At(15), 95f, null);
        Assert.Equal(1, card.Restores);
        Assert.True(safety.IsArmed);

        safety.Note(At(16), 95f, null);
        Assert.Equal(1, card.Restores);
        safety.Note(At(31), 95f, null);
        Assert.Equal(2, card.Restores);
    }

    [Fact]
    public void SuccessfulRestore_StaysDisarmed()
    {
        var card = new FakeCard();
        var safety = new GpuThermalSafety(card);
        card.AfterRestoreCallback = () => safety.UpdateArming(GpuOverclockRaise.IsRaised(card.Overclock, card.PowerSnapshot));
        safety.UpdateArming(true);

        safety.Note(At(0), 95f, null);
        safety.Note(At(15), 95f, null);

        Assert.False(safety.IsArmed);
    }

    [Fact]
    public void Restart_SleepTimeCountsNeitherAsHeatNorAsLoss()
    {
        var card = new FakeCard();
        var safety = new GpuThermalSafety(card);
        safety.UpdateArming(true);
        safety.Note(At(0), 91f, null);

        // Veille d'une heure, puis réveil.
        safety.Restart();
        safety.Note(At(3600), 91f, null);
        safety.Note(At(3601), null, null);

        Assert.Equal(0, card.Restores);
        Assert.True(safety.IsArmed);
    }

    [Fact]
    public void NoReadingAnyMore_RestoresAfterTheLossDelay()
    {
        // Lecture du GPU qui lève ou bloque (TDR) : plus aucun relevé, le chien de garde de l'onglet prend le relais.
        var card = new FakeCard();
        var safety = new GpuThermalSafety(card);
        safety.UpdateArming(true);
        safety.Note(At(0), 70f, null);

        safety.NoteNoReading(At(10));
        Assert.Equal(0, card.Restores);
        safety.NoteNoReading(At(15));
        Assert.Equal(1, card.Restores);
        Assert.Contains("n'est plus lue", safety.LastTripMessage);
    }

    [Fact]
    public void NoReading_NotArmed_DoesNothing()
    {
        var card = new FakeCard();
        var safety = new GpuThermalSafety(card);
        safety.Note(At(0), 70f, null);

        safety.NoteNoReading(At(60));

        Assert.Equal(0, card.Restores);
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
