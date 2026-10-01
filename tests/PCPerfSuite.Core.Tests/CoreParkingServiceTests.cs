using PCPerfSuite.Core.Hardware.Cpu.CoreParking;
using PCPerfSuite.Core.PowerSettings;
using PCPerfSuite.Core.SystemChanges;

namespace PCPerfSuite.Core.Tests;

/// <summary>Plans d'alimentation en mémoire : valeurs par plan et par réglage, plan actif, refus d'écriture, et réglage
/// « piloté par le fabricant » qui ne retient pas ce qu'on écrit.</summary>
internal sealed class FakePowerPlan : IPowerPlanValues
{
    public static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    public static readonly Guid Performance = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");

    private readonly Dictionary<(Guid Scheme, Guid Setting), (uint Ac, uint Dc)> _values = new();

    public Guid? Active { get; set; } = Balanced;
    public bool RefuseWrites { get; set; }
    public HashSet<Guid> PinnedByOem { get; } = new();
    public HashSet<Guid> DeletedSchemes { get; } = new();
    public int Activations { get; private set; }
    public int WriteCalls { get; private set; }

    public FakePowerPlan Set(Guid scheme, CoreParkingSetting setting, uint ac, uint dc)
    {
        _values[(scheme, setting.Guid)] = (ac, dc);
        return this;
    }

    public (uint Ac, uint Dc) Get(Guid scheme, CoreParkingSetting setting) => _values[(scheme, setting.Guid)];

    public Guid? ActiveScheme() => Active;

    public string? FriendlyName(Guid scheme)
        => DeletedSchemes.Contains(scheme) ? null : scheme == Balanced ? "Équilibré" : "Performances élevées";

    public bool TryRead(Guid scheme, Guid subGroup, Guid setting, out uint ac, out uint dc)
    {
        ac = dc = 0;
        if (DeletedSchemes.Contains(scheme) || !_values.TryGetValue((scheme, setting), out (uint Ac, uint Dc) value)) return false;
        (ac, dc) = value;
        return true;
    }

    public bool TryWrite(Guid scheme, Guid subGroup, IReadOnlyList<PowerValueWrite> writes)
    {
        WriteCalls++;
        if (RefuseWrites) return false;

        foreach (PowerValueWrite write in writes)
        {
            if (PinnedByOem.Contains(write.Setting)) continue;
            _values[(scheme, write.Setting)] = (write.Ac, write.Dc);
        }

        if (scheme == Active) Activations++;
        return true;
    }
}

internal sealed class FakeOriginStore : ICoreParkingOriginStore
{
    public Dictionary<string, uint> Stored { get; } = new();
    public Guid? StoredScheme { get; set; }
    public bool FailRemember { get; set; }

    public IReadOnlyDictionary<string, uint> Values => new Dictionary<string, uint>(Stored);
    public Guid? Scheme => StoredScheme;

    public bool Remember(Guid scheme, IReadOnlyDictionary<string, uint> values)
    {
        if (FailRemember) return false;
        foreach ((string key, uint value) in values) Stored.TryAdd(key, value);
        StoredScheme ??= scheme;
        return true;
    }

    public void Forget(IReadOnlyCollection<string> keys, bool forgetScheme)
    {
        foreach (string key in keys) Stored.Remove(key);
        if (forgetScheme) StoredScheme = null;
    }
}

/// <summary>Le service de parking : origine notée avant la première écriture et rendue par RestoreAll, reprise de ce
/// que le tweak avait noté, plan modifié retenu, relecture.</summary>
public class CoreParkingServiceTests
{
    private static readonly CoreParkingSetting Min = CoreParkingCatalog.MinCores;
    private static readonly CoreParkingSetting MinPerf = CoreParkingCatalog.MinCoresPerformance;
    private static readonly CoreParkingSetting Max = CoreParkingCatalog.MaxCores;

    private static string Key(CoreParkingSetting setting) => CoreParkingCatalog.OriginKey(setting);

    /// <summary>Le PC de test des agents (i5-13500T) : CPMINCORES à 4 %, CPMINCORES1 à 0 %.</summary>
    private static FakePowerPlan HybridPlan() => new FakePowerPlan()
        .Set(FakePowerPlan.Balanced, Min, 4, 4)
        .Set(FakePowerPlan.Balanced, MinPerf, 0, 0)
        .Set(FakePowerPlan.Balanced, Max, 100, 100)
        .Set(FakePowerPlan.Balanced, CoreParkingCatalog.MaxCoresPerformance, 100, 100)
        .Set(FakePowerPlan.Performance, Min, 100, 100);

    private static CoreParkingTarget Target(CoreParkingSetting setting, uint ac, uint dc) => new(setting, new CoreParkingValue(ac, dc));

    [Fact]
    public void La_cle_d_origine_reprend_le_format_du_tweak()
    {
        Assert.Equal($"{PowerSubGroups.Processor}/{PowerSubGroups.ProcessorMinCoreParkingState}", Key(Min));
    }

    [Fact]
    public void L_origine_est_notee_avant_la_premiere_ecriture_et_jamais_ecrasee()
    {
        FakePowerPlan plan = HybridPlan();
        var origins = new FakeOriginStore();
        var service = new CoreParkingService(isHybrid: true, hasBattery: true, plan, origins);

        Assert.True(service.Write([Target(Min, 100, 100), Target(MinPerf, 100, 100)]).Succeeded);
        Assert.True(service.Write([Target(Min, 50, 50)]).Succeeded);

        Assert.Equal(new CoreParkingValue(4, 4), service.Origin(Min));
        Assert.Equal(new CoreParkingValue(0, 0), service.Origin(MinPerf));
        Assert.Equal(FakePowerPlan.Balanced, origins.StoredScheme);
        Assert.True(service.HasChanges);
    }

    [Fact]
    public void Sans_origine_enregistree_rien_n_est_ecrit()
    {
        FakePowerPlan plan = HybridPlan();
        var service = new CoreParkingService(true, true, plan, new FakeOriginStore { FailRemember = true });

        CoreParkingWriteResult result = service.Write([Target(Min, 100, 100)]);

        Assert.False(result.Succeeded);
        Assert.Contains("origine", result.Error);
        Assert.Equal((4u, 4u), plan.Get(FakePowerPlan.Balanced, Min));
        Assert.Equal(0, plan.WriteCalls);
    }

    [Fact]
    public void Une_rafale_d_ecritures_ne_reactive_le_plan_qu_une_fois()
    {
        FakePowerPlan plan = HybridPlan();
        var service = new CoreParkingService(true, true, plan, new FakeOriginStore());

        service.Write([Target(Min, 100, 100), Target(MinPerf, 100, 100), Target(Max, 100, 100)]);

        Assert.Equal(1, plan.Activations);
    }

    [Fact]
    public void RestoreAll_rend_l_origine_et_oublie_tout()
    {
        FakePowerPlan plan = HybridPlan();
        var origins = new FakeOriginStore();
        var service = new CoreParkingService(true, true, plan, origins);
        service.Write([Target(Min, 100, 100), Target(MinPerf, 100, 80)]);

        SystemRestoreResult result = service.RestoreAll();

        Assert.Equal(SystemRestoreStatus.Restored, result.Status);
        Assert.Equal((4u, 4u), plan.Get(FakePowerPlan.Balanced, Min));
        Assert.Equal((0u, 0u), plan.Get(FakePowerPlan.Balanced, MinPerf));
        Assert.False(service.HasChanges);
        Assert.Empty(origins.Stored);
        Assert.Null(origins.StoredScheme);
        Assert.Equal(SystemRestoreStatus.NothingToRestore, service.RestoreAll().Status);
    }

    [Fact]
    public void Describe_dit_le_reglage_le_plan_l_origine_et_la_valeur_actuelle()
    {
        var service = new CoreParkingService(true, hasBattery: true, HybridPlan(), new FakeOriginStore());
        service.Write([Target(MinPerf, 100, 0)]);

        SystemChange change = Assert.Single(service.Describe());

        Assert.Contains("CPMINCORES1", change.Title);
        Assert.Contains("Équilibré", change.Detail);
        Assert.Contains("sur secteur 100 % (origine 0 %)", change.Detail);
        Assert.Contains("sur batterie 0 % (origine 0 %)", change.Detail);
        Assert.True(change.CanRestore);
    }

    [Fact]
    public void Sans_batterie_la_valeur_secteur_est_ecrite_aux_deux()
    {
        FakePowerPlan plan = HybridPlan();
        var service = new CoreParkingService(true, hasBattery: false, plan, new FakeOriginStore());

        service.Write([Target(Min, 100, 30)]);

        Assert.Equal((100u, 100u), plan.Get(FakePowerPlan.Balanced, Min));
    }

    [Fact]
    public void L_origine_notee_par_l_ancien_tweak_est_reprise()
    {
        FakePowerPlan plan = HybridPlan().Set(FakePowerPlan.Balanced, Min, 100, 100);
        var origins = new FakeOriginStore();
        origins.Stored[$"{Key(Min)}/ac"] = 10;
        origins.Stored[$"{Key(Min)}/dc"] = 5;
        var service = new CoreParkingService(true, true, plan, origins);

        Assert.True(service.HasChanges);
        Assert.Equal(new CoreParkingValue(10, 5), service.Origin(Min));

        service.RestoreAll();

        Assert.Equal((10u, 5u), plan.Get(FakePowerPlan.Balanced, Min));
    }

    [Fact]
    public void La_cle_sans_suffixe_des_premieres_versions_sert_de_repli_aux_deux()
    {
        var origins = new FakeOriginStore();
        origins.Stored[Key(Min)] = 5;
        var service = new CoreParkingService(true, true, HybridPlan(), origins);

        Assert.Equal(new CoreParkingValue(5, 5), service.Origin(Min));

        service.RestoreAll();
        Assert.Empty(origins.Stored);
    }

    [Fact]
    public void Revenir_a_la_main_sur_l_origine_n_est_plus_une_modification()
    {
        var service = new CoreParkingService(true, true, HybridPlan(), new FakeOriginStore());
        service.Write([Target(Min, 100, 100)]);

        service.Write([Target(Min, 4, 4)]);

        Assert.False(service.HasChanges);
        Assert.Empty(service.Describe());
    }

    [Fact]
    public void Une_ecriture_refusee_le_dit_et_ne_laisse_pas_de_modification_fantome()
    {
        FakePowerPlan plan = HybridPlan();
        plan.RefuseWrites = true;
        var service = new CoreParkingService(true, true, plan, new FakeOriginStore());

        CoreParkingWriteResult result = service.Write([Target(Min, 100, 100)]);

        Assert.False(result.Succeeded);
        Assert.Contains("administrateur", result.Error);
        Assert.False(service.HasChanges);
    }

    [Fact]
    public void La_relecture_montre_ce_que_Windows_a_retenu()
    {
        FakePowerPlan plan = HybridPlan();
        plan.PinnedByOem.Add(MinPerf.Guid);
        var service = new CoreParkingService(true, true, plan, new FakeOriginStore());

        CoreParkingApplied applied = Assert.Single(service.Write([Target(MinPerf, 100, 100)]).Applied);

        Assert.False(applied.Matches);
        Assert.Equal(new CoreParkingValue(0, 0), applied.Applied);
    }

    [Fact]
    public void Ecrire_dans_un_autre_plan_rend_d_abord_au_precedent_son_origine()
    {
        FakePowerPlan plan = HybridPlan();
        var origins = new FakeOriginStore();
        var service = new CoreParkingService(true, true, plan, origins);
        service.Write([Target(Min, 100, 100)]);

        plan.Active = FakePowerPlan.Performance;
        CoreParkingWriteResult result = service.Write([Target(Min, 50, 50)]);

        Assert.True(result.Succeeded);
        Assert.Contains("Équilibré", result.Note);
        Assert.Equal((4u, 4u), plan.Get(FakePowerPlan.Balanced, Min));
        Assert.Equal(FakePowerPlan.Performance, origins.StoredScheme);
        Assert.Equal(new CoreParkingValue(100, 100), service.Origin(Min));
    }

    [Fact]
    public void RestoreAll_ecrit_dans_le_plan_modifie_meme_s_il_n_est_plus_actif()
    {
        FakePowerPlan plan = HybridPlan();
        var service = new CoreParkingService(true, true, plan, new FakeOriginStore());
        service.Write([Target(Min, 100, 100)]);
        plan.Active = FakePowerPlan.Performance;

        Assert.Equal(SystemRestoreStatus.Restored, service.RestoreAll().Status);

        Assert.Equal((4u, 4u), plan.Get(FakePowerPlan.Balanced, Min));
        Assert.Equal((100u, 100u), plan.Get(FakePowerPlan.Performance, Min));
    }

    [Fact]
    public void Un_plan_supprime_depuis_n_a_plus_rien_a_rendre()
    {
        FakePowerPlan plan = HybridPlan();
        var origins = new FakeOriginStore();
        var service = new CoreParkingService(true, true, plan, origins);
        service.Write([Target(Min, 100, 100)]);

        plan.DeletedSchemes.Add(FakePowerPlan.Balanced);
        Assert.False(Assert.Single(service.Describe()).CanRestore);

        Assert.Equal(SystemRestoreStatus.NothingToRestore, service.RestoreAll().Status);
        Assert.False(service.HasChanges);
    }

    [Fact]
    public void Une_restauration_refusee_garde_l_origine_et_dit_ce_qui_reste()
    {
        FakePowerPlan plan = HybridPlan();
        var service = new CoreParkingService(true, true, plan, new FakeOriginStore());
        service.Write([Target(Min, 100, 100)]);
        plan.RefuseWrites = true;

        SystemRestoreResult result = service.RestoreAll();

        Assert.Equal(SystemRestoreStatus.Failed, result.Status);
        Assert.Single(result.NotRestored);
        Assert.True(service.HasChanges);
    }

    [Fact]
    public void Le_repli_du_tweak_ne_devient_pas_une_modification()
    {
        // Tweak activé hors de l'app (ou par une version sans origine) : décocher pose 5 %, sans le noter.
        FakePowerPlan plan = HybridPlan().Set(FakePowerPlan.Balanced, Min, 100, 100);
        var service = new CoreParkingService(true, true, plan, new FakeOriginStore());

        SystemRestoreResult result = service.Restore([Min, MinPerf], fallbackMinCores: 5);

        Assert.Equal(SystemRestoreStatus.Restored, result.Status);
        Assert.Equal((5u, 5u), plan.Get(FakePowerPlan.Balanced, Min));
        Assert.Equal((0u, 0u), plan.Get(FakePowerPlan.Balanced, MinPerf));
        Assert.False(service.HasChanges);
    }

    [Fact]
    public void Un_choix_absent_de_la_liste_est_refuse()
    {
        FakePowerPlan plan = HybridPlan().Set(FakePowerPlan.Balanced, CoreParkingCatalog.SchedulingPolicy, 5, 5);
        var service = new CoreParkingService(true, true, plan, new FakeOriginStore());

        CoreParkingWriteResult result = service.Write([Target(CoreParkingCatalog.SchedulingPolicy, 9, 9)]);

        Assert.False(result.Succeeded);
        Assert.Equal((5u, 5u), plan.Get(FakePowerPlan.Balanced, CoreParkingCatalog.SchedulingPolicy));
        Assert.False(service.HasChanges);
    }

    [Fact]
    public void Un_processeur_non_hybride_n_expose_que_le_plancher_et_le_plafond()
    {
        var service = new CoreParkingService(isHybrid: false, hasBattery: false, HybridPlan(), new FakeOriginStore());

        Assert.Equal(["CPMINCORES", "CPMAXCORES"], service.Settings.Select(s => s.Alias));
    }

    [Fact]
    public void Le_service_s_inscrit_au_registre_sous_son_identifiant()
    {
        var registry = new SystemChangeRegistry();
        var service = new CoreParkingService(true, true, HybridPlan(), new FakeOriginStore());

        Assert.True(registry.Register(service));
        Assert.Equal("core-parking", Assert.Single(registry.Owners).Id);

        service.Write([Target(Min, 100, 100)]);
        SystemRestoreReport report = Assert.Single(registry.RestoreAll());
        Assert.Equal(SystemRestoreStatus.Restored, report.Result.Status);
    }
}
