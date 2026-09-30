using PCPerfSuite.Core.Safety;

namespace PCPerfSuite.Core.Tests;

/// <summary>Veille ou panne de relevé : ce que voit le chien de garde de la sécurité thermique GPU.</summary>
public class ReadingWatchdogTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Check = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan OneSecondReadings = TimeSpan.FromSeconds(1);

    private static DateTimeOffset At(double seconds) => T0.AddSeconds(seconds);

    [Fact]
    public void RegularReadings_NothingToDo()
    {
        var watchdog = new ReadingWatchdog(Check, At(0));

        for (int s = 1; s <= 30; s++)
        {
            Assert.Equal(WatchdogAction.None, watchdog.OnReading(At(s), OneSecondReadings));
            if (s % 5 == 0) Assert.Equal(WatchdogAction.None, watchdog.OnTick(At(s), OneSecondReadings));
        }
    }

    [Fact]
    public void ReadingsStop_TicksKeepComing_ChecksTheLoss()
    {
        // TDR : le relevé lève ou bloque, l'interface tourne.
        var watchdog = new ReadingWatchdog(Check, At(0));
        watchdog.OnReading(At(1), OneSecondReadings);

        Assert.Equal(WatchdogAction.None, watchdog.OnTick(At(5), OneSecondReadings));
        Assert.Equal(WatchdogAction.CheckLoss, watchdog.OnTick(At(10), OneSecondReadings));
    }

    [Fact]
    public void SleepWithoutResumeEvent_FirstTickRestarts_AndDoesNotCheckTheLoss()
    {
        // Réveil automatique : ni Resume ni relevé avant le premier tick, en retard d'une heure.
        var watchdog = new ReadingWatchdog(Check, At(0));
        watchdog.OnReading(At(1), OneSecondReadings);
        watchdog.OnTick(At(5), OneSecondReadings);

        Assert.Equal(WatchdogAction.Restart, watchdog.OnTick(At(3605), OneSecondReadings));
    }

    [Fact]
    public void SleepWithoutResumeEvent_DoesNotRestoreAnArmedOverclock()
    {
        // Le scénario complet, comme l'onglet GPU l'enchaîne : sécurité armée, veille d'une heure, réveil automatique.
        var card = new NoOpCard();
        var safety = new GpuThermalSafety(card);
        safety.UpdateArming(true);
        var watchdog = new ReadingWatchdog(Check, At(0));
        watchdog.OnReading(At(0), OneSecondReadings);
        safety.Note(At(0), 70f, null);

        foreach (double s in new[] { 3605.0, 3610.0, 3615.0, 3620.0 })
        {
            switch (watchdog.OnTick(At(s), OneSecondReadings))
            {
                case WatchdogAction.Restart:
                    safety.Restart();
                    break;
                case WatchdogAction.CheckLoss:
                    safety.NoteNoReading(At(s));
                    break;
            }
        }

        Assert.Equal(0, card.Restores);
        Assert.True(safety.IsArmed);
    }

    private sealed class NoOpCard : IGpuOverclockTarget
    {
        public int Restores { get; private set; }
        public void RestoreOverclockDefaults() => Restores++;
        public Hardware.GpuOverclockSnapshot? GetOverclock() => null;
        public Hardware.GpuControlSnapshot? GetSnapshot() => null;
    }

    [Fact]
    public void SleepSeenByTheFirstReading_Restarts()
    {
        var watchdog = new ReadingWatchdog(Check, At(0));
        watchdog.OnReading(At(1), OneSecondReadings);

        Assert.Equal(WatchdogAction.Restart, watchdog.OnReading(At(3600), OneSecondReadings));
        Assert.Equal(WatchdogAction.None, watchdog.OnReading(At(3601), OneSecondReadings));
    }

    [Fact]
    public void SlowReadingsChosenByTheUser_AreNeitherSleepNorLoss()
    {
        // Relevé toutes les 60 s : ni la chaleur ne doit être remise à zéro à chaque relevé, ni la perte déclarée.
        var watchdog = new ReadingWatchdog(Check, At(0));
        TimeSpan sixtySeconds = TimeSpan.FromSeconds(60);

        for (int s = 5; s <= 120; s += 5)
        {
            Assert.Equal(WatchdogAction.None, watchdog.OnTick(At(s), sixtySeconds));
            if (s % 60 == 0) Assert.Equal(WatchdogAction.None, watchdog.OnReading(At(s), sixtySeconds));
        }
    }

    [Fact]
    public void Restarted_ForgetsTheSilence()
    {
        var watchdog = new ReadingWatchdog(Check, At(0));
        watchdog.OnTick(At(5), OneSecondReadings);

        watchdog.Restarted(At(9));

        Assert.Equal(WatchdogAction.None, watchdog.OnTick(At(10), OneSecondReadings));
    }
}
