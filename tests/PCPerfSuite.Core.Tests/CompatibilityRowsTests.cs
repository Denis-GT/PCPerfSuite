using PCPerfSuite.Core.Compatibility;

namespace PCPerfSuite.Core.Tests;

/// <summary>Les lignes que les fonctions ajoutent au diagnostic : un fournisseur qui lève ne casse ni le diagnostic ni
/// les autres rubriques (règle 2), et sa ligne dit pourquoi elle manque (règle 3).</summary>
public class CompatibilityRowsTests
{
    private sealed class Provider : ICompatibilityRowProvider
    {
        private readonly Func<IReadOnlyList<CompatibilityRow>> _rows;
        private readonly Func<string>? _title;
        private readonly Func<Task>? _refresh;

        public Provider(string title, Func<IReadOnlyList<CompatibilityRow>> rows, Func<Task>? refresh = null, Func<string>? titleOverride = null)
        {
            _title = titleOverride ?? (() => title);
            _rows = rows;
            _refresh = refresh;
        }

        public bool Refreshed { get; private set; }

        public string Title => _title!();

        public IReadOnlyList<CompatibilityRow> GetRows() => _rows();

        public async Task RefreshAsync(CancellationToken cancellationToken)
        {
            Refreshed = true;
            if (_refresh is not null) await _refresh();
        }
    }

    private static CompatibilityRow Row(string title) => new(title, "OK", "détail", true);

    [Fact]
    public void Les_lignes_suivent_l_ordre_des_fournisseurs_puis_le_leur()
    {
        var providers = new[]
        {
            new Provider("Écrans", () => new[] { Row("Écran 1"), Row("Écran 2") }),
            new Provider("Boîte à outils", () => new[] { Row("Boîte à outils") }),
        };

        CompatibilityRowsResult result = CompatibilityRows.Collect(providers);

        Assert.Equal(new[] { "Écran 1", "Écran 2", "Boîte à outils" }, result.Rows.Select(r => r.Title));
        Assert.Empty(result.Failures);
    }

    [Fact]
    public void Un_fournisseur_qui_leve_donne_une_ligne_lecture_impossible_et_les_autres_restent()
    {
        var providers = new[]
        {
            new Provider("Écrans", () => throw new InvalidOperationException("QueryDisplayConfig a échoué")),
            new Provider("Boîte à outils", () => new[] { Row("Boîte à outils") }),
        };

        CompatibilityRowsResult result = CompatibilityRows.Collect(providers);

        CompatibilityRow failed = result.Rows[0];
        Assert.Equal("Écrans", failed.Title);
        Assert.Equal(CompatibilityRows.ReadFailedStatus, failed.Status);
        Assert.False(failed.IsSupported);
        Assert.Contains("InvalidOperationException", failed.Detail);
        Assert.Contains("QueryDisplayConfig a échoué", failed.Detail);
        Assert.Equal("Boîte à outils", result.Rows[1].Title);
        Assert.Equal("Écrans", Assert.Single(result.Failures).Title);
    }

    [Fact]
    public void Un_titre_qui_leve_est_remplace_par_le_nom_du_fournisseur()
    {
        var provider = new Provider("", () => throw new InvalidOperationException(), titleOverride: () => throw new NotSupportedException());

        CompatibilityRow failed = Assert.Single(CompatibilityRows.Collect(new[] { provider }).Rows);

        Assert.Equal(nameof(Provider), failed.Title);
    }

    [Fact]
    public void Une_ligne_nulle_ou_une_liste_nulle_est_ignoree()
    {
        var providers = new[]
        {
            new Provider("A", () => new CompatibilityRow[] { null!, Row("A") }),
            new Provider("B", () => null!),
        };

        Assert.Equal(new[] { "A" }, CompatibilityRows.Collect(providers).Rows.Select(r => r.Title));
    }

    [Fact]
    public async Task Une_lecture_lente_qui_leve_n_empeche_pas_les_autres()
    {
        var failing = new Provider("A", () => new[] { Row("A") }, refresh: () => throw new TimeoutException());
        var working = new Provider("B", () => new[] { Row("B") });

        await CompatibilityRows.RefreshAllAsync(new[] { failing, working }, CancellationToken.None);

        Assert.True(failing.Refreshed);
        Assert.True(working.Refreshed);
    }
}
