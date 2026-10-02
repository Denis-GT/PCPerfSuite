using PCPerfSuite.Core.Safety.Events;

namespace PCPerfSuite.Core.Profiles;

/// <summary>Ce que la surveillance en session fait de la période probatoire, à chaque minute.</summary>
public enum ProbationTickAction
{
    /// <summary>Rien à faire : la période continue.</summary>
    Wait,

    /// <summary>Terme atteint sans TDR : la période est close « terminée ».</summary>
    Close,

    /// <summary>Terme atteint sans avoir pu lire le journal Système : rien ne permet de dire « sans incident ».</summary>
    CloseUnverified,

    /// <summary>Un TDR a eu lieu pendant la période : elle échoue, le groupe à qui revient l'OC est suspendu.</summary>
    Tdr,
}

/// <summary>La décision, avec le TDR vu ou la raison pour laquelle le journal n'a pas pu être lu.</summary>
public sealed record ProbationTickDecision(ProbationTickAction Action, Incident? Tdr = null, string? Why = null);

/// <summary>
/// Décision de la surveillance en session, en logique pure (testée sans journal Système ni minuterie). Une lecture ratée
/// n'est jamais prise pour « aucun TDR » : au lancement, un journal illisible compte déjà comme un incident (cause non
/// établie) ; en session, la période attend la lecture suivante, qui reprend depuis son début, et se clôt « non
/// vérifiée » si le journal est toujours illisible à son terme.
/// </summary>
public static class ProbationWatch
{
    public const string ReadFailed = "la lecture du journal Système de Windows a échoué";

    /// <summary>Le journal Système doit être lu : seul un OC GPU (du groupe ou repris d'un groupe précédent) rend un
    /// TDR imputable.</summary>
    public static bool NeedsEvents(ProbationInfo current) => current.WatchesGpu;

    /// <param name="read">La lecture du journal Système ; null si elle a levé.</param>
    public static ProbationTickDecision Decide(ProbationInfo current, DateTimeOffset now, SystemEventReadResult? read)
    {
        bool due = now >= current.DeadlineUtc;
        if (!NeedsEvents(current)) return new ProbationTickDecision(due ? ProbationTickAction.Close : ProbationTickAction.Wait);

        if (read?.Events is not { } events)
        {
            string why = read?.Problem?.Reason is { Length: > 0 } reason ? reason : ReadFailed;
            return new ProbationTickDecision(due ? ProbationTickAction.CloseUnverified : ProbationTickAction.Wait, Why: why);
        }

        if (ProfileGroupIncidentPolicy.Tdr(IncidentClassifier.ClassifyAll(events), current.SinceUtc) is { } tdr)
        {
            return new ProbationTickDecision(ProbationTickAction.Tdr, tdr);
        }

        return new ProbationTickDecision(due ? ProbationTickAction.Close : ProbationTickAction.Wait);
    }
}
