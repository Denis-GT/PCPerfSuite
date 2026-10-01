using PCPerfSuite.Core.Hardware.Cpu;
using PCPerfSuite.Core.Profiles;
using PCPerfSuite.Core.SystemChanges;
using static PCPerfSuite.Core.Tests.ProfileGroupTestData;

namespace PCPerfSuite.Core.Tests;

/// <summary>Origines notées en mémoire, par plan.</summary>
internal sealed class FakePlanOrigins : IPowerPlanOriginStore
{
    public Dictionary<Guid, Dictionary<string, uint>> Stored { get; } = new();
    public bool FailRemember { get; set; }

    public IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, uint>> All
        => Stored.Where(s => s.Value.Count > 0).ToDictionary(s => s.Key, s => (IReadOnlyDictionary<string, uint>)new Dictionary<string, uint>(s.Value));

    public bool Remember(Guid scheme, IReadOnlyDictionary<string, uint> values)
    {
        if (FailRemember) return false;
        if (!Stored.TryGetValue(scheme, out Dictionary<string, uint>? noted)) Stored[scheme] = noted = new();
        foreach ((string key, uint value) in values) noted.TryAdd(key, value);
        return true;
    }

    public void Forget(Guid scheme, IReadOnlyCollection<string> keys)
    {
        if (!Stored.TryGetValue(scheme, out Dictionary<string, uint>? noted)) return;
        foreach (string key in keys) noted.Remove(key);
        if (noted.Count == 0) Stored.Remove(scheme);
    }
}

/// <summary>Les réglages du plan qu'un groupe écrit restent après la fermeture : leur origine est notée d'abord, et
/// « Tout rétablir » la rend.</summary>
public class ProfileGroupPowerPlanChangesTests
{
    private readonly FakePowerPlan _plan = new();
    private readonly FakePlanOrigins _origins = new();
    private readonly ProfileGroupPowerPlanChanges _changes;

    public ProfileGroupPowerPlanChangesTests()
    {
        _plan.Set(FakePowerPlan.Balanced, Boost.Guid, 1, 1).Set(FakePowerPlan.Balanced, Epp.Guid, 50, 50);
        _plan.Set(FakePowerPlan.Performance, Boost.Guid, 2, 2).Set(FakePowerPlan.Performance, Epp.Guid, 20, 20);
        _changes = new ProfileGroupPowerPlanChanges([Boost, Epp], hasBattery: false, _plan, _origins);
    }

    private string Key(CpuPowerSetting setting, string side) => $"{setting.Guid:D}/{side}";

    [Fact]
    public void L_origine_est_notee_avant_l_ecriture()
    {
        bool notedBeforeWrite = false;
        _plan.BeforeWrite = () => notedBeforeWrite = _origins.Stored.TryGetValue(FakePowerPlan.Balanced, out var noted)
                                                     && noted[Key(Epp, "ac")] == 50;

        PowerPlanWriteResult result = _changes.Write([new CpuPlanWrite(Epp, 80, 80)]);

        Assert.True(result.Succeeded);
        Assert.True(notedBeforeWrite);
        Assert.Equal((80u, 80u), _plan.Get(FakePowerPlan.Balanced, Epp.Guid));
        Assert.True(Assert.Single(result.Settings).Retained);
        Assert.Equal("Équilibré", result.PlanName);
    }

    [Fact]
    public void Une_seconde_ecriture_ne_remplace_pas_l_origine()
    {
        _changes.Write([new CpuPlanWrite(Epp, 80, 80)]);
        _changes.Write([new CpuPlanWrite(Epp, 90, 90)]);

        Assert.Equal(50u, _origins.Stored[FakePowerPlan.Balanced][Key(Epp, "ac")]);
    }

    [Fact]
    public void Un_lot_ne_reactive_le_plan_qu_une_fois_et_ignore_ce_qui_est_en_place()
    {
        _changes.Write([new CpuPlanWrite(Boost, 1, 1), new CpuPlanWrite(Epp, 80, 80)]);

        Assert.Equal(1, _plan.WriteCalls);
        Assert.False(_origins.Stored[FakePowerPlan.Balanced].ContainsKey(Key(Boost, "ac")));
    }

    [Fact]
    public void Rien_a_changer_n_ecrit_rien()
    {
        PowerPlanWriteResult result = _changes.Write([new CpuPlanWrite(Epp, 50, 50)]);

        Assert.True(result.Succeeded);
        Assert.Equal(0, _plan.WriteCalls);
        Assert.False(_changes.HasChanges);
    }

    [Fact]
    public void Sans_origine_enregistree_rien_n_est_ecrit()
    {
        _origins.FailRemember = true;

        PowerPlanWriteResult result = _changes.Write([new CpuPlanWrite(Epp, 80, 80)]);

        Assert.False(result.Succeeded);
        Assert.Contains("rien n'a été écrit", result.Error);
        Assert.Equal(0, _plan.WriteCalls);
        Assert.Equal((50u, 50u), _plan.Get(FakePowerPlan.Balanced, Epp.Guid));
    }

    [Fact]
    public void Un_reglage_revenu_a_son_origine_est_oublie()
    {
        _changes.Write([new CpuPlanWrite(Epp, 80, 80)]);
        _changes.Write([new CpuPlanWrite(Epp, 50, 50)]);

        Assert.False(_changes.HasChanges);
    }

    [Fact]
    public void Un_refus_de_windows_est_dit()
    {
        _plan.RefuseWrites = true;

        PowerPlanWriteResult result = _changes.Write([new CpuPlanWrite(Epp, 80, 80)]);

        Assert.False(result.Succeeded);
        Assert.Contains("administrateur", result.Error);
        Assert.False(Assert.Single(result.Settings).Retained);
    }

    [Fact]
    public void Un_reglage_pilote_par_le_fabricant_est_relu_tel_quel()
    {
        _plan.PinnedByOem.Add(Epp.Guid);

        PowerPlanWriteResult result = _changes.Write([new CpuPlanWrite(Epp, 80, 80)]);

        PowerPlanSettingResult setting = Assert.Single(result.Settings);
        Assert.False(setting.Retained);
        Assert.Equal(50u, setting.Ac);
    }

    [Fact]
    public void L_origine_du_plan_actif_se_rend()
    {
        _changes.Write([new CpuPlanWrite(Epp, 80, 80), new CpuPlanWrite(Boost, 0, 0)]);

        PowerPlanWriteResult result = _changes.RestoreActiveScheme();

        Assert.True(result.Succeeded);
        Assert.Equal((50u, 50u), _plan.Get(FakePowerPlan.Balanced, Epp.Guid));
        Assert.Equal((1u, 1u), _plan.Get(FakePowerPlan.Balanced, Boost.Guid));
        Assert.False(_changes.HasChanges);
    }

    [Fact]
    public void L_origine_est_gardee_par_plan_et_tout_retablir_les_rend_toutes()
    {
        _changes.Write([new CpuPlanWrite(Epp, 80, 80)]);
        _plan.Active = FakePowerPlan.Performance;
        _changes.Write([new CpuPlanWrite(Epp, 90, 90)]);

        Assert.Equal(2, _changes.Describe().Count);

        SystemRestoreResult restored = _changes.RestoreAll();

        Assert.Equal(SystemRestoreStatus.Restored, restored.Status);
        Assert.Equal((50u, 50u), _plan.Get(FakePowerPlan.Balanced, Epp.Guid));
        Assert.Equal((20u, 20u), _plan.Get(FakePowerPlan.Performance, Epp.Guid));
        Assert.False(_changes.HasChanges);
    }

    [Fact]
    public void Un_plan_supprime_depuis_est_oublie()
    {
        _changes.Write([new CpuPlanWrite(Epp, 80, 80)]);
        _plan.DeletedSchemes.Add(FakePowerPlan.Balanced);

        SystemRestoreResult restored = _changes.RestoreAll();

        Assert.Equal(SystemRestoreStatus.NothingToRestore, restored.Status);
        Assert.False(_changes.HasChanges);
    }

    [Fact]
    public void Ce_qui_ne_revient_pas_est_decrit()
    {
        _changes.Write([new CpuPlanWrite(Epp, 80, 80)]);
        _plan.PinnedByOem.Add(Epp.Guid);

        SystemRestoreResult restored = _changes.RestoreAll();

        Assert.Equal(SystemRestoreStatus.Failed, restored.Status);
        SystemChange change = Assert.Single(restored.NotRestored);
        Assert.Equal("Plan « Équilibré » : 80 (origine 50).", change.Detail);
        Assert.True(_changes.HasChanges);
    }

    [Fact]
    public void Sans_plan_actif_rien_n_est_ecrit_et_rien_ne_leve()
    {
        _plan.Active = null;

        PowerPlanWriteResult result = _changes.Write([new CpuPlanWrite(Epp, 80, 80)]);

        Assert.False(result.Succeeded);
        Assert.Equal(0, _plan.WriteCalls);
    }
}
