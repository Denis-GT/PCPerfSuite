using PCPerfSuite.Core.Benchmark.Disk;
using PCPerfSuite.Core.Safety;
using PCPerfSuite.Core.Safety.Events;

namespace PCPerfSuite.Core.Benchmark.Session;

/// <summary>
/// Reprise au lancement du bench (#10) : une ligne « bench » restée en cours dit qu'un test a été interrompu. On le note
/// pour le diagnostic (« bench interrompu » si l'app seule s'est arrêtée, « arrêt brutal pendant le bench (…) » si
/// Windows est tombé), et on supprime le fichier du test disque sur le volume noté dans la ligne (jamais un chemin dans
/// la note). Aucun réglage n'est à rendre : le bench n'en change pas.
/// </summary>
public sealed class BenchRecoveryHandler : IStartupRecoveryHandler
{
    public const string VolumeKey = "volume";
    public const string SystemVolumeKey = "systeme";
    public const string FileSizeKey = "taille-mo";

    private readonly Func<string, bool, (bool Ok, string? Error)> _deleteTestFile;

    public BenchRecoveryHandler(Func<string, bool, (bool Ok, string? Error)>? deleteTestFile = null)
    {
        _deleteTestFile = deleteTestFile ?? ((letter, isSystem) =>
        {
            bool ok = DiskTestFile.TryDeleteOnVolume(letter, isSystem, out string? error);
            return (ok, error);
        });
    }

    public string Id => BenchVersion.Requester;

    public RecoveryStage Stage => RecoveryStage.Bench;

    public IReadOnlyCollection<string> Components { get; } = [BenchVersion.Requester];

    public string? Handle(IReadOnlyList<RecoveredEntry> entries)
    {
        if (entries.Count == 0) return null;
        var notes = new List<string>();
        var volumesSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (RecoveredEntry recovered in entries)
        {
            BenchTestKind? kind = BenchTestKinds.Parse(recovered.Entry.Action);
            string test = kind is { } k ? BenchTestKinds.Title(k).ToLowerInvariant() : recovered.Entry.Action;
            notes.Add(IsBrutal(recovered.Qualification.Kind)
                ? $"arrêt brutal pendant le bench ({recovered.Qualification.Summary}) : {test}"
                : $"bench interrompu : {test}");

            if (kind != BenchTestKind.Disk) continue;
            if (!recovered.Entry.Values.TryGetValue(VolumeKey, out string? letter) || string.IsNullOrWhiteSpace(letter)) continue;
            string normalized = letter.Trim().TrimEnd(':').ToUpperInvariant() + ":";
            if (normalized.Length != 2 || !char.IsAsciiLetter(normalized[0]) || !volumesSeen.Add(normalized)) continue;
            bool isSystem = recovered.Entry.Values.TryGetValue(SystemVolumeKey, out string? system) && system == "oui";

            (bool ok, string? error) = _deleteTestFile(normalized, isSystem);
            notes.Add(ok ? "fichier de test disque supprimé" : $"fichier de test disque non supprimé ({error})");
        }

        return string.Join(" ; ", notes.Distinct());
    }

    /// <summary>Windows est tombé (ou on ne sait pas), par opposition à l'app seule arrêtée ou un arrêt propre.</summary>
    public static bool IsBrutal(IncidentQualificationKind kind) => kind is IncidentQualificationKind.PowerLoss
        or IncidentQualificationKind.BlueScreen or IncidentQualificationKind.ForcedShutdown
        or IncidentQualificationKind.UnexpectedShutdown or IncidentQualificationKind.Unknown;
}
