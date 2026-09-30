namespace PCPerfSuite.Core.SystemChanges;

/// <summary>Modifications en cours d'un propriétaire, ou pourquoi elles n'ont pas pu être lues.</summary>
public sealed record SystemChangeReport(string OwnerId, string OwnerTitle, IReadOnlyList<SystemChange> Changes, string? Error);

/// <summary>Résultat de « Tout rétablir » pour un propriétaire.</summary>
public sealed record SystemRestoreReport(string OwnerId, string OwnerTitle, SystemRestoreResult Result);

/// <summary>
/// Registre des fonctions qui modifient Windows durablement (<see cref="ISystemChangeOwner"/>). Il ne garde aucun état
/// d'origine lui-même : il sait seulement qui interroger, et fait en sorte qu'un propriétaire qui échoue n'empêche
/// jamais les autres de rendre ce qu'ils ont changé. Utilisable depuis n'importe quel thread.
/// </summary>
public sealed class SystemChangeRegistry
{
    private readonly object _gate = new();
    private readonly List<ISystemChangeOwner> _owners = new();

    /// <summary>Inscrit un propriétaire. False si son Id est déjà pris : deux propriétaires qui se disputent la même
    /// modification rendraient l'origine deux fois.</summary>
    public bool Register(ISystemChangeOwner owner)
    {
        lock (_gate)
        {
            string id = owner.Id;
            if (_owners.Any(o => string.Equals(o.Id, id, StringComparison.OrdinalIgnoreCase))) return false;
            _owners.Add(owner);
            return true;
        }
    }

    /// <summary>Propriétaires dans l'ordre d'inscription.</summary>
    public IReadOnlyList<ISystemChangeOwner> Owners
    {
        get { lock (_gate) return _owners.ToArray(); }
    }

    public IReadOnlyList<SystemChangeReport> DescribeAll()
        => Owners.Select(owner =>
        {
            try
            {
                return new SystemChangeReport(owner.Id, owner.Title, owner.Describe() ?? Array.Empty<SystemChange>(), null);
            }
            catch (Exception ex)
            {
                return new SystemChangeReport(SafeId(owner), SafeTitle(owner), Array.Empty<SystemChange>(),
                    $"Lecture impossible ({ex.GetType().Name} : {ex.Message}).");
            }
        }).ToList();

    /// <summary>
    /// Rend l'origine de tout ce qui peut l'être, dans l'ordre inverse des inscriptions (comme on défait des
    /// changements empilés), sans appeler un propriétaire qui n'a rien à rendre. Un propriétaire qui lève est noté
    /// en échec avec sa raison, et le parcours continue : la liste rendue est toujours complète.
    /// </summary>
    public IReadOnlyList<SystemRestoreReport> RestoreAll()
    {
        IReadOnlyList<ISystemChangeOwner> owners = Owners;
        var reports = new List<SystemRestoreReport>(owners.Count);

        for (int i = owners.Count - 1; i >= 0; i--)
        {
            ISystemChangeOwner owner = owners[i];
            SystemRestoreResult result;
            try
            {
                result = owner.HasChanges ? owner.RestoreAll() ?? Failed("Aucun résultat rendu.") : SystemRestoreResult.Nothing;
            }
            catch (Exception ex)
            {
                result = Failed($"Rétablissement impossible ({ex.GetType().Name} : {ex.Message}).");
            }

            reports.Add(new SystemRestoreReport(SafeId(owner), SafeTitle(owner), result));
        }

        return reports;
    }

    private static SystemRestoreResult Failed(string message)
        => new(SystemRestoreStatus.Failed, Array.Empty<SystemChange>(), message);

    private static string SafeId(ISystemChangeOwner owner)
    {
        try { return owner.Id; }
        catch { return owner.GetType().Name; }
    }

    private static string SafeTitle(ISystemChangeOwner owner)
    {
        try { return owner.Title; }
        catch { return owner.GetType().Name; }
    }
}
