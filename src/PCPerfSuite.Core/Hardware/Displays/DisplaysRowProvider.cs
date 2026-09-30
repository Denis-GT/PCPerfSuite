using System.Globalization;
using PCPerfSuite.Core.Compatibility;

namespace PCPerfSuite.Core.Hardware.Displays;

/// <summary>
/// Diagnostic « Écrans » : chaque écran branché, avec son nom, sa résolution, sa fréquence, son échelle, sa sortie et le
/// GPU qui le pilote. C'est ce qui explique qu'un écran manque dans la liste de l'overlay, et ce dont auront besoin
/// l'OC d'écran (#17) et le GPU dédié des portables (#22). Aucun numéro de série, ni empreinte, n'y figure.
/// </summary>
public sealed class DisplaysRowProvider : ICompatibilityRowProvider
{
    public const string RowTitle = "Écrans";

    private const string Experimental = "Expérimental : ce cas n'a pas encore été vérifié sur une vraie machine.";

    private DisplayTopologySnapshot? _snapshot;

    public string Title => RowTitle;

    public Task RefreshAsync(CancellationToken cancellationToken)
    {
        Volatile.Write(ref _snapshot, DisplayTopology.Read());
        return Task.CompletedTask;
    }

    public IReadOnlyList<CompatibilityRow> GetRows() => BuildRows(Volatile.Read(ref _snapshot));

    /// <summary>Les lignes, isolées ici pour être testées sans écran.</summary>
    public static IReadOnlyList<CompatibilityRow> BuildRows(DisplayTopologySnapshot? snapshot)
    {
        if (snapshot is null) return [new CompatibilityRow(RowTitle, "Pas encore lu", "Lecture des écrans en cours.", true)];

        if (snapshot.Monitors.Count == 0)
        {
            return
            [
                new CompatibilityRow(RowTitle, "Aucun écran lu",
                    $"Windows n'a décrit aucun écran{(snapshot.Problem is { } p ? $" : {p}" : ".")} L'overlay fenêtre reste sur l'écran principal.",
                    false),
            ];
        }

        var rows = new List<CompatibilityRow>();
        int count = snapshot.Monitors.Count;
        string summary = count == 1 ? "1 écran" : $"{count} écrans";
        string summaryDetail = snapshot.Problem is { } problem
            ? $"Lecture partielle : {problem}"
            : "Lus par les API de Windows (DisplayConfig), sans droits administrateur.";
        rows.Add(new CompatibilityRow(RowTitle, summary, summaryDetail, snapshot.Problem is null));

        for (int i = 0; i < count; i++) rows.Add(BuildMonitorRow(snapshot.Monitors[i], i + 1));
        return rows;
    }

    private static CompatibilityRow BuildMonitorRow(DisplayMonitor monitor, int number)
    {
        string label = DisplayNames.Label(monitor, number);
        var details = new List<string>
        {
            $"{monitor.Bounds.Width}×{monitor.Bounds.Height}",
            monitor.RefreshHz is { } hz ? $"{hz.ToString("0.##", CultureInfo.GetCultureInfo("fr-FR"))} Hz" : "fréquence non donnée par Windows",
            monitor.DpiIsKnown ? $"échelle {Math.Round(monitor.Scale * 100)} %" : "échelle non lue (100 % supposé)",
        };

        List<string> outputs = monitor.Targets.Select(t => DisplayNames.Describe(t.Output)).Distinct().ToList();
        details.Add(outputs.Count > 0 ? $"sortie {string.Join(" + ", outputs)}" : "sortie inconnue");
        details.Add(monitor.AdapterName is { } adapter ? $"piloté par {adapter}" : "GPU non identifié");
        if (monitor.IsPrimary) details.Add("écran principal");

        string detail = string.Join(", ", details) + ".";
        bool supported = true;

        if (monitor.Targets.Count == 0)
        {
            detail += " Windows n'a pas décrit cet écran (nom, EDID, sortie) : il reste utilisable par l'overlay.";
            supported = false;
        }
        else if (monitor.Targets.Any(t => !t.HasEdid))
        {
            detail += " Pas d'identifiants EDID (écran forcé, dock ou adaptateur) : il sera retrouvé par son connecteur seulement. "
                      + Experimental;
        }

        if (monitor.IsCloned) detail += $" Écrans dupliqués : l'overlay s'affiche sur tous à la fois. {Experimental}";

        return new CompatibilityRow($"Écran {number}", label, detail, supported);
    }
}
