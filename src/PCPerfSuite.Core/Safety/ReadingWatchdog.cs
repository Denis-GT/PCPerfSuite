namespace PCPerfSuite.Core.Safety;

public enum WatchdogAction
{
    /// <summary>Rien à faire.</summary>
    None,

    /// <summary>Une veille vient de finir : repartir de zéro (<see cref="GpuThermalSafety.Restart"/>).</summary>
    Restart,

    /// <summary>Plus aucun relevé depuis trop longtemps : vérifier la perte de température.</summary>
    CheckLoss,
}

/// <summary>
/// Distingue, en logique pure, une veille d'une panne de relevé. Les deux se voient pareil : du temps passe sans
/// relevé. Mais pendant une veille, le fil de l'interface ne tourne pas non plus : le tick du chien de garde lui-même
/// arrive en retard. L'événement Resume de Windows ne suffit pas (absent d'un réveil automatique, ou postérieur au
/// premier relevé). L'heure donnée doit être monotone (<c>Environment.TickCount64</c>), qui avance pendant la veille.
/// </summary>
public sealed class ReadingWatchdog
{
    private readonly TimeSpan _checkInterval;
    private DateTimeOffset _lastReadingAt;
    private DateTimeOffset _lastTickAt;

    public ReadingWatchdog(TimeSpan checkInterval, DateTimeOffset now)
    {
        _checkInterval = checkInterval;
        _lastReadingAt = now;
        _lastTickAt = now;
    }

    /// <summary>Un tick qui arrive plus de trois intervalles après le précédent : le fil de l'interface était arrêté.</summary>
    private TimeSpan FrozenThreshold => TimeSpan.FromTicks(3 * _checkInterval.Ticks);

    /// <summary>Trois relevés manqués, au moins un intervalle de contrôle : un relevé lent choisi par l'utilisateur
    /// (jusqu'à 60 s) n'est pas une panne.</summary>
    private TimeSpan Silence(TimeSpan readingInterval) => TimeSpan.FromTicks(Math.Max(_checkInterval.Ticks, 3 * readingInterval.Ticks));

    /// <summary>Tick du chien de garde, toutes les <c>checkInterval</c>.</summary>
    public WatchdogAction OnTick(DateTimeOffset now, TimeSpan readingInterval)
    {
        bool frozen = now - _lastTickAt > FrozenThreshold;
        _lastTickAt = now;

        if (frozen)
        {
            Restarted(now);
            return WatchdogAction.Restart;
        }

        return now - _lastReadingAt >= Silence(readingInterval) ? WatchdogAction.CheckLoss : WatchdogAction.None;
    }

    /// <summary>Un relevé arrive. <see cref="WatchdogAction.Restart"/> si c'est le premier après une veille : il arrive
    /// parfois avant le tick du chien de garde et avant l'événement Resume. Le seuil suit la cadence du relevé, sinon
    /// un relevé réglé à 60 s remettrait tout à zéro à chaque fois et la chaleur ne déclencherait jamais.</summary>
    public WatchdogAction OnReading(DateTimeOffset now, TimeSpan readingInterval)
    {
        TimeSpan gap = now - _lastReadingAt;
        _lastReadingAt = now;
        return gap > TimeSpan.FromTicks(Math.Max(FrozenThreshold.Ticks, 3 * readingInterval.Ticks))
            ? WatchdogAction.Restart
            : WatchdogAction.None;
    }

    /// <summary>Repartir de zéro (événement Resume, ou veille détectée).</summary>
    public void Restarted(DateTimeOffset now)
    {
        _lastReadingAt = now;
        _lastTickAt = now;
    }
}
