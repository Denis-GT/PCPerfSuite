namespace PCPerfSuite.Core.Profiles;

/// <summary>
/// L'heure à laquelle élaguer l'historique d'usage (30 jours). L'horloge de Windows peut sauter en avant pendant que
/// l'app tourne (date avancée à la main, synchronisation ratée après un changement de pile du BIOS) : élaguer d'après
/// elle effacerait tout l'historique et le journal des bascules, sans retour possible une fois l'heure corrigée. Le temps
/// écoulé depuis le lancement est donc mesuré à part, par un compteur qui ne suit pas les changements d'heure : au-delà
/// d'un jour d'écart en avant, c'est lui qui fait foi.
///
/// Limite : une horloge déjà fausse au lancement ne se distingue pas d'une vraie absence de 30 jours ; l'historique est
/// alors élagué, la rétention annoncée l'emporte.
/// </summary>
public sealed class RetentionClock
{
    /// <summary>Écart toléré : une resynchronisation ordinaire déplace l'heure de quelques secondes.</summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromDays(1);

    private readonly DateTimeOffset _startUtc;
    private readonly long _startMs;
    private readonly Func<long> _elapsedMs;

    /// <param name="startUtc">L'heure du lancement.</param>
    /// <param name="elapsedMs">Compteur en millisecondes insensible aux changements d'heure ; par défaut
    /// <see cref="Environment.TickCount64"/>, qui compte aussi la veille.</param>
    public RetentionClock(DateTimeOffset startUtc, Func<long>? elapsedMs = null)
    {
        _elapsedMs = elapsedMs ?? (() => Environment.TickCount64);
        _startUtc = startUtc;
        _startMs = _elapsedMs();
    }

    /// <summary>L'heure à passer à <see cref="UsageHistory.Prune"/> : celle de Windows, sauf si elle a sauté en avant
    /// depuis le lancement ; un recul n'élague que moins, il est gardé.</summary>
    public DateTimeOffset PruneTime(DateTimeOffset wallClockUtc)
    {
        DateTimeOffset measured = _startUtc + TimeSpan.FromMilliseconds(Math.Max(0, _elapsedMs() - _startMs));
        return wallClockUtc > measured + Tolerance ? measured : wallClockUtc;
    }
}
