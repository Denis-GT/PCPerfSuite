using PCPerfSuite.Core.Safety;

namespace PCPerfSuite.Core.Tests;

/// <summary>Seuil tenu pendant un délai, capteurs absents et perte de lecture, à heure injectée.</summary>
public class ThermalGuardTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    private static readonly ThermalLimit Core = new("cœur", 90f, TimeSpan.FromSeconds(15));
    private static readonly ThermalLimit HotSpot = new("point chaud", 105f, TimeSpan.FromSeconds(15));

    private static DateTimeOffset At(double seconds) => T0.AddSeconds(seconds);

    [Fact]
    public void BelowThreshold_IsOk()
    {
        var guard = new ThermalGuard([Core]);

        Assert.Equal(ThermalState.Ok, guard.Note(At(0), [89.9f]).State);
    }

    [Fact]
    public void AtThreshold_HeatsThenTripsAfterTheDelay()
    {
        var guard = new ThermalGuard([Core]);

        Assert.Equal(ThermalState.Heating, guard.Note(At(0), [90f]).State);
        Assert.Equal(ThermalState.Heating, guard.Note(At(14.9), [92f]).State);

        ThermalVerdict tripped = guard.Note(At(15), [93f]);
        Assert.Equal(ThermalState.Tripped, tripped.State);
        Assert.Equal("cœur", tripped.Sensor);
        Assert.Equal(93f, tripped.TemperatureC);
    }

    [Fact]
    public void DroppingBelowThreshold_RestartsTheDelay()
    {
        var guard = new ThermalGuard([Core]);

        guard.Note(At(0), [91f]);
        guard.Note(At(10), [85f]);
        Assert.Equal(ThermalState.Heating, guard.Note(At(12), [91f]).State);
        Assert.Equal(ThermalState.Heating, guard.Note(At(26), [91f]).State);
        Assert.Equal(ThermalState.Tripped, guard.Note(At(27), [91f]).State);
    }

    [Fact]
    public void AfterTripping_EverythingStartsOver()
    {
        var guard = new ThermalGuard([Core]);
        guard.Note(At(0), [95f]);
        Assert.Equal(ThermalState.Tripped, guard.Note(At(15), [95f]).State);

        Assert.Equal(ThermalState.Heating, guard.Note(At(16), [95f]).State);
    }

    [Fact]
    public void SecondSensor_TripsOnItsOwnThreshold()
    {
        var guard = new ThermalGuard([Core, HotSpot]);

        guard.Note(At(0), [80f, 106f]);
        ThermalVerdict verdict = guard.Note(At(15), [80f, 107f]);

        Assert.Equal(ThermalState.Tripped, verdict.State);
        Assert.Equal("point chaud", verdict.Sensor);
    }

    [Fact]
    public void MissingSecondSensor_GuardHoldsOnTheFirst()
    {
        // RTX 50 : pas de point chaud publié.
        var guard = new ThermalGuard([Core, HotSpot]);

        guard.Note(At(0), [91f, null]);
        Assert.Equal(ThermalState.Tripped, guard.Note(At(15), [91f, null]).State);
    }

    [Fact]
    public void MissingReading_ResetsThatSensorsDelay()
    {
        var guard = new ThermalGuard([Core]);

        guard.Note(At(0), [91f]);
        guard.Note(At(5), [null]);
        Assert.Equal(ThermalState.Heating, guard.Note(At(15), [91f]).State);
    }

    [Fact]
    public void NeverRead_IsNotMonitorable()
    {
        var guard = new ThermalGuard([Core, HotSpot], lossDelay: TimeSpan.FromSeconds(15));

        Assert.Equal(ThermalState.NotMonitorable, guard.Note(At(0), [null, null]).State);
        Assert.Equal(ThermalState.NotMonitorable, guard.Note(At(60), [null, null]).State);
    }

    [Fact]
    public void ReadingLostForTheLossDelay_IsLost()
    {
        var guard = new ThermalGuard([Core], lossDelay: TimeSpan.FromSeconds(15));

        guard.Note(At(0), [70f]);
        Assert.Equal(ThermalState.Ok, guard.Note(At(10), [null]).State);
        Assert.Equal(ThermalState.Lost, guard.Note(At(15), [null]).State);
    }

    [Fact]
    public void WithoutLossDelay_AMissingReadingIsNeverALoss()
    {
        // Comportement historique du CPU : une température absente ne fait que remettre le délai à zéro.
        var guard = new ThermalGuard([Core]);

        guard.Note(At(0), [70f]);
        Assert.Equal(ThermalState.Ok, guard.Note(At(3600), [null]).State);
    }

    [Fact]
    public void CheckLoss_KeepsARunningHeatDelay()
    {
        // Le chien de garde ne doit pas effacer une chaleur en cours comme le ferait un relevé vide.
        var guard = new ThermalGuard([Core], lossDelay: TimeSpan.FromSeconds(15));
        guard.Note(At(0), [95f]);

        Assert.Equal(ThermalState.Ok, guard.CheckLoss(At(8)).State);
        Assert.Equal(ThermalState.Tripped, guard.Note(At(15), [95f]).State);
    }

    [Fact]
    public void CheckLoss_AfterTheLossDelay_IsLost()
    {
        var guard = new ThermalGuard([Core], lossDelay: TimeSpan.FromSeconds(15));
        guard.Note(At(0), [70f]);

        Assert.Equal(ThermalState.Lost, guard.CheckLoss(At(15)).State);
        Assert.Equal(ThermalState.NotMonitorable, guard.CheckLoss(At(16)).State);
    }

    [Fact]
    public void CheckLoss_NeverRead_IsNotMonitorable()
        => Assert.Equal(ThermalState.NotMonitorable,
            new ThermalGuard([Core], lossDelay: TimeSpan.FromSeconds(15)).CheckLoss(At(60)).State);

    [Fact]
    public void NaN_CountsAsNotRead()
    {
        var guard = new ThermalGuard([Core]);

        Assert.Equal(ThermalState.NotMonitorable, guard.Note(At(0), [float.NaN]).State);
    }

    [Fact]
    public void Reset_ForgetsARunningDelay()
    {
        var guard = new ThermalGuard([Core]);
        guard.Note(At(0), [95f]);

        guard.Reset();

        Assert.Equal(ThermalState.Heating, guard.Note(At(15), [95f]).State);
    }
}
