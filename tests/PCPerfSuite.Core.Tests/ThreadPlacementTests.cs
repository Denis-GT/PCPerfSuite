using PCPerfSuite.Core.Benchmark.Cpu;
using PCPerfSuite.Core.Benchmark.Protocol;
using PCPerfSuite.Core.Hardware.Cpu;

namespace PCPerfSuite.Core.Tests;

/// <summary>Épinglage d'un thread sur un processeur logique, vérifié par Windows lui-même. Toujours depuis un thread
/// dédié : l'affinité posée n'est jamais rendue.</summary>
public class ThreadPlacementTests
{
    [Fact]
    public void Un_thread_epingle_sur_le_premier_processeur_y_tourne()
    {
        ThreadPlacementResult? placement = null;
        (int Group, int Index) current = (-1, -1);

        var thread = new Thread(() =>
        {
            placement = ThreadPlacement.PinCurrentThread(new LogicalProcessorTarget { Group = 0, Index = 0 });
            // Quelques calculs pour laisser l'ordonnanceur appliquer l'affinité avant la lecture.
            for (int i = 0; i < 3; i++)
            {
                Thread.Sleep(1);
                current = ThreadPlacement.CurrentProcessor();
            }
        });
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));

        Assert.NotNull(placement);
        Assert.NotNull(placement.Mechanism);
        Assert.Contains("affinite", placement.Mechanism);
        Assert.Equal((0, 0), current);
        Assert.Equal(placement.Mechanism, placement.Describe().Replace(" (non vérifié)", ""));
    }

    [Fact]
    public void Avec_l_identifiant_de_cpu_set_de_la_topologie_le_mecanisme_cpu_set_est_employe()
    {
        CpuTopology? topology = CpuTopology.Read().Topology;
        LogicalProcessor? first = topology?.LogicalProcessors.FirstOrDefault(p => p.Id.Group == 0 && p.Id.Index == 0 && p.CpuSetId != 0);
        if (first is null) return; // Pas de CPU set lisible sur cette machine : rien à vérifier ici (règle 2 : jamais planter).

        ThreadPlacementResult? placement = null;
        var thread = new Thread(() =>
        {
            placement = ThreadPlacement.PinCurrentThread(new LogicalProcessorTarget { Group = 0, Index = 0, CpuSetId = first.CpuSetId });
        });
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));

        Assert.NotNull(placement?.Mechanism);
        Assert.Contains("cpu-set", placement.Mechanism);
    }

    [Fact]
    public void Un_processeur_hors_de_portee_ne_fait_pas_planter_et_le_dit()
    {
        ThreadPlacementResult? placement = null;
        var thread = new Thread(() =>
        {
            placement = ThreadPlacement.PinCurrentThread(new LogicalProcessorTarget { Group = 0, Index = 63 });
        });
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));

        Assert.NotNull(placement);
        // Soit Windows refuse (aucun mécanisme), soit il accepte sans que le thread n'y tourne : dans les deux cas,
        // le résultat le dit et rien ne lève.
        Assert.True(placement.Mechanism is null || !placement.Verified || Environment.ProcessorCount >= 64);
        Assert.Equal("non épinglé", new ThreadPlacementResult(null, false).Describe());
        Assert.Equal("affinite (non vérifié)", new ThreadPlacementResult("affinite", false).Describe());
    }
}
