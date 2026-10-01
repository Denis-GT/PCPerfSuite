using PCPerfSuite.Core.Compatibility;

namespace PCPerfSuite.Core.Hardware.Cpu.CoreParking;

/// <summary>Ce que le diagnostic « Cœurs et parking » a lu, publié en une seule affectation.</summary>
/// <param name="Counters">Compteurs PDH ouverts, null si la requête n'a pas pu s'ouvrir (voir <paramref name="CountersProblem"/>).</param>
/// <param name="Values">Réglages du plan actif, null pour un réglage que ce Windows ne connaît pas.</param>
public sealed record CoreParkingDiagnostic(
    CpuTopologyRead Topology,
    CoreCounterAvailability? Counters,
    Unavailable? CountersProblem,
    IReadOnlyList<(CoreParkingSetting Setting, CoreParkingValue? Value)> Values,
    bool VCacheDriverInstalled,
    bool HasChanges);

/// <summary>Quels compteurs PDH par cœur existent sur ce PC.</summary>
public sealed record CoreCounterAvailability(bool Utility, bool ParkingStatus, bool Performance);

/// <summary>
/// Diagnostic « Cœurs et parking » : la topologie lue par les CPU sets (processeurs logiques, cœurs, classes, caches
/// L3), les compteurs par cœur disponibles (dont « Parking Status »), les réglages CPMINCORES et CPMAXCORES (et leurs
/// variantes) du plan actif sur secteur et sur batterie, et la présence du pilote AMD 3D V-Cache. Le nom du plan n'y
/// figure pas : un plan personnalisé peut porter le nom de son propriétaire.
/// </summary>
public sealed class CoreParkingRowProvider : ICompatibilityRowProvider
{
    public const string RowTitle = "Cœurs et parking";

    private readonly CoreParkingService _service;
    private readonly CpuPlatform _platform;
    private CoreParkingDiagnostic? _diagnostic;

    public CoreParkingRowProvider(CoreParkingService service, CpuPlatform platform)
    {
        _service = service;
        _platform = platform;
    }

    public string Title => RowTitle;

    public Task RefreshAsync(CancellationToken cancellationToken)
    {
        CpuTopologyRead topology = CpuTopology.Read();
        cancellationToken.ThrowIfCancellationRequested();

        CoreCounterAvailability? counters = null;
        Unavailable? countersProblem;
        using (var reader = new CoreActivityReader())
        {
            countersProblem = reader.QueryUnavailable;
            if (countersProblem is null) counters = new CoreCounterAvailability(reader.HasUtility, reader.HasParkingStatus, reader.HasPerformance);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var values = CoreParkingCatalog.For(topology.Topology?.IsHybrid ?? _service.IsHybrid)
            .Where(s => !s.IsAdvanced)
            .Select(s => (s, _service.Read(s)))
            .ToList();

        Volatile.Write(ref _diagnostic,
            new CoreParkingDiagnostic(topology, counters, countersProblem, values, AmdVCacheDriver.IsInstalled(), _service.HasChanges));
        return Task.CompletedTask;
    }

    public IReadOnlyList<CompatibilityRow> GetRows() => [BuildRow(Volatile.Read(ref _diagnostic), _platform)];

    public static CompatibilityRow BuildRow(CoreParkingDiagnostic? diagnostic, CpuPlatform platform)
    {
        if (diagnostic is null)
        {
            return new CompatibilityRow(RowTitle, "Pas encore lu", "La topologie et les réglages du parking se lisent à l'ouverture de l'onglet.", true);
        }

        var parts = new List<string>();
        string status;
        bool supported = true;

        if (diagnostic.Topology.Topology is { } topology)
        {
            int classes = topology.EfficiencyClasses.Count;
            status = $"{topology.LogicalProcessors.Count} processeurs logiques, {topology.PhysicalCoreCount} cœurs, "
                     + (classes > 1 ? $"{classes} classes" : "1 classe");

            string caches = string.Join(", ", topology.Clusters.Select(c => c.L3Bytes is { } l3 ? FormatBytes(l3) : "taille inconnue"));
            parts.Add($"CPU sets lus : SMT {(topology.HasSmt ? "oui" : "non")}, {topology.Clusters.Count} groupe(s) de cache L3 ({caches})"
                      + (topology.HasMixedL3Sizes ? ", tailles différentes (3D V-Cache)." : "."));

            CoreTopologyAssessment assessment = CoreTopologySupport.Assess(platform.Vendor, platform.Family, platform.Model, topology);
            if (!assessment.IsVerified) parts.Add($"Expérimental : {string.Join(" ; ", assessment.ExperimentalReasons)}.");
        }
        else
        {
            status = "N/D";
            supported = false;
            parts.Add($"N/D : {diagnostic.Topology.Problem?.Reason ?? "topologie illisible"}.");
        }

        parts.Add(diagnostic.Counters switch
        {
            { } counters => $"Compteurs par cœur : charge {Presence(counters.Utility)}, Parking Status {Presence(counters.ParkingStatus)}"
                            + (counters.ParkingStatus ? "" : " (état parqué lu dans les CPU sets)")
                            + $", performance {Presence(counters.Performance)}.",
            null => $"Compteurs par cœur : N/D ({diagnostic.CountersProblem?.Reason ?? "requête PDH impossible"}).",
        });

        string values = string.Join(", ", diagnostic.Values.Select(v => v.Value is { } value
            ? $"{v.Setting.Alias} {value.Ac} %/{value.Dc} %"
            : $"{v.Setting.Alias} N/D"));
        parts.Add($"Plan actif (secteur/batterie) : {values}.");

        parts.Add($"Pilote AMD 3D V-Cache (amd3dvcache) : {(diagnostic.VCacheDriverInstalled ? "installé" : "absent")}.");
        parts.Add(diagnostic.HasChanges ? "Parking modifié par PCPerfSuite (origine notée)." : "Parking jamais modifié par PCPerfSuite.");

        return new CompatibilityRow(RowTitle, status, string.Join(" ", parts), supported);
    }

    private static string Presence(bool present) => present ? "disponible" : "absent";

    private static string FormatBytes(long bytes)
        => bytes >= 1024 * 1024 ? $"{bytes / (1024.0 * 1024):0.#} Mo" : $"{bytes / 1024.0:0} Ko";
}
