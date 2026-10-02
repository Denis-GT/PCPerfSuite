namespace PCPerfSuite.Core.Benchmark.Protocol;

/// <summary>
/// Veille de vivacité, en logique pure : chaque bout du tube note l'heure du dernier signe de vie de l'autre, et le tient
/// pour mort passé le délai. Le worker s'en sert pour couper toute charge sans nouvelles de l'app (2 s) ; l'app pour
/// tuer un worker muet (5 s) et pour arrêter un test sans relevé de capteurs (10 s). L'heure doit être monotone
/// (Environment.TickCount64), jamais l'horloge murale : un changement d'heure ne doit ni tuer ni ressusciter.
/// </summary>
public sealed class LivenessWatch
{
    public LivenessWatch(TimeSpan timeout, DateTimeOffset now)
    {
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        Timeout = timeout;
        LastSignalAt = now;
    }

    public TimeSpan Timeout { get; }

    public DateTimeOffset LastSignalAt { get; private set; }

    /// <summary>Un signe de vie. Une heure antérieure à la dernière notée est ignorée.</summary>
    public void Note(DateTimeOffset now)
    {
        if (now > LastSignalAt) LastSignalAt = now;
    }

    public TimeSpan Silence(DateTimeOffset now) => now > LastSignalAt ? now - LastSignalAt : TimeSpan.Zero;

    public bool IsExpired(DateTimeOffset now) => Silence(now) >= Timeout;

    /// <summary>Heure monotone, pour la production : l'origine n'a pas de sens, seules les différences comptent.</summary>
    public static DateTimeOffset MonotonicNow() => DateTimeOffset.UnixEpoch.AddMilliseconds(Environment.TickCount64);
}
