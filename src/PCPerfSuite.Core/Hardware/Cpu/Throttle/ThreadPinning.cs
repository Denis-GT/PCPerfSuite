using System.Runtime.InteropServices;

namespace PCPerfSuite.Core.Hardware.Cpu.Throttle;

/// <summary>
/// Épingle le thread courant sur un processeur logique après l'autre, puis lui rend son affinité d'origine : un MSR par
/// cœur se lit sur le cœur lui-même. LibreHardwareMonitor fait déjà de même sur le thread du relevé pour ses
/// températures par cœur. Gère les groupes de processeurs (plus de 64 processeurs logiques). Best-effort : un
/// processeur sur lequel le thread ne peut pas aller est sauté.
/// </summary>
internal sealed class ThreadPinning : IDisposable
{
    private readonly IntPtr _thread;
    private readonly GroupAffinity _original;
    private bool _moved;

    private ThreadPinning(IntPtr thread, GroupAffinity original)
    {
        _thread = thread;
        _original = original;
    }

    /// <summary>Retient l'affinité d'origine du thread courant ; null si elle est illisible.</summary>
    public static ThreadPinning? Begin()
    {
        try
        {
            IntPtr thread = GetCurrentThread();
            return GetThreadGroupAffinity(thread, out GroupAffinity original) ? new ThreadPinning(thread, original) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Processeurs logiques actifs, groupe par groupe.</summary>
    public static IReadOnlyList<(ushort Group, byte Number)> LogicalProcessors()
    {
        var processors = new List<(ushort, byte)>();
        try
        {
            ushort groups = GetActiveProcessorGroupCount();
            for (ushort group = 0; group < groups; group++)
            {
                uint count = Math.Min(GetActiveProcessorCount(group), 64u);
                for (uint number = 0; number < count; number++) processors.Add((group, (byte)number));
            }
        }
        catch (Exception)
        {
            // Liste vide : aucun balayage.
        }
        return processors;
    }

    /// <summary>Déplace le thread sur ce processeur et vérifie qu'il y est bien.</summary>
    public bool PinTo(ushort group, byte number)
    {
        try
        {
            var target = new GroupAffinity { Mask = (UIntPtr)(1UL << number), Group = group };
            if (!SetThreadGroupAffinity(_thread, ref target, out _)) return false;
            _moved = true;

            for (int attempt = 0; attempt < 3; attempt++)
            {
                GetCurrentProcessorNumberEx(out ProcessorNumber current);
                if (current.Group == group && current.Number == number) return true;
                Thread.Sleep(0);
            }
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (!_moved) return;
        try
        {
            GroupAffinity original = _original;
            SetThreadGroupAffinity(_thread, ref original, out _);
        }
        catch (Exception)
        {
            // Rien de mieux à faire : le thread reste épinglé, sans conséquence sur la justesse du relevé.
        }
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
    private static extern bool GetThreadGroupAffinity(IntPtr thread, out GroupAffinity affinity);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadGroupAffinity(IntPtr thread, ref GroupAffinity affinity, out GroupAffinity previous);

    [DllImport("kernel32.dll")]
    private static extern void GetCurrentProcessorNumberEx(out ProcessorNumber number);

    [DllImport("kernel32.dll")]
    private static extern ushort GetActiveProcessorGroupCount();

    [DllImport("kernel32.dll")]
    private static extern uint GetActiveProcessorCount(ushort group);
}
