using PCPerfSuite.Core.Benchmark.Results;
using PCPerfSuite.Core.Compatibility;

namespace PCPerfSuite.Core.Benchmark;

/// <summary>Ce que la page Bench sait à l'instant, pour la ligne du diagnostic (rapide, déjà lu).</summary>
public sealed record BenchDiagnosticStatus(
    IReadOnlyDictionary<BenchTestKind, Unavailable?> PerTest,
    string? WorkerProblem,
    string? ThermalLimit,
    int EligibleVolumes,
    IReadOnlyList<string> Notes);

/// <summary>
/// Ligne « Bench » du diagnostic « Compatibilité de ce PC » : les tests disponibles ici et pourquoi pas les autres
/// (règles 1 à 3), le seuil thermique retenu, les volumes éligibles, et la dernière session enregistrée (lue hors du fil
/// d'interface par <see cref="RefreshAsync"/>).
/// </summary>
public sealed class BenchRowProvider : ICompatibilityRowProvider
{
    public const string RowTitle = "Bench";

    private readonly Func<BenchDiagnosticStatus?> _status;
    private readonly BenchResultStore _store;
    private string? _lastSession;
    private bool _refreshed;

    public BenchRowProvider(Func<BenchDiagnosticStatus?> status, BenchResultStore store)
    {
        _status = status;
        _store = store;
    }

    public string Title => RowTitle;

    public IReadOnlyList<CompatibilityRow> GetRows()
    {
        BenchDiagnosticStatus? status = _status();
        var rows = new List<CompatibilityRow>();

        if (status is null)
        {
            rows.Add(new CompatibilityRow(RowTitle, "Pas encore lu", "La page Bench n'a pas encore été ouverte : ouvre-la pour évaluer les tests disponibles sur ce PC.", true));
        }
        else
        {
            List<BenchTestKind> available = BenchTestKinds.All.Where(k => status.PerTest.GetValueOrDefault(k) is null).ToList();
            var details = new List<string>();
            foreach (BenchTestKind kind in BenchTestKinds.All)
            {
                Unavailable? problem = status.PerTest.GetValueOrDefault(kind);
                details.Add(problem is null ? $"{BenchTestKinds.Title(kind)} : disponible" : $"{BenchTestKinds.Title(kind)} : N/D ({problem.Reason})");
            }
            if (status.WorkerProblem is { } worker) details.Add($"Worker de charge : {worker}");
            if (status.ThermalLimit is { } thermal) details.Add($"Arrêt thermique : {thermal}");
            details.Add($"Volumes éligibles au test disque : {status.EligibleVolumes}");
            details.AddRange(status.Notes);

            string summary = available.Count == BenchTestKinds.All.Count
                ? "Tous les tests disponibles"
                : available.Count == 0 ? "Aucun test disponible" : $"{available.Count} test(s) sur {BenchTestKinds.All.Count}";
            rows.Add(new CompatibilityRow(RowTitle, summary + " (expérimental)", string.Join(" ; ", details), available.Count > 0));
        }

        rows.Add(new CompatibilityRow("Dernière session de bench", _refreshed ? (_lastSession is null ? "Aucune" : "Enregistrée") : "Pas encore lu",
            _lastSession ?? (_refreshed ? "Aucune session enregistrée sur ce PC." : "Lecture à l'ouverture du diagnostic."), true));
        return rows;
    }

    public Task RefreshAsync(CancellationToken cancellationToken) => Task.Run(() =>
    {
        IReadOnlyList<BenchSessionResult> sessions = _store.LoadAll(out int unreadable, max: 1);
        cancellationToken.ThrowIfCancellationRequested();
        BenchSessionResult? last = sessions.FirstOrDefault();
        string? text = last is null ? null : $"{last.StartedUtc.ToLocalTime():dd/MM/yyyy HH:mm} : {last.Summary()} (bench v{last.BenchVersion})";
        if (unreadable > 0) text = (text is null ? "" : text + " ; ") + $"{unreadable} fichier(s) illisible(s)";
        _lastSession = text;
        _refreshed = true;
    }, cancellationToken);
}
