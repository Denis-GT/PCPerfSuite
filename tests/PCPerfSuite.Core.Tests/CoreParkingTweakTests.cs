using PCPerfSuite.Core.Hardware.Cpu;
using PCPerfSuite.Core.Hardware.Cpu.CoreParking;
using PCPerfSuite.Core.PowerSettings;

namespace PCPerfSuite.Core.Tests;

/// <summary>Le tweak « core-parking » d'Optimisation Windows comme façade du service, et les cas relevés par la
/// relecture : Strix Point, origine laissée par l'ancien tweak, PC sans batterie.</summary>
public class CoreParkingTweakTests
{
    private static readonly CoreParkingSetting Min = CoreParkingCatalog.MinCores;
    private static readonly CoreParkingSetting MinPerf = CoreParkingCatalog.MinCoresPerformance;

    private static FakePowerPlan Plan() => new FakePowerPlan()
        .Set(FakePowerPlan.Balanced, Min, 4, 4)
        .Set(FakePowerPlan.Balanced, MinPerf, 0, 0)
        .Set(FakePowerPlan.Balanced, CoreParkingCatalog.MaxCores, 100, 100)
        .Set(FakePowerPlan.Performance, Min, 100, 100)
        .Set(FakePowerPlan.Performance, MinPerf, 100, 100)
        .Set(FakePowerPlan.Performance, CoreParkingCatalog.MaxCores, 100, 100);

    private static PerformanceTweak Tweak(CoreParkingService service)
        => new WindowsPerformanceSettingsService(service).GetTweaks().Single(t => t.Id == "core-parking");

    [Fact]
    public void Le_tweak_met_aussi_les_coeurs_performants_a_100_puis_rend_l_origine()
    {
        FakePowerPlan plan = Plan();
        var service = new CoreParkingService(isHybrid: true, hasBattery: true, plan, new FakeOriginStore());
        PerformanceTweak tweak = Tweak(service);

        Assert.Equal(TweakState.Disabled, tweak.GetState());

        tweak.Apply(true);
        Assert.Equal((100u, 100u), plan.Get(FakePowerPlan.Balanced, Min));
        Assert.Equal((100u, 100u), plan.Get(FakePowerPlan.Balanced, MinPerf));
        Assert.Equal(TweakState.Enabled, tweak.GetState());
        Assert.True(service.HasChanges);

        tweak.Apply(false);
        Assert.Equal((4u, 4u), plan.Get(FakePowerPlan.Balanced, Min));
        Assert.Equal((0u, 0u), plan.Get(FakePowerPlan.Balanced, MinPerf));
        Assert.False(service.HasChanges);
    }

    [Fact]
    public void Sur_un_hybride_le_seul_plancher_general_a_100_n_est_pas_active()
    {
        FakePowerPlan plan = Plan().Set(FakePowerPlan.Balanced, Min, 100, 100);

        Assert.Equal(TweakState.Disabled, Tweak(new CoreParkingService(true, true, plan, new FakeOriginStore())).GetState());
    }

    [Fact]
    public void Decocher_dans_un_autre_plan_le_dit_au_lieu_d_ecrire_un_repli_dans_le_plan_modifie()
    {
        FakePowerPlan plan = Plan();
        var service = new CoreParkingService(true, true, plan, new FakeOriginStore());
        service.Write([new CoreParkingTarget(CoreParkingCatalog.MaxCores, new CoreParkingValue(50, 50))]);
        plan.Active = FakePowerPlan.Performance;
        PerformanceTweak tweak = Tweak(service);
        Assert.Equal(TweakState.Enabled, tweak.GetState());

        var error = Assert.Throws<InvalidOperationException>(() => tweak.Apply(false));

        Assert.Contains("Processeur › Cœurs", error.Message);
        // Le repli de 5 % n'a pas été écrit dans le plan modifié, qui n'est pas le plan actif.
        Assert.Equal((4u, 4u), plan.Get(FakePowerPlan.Balanced, Min));
        // Le tweak ne rend que les planchers : le plafond réglé dans Processeur › Cœurs reste modifié, et le dit.
        Assert.Equal((50u, 50u), plan.Get(FakePowerPlan.Balanced, CoreParkingCatalog.MaxCores));
        Assert.True(service.HasChanges);
    }

    [Fact]
    public void Une_origine_de_l_ancien_tweak_deja_rendue_n_est_plus_une_modification()
    {
        // L'ancien tweak gardait l'origine après avoir été décoché, sans retenir le plan.
        var origins = new FakeOriginStore();
        origins.Stored[$"{CoreParkingCatalog.OriginKey(Min)}/ac"] = 10;
        origins.Stored[$"{CoreParkingCatalog.OriginKey(Min)}/dc"] = 10;
        FakePowerPlan plan = Plan();
        plan.Active = FakePowerPlan.Performance;
        plan.Set(FakePowerPlan.Performance, Min, 30, 30);
        var service = new CoreParkingService(true, true, plan, origins);

        Assert.False(service.HasChanges);
        Assert.Empty(origins.Stored);
        Assert.Equal(SystemChanges.SystemRestoreStatus.NothingToRestore, service.RestoreAll().Status);
        Assert.Equal((30u, 30u), plan.Get(FakePowerPlan.Performance, Min));
    }

    [Fact]
    public void Une_origine_de_l_ancien_tweak_encore_active_reste_a_rendre()
    {
        var origins = new FakeOriginStore();
        origins.Stored[$"{CoreParkingCatalog.OriginKey(Min)}/ac"] = 10;
        FakePowerPlan plan = Plan().Set(FakePowerPlan.Balanced, Min, 100, 100);
        var service = new CoreParkingService(true, true, plan, origins);

        Assert.True(service.HasChanges);
    }

    [Fact]
    public void Sans_batterie_revenir_a_l_origine_secteur_rend_aussi_la_batterie()
    {
        FakePowerPlan plan = Plan().Set(FakePowerPlan.Balanced, Min, 10, 5);
        var service = new CoreParkingService(true, hasBattery: false, plan, new FakeOriginStore());

        service.Write([new CoreParkingTarget(Min, new CoreParkingValue(100, 100))]);
        service.Write([new CoreParkingTarget(Min, new CoreParkingValue(10, 10))]);

        Assert.Equal((10u, 5u), plan.Get(FakePowerPlan.Balanced, Min));
        Assert.False(service.HasChanges);
    }

    [Fact]
    public void Un_Strix_Point_n_est_pas_pris_pour_un_X3D()
    {
        // Ryzen AI 9 HX 370 : 4 Zen 5 (16 Mo de L3) et 8 Zen 5c (8 Mo), deux classes.
        var processors = new List<LogicalProcessor>();
        for (int i = 0; i < 4; i++) processors.Add(new(new LogicalProcessorId(0, i), i, 0, 0, 1, false));
        for (int i = 4; i < 12; i++) processors.Add(new(new LogicalProcessorId(0, i), i, 4, 0, 0, false));
        CpuTopology topology = CpuTopology.Build(processors,
        [
            new LastLevelCacheInfo(16L * 1024 * 1024, processors.Take(4).Select(p => p.Id).ToList()),
            new LastLevelCacheInfo(8L * 1024 * 1024, processors.Skip(4).Select(p => p.Id).ToList()),
        ])!;

        Assert.False(topology.HasMixedL3Sizes);
        Assert.False(CoreTopologySupport.IsDualCcdX3D(CpuVendor.Amd, topology, vcacheDriverInstalled: true));
    }

    [Fact]
    public void Le_pilote_AMD_seul_ne_fait_pas_d_un_7950X_un_X3D()
    {
        (byte[] cpuSets, _) = CpuSetBuffers.DualCcdX3D();
        byte[] caches = CpuSetBuffers.Concat(
        [
            CpuSetBuffers.Cache(level: 3, sizeBytes: 32u * 1024 * 1024, mask: 0x0000FFFF),
            CpuSetBuffers.Cache(level: 3, sizeBytes: 32u * 1024 * 1024, mask: 0xFFFF0000),
        ]);
        CpuTopology withSizes = CpuTopology.Build(CpuTopology.ParseCpuSets(cpuSets), CpuTopology.ParseCaches(caches))!;
        CpuTopology withoutSizes = CpuTopology.Build(CpuTopology.ParseCpuSets(cpuSets), [])!;

        Assert.False(CoreTopologySupport.IsDualCcdX3D(CpuVendor.Amd, withSizes, vcacheDriverInstalled: true));
        Assert.True(CoreTopologySupport.IsDualCcdX3D(CpuVendor.Amd, withoutSizes, vcacheDriverInstalled: true));
    }
}
