using System.Globalization;
using PCPerfSuite.Core.Compatibility;

namespace PCPerfSuite.Core.Profiles;

/// <summary>Ce que la bascule automatique sait et que le diagnostic reprend, sans nom d'application ni chemin.</summary>
/// <param name="Text">Texte de l'état (<see cref="AutoSwitchDecision.Text"/>), écrit sans nom d'application.</param>
/// <param name="LastSwitch">Dernière ligne « bascule » ou « refus » du journal.</param>
/// <param name="LastIncident">Dernier incident après une bascule.</param>
public sealed record AutoSwitchStatus(
    bool Enabled,
    AutoSwitchState State,
    string Text,
    AutoSwitchJournalEntry? LastSwitch,
    AutoSwitchJournalEntry? LastIncident,
    int RuleCount,
    int HistoryDays,
    string? HistoryProblem = null,
    string? Signals = null);

/// <summary>
/// Ligne « Bascule automatique » du diagnostic « Compatibilité de ce PC » (le rapport copié sert aussi de rapport de
/// bug) : activée ou non, état, dernier changement (heure, usage, sorte de raison, parties non appliquées), dernier
/// incident, nombre de règles et jours d'historique. Jamais de nom d'exécutable ni de chemin : la raison détaillée d'une
/// règle, qui nomme l'application, n'y figure pas. Tout vient de la mémoire de la bascule. « Expérimental » (règle 6).
/// </summary>
public sealed class AutoSwitchRowProvider : ICompatibilityRowProvider
{
    public const string RowTitle = "Bascule automatique";

    private readonly Func<AutoSwitchStatus> _status;

    public AutoSwitchRowProvider(Func<AutoSwitchStatus> status) => _status = status;

    public string Title => RowTitle;

    public IReadOnlyList<CompatibilityRow> GetRows()
    {
        AutoSwitchStatus status = _status();
        if (!status.Enabled)
        {
            return [new CompatibilityRow(RowTitle, "désactivée",
                "Activable dans Profils › Automatique. Rien n'est relevé tant qu'elle est désactivée. Expérimental : pas encore vérifié sur une vraie machine.",
                IsSupported: true)];
        }

        var details = new List<string> { $"État : {status.Text}" };
        if (status.LastSwitch is { } last)
        {
            string usage = ProfileGroupUsage.Label(last.Usage) ?? "usage inconnu";
            string kind = last.Kind == AutoSwitchJournalKinds.Refused ? "bascule refusée" : "dernière bascule";
            string group = string.IsNullOrWhiteSpace(last.GroupName) ? "" : $", groupe « {last.GroupName} »";
            details.Add($"{Upper(kind)} le {Local(last.TimeUtc)} : {usage}{group} ({UsageReasons.Label(last.ReasonKind)}).");
            if (last.NotApplied is { Count: > 0 } notApplied) details.Add($"Non appliqué : {string.Join(" ; ", notApplied)}.");
        }
        else
        {
            details.Add("Aucune bascule pour l'instant.");
        }

        if (status.LastIncident is { } incident)
        {
            string group = string.IsNullOrWhiteSpace(incident.GroupName) ? "un groupe" : $"« {incident.GroupName} »";
            details.Add($"Incident le {Local(incident.TimeUtc)} après une bascule vers {group} : groupe suspendu.");
        }

        details.Add(status.RuleCount switch
        {
            0 => "Aucune règle par application.",
            1 => "1 règle par application.",
            _ => $"{status.RuleCount.ToString(CultureInfo.InvariantCulture)} règles par application.",
        });
        details.Add($"Historique : {status.HistoryDays.ToString(CultureInfo.InvariantCulture)} jour(s).");
        if (status.Signals is { } signals) details.Add(signals);
        if (status.HistoryProblem is { } historyProblem) details.Add($"usage.json : {historyProblem}.");
        details.Add("Expérimental : seuils et délais pas encore vérifiés sur une vraie machine.");

        string value = status.State switch
        {
            AutoSwitchState.Locked => "verrouillée",
            AutoSwitchState.Paused => "en pause",
            _ => "activée",
        };
        bool healthy = status.State != AutoSwitchState.Locked && status.LastIncident is null;
        return [new CompatibilityRow(RowTitle, value, string.Join(" ", details), IsSupported: healthy)];
    }

    private static string Local(DateTimeOffset utc) => utc.ToLocalTime().ToString("dd/MM à HH:mm", CultureInfo.InvariantCulture);

    private static string Upper(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
