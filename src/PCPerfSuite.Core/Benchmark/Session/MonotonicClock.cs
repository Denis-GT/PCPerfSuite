using PCPerfSuite.Core.Hardware;

namespace PCPerfSuite.Core.Benchmark.Session;

/// <summary>
/// Heure de la veille de sécurité et du retour au repos du bench : l'heure murale de la création, puis l'avance de
/// l'horloge monotone (<see cref="TimeProvider.GetTimestamp"/>, celle de <see cref="System.Diagnostics.Stopwatch"/> en
/// production, comme <see cref="HardwareSnapshot.CapturedTimestamp"/>). Seules les différences comptent : un changement
/// d'heure de Windows pendant un test (recalage par le service de temps, réglage à la main) ne déclenche ni ne retarde
/// aucun arrêt (même règle que <see cref="Protocol.LivenessWatch"/>).
/// </summary>
public sealed class MonotonicClock
{
    private readonly TimeProvider _time;
    private readonly DateTimeOffset _origin;
    private readonly long _originTimestamp;

    public MonotonicClock(TimeProvider time)
    {
        _time = time;
        _originTimestamp = time.GetTimestamp();
        _origin = time.GetUtcNow();
    }

    public DateTimeOffset Now() => At(_time.GetTimestamp());

    /// <summary>L'heure d'un horodatage pris sur la même horloge monotone, celui de la capture d'un relevé par exemple.</summary>
    public DateTimeOffset At(long timestamp) => _origin + _time.GetElapsedTime(_originTimestamp, timestamp);
}
