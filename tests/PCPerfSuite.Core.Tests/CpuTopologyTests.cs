using System.Buffers.Binary;
using PCPerfSuite.Core.Hardware.Cpu;

namespace PCPerfSuite.Core.Tests;

/// <summary>Tampons synthétiques au format de Windows, pour <see cref="CpuTopology"/>.</summary>
internal static class CpuSetBuffers
{
    /// <summary>Une entrée SYSTEM_CPU_SET_INFORMATION de 32 octets (taille de Windows 10 et 11).</summary>
    public static byte[] Entry(int index, int core, int llc, int efficiencyClass, bool parked = false, int group = 0, int size = 32)
    {
        byte[] entry = new byte[size];
        BinaryPrimitives.WriteInt32LittleEndian(entry, size);
        BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(4), 0);
        BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(8), 256 + index);
        BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(12), (ushort)group);
        entry[14] = (byte)index;
        entry[15] = (byte)core;
        entry[16] = (byte)llc;
        entry[17] = 0;
        entry[18] = (byte)efficiencyClass;
        entry[19] = parked ? (byte)0x01 : (byte)0x00;
        return entry;
    }

    /// <summary>Une entrée RelationCache : niveau, taille, et le masque des processeurs logiques du groupe 0.</summary>
    public static byte[] Cache(int level, uint sizeBytes, ulong mask, ushort groupCount = 1)
    {
        byte[] entry = new byte[56];
        BinaryPrimitives.WriteInt32LittleEndian(entry, 2);
        BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(4), entry.Length);
        entry[8] = (byte)level;
        BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(12), sizeBytes);
        BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(38), groupCount);
        BinaryPrimitives.WriteUInt64LittleEndian(entry.AsSpan(40), mask);
        BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(48), 0);
        return entry;
    }

    public static byte[] Concat(IEnumerable<byte[]> entries) => entries.SelectMany(e => e).ToArray();

    /// <summary>i5-13500T : 6 P-cores à deux fils (classe 1, processeurs 0 à 11), puis 8 E-cores (classe 0, 12 à 19), un
    /// seul L3.</summary>
    public static byte[] Hybrid6P8E()
    {
        var entries = new List<byte[]>();
        for (int core = 0; core < 6; core++)
        {
            entries.Add(Entry(core * 2, core * 2, llc: 0, efficiencyClass: 1));
            entries.Add(Entry(core * 2 + 1, core * 2, llc: 0, efficiencyClass: 1, parked: true));
        }

        for (int e = 0; e < 8; e++) entries.Add(Entry(12 + e, 12 + e, llc: 0, efficiencyClass: 0));
        return Concat(entries);
    }

    /// <summary>Ryzen 9 7950X3D : deux CCD de 8 cœurs à deux fils, L3 de 96 Mo puis 32 Mo.</summary>
    public static (byte[] CpuSets, byte[] Caches) DualCcdX3D()
    {
        var entries = new List<byte[]>();
        for (int ccd = 0; ccd < 2; ccd++)
        {
            for (int core = 0; core < 8; core++)
            {
                int first = ccd * 16 + core * 2;
                entries.Add(Entry(first, first, llc: ccd * 16, efficiencyClass: 0));
                entries.Add(Entry(first + 1, first, llc: ccd * 16, efficiencyClass: 0, parked: ccd == 1));
            }
        }

        byte[] caches = Concat(
        [
            Cache(level: 2, sizeBytes: 1024 * 1024, mask: 0b11),
            Cache(level: 3, sizeBytes: 96u * 1024 * 1024, mask: 0x0000FFFF),
            Cache(level: 3, sizeBytes: 32u * 1024 * 1024, mask: 0xFFFF0000),
        ]);
        return (Concat(entries), caches);
    }
}

/// <summary>La topologie lue dans les CPU sets : fils SMT, classes P/E, groupes de cache L3 et repérage du CCD avec
/// 3D V-Cache.</summary>
public class CpuTopologyTests
{
    [Fact]
    public void Les_champs_d_une_entree_sont_lus_a_leur_place()
    {
        byte[] buffer = CpuSetBuffers.Entry(index: 7, core: 6, llc: 3, efficiencyClass: 1, parked: true, group: 1);

        LogicalProcessor processor = Assert.Single(CpuTopology.ParseCpuSets(buffer));

        Assert.Equal(new LogicalProcessorId(1, 7), processor.Id);
        Assert.Equal(6, processor.CoreIndex);
        Assert.Equal(3, processor.LastLevelCacheIndex);
        Assert.Equal(1, processor.EfficiencyClass);
        Assert.True(processor.IsParked);
    }

    [Fact]
    public void Les_entrees_de_taille_variable_sont_suivies_par_leur_champ_Size()
    {
        // Une version future de Windows peut allonger l'entrée : le champ Size dit où commence la suivante.
        byte[] buffer = CpuSetBuffers.Concat([CpuSetBuffers.Entry(0, 0, 0, 0, size: 48), CpuSetBuffers.Entry(1, 0, 0, 0)]);

        Assert.Equal([0, 1], CpuTopology.ParseCpuSets(buffer).Select(p => p.Id.Index));
    }

    [Fact]
    public void Un_tampon_tronque_ne_fait_pas_planter_la_lecture()
    {
        byte[] full = CpuSetBuffers.Hybrid6P8E();

        Assert.Equal(19, CpuTopology.ParseCpuSets(full.AsSpan(0, full.Length - 5)).Count);
        Assert.Empty(CpuTopology.ParseCpuSets(new byte[4]));
    }

    [Fact]
    public void Un_hybride_range_les_coeurs_performants_avant_les_efficaces_et_leurs_fils_cote_a_cote()
    {
        CpuTopology topology = CpuTopology.Build(CpuTopology.ParseCpuSets(CpuSetBuffers.Hybrid6P8E()), [])!;

        Assert.True(topology.IsHybrid);
        Assert.True(topology.HasSmt);
        Assert.Equal(14, topology.PhysicalCoreCount);
        Assert.Equal([1, 0], topology.EfficiencyClasses);
        Assert.Equal(1, topology.TopEfficiencyClass);

        CacheCluster cluster = Assert.Single(topology.Clusters);
        Assert.Equal([1, 0], cluster.Classes.Select(c => c.EfficiencyClass));
        Assert.Equal(6, cluster.Classes[0].Cores.Count);
        Assert.All(cluster.Classes[0].Cores, core => Assert.Equal(2, core.Threads.Count));
        Assert.Equal([new LogicalProcessorId(0, 0), new LogicalProcessorId(0, 1)], cluster.Classes[0].Cores[0].Threads.Select(t => t.Id));
        Assert.Equal(8, cluster.Classes[1].Cores.Count);
        Assert.All(cluster.Classes[1].Cores, core => Assert.Single(core.Threads));
    }

    [Fact]
    public void Un_X3D_a_deux_CCD_marque_celui_qui_a_le_plus_de_L3()
    {
        (byte[] cpuSets, byte[] caches) = CpuSetBuffers.DualCcdX3D();

        CpuTopology topology = CpuTopology.Build(CpuTopology.ParseCpuSets(cpuSets), CpuTopology.ParseCaches(caches))!;

        Assert.False(topology.IsHybrid);
        Assert.True(topology.HasMixedL3Sizes);
        Assert.Equal(2, topology.Clusters.Count);
        Assert.Equal(96L * 1024 * 1024, topology.Clusters[0].L3Bytes);
        Assert.True(topology.Clusters[0].HasLargerL3);
        Assert.Equal(32L * 1024 * 1024, topology.Clusters[1].L3Bytes);
        Assert.False(topology.Clusters[1].HasLargerL3);
        Assert.Equal(8, topology.Clusters[1].Cores.Count());
    }

    [Fact]
    public void Seuls_les_caches_L3_sont_retenus_et_un_GroupCount_nul_vaut_un_masque()
    {
        byte[] caches = CpuSetBuffers.Concat(
            [CpuSetBuffers.Cache(level: 2, sizeBytes: 2048, mask: 1), CpuSetBuffers.Cache(level: 3, sizeBytes: 4096, mask: 0b101, groupCount: 0)]);

        LastLevelCacheInfo l3 = Assert.Single(CpuTopology.ParseCaches(caches));

        Assert.Equal(4096, l3.SizeBytes);
        Assert.Equal([new LogicalProcessorId(0, 0), new LogicalProcessorId(0, 2)], l3.Processors);
    }

    [Fact]
    public void Deux_CCD_de_meme_L3_ne_sont_pas_pris_pour_un_X3D()
    {
        (byte[] cpuSets, _) = CpuSetBuffers.DualCcdX3D();
        byte[] caches = CpuSetBuffers.Concat(
        [
            CpuSetBuffers.Cache(level: 3, sizeBytes: 32u * 1024 * 1024, mask: 0x0000FFFF),
            CpuSetBuffers.Cache(level: 3, sizeBytes: 32u * 1024 * 1024, mask: 0xFFFF0000),
        ]);

        CpuTopology topology = CpuTopology.Build(CpuTopology.ParseCpuSets(cpuSets), CpuTopology.ParseCaches(caches))!;

        Assert.False(topology.HasMixedL3Sizes);
    }

    [Fact]
    public void Sans_taille_de_L3_on_ne_conclut_rien()
    {
        (byte[] cpuSets, _) = CpuSetBuffers.DualCcdX3D();

        CpuTopology topology = CpuTopology.Build(CpuTopology.ParseCpuSets(cpuSets), [])!;

        Assert.All(topology.Clusters, c => Assert.Null(c.L3Bytes));
        Assert.False(topology.HasMixedL3Sizes);
    }

    [Fact]
    public void Aucun_processeur_ne_donne_aucune_topologie()
    {
        Assert.Null(CpuTopology.Build([], []));
    }
}
