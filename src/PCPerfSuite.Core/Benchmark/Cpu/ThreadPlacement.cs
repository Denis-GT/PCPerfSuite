using System.Runtime.InteropServices;
using PCPerfSuite.Core.Benchmark.Protocol;

namespace PCPerfSuite.Core.Benchmark.Cpu;

/// <summary>Comment le thread a été placé, et si Windows confirme qu'il tourne bien là.</summary>
public sealed record ThreadPlacementResult(string? Mechanism, bool Verified)
{
    public string Describe() => Mechanism is null ? "non épinglé" : Verified ? Mechanism : $"{Mechanism} (non vérifié)";
}

/// <summary>
/// Épingle le thread courant sur un processeur logique : d'abord par CPU set (SetThreadSelectedCpuSets, l'API que
/// Windows préfère sur les hybrides, l'ordonnanceur garde la main en cas de besoin), puis par affinité de groupe
/// (SetThreadGroupAffinity, contrainte dure) ; sur un hybride Intel, sans cela, un mono-thread peut tomber sur un
/// cœur E. Best-effort : rien ne lève, le résultat dit ce qui a marché. À appeler depuis un thread dédié au bench, dont
/// l'affinité n'a pas à être rendue.
/// </summary>
public static unsafe class ThreadPlacement
{
    public static ThreadPlacementResult PinCurrentThread(LogicalProcessorTarget target)
    {
        string? mechanism = null;
        try
        {
            IntPtr thread = GetCurrentThread();

            if (target.CpuSetId != 0)
            {
                uint id = target.CpuSetId;
                if (SetThreadSelectedCpuSets(thread, &id, 1)) mechanism = "cpu-set";
            }

            if (target.Index is >= 0 and < 64 && target.Group >= 0)
            {
                var affinity = new GroupAffinity { Mask = (UIntPtr)(1UL << target.Index), Group = (ushort)target.Group };
                if (SetThreadGroupAffinity(thread, ref affinity, out _)) mechanism = mechanism is null ? "affinite" : "cpu-set+affinite";
            }

            if (mechanism is null) return new ThreadPlacementResult(null, false);

            for (int attempt = 0; attempt < 5; attempt++)
            {
                GetCurrentProcessorNumberEx(out ProcessorNumber current);
                if (current.Group == target.Group && current.Number == target.Index) return new ThreadPlacementResult(mechanism, true);
                Thread.Sleep(0);
            }
            return new ThreadPlacementResult(mechanism, false);
        }
        catch (Exception)
        {
            return new ThreadPlacementResult(mechanism, false);
        }
    }

    /// <summary>Le processeur logique sur lequel le thread courant tourne en ce moment.</summary>
    public static (int Group, int Index) CurrentProcessor()
    {
        GetCurrentProcessorNumberEx(out ProcessorNumber current);
        return (current.Group, current.Number);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GroupAffinity
    {
        public UIntPtr Mask;
        public ushort Group;
        public ushort Reserved0;
        public ushort Reserved1;
        public ushort Reserved2;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessorNumber
    {
        public ushort Group;
        public byte Number;
        public byte Reserved;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadSelectedCpuSets(IntPtr thread, uint* cpuSetIds, uint cpuSetIdCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadGroupAffinity(IntPtr thread, ref GroupAffinity affinity, out GroupAffinity previous);

    [DllImport("kernel32.dll")]
    private static extern void GetCurrentProcessorNumberEx(out ProcessorNumber number);
}
