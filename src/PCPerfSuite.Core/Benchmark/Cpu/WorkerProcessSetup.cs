using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PCPerfSuite.Core.Benchmark.Cpu;

/// <summary>
/// Réglages du processus worker avant toute charge : sortie d'EcoQoS (sans quoi Windows 11 peut le ranger sur les
/// cœurs E à fréquence réduite) et priorité High, jamais Realtime (qui affamerait le relevé des capteurs et
/// l'interface). Best-effort : chaque réglage dit s'il a été posé, pour le résultat et le diagnostic.
/// </summary>
public static class WorkerProcessSetup
{
    private const int ProcessPowerThrottling = 4;
    private const uint PowerThrottlingCurrentVersion = 1;
    private const uint PowerThrottlingExecutionSpeed = 0x1;

    /// <summary>Applique les deux réglages ; rend une note par réglage (« EcoQoS : désactivé », « priorité : High »).</summary>
    public static IReadOnlyList<string> Apply()
    {
        var notes = new List<string> { DisableEcoQos(), RaisePriority() };
        return notes;
    }

    /// <summary>ControlMask = EXECUTION_SPEED et StateMask = 0 : « ne me bride jamais pour économiser ».</summary>
    public static string DisableEcoQos()
    {
        try
        {
            var state = new ProcessPowerThrottlingState
            {
                Version = PowerThrottlingCurrentVersion,
                ControlMask = PowerThrottlingExecutionSpeed,
                StateMask = 0,
            };
            bool ok = SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref state, (uint)Marshal.SizeOf<ProcessPowerThrottlingState>());
            return ok ? "EcoQoS : désactivé" : $"EcoQoS : non modifié (erreur Windows {Marshal.GetLastWin32Error()})";
        }
        catch (Exception ex)
        {
            return $"EcoQoS : non modifié ({ex.GetType().Name})";
        }
    }

    public static string RaisePriority()
    {
        try
        {
            using Process current = Process.GetCurrentProcess();
            current.PriorityClass = ProcessPriorityClass.High;
            return "priorité : High";
        }
        catch (Exception ex)
        {
            return $"priorité : inchangée ({ex.GetType().Name})";
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessPowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessInformation(IntPtr process, int informationClass, ref ProcessPowerThrottlingState information, uint size);
}
