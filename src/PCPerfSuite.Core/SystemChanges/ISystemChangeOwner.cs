namespace PCPerfSuite.Core.SystemChanges;

/// <summary>Une modification durable de Windows faite par l'app, décrite par son propriétaire.
/// <paramref name="CanRestore"/> à false dit ce qui ne reviendra pas (fantôme supprimé, partition formatée) : l'app le
/// dit plutôt que de le taire.</summary>
public sealed record SystemChange(string Title, string Detail, bool CanRestore);

public enum SystemRestoreStatus
{
    /// <summary>Rien n'était modifié.</summary>
    NothingToRestore,

    /// <summary>Tout est revenu à l'état d'origine.</summary>
    Restored,

    /// <summary>Une partie seulement : <see cref="SystemRestoreResult.NotRestored"/> dit laquelle.</summary>
    Partial,

    /// <summary>Rien n'a pu être rendu : <see cref="SystemRestoreResult.Message"/> dit pourquoi.</summary>
    Failed,
}

/// <summary>Ce qu'un propriétaire a rendu : ce qui reste modifié, et pourquoi.</summary>
public sealed record SystemRestoreResult(SystemRestoreStatus Status, IReadOnlyList<SystemChange> NotRestored, string? Message)
{
    public static SystemRestoreResult Nothing { get; } = new(SystemRestoreStatus.NothingToRestore, Array.Empty<SystemChange>(), null);
}

/// <summary>
/// Fonction qui modifie Windows durablement (parking des cœurs, animations, plan d'alimentation, pilotes,
/// périphériques, disques, limites par processus, préférence GPU…). Elle garde elle-même l'état d'origine de ce
/// qu'elle a changé, et s'inscrit au <see cref="SystemChangeRegistry"/> : « Tout rétablir » (mode technicien, avant de
/// rendre un PC) passe par là, et le rapport liste ce qui a été rendu et ce qui ne peut pas l'être.
///
/// Best-effort (règle 2) : aucune méthode ne devrait lever ; le registre protège quand même contre celle qui le fait.
/// </summary>
public interface ISystemChangeOwner
{
    /// <summary>Identifiant stable, en kebab-case (« core-parking », « animations »…), unique dans le registre.</summary>
    string Id { get; }

    /// <summary>Nom affiché (« Parking des cœurs »).</summary>
    string Title { get; }

    /// <summary>Vrai tant que quelque chose reste modifié. Rapide : sert à griser « Tout rétablir ».</summary>
    bool HasChanges { get; }

    /// <summary>Chaque modification en cours, restaurable ou non.</summary>
    IReadOnlyList<SystemChange> Describe();

    /// <summary>Remet tout ce qui peut l'être dans l'état d'origine.</summary>
    SystemRestoreResult RestoreAll();
}
