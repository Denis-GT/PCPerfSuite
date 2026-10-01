using System.Buffers.Binary;
using System.Runtime.InteropServices;
using PCPerfSuite.Core.Compatibility;

namespace PCPerfSuite.Core.Hardware.Cpu;

/// <summary>Un processeur logique désigné comme Windows le fait : groupe de processeurs (au-delà de 64) et index dans
/// le groupe. <see cref="ToString"/> rend la forme « groupe,index » des instances de compteurs PDH.</summary>
public readonly record struct LogicalProcessorId(int Group, int Index)
{
    public override string ToString() => $"{Group},{Index}";
}

/// <summary>Un processeur logique (fil d'exécution) tel que décrit par les CPU sets de Windows.</summary>
/// <param name="CoreIndex">Cœur physique : les fils SMT d'un même cœur ont le même, dans le même groupe.</param>
/// <param name="LastLevelCacheIndex">Cache de dernier niveau partagé (L3, donc CCD ou CCX chez AMD).</param>
/// <param name="EfficiencyClass">Classe d'efficacité : plus elle est haute, plus le cœur est performant (P-cores
/// Intel en 1, E-cores en 0). Tous à 0 sur un processeur non hybride.</param>
/// <param name="IsParked">Parqué au moment de la lecture (instantané, l'état bascule vite).</param>
public sealed record LogicalProcessor(
    LogicalProcessorId Id, int CoreIndex, int LastLevelCacheIndex, int NumaNode, int EfficiencyClass, bool IsParked);

/// <summary>Un cœur physique et ses fils SMT, dans l'ordre de leur index.</summary>
public sealed record PhysicalCore(int Group, int CoreIndex, int EfficiencyClass, IReadOnlyList<LogicalProcessor> Threads);

/// <summary>Les cœurs d'une même classe d'efficacité, dans un groupe de cache.</summary>
public sealed record EfficiencyClassGroup(int EfficiencyClass, IReadOnlyList<PhysicalCore> Cores);

/// <summary>
/// Les cœurs qui partagent un cache de dernier niveau (un CCD ou un CCX chez AMD, tout le processeur sur la plupart des
/// Intel), rangés par classe d'efficacité, la plus performante d'abord.
/// </summary>
/// <param name="L3Bytes">Taille du cache L3 de ce groupe, null si Windows ne l'a pas donnée.</param>
/// <param name="HasLargerL3">Au moins deux fois plus de L3 que le plus petit groupe, tous les groupes ayant autant de
/// cœurs : le CCD avec 3D V-Cache d'un Ryzen X3D à deux CCD (96 Mo contre 32). Faux sur un Strix Point, dont les deux
/// blocs de L3 (16 Mo pour 4 Zen 5, 8 Mo pour 8 Zen 5c) n'ont pas le même nombre de cœurs.</param>
public sealed record CacheCluster(
    int Group, int LastLevelCacheIndex, long? L3Bytes, bool HasLargerL3, IReadOnlyList<EfficiencyClassGroup> Classes)
{
    public IEnumerable<PhysicalCore> Cores => Classes.SelectMany(c => c.Cores);
}

/// <summary>Un cache L3 tel que décrit par GetLogicalProcessorInformationEx : sa taille et les processeurs logiques qui le
/// partagent.</summary>
public sealed record LastLevelCacheInfo(long SizeBytes, IReadOnlyList<LogicalProcessorId> Processors);

/// <summary>La topologie lue, ou pourquoi elle manque (règle 3).</summary>
public sealed record CpuTopologyRead(CpuTopology? Topology, Unavailable? Problem);

/// <summary>
/// Topologie du processeur, d'après les CPU sets de Windows (GetSystemCpuSetInformation) : pour chaque processeur
/// logique, son cœur physique (fils SMT), son cache de dernier niveau (L3 : CCD ou CCX), sa classe d'efficacité (P/E)
/// et son état parqué. Sans pilote ni administrateur, depuis Windows 10.
///
/// Logique pure sur les tampons de Windows, lus à la main (entrées de taille variable, champ Size en tête) plutôt que
/// par des structures marshalées, dont la taille change selon la version de Windows. Réutilisée par le parking des
/// cœurs (#5), le bench (#10), le Curve Optimizer (#16) et les limites par processus (#21).
/// </summary>
public sealed class CpuTopology
{
    // SYSTEM_CPU_SET_INFORMATION : Size (DWORD) @0, Type @4, puis CpuSet : Id @8, Group (WORD) @12,
    // LogicalProcessorIndex @14, CoreIndex @15, LastLevelCacheIndex @16, NumaNodeIndex @17, EfficiencyClass @18,
    // AllFlags @19 (bit 0 = Parked).
    private const int CpuSetMinimumSize = 20;
    private const int CpuSetInformationType = 0;
    private const byte ParkedFlag = 0x01;

    // SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX, RelationCache : Relationship @0, Size @4, puis CACHE_RELATIONSHIP :
    // Level @8, CacheSize (DWORD) @12, Type @16, GroupCount (WORD) @38 (0 avant Windows 11 : un seul masque),
    // GROUP_AFFINITY @40 (Mask sur 8 octets en x64, Group WORD juste après).
    private const int RelationCache = 2;
    private const int CacheGroupCountOffset = 38;
    private const int CacheGroupMaskOffset = 40;
    private const int GroupAffinitySize = 16;

    private CpuTopology(IReadOnlyList<LogicalProcessor> processors, IReadOnlyList<CacheCluster> clusters)
    {
        LogicalProcessors = processors;
        Clusters = clusters;
        EfficiencyClasses = processors.Select(p => p.EfficiencyClass).Distinct().OrderByDescending(c => c).ToList();
        PhysicalCoreCount = clusters.Sum(c => c.Cores.Count());
        HasSmt = clusters.SelectMany(c => c.Cores).Any(core => core.Threads.Count > 1);
    }

    /// <summary>Processeurs logiques, par groupe puis par index.</summary>
    public IReadOnlyList<LogicalProcessor> LogicalProcessors { get; }

    /// <summary>Groupes de cache, par groupe de processeurs puis par index de cache.</summary>
    public IReadOnlyList<CacheCluster> Clusters { get; }

    /// <summary>Classes d'efficacité présentes, la plus performante d'abord.</summary>
    public IReadOnlyList<int> EfficiencyClasses { get; }

    public int PhysicalCoreCount { get; }

    public bool HasSmt { get; }

    /// <summary>Au moins deux classes d'efficacité (P-cores et E-cores…).</summary>
    public bool IsHybrid => EfficiencyClasses.Count > 1;

    /// <summary>Plusieurs groupes de cache dont les L3 n'ont pas la même taille : un Ryzen X3D à deux CCD.</summary>
    public bool HasMixedL3Sizes => Clusters.Any(c => c.HasLargerL3);

    /// <summary>Tous les groupes de cache ont autant de cœurs physiques (les CCD d'un Ryzen à deux CCD).</summary>
    public bool HasUniformClusters => Clusters.Select(c => c.Cores.Count()).Distinct().Count() == 1;

    /// <summary>Classe la plus performante (P-cores), celle que visent les réglages Windows suffixés « 1 ».</summary>
    public int TopEfficiencyClass => EfficiencyClasses.Count > 0 ? EfficiencyClasses[0] : 0;

    /// <summary>Lit la topologie de ce PC. Ne lève jamais : une absence rend sa raison.</summary>
    public static CpuTopologyRead Read()
    {
        byte[]? cpuSets = ReadCpuSetBuffer();
        if (cpuSets is null)
        {
            return new CpuTopologyRead(null, new Unavailable(UnavailableCause.HardwareOrDriver,
                "Windows ne décrit pas les cœurs de ce PC (GetSystemCpuSetInformation indisponible, Windows trop ancien ?)"));
        }

        IReadOnlyList<LastLevelCacheInfo> caches = ReadCacheBuffer() is { } cacheBuffer ? ParseCaches(cacheBuffer) : [];
        CpuTopology? topology = Build(ParseCpuSets(cpuSets), caches);
        return topology is null
            ? new CpuTopologyRead(null, new Unavailable(UnavailableCause.HardwareOrDriver, "Windows n'a décrit aucun processeur logique"))
            : new CpuTopologyRead(topology, null);
    }

    /// <summary>État parqué de chaque processeur logique, relu dans les CPU sets : le repli quand le compteur
    /// « Parking Status » manque. Null si Windows ne répond pas.</summary>
    public static IReadOnlyDictionary<LogicalProcessorId, bool>? ReadParkedFlags()
        => ReadCpuSetBuffer() is { } buffer ? ParseCpuSets(buffer).ToDictionary(p => p.Id, p => p.IsParked) : null;

    /// <summary>Topologie bâtie à partir des processeurs logiques et des caches L3, null s'il n'y a aucun processeur.</summary>
    public static CpuTopology? Build(IReadOnlyList<LogicalProcessor> processors, IReadOnlyList<LastLevelCacheInfo> caches)
    {
        if (processors.Count == 0) return null;

        List<LogicalProcessor> sorted = processors.OrderBy(p => p.Id.Group).ThenBy(p => p.Id.Index).ToList();

        var clusters = sorted
            .GroupBy(p => (p.Id.Group, p.LastLevelCacheIndex))
            .OrderBy(g => g.Key.Group).ThenBy(g => g.Key.LastLevelCacheIndex)
            .Select(cluster =>
            {
                List<EfficiencyClassGroup> classes = cluster
                    .GroupBy(p => p.EfficiencyClass)
                    .OrderByDescending(g => g.Key)
                    .Select(klass => new EfficiencyClassGroup(klass.Key, klass
                        .GroupBy(p => p.CoreIndex)
                        .OrderBy(core => core.Min(p => p.Id.Index))
                        .Select(core => new PhysicalCore(cluster.Key.Group, core.Key, klass.Key,
                            core.OrderBy(p => p.Id.Index).ToList()))
                        .ToList()))
                    .ToList();

                long? l3 = caches.FirstOrDefault(c => cluster.Any(p => c.Processors.Contains(p.Id)))?.SizeBytes;
                return (cluster.Key.Group, cluster.Key.LastLevelCacheIndex, L3: l3, Classes: classes);
            })
            .ToList();

        // Un X3D à deux CCD : autant de cœurs dans chaque CCD, et l'un des L3 trois fois plus gros. On ne compare que
        // des tailles connues, entre groupes de même forme.
        bool sameShape = clusters.Select(c => c.Classes.Sum(k => k.Cores.Count)).Distinct().Count() == 1;
        long? smallestL3 = clusters.Count > 1 && sameShape && clusters.All(c => c.L3 is not null) ? clusters.Min(c => c.L3) : null;

        return new CpuTopology(sorted, clusters
            .Select(c => new CacheCluster(c.Group, c.LastLevelCacheIndex, c.L3, smallestL3 is { } min && c.L3 >= 2 * min, c.Classes))
            .ToList());
    }

    /// <summary>Processeurs logiques d'un tampon de GetSystemCpuSetInformation. Une entrée tronquée ou d'un autre type
    /// est ignorée ; une taille nulle arrête la lecture (tampon abîmé).</summary>
    public static IReadOnlyList<LogicalProcessor> ParseCpuSets(ReadOnlySpan<byte> buffer)
    {
        var processors = new List<LogicalProcessor>();
        int offset = 0;
        while (offset + 8 <= buffer.Length)
        {
            int size = BinaryPrimitives.ReadInt32LittleEndian(buffer[offset..]);
            if (size <= 0 || offset + size > buffer.Length) break;

            ReadOnlySpan<byte> entry = buffer.Slice(offset, size);
            if (size >= CpuSetMinimumSize && BinaryPrimitives.ReadInt32LittleEndian(entry[4..]) == CpuSetInformationType)
            {
                processors.Add(new LogicalProcessor(
                    new LogicalProcessorId(BinaryPrimitives.ReadUInt16LittleEndian(entry[12..]), entry[14]),
                    CoreIndex: entry[15],
                    LastLevelCacheIndex: entry[16],
                    NumaNode: entry[17],
                    EfficiencyClass: entry[18],
                    IsParked: (entry[19] & ParkedFlag) != 0));
            }

            offset += size;
        }

        return processors;
    }

    /// <summary>Caches L3 d'un tampon de GetLogicalProcessorInformationEx(RelationCache). Les autres niveaux sont
    /// ignorés. Avant Windows 11, GroupCount vaut 0 : un seul masque suit.</summary>
    public static IReadOnlyList<LastLevelCacheInfo> ParseCaches(ReadOnlySpan<byte> buffer)
    {
        var caches = new List<LastLevelCacheInfo>();
        int offset = 0;
        while (offset + 8 <= buffer.Length)
        {
            int size = BinaryPrimitives.ReadInt32LittleEndian(buffer[(offset + 4)..]);
            if (size <= 0 || offset + size > buffer.Length) break;

            ReadOnlySpan<byte> entry = buffer.Slice(offset, size);
            if (BinaryPrimitives.ReadInt32LittleEndian(entry) == RelationCache && size >= CacheGroupMaskOffset + GroupAffinitySize
                && entry[8] == 3)
            {
                long cacheSize = BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]);
                int groupCount = Math.Max(1, (int)BinaryPrimitives.ReadUInt16LittleEndian(entry[CacheGroupCountOffset..]));

                var members = new List<LogicalProcessorId>();
                for (int i = 0; i < groupCount; i++)
                {
                    int maskOffset = CacheGroupMaskOffset + i * GroupAffinitySize;
                    if (maskOffset + GroupAffinitySize > size) break;

                    ulong mask = BinaryPrimitives.ReadUInt64LittleEndian(entry[maskOffset..]);
                    int group = BinaryPrimitives.ReadUInt16LittleEndian(entry[(maskOffset + 8)..]);
                    for (int bit = 0; bit < 64; bit++)
                    {
                        if ((mask & (1UL << bit)) != 0) members.Add(new LogicalProcessorId(group, bit));
                    }
                }

                caches.Add(new LastLevelCacheInfo(cacheSize, members));
            }

            offset += size;
        }

        return caches;
    }

    private static byte[]? ReadCpuSetBuffer()
    {
        try
        {
            GetSystemCpuSetInformation(IntPtr.Zero, 0, out uint needed, IntPtr.Zero, 0);
            if (needed == 0) return null;

            byte[] buffer = new byte[needed];
            GCHandle handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                if (!GetSystemCpuSetInformation(handle.AddrOfPinnedObject(), needed, out uint returned, IntPtr.Zero, 0)) return null;
                return returned < needed ? buffer[..(int)returned] : buffer;
            }
            finally
            {
                handle.Free();
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Le masque d'affinité y est sur 8 octets : en 32 bits il en ferait 4, et la lecture serait fausse. L'app
    /// est publiée en x64 (D9) ; ailleurs, on se passe simplement des tailles de L3.</summary>
    private static byte[]? ReadCacheBuffer()
    {
        if (IntPtr.Size != 8) return null;

        try
        {
            uint length = 0;
            GetLogicalProcessorInformationEx(RelationCache, IntPtr.Zero, ref length);
            if (length == 0) return null;

            byte[] buffer = new byte[length];
            GCHandle handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                return GetLogicalProcessorInformationEx(RelationCache, handle.AddrOfPinnedObject(), ref length)
                    ? buffer[..(int)Math.Min(length, (uint)buffer.Length)]
                    : null;
            }
            finally
            {
                handle.Free();
            }
        }
        catch
        {
            return null;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemCpuSetInformation(
        IntPtr information, uint bufferLength, out uint returnedLength, IntPtr process, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLogicalProcessorInformationEx(int relationshipType, IntPtr buffer, ref uint returnedLength);
}
