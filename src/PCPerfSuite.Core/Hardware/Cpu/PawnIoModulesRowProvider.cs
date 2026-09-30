using PCPerfSuite.Core.Compatibility;

namespace PCPerfSuite.Core.Hardware.Cpu;

/// <summary>
/// Diagnostic « Modules PawnIO » : pour chaque module chargé par l'app, sa provenance (livré avec PCPerfSuite ou pris
/// dans LibreHardwareMonitorLib), sa version et les fonctions qu'il expose. C'est ce qui dit, dans un rapport de bug,
/// pourquoi PL1/PL2 sont en lecture seule : un IntelMSR sans ioctl_write_msr. Le module livré qui n'a pas été chargé
/// (processeur AMD, pilote absent) est décrit aussi, d'après son fichier.
/// </summary>
public sealed class PawnIoModulesRowProvider : ICompatibilityRowProvider
{
    public const string RowTitle = "Modules PawnIO";

    /// <summary>Modules livrés lus sur le disque par <see cref="RefreshAsync"/> ; vide tant que non lus.</summary>
    private IReadOnlyList<PawnIoModuleInfo> _shipped = Array.Empty<PawnIoModuleInfo>();

    public string Title => RowTitle;

    public Task RefreshAsync(CancellationToken cancellationToken)
    {
        var shipped = new List<PawnIoModuleInfo>();
        foreach (string name in ShippedPawnIoModules.ExpectedSha256.Keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ShippedPawnIoModules.TryRead(name) is { } candidate) shipped.Add(candidate.Info);
        }

        Volatile.Write(ref _shipped, shipped);
        return Task.CompletedTask;
    }

    public IReadOnlyList<CompatibilityRow> GetRows()
        => BuildRows(PawnIoDriver.LoadedModules, Volatile.Read(ref _shipped), PawnIoDriver.IsInstalled, PawnIoDriver.UnavailableReason);

    /// <summary>Les lignes, à partir de ce qui est déjà lu : isolé ici pour être testé sans pilote.</summary>
    public static IReadOnlyList<CompatibilityRow> BuildRows(
        IReadOnlyList<PawnIoModuleInfo> loaded, IReadOnlyList<PawnIoModuleInfo> shipped, bool driverReady, string? driverReason)
    {
        var rows = new List<CompatibilityRow>();

        foreach (PawnIoModuleInfo module in loaded)
        {
            bool readOnlyMsr = module.Name == "IntelMSR" && !module.Supports(IntelPowerLimitBackend.WriteFunction);
            string status = readOnlyMsr ? "Lecture seule" : $"{module.Functions.Count} fonction(s)";
            var detail = new List<string>
            {
                $"Chargé : {module.SourceLabel}.",
                $"Fonctions : {(module.Functions.Count == 0 ? "aucune relevée" : string.Join(", ", module.Functions))}.",
            };
            if (readOnlyMsr) detail.Add(IntelPowerLimitBackend.DescribeModuleWriteRefusal(module)!);
            else if (module.Note is { } note) detail.Add(note);
            detail.Add($"SHA-256 {module.Sha256}.");

            rows.Add(new CompatibilityRow($"{RowTitle} · {module.Name}", status, string.Join(" ", detail), !readOnlyMsr));
        }

        foreach (PawnIoModuleInfo module in shipped.Where(s => loaded.All(l => l.Name != s.Name)))
        {
            string why = driverReady
                ? "non chargé : ce processeur n'en a pas besoin"
                : $"non chargé : {driverReason ?? "pilote PawnIO indisponible"}";
            rows.Add(new CompatibilityRow($"{RowTitle} · {module.Name}", "Livré, non chargé",
                $"{module.SourceLabel}, {why}. Fonctions : {string.Join(", ", module.Functions)}." +
                (module.IsOfficialCopy == false ? " Ce fichier ne correspond pas à celui de la release." : ""), true));
        }

        if (rows.Count == 0)
        {
            rows.Add(new CompatibilityRow(RowTitle, "Aucun module chargé",
                driverReady
                    ? "Le pilote PawnIO est prêt, mais aucun réglage bas niveau n'a chargé de module sur ce PC."
                    : driverReason ?? "Pilote PawnIO indisponible.", driverReady));
        }

        return rows;
    }
}
