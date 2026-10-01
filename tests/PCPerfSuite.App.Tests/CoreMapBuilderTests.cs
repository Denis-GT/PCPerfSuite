using PCPerfSuite.App.ViewModels;
using PCPerfSuite.Core.Hardware.Cpu;

namespace PCPerfSuite.App.Tests;

/// <summary>Le visuel par cœur : groupes de cache, classes libellées P/E seulement s'il y en a deux, cœurs et fils.</summary>
public class CoreMapBuilderTests
{
    private static LogicalProcessor Thread(int index, int core, int llc, int efficiencyClass)
        => new(new LogicalProcessorId(0, index), core, llc, NumaNode: 0, efficiencyClass, IsParked: false);

    private static CpuTopology Hybrid()
    {
        var processors = new List<LogicalProcessor>();
        for (int core = 0; core < 2; core++)
        {
            processors.Add(Thread(core * 2, core * 2, 0, 1));
            processors.Add(Thread(core * 2 + 1, core * 2, 0, 1));
        }

        for (int e = 0; e < 4; e++) processors.Add(Thread(4 + e, 4 + e, 0, 0));
        return CpuTopology.Build(processors, [])!;
    }

    private static readonly CpuPlatform Intel = new() { Vendor = CpuVendor.Intel, Name = "Intel", Family = 6, Model = 0xB7 };

    [Fact]
    public void Un_hybride_a_ses_classes_libellees_et_ses_coeurs_numerotes_par_classe()
    {
        CoreClusterViewModel cluster = Assert.Single(CoreMapBuilder.Build(Hybrid(), Intel));

        Assert.Null(cluster.Title);
        Assert.Equal(["Cœurs performants (P)", "Cœurs efficaces (E)"], cluster.Classes.Select(c => c.Title));
        Assert.Equal(["P0", "P1"], cluster.Classes[0].Cores.Select(c => c.Label));
        Assert.Equal(2, cluster.Classes[0].Cores[0].Threads.Count);
        Assert.Equal(["E0", "E1", "E2", "E3"], cluster.Classes[1].Cores.Select(c => c.Label));
        Assert.Equal("2 cœurs performants à 2 fils, 4 cœurs efficaces · 8 fils", CoreMapBuilder.Summary(Hybrid()));
    }

    [Fact]
    public void Un_Ryzen_a_deux_CCD_nomme_ses_CCD_sans_libelle_de_classe()
    {
        var processors = Enumerable.Range(0, 8).Select(i => Thread(i, i / 2 * 2, i < 4 ? 0 : 4, 0)).ToList();
        CpuTopology topology = CpuTopology.Build(processors,
        [
            new LastLevelCacheInfo(96L * 1024 * 1024, processors.Take(4).Select(p => p.Id).ToList()),
            new LastLevelCacheInfo(32L * 1024 * 1024, processors.Skip(4).Select(p => p.Id).ToList()),
        ])!;
        var amd = new CpuPlatform { Vendor = CpuVendor.Amd, Name = "Ryzen 9 7950X3D", Family = 0x19, Model = 0x61 };

        IReadOnlyList<CoreClusterViewModel> clusters = CoreMapBuilder.Build(topology, amd);

        Assert.Equal(["CCD 1", "CCD 2"], clusters.Select(c => c.Title));
        Assert.Equal(["L3 de 96 Mo", "L3 de 32 Mo"], clusters.Select(c => c.CacheText));
        Assert.True(clusters[0].IsVCache);
        Assert.False(clusters[1].IsVCache);
        Assert.All(clusters, c => Assert.Null(Assert.Single(c.Classes).Title));
        Assert.Equal(["C0", "C1", "C2", "C3"], clusters.SelectMany(c => c.Classes).SelectMany(c => c.Cores).Select(c => c.Label));
    }

    [Fact]
    public void Un_fil_jamais_releve_affiche_des_tirets()
    {
        var thread = new CoreThreadViewModel(new LogicalProcessorId(0, 3));
        Assert.Equal("--", thread.LoadText);
        Assert.False(thread.HasValue);

        thread.Update(utility: 42.4, parkedNow: true, parkedShare: 0.6, performance: 150, windowSeconds: 5);

        Assert.Equal("42", thread.LoadText);
        Assert.Equal(0.6, thread.ParkedShare);
        Assert.Contains("Parqué 60", thread.ToolTip);

        thread.Update(utility: null, parkedNow: null, parkedShare: null, performance: null, windowSeconds: 5);
        Assert.Equal("--", thread.LoadText);
        Assert.True(double.IsNaN(thread.ParkedShare));
        Assert.Contains("N/D", thread.ToolTip);
    }
}
