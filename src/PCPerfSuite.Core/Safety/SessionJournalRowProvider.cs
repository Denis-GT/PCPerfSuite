using PCPerfSuite.Core.Compatibility;
using PCPerfSuite.Core.Safety.Events;

namespace PCPerfSuite.Core.Safety;

/// <summary>
/// Diagnostic « Journal de session » : état du fichier (opérations gardées, en cours, lignes ignorées) et bilan de la
/// reprise au lancement (opérations interrompues, par composant, et ce qui leur est arrivé). Jamais les valeurs des
/// opérations : seulement leurs composants et leur qualification.
/// </summary>
public sealed class SessionJournalRowProvider : ICompatibilityRowProvider
{
    public const string RowTitle = "Journal de session";

    private readonly StartupRecoveryReport _report;
    private readonly SessionJournal _journal;
    private SessionJournalContent? _content;

    public SessionJournalRowProvider(StartupRecoveryReport report, SessionJournal journal)
    {
        _report = report;
        _journal = journal;
    }

    public string Title => RowTitle;

    public Task RefreshAsync(CancellationToken cancellationToken)
    {
        Volatile.Write(ref _content, _journal.Read());
        return Task.CompletedTask;
    }

    public IReadOnlyList<CompatibilityRow> GetRows() => [BuildRow(_report, Volatile.Read(ref _content), _journal.LastWriteError)];

    public static CompatibilityRow BuildRow(StartupRecoveryReport report, SessionJournalContent? content, string? writeError)
    {
        var parts = new List<string>();

        if (report.JournalProblem is { } problem) parts.Add($"Journal illisible au lancement : {problem}.");

        if (report.Recovered.Count > 0)
        {
            string items = string.Join(" ; ", report.Recovered.Select(item =>
                $"{item.Entry.Component} : {IncidentClassifier.Label(item.Qualification.Kind)}"));
            parts.Add($"Au lancement, {Plural(report.Recovered.Count, "opération interrompue", "opérations interrompues")} : {items}.");
            if (report.EventsProblem is { } events) parts.Add($"Qualification incomplète : {events.Reason}.");
            if (report.QualificationDeferred) parts.Add("La cause complète est réécrite dans le journal dès que Windows a répondu.");
        }

        foreach (RecoveryHandlerOutcome outcome in report.Handlers.Where(outcome => !outcome.Succeeded))
        {
            parts.Add($"Reprise « {outcome.HandlerId} » {outcome.Note}.");
        }

        if (report.SkippedLive.Count > 0)
        {
            parts.Add($"{Plural(report.SkippedLive.Count, "opération tenue par un autre processus PCPerfSuite, laissée en l'état",
                "opérations tenues par un autre processus PCPerfSuite, laissées en l'état")}.");
        }

        if (content is not null)
        {
            int pending = content.Pending.Count();
            parts.Add($"Fichier : {Plural(content.Entries.Count, "opération gardée", "opérations gardées")} ({SessionJournal.Retention.TotalDays:0} jours)"
                + (pending > 0 ? $", dont {pending} en cours" : "")
                + (content.IgnoredLines > 0 ? $", {Plural(content.IgnoredLines, "ligne illisible ignorée", "lignes illisibles ignorées")}" : "")
                + ".");
        }

        if (writeError is not null) parts.Add($"Dernière écriture en échec : {writeError}.");

        bool healthy = report.JournalProblem is null && writeError is null && report.Handlers.All(outcome => outcome.Succeeded);
        string status = report.Recovered.Count > 0
            ? Plural(report.Recovered.Count, "opération interrompue", "opérations interrompues")
            : healthy ? "Rien d'interrompu" : "Problème d'écriture ou de lecture";

        if (parts.Count == 0) parts.Add("Aucune opération risquée n'a été journalisée.");
        return new CompatibilityRow(RowTitle, status, string.Join(" ", parts), healthy && report.Recovered.Count == 0);
    }

    private static string Plural(int count, string one, string many) => count == 1 ? $"1 {one}" : $"{count} {many}";
}
