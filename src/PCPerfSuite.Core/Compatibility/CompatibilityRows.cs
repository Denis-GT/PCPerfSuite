namespace PCPerfSuite.Core.Compatibility;

/// <summary>Un fournisseur qui a levé : de quoi le journaliser une fois, en plus de sa ligne « Lecture impossible ».</summary>
public sealed record CompatibilityProviderFailure(string Title, Exception Error);

/// <summary>Lignes réunies depuis les fournisseurs, et ceux qui ont échoué.</summary>
public sealed record CompatibilityRowsResult(IReadOnlyList<CompatibilityRow> Rows, IReadOnlyList<CompatibilityProviderFailure> Failures);

/// <summary>Réunion des lignes des fournisseurs, isolée ici pour être testée sans matériel ni interface.</summary>
public static class CompatibilityRows
{
    public const string ReadFailedStatus = "Lecture impossible";

    /// <summary>Lignes de chaque fournisseur, dans l'ordre. Un fournisseur qui lève (même en donnant son titre) ne
    /// fait pas disparaître sa rubrique : elle devient une ligne « Lecture impossible » qui dit pourquoi.</summary>
    public static CompatibilityRowsResult Collect(IEnumerable<ICompatibilityRowProvider> providers)
    {
        var rows = new List<CompatibilityRow>();
        var failures = new List<CompatibilityProviderFailure>();

        foreach (ICompatibilityRowProvider provider in providers)
        {
            try
            {
                foreach (CompatibilityRow? row in provider.GetRows() ?? Array.Empty<CompatibilityRow>())
                {
                    if (row is not null) rows.Add(row);
                }
            }
            catch (Exception ex)
            {
                string title = SafeTitle(provider);
                failures.Add(new CompatibilityProviderFailure(title, ex));
                rows.Add(new CompatibilityRow(title, ReadFailedStatus,
                    $"Cette rubrique n'a pas pu être lue sur ce PC ({ex.GetType().Name} : {ex.Message}). "
                    + "Merci de signaler ce PC avec ce rapport.", false));
            }
        }

        return new CompatibilityRowsResult(rows, failures);
    }

    /// <summary>Lectures lentes de tous les fournisseurs, hors du thread d'interface et en parallèle : une lecture qui
    /// traîne ou qui lève ne retient pas les autres. Une erreur ici n'est pas remontée : c'est
    /// <see cref="Collect"/> qui la verra, ou la ligne qui dira ce qui manque.</summary>
    public static Task RefreshAllAsync(IEnumerable<ICompatibilityRowProvider> providers, CancellationToken cancellationToken)
        => Task.WhenAll(providers.Select(provider => Task.Run(async () =>
        {
            try { await provider.RefreshAsync(cancellationToken).ConfigureAwait(false); }
            catch { /* best-effort : la ligne du fournisseur dira ce qui manque */ }
        }, CancellationToken.None)));

    private static string SafeTitle(ICompatibilityRowProvider provider)
    {
        try
        {
            string? title = provider.Title;
            if (!string.IsNullOrWhiteSpace(title)) return title;
        }
        catch { /* le titre lui-même peut lever : on retombe sur le nom du type */ }

        return provider.GetType().Name;
    }
}
