using PCPerfSuite.Core.SystemChanges;

namespace PCPerfSuite.Core.Tests;

/// <summary>Le registre des modifications de Windows : « Tout rétablir » va jusqu'au bout, même quand un propriétaire
/// échoue, et dit ce qui a été rendu.</summary>
public class SystemChangeRegistryTests
{
    private sealed class Owner : ISystemChangeOwner
    {
        private readonly Func<SystemRestoreResult>? _restore;
        private readonly Func<IReadOnlyList<SystemChange>>? _describe;

        public Owner(string id, bool hasChanges = true, Func<SystemRestoreResult>? restore = null,
            Func<IReadOnlyList<SystemChange>>? describe = null)
        {
            Id = id;
            HasChanges = hasChanges;
            _restore = restore;
            _describe = describe;
        }

        public string Id { get; }
        public string Title => $"Titre {Id}";
        public bool HasChanges { get; }
        public int RestoreCalls { get; private set; }

        public IReadOnlyList<SystemChange> Describe()
            => _describe?.Invoke() ?? new[] { new SystemChange($"{Id} modifié", "détail", CanRestore: true) };

        public SystemRestoreResult RestoreAll()
        {
            RestoreCalls++;
            return _restore?.Invoke() ?? new SystemRestoreResult(SystemRestoreStatus.Restored, Array.Empty<SystemChange>(), null);
        }
    }

    [Fact]
    public void Un_identifiant_deja_pris_est_refuse()
    {
        var registry = new SystemChangeRegistry();

        Assert.True(registry.Register(new Owner("core-parking")));
        Assert.False(registry.Register(new Owner("Core-Parking")));
        Assert.Single(registry.Owners);
    }

    [Fact]
    public void Le_retablissement_suit_l_ordre_inverse_des_inscriptions()
    {
        var registry = new SystemChangeRegistry();
        registry.Register(new Owner("animations"));
        registry.Register(new Owner("core-parking"));
        registry.Register(new Owner("pilotes"));

        Assert.Equal(new[] { "pilotes", "core-parking", "animations" }, registry.RestoreAll().Select(r => r.OwnerId));
    }

    [Fact]
    public void Un_proprietaire_sans_modification_n_est_pas_appele()
    {
        var registry = new SystemChangeRegistry();
        var idle = new Owner("animations", hasChanges: false);
        registry.Register(idle);

        SystemRestoreReport report = Assert.Single(registry.RestoreAll());

        Assert.Equal(SystemRestoreStatus.NothingToRestore, report.Result.Status);
        Assert.Equal(0, idle.RestoreCalls);
    }

    [Fact]
    public void Un_proprietaire_qui_leve_est_note_en_echec_et_les_autres_sont_rendus()
    {
        var registry = new SystemChangeRegistry();
        var first = new Owner("animations");
        registry.Register(first);
        registry.Register(new Owner("pilotes", restore: () => throw new UnauthorizedAccessException("accès refusé")));

        IReadOnlyList<SystemRestoreReport> reports = registry.RestoreAll();

        Assert.Equal(2, reports.Count);
        Assert.Equal(SystemRestoreStatus.Failed, reports[0].Result.Status);
        Assert.Contains("accès refusé", reports[0].Result.Message);
        Assert.Equal("pilotes modifié", Assert.Single(reports[0].Result.NotRestored).Title);
        Assert.Equal(SystemRestoreStatus.Restored, reports[1].Result.Status);
        Assert.Equal(1, first.RestoreCalls);
    }

    [Fact]
    public void Une_restauration_partielle_garde_ce_qui_ne_revient_pas()
    {
        var ghost = new SystemChange("Périphérique fantôme supprimé", "USB\\VID_1234", CanRestore: false);
        var registry = new SystemChangeRegistry();
        registry.Register(new Owner("peripheriques",
            restore: () => new SystemRestoreResult(SystemRestoreStatus.Partial, new[] { ghost }, "Un fantôme supprimé ne se restaure pas.")));

        SystemRestoreReport report = Assert.Single(registry.RestoreAll());

        Assert.Equal(SystemRestoreStatus.Partial, report.Result.Status);
        Assert.Equal(ghost, Assert.Single(report.Result.NotRestored));
    }

    [Fact]
    public void Une_description_qui_leve_devient_une_erreur_lisible()
    {
        var registry = new SystemChangeRegistry();
        registry.Register(new Owner("disques", describe: () => throw new InvalidOperationException("WMI muet")));
        registry.Register(new Owner("animations"));

        IReadOnlyList<SystemChangeReport> reports = registry.DescribeAll();

        Assert.Contains("WMI muet", reports[0].Error);
        Assert.Empty(reports[0].Changes);
        Assert.Null(reports[1].Error);
        Assert.Single(reports[1].Changes);
    }
}
