using System.Globalization;
using PCPerfSuite.Core.Compatibility;

namespace PCPerfSuite.Core.Safety.Events;

/// <summary>
/// Diagnostic « Journaux Windows » : les incidents des 30 derniers jours lus dans le journal Système (arrêts brutaux,
/// écrans bleus, arrêts inattendus, TDR, erreurs WHEA, erreurs de disque), avec leur nombre et la date du dernier.
/// Aucun texte de message : seulement les types, les numéros d'événement et des codes.
/// </summary>
public sealed class WindowsEventsRowProvider : ICompatibilityRowProvider
{
    public const string RowTitle = "Journaux Windows";

    public const int Days = 30;

    private SystemEventReadResult? _reading;

    public string Title => RowTitle;

    public Task RefreshAsync(CancellationToken cancellationToken)
    {
        SystemEventReadResult reading = SystemEventReader.ReadLastDays(Days, cancellationToken: cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        Volatile.Write(ref _reading, reading);
        return Task.CompletedTask;
    }

    public IReadOnlyList<CompatibilityRow> GetRows() => [BuildRow(Volatile.Read(ref _reading))];

    public static CompatibilityRow BuildRow(SystemEventReadResult? reading)
    {
        if (reading is null) return new CompatibilityRow(RowTitle, "Pas encore lu", "Lecture du journal Système en cours.", true);

        if (reading.Events is not { } events)
        {
            string reason = reading.Problem?.Reason ?? "journal Système illisible";
            return new CompatibilityRow(RowTitle, "Non lu", $"N/D : {reason}.", false);
        }

        IReadOnlyList<Incident> incidents = IncidentClassifier.ClassifyAll(events);
        string truncated = reading.Truncated ? $" Lecture arrêtée à {SystemEventReader.MaxRecords} événements." : "";

        if (incidents.Count == 0)
        {
            return new CompatibilityRow(RowTitle, $"Aucun incident sur {Days} jours",
                $"Ni arrêt brutal, ni écran bleu, ni TDR, ni erreur matérielle ou de disque dans le journal Système.{truncated}", true);
        }

        string detail = string.Join(" ; ", incidents
            .GroupBy(i => i.Kind)
            .OrderBy(g => g.Key)
            .Select(g => $"{IncidentClassifier.Label(g.Key)} : {g.Count()} (dernier le {FormatDate(g.Max(i => i.LoggedUtc))})"));

        string status = incidents.Count == 1 ? $"1 incident sur {Days} jours" : $"{incidents.Count} incidents sur {Days} jours";
        return new CompatibilityRow(RowTitle, status,
            $"{detail}. Un arrêt est daté du démarrage qui l'a suivi.{truncated}", false);
    }

    private static string FormatDate(DateTimeOffset utc)
        => utc.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);
}
