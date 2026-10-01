using PCPerfSuite.Core.Hardware.Cpu;
using PCPerfSuite.Core.Hardware.Cpu.CoreParking;

namespace PCPerfSuite.Core.Tests;

/// <summary>Ce que posent les préréglages, la topologie « expérimentale » (règle 6) et la ligne du diagnostic.</summary>
public class CoreParkingPresetsTests
{
    private static readonly IReadOnlyList<CoreParkingSetting> Hybrid = CoreParkingCatalog.For(isHybrid: true);

    private static readonly Dictionary<CoreParkingSetting, CoreParkingValue> I5_13500T = new()
    {
        [CoreParkingCatalog.MinCores] = new(4, 4),
        [CoreParkingCatalog.MinCoresPerformance] = new(0, 0),
        [CoreParkingCatalog.MaxCores] = new(100, 100),
        [CoreParkingCatalog.MaxCoresPerformance] = new(100, 100),
        [CoreParkingCatalog.SchedulingPolicy] = new(5, 5),
    };

    private static Dictionary<CoreParkingSetting, CoreParkingValue> Targets(
        CoreParkingPreset preset, Dictionary<CoreParkingSetting, CoreParkingValue> current, bool hasBattery,
        Dictionary<CoreParkingSetting, CoreParkingValue>? origin = null)
        => CoreParkingPresets.Targets(preset, Hybrid,
                s => current.TryGetValue(s, out CoreParkingValue v) ? v : null,
                s => origin is not null && origin.TryGetValue(s, out CoreParkingValue v) ? v : null,
                hasBattery)
            .ToDictionary(t => t.Setting, t => t.Value);

    [Fact]
    public void Tous_les_coeurs_actifs_met_les_deux_planchers_a_100_sur_un_PC_de_bureau()
    {
        Dictionary<CoreParkingSetting, CoreParkingValue> targets = Targets(CoreParkingPreset.AllCoresActive, I5_13500T, hasBattery: false);

        Assert.Equal(new CoreParkingValue(100, 100), targets[CoreParkingCatalog.MinCores]);
        Assert.Equal(new CoreParkingValue(100, 100), targets[CoreParkingCatalog.MinCoresPerformance]);
        // Plafonds déjà à 100, ordonnancement jamais touché : rien à écrire.
        Assert.Equal(2, targets.Count);
    }

    [Fact]
    public void Sur_un_portable_tous_les_coeurs_actifs_laisse_la_batterie_telle_quelle()
    {
        Dictionary<CoreParkingSetting, CoreParkingValue> targets = Targets(CoreParkingPreset.AllCoresActive, I5_13500T, hasBattery: true);

        Assert.Equal(new CoreParkingValue(100, 4), targets[CoreParkingCatalog.MinCores]);
        Assert.Equal(new CoreParkingValue(100, 0), targets[CoreParkingCatalog.MinCoresPerformance]);
    }

    [Fact]
    public void Tous_les_coeurs_actifs_releve_un_plafond_abaisse()
    {
        var current = new Dictionary<CoreParkingSetting, CoreParkingValue>(I5_13500T) { [CoreParkingCatalog.MaxCores] = new(50, 50) };

        Assert.Equal(new CoreParkingValue(100, 100), Targets(CoreParkingPreset.AllCoresActive, current, false)[CoreParkingCatalog.MaxCores]);
    }

    [Fact]
    public void Economie_reduit_les_plafonds_et_garde_les_planchers_dessous()
    {
        var current = new Dictionary<CoreParkingSetting, CoreParkingValue>(I5_13500T)
        {
            [CoreParkingCatalog.MinCores] = new(100, 100),
            [CoreParkingCatalog.MinCoresPerformance] = new(100, 100),
        };
        var origin = new Dictionary<CoreParkingSetting, CoreParkingValue>
        {
            [CoreParkingCatalog.MinCores] = new(4, 4),
            [CoreParkingCatalog.MinCoresPerformance] = new(80, 60),
        };

        Dictionary<CoreParkingSetting, CoreParkingValue> targets = Targets(CoreParkingPreset.Economy, current, hasBattery: true, origin);

        Assert.Equal(new CoreParkingValue(50, 50), targets[CoreParkingCatalog.MaxCores]);
        Assert.Equal(new CoreParkingValue(50, 50), targets[CoreParkingCatalog.MaxCoresPerformance]);
        Assert.Equal(new CoreParkingValue(4, 4), targets[CoreParkingCatalog.MinCores]);
        Assert.Equal(new CoreParkingValue(50, 50), targets[CoreParkingCatalog.MinCoresPerformance]);
    }

    [Fact]
    public void Windows_origine_passe_par_la_restauration_et_non_par_des_valeurs()
    {
        Assert.Empty(Targets(CoreParkingPreset.WindowsOrigin, I5_13500T, false));
    }

    [Fact]
    public void Un_reglage_illisible_est_saute()
    {
        var current = new Dictionary<CoreParkingSetting, CoreParkingValue> { [CoreParkingCatalog.MinCores] = new(4, 4) };

        Assert.Equal([CoreParkingCatalog.MinCores], Targets(CoreParkingPreset.AllCoresActive, current, false).Keys);
    }

    [Fact]
    public void Un_hybride_Raptor_Lake_est_verifie_un_Meteor_Lake_est_experimental()
    {
        CpuTopology topology = CpuTopology.Build(CpuTopology.ParseCpuSets(CpuSetBuffers.Hybrid6P8E()), [])!;

        Assert.True(CoreTopologySupport.Assess(CpuVendor.Intel, 6, 0xBF, topology).IsVerified);
        Assert.True(CoreTopologySupport.Assess(CpuVendor.Intel, 6, 0xB7, topology).IsVerified);
        Assert.False(CoreTopologySupport.Assess(CpuVendor.Intel, 6, 0xAA, topology).IsVerified);
        Assert.False(CoreTopologySupport.Assess(CpuVendor.Qualcomm, 0, 0, topology).IsVerified);
        Assert.False(CoreTopologySupport.Assess(CpuVendor.Amd, 0x1A, 0x24, topology).IsVerified);
    }

    [Fact]
    public void Un_X3D_est_experimental_et_reconnu_comme_bi_CCD()
    {
        (byte[] cpuSets, byte[] caches) = CpuSetBuffers.DualCcdX3D();
        CpuTopology topology = CpuTopology.Build(CpuTopology.ParseCpuSets(cpuSets), CpuTopology.ParseCaches(caches))!;

        CoreTopologyAssessment assessment = CoreTopologySupport.Assess(CpuVendor.Amd, 0x19, 0x61, topology);

        Assert.Contains(assessment.ExperimentalReasons, r => r.Contains("V-Cache"));
        Assert.True(CoreTopologySupport.IsDualCcdX3D(CpuVendor.Amd, topology, vcacheDriverInstalled: false));
        Assert.False(CoreTopologySupport.IsDualCcdX3D(CpuVendor.Intel, topology, vcacheDriverInstalled: true));
    }

    [Fact]
    public void La_ligne_du_diagnostic_dit_la_topologie_les_compteurs_et_les_reglages()
    {
        CpuTopology topology = CpuTopology.Build(CpuTopology.ParseCpuSets(CpuSetBuffers.Hybrid6P8E()), [])!;
        var diagnostic = new CoreParkingDiagnostic(
            new CpuTopologyRead(topology, null),
            new CoreCounterAvailability(Utility: true, ParkingStatus: false, Performance: true),
            null,
            [(CoreParkingCatalog.MinCores, new CoreParkingValue(4, 4)), (CoreParkingCatalog.MinCoresPerformance, null)],
            VCacheDriverInstalled: false,
            HasChanges: false);
        var platform = new CpuPlatform { Vendor = CpuVendor.Intel, Name = "i5-13500T", Family = 6, Model = 0xBF };

        var row = CoreParkingRowProvider.BuildRow(diagnostic, platform);

        Assert.Equal("Cœurs et parking", row.Title);
        Assert.Equal("20 processeurs logiques, 14 cœurs, 2 classes", row.Status);
        Assert.True(row.IsSupported);
        Assert.Contains("Parking Status absent (état parqué lu dans les CPU sets)", row.Detail);
        Assert.Contains("CPMINCORES 4 %/4 %", row.Detail);
        Assert.Contains("CPMINCORES1 N/D", row.Detail);
        Assert.Contains("amd3dvcache) : absent", row.Detail);
        Assert.DoesNotContain("Expérimental", row.Detail);
    }

    [Fact]
    public void Sans_CPU_sets_la_ligne_dit_N_D_et_pourquoi()
    {
        var diagnostic = new CoreParkingDiagnostic(
            new CpuTopologyRead(null, new Compatibility.Unavailable(Compatibility.UnavailableCause.HardwareOrDriver, "Windows trop ancien")),
            null, new Compatibility.Unavailable(Compatibility.UnavailableCause.HardwareOrDriver, "compteurs abîmés"),
            [], false, false);

        var row = CoreParkingRowProvider.BuildRow(diagnostic, new CpuPlatform { Vendor = CpuVendor.Other, Name = "?" });

        Assert.Equal("N/D", row.Status);
        Assert.False(row.IsSupported);
        Assert.Contains("Windows trop ancien", row.Detail);
        Assert.Contains("compteurs abîmés", row.Detail);
        Assert.Equal("Pas encore lu", CoreParkingRowProvider.BuildRow(null, new CpuPlatform { Vendor = CpuVendor.Other, Name = "?" }).Status);
    }
}
