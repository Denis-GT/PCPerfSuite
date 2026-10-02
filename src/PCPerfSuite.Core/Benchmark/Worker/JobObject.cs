using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PCPerfSuite.Core.Benchmark.Worker;

/// <summary>
/// Job Object Windows à JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE : le worker qu'on y assigne meurt avec le dernier handle du
/// job, donc avec l'app, même tuée par le Gestionnaire des tâches ou plantée. Deuxième filet après le battement de
/// cœur du tube. Best-effort : un échec est dit, jamais levé.
/// </summary>
public sealed class JobObject : IDisposable
{
    private const int ExtendedLimitInformationClass = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    private IntPtr _handle;

    private JobObject(IntPtr handle) => _handle = handle;

    public bool IsOpen => _handle != IntPtr.Zero;

    public static JobObject? TryCreateKillOnClose(out string? error)
    {
        try
        {
            IntPtr handle = CreateJobObjectW(IntPtr.Zero, null);
            if (handle == IntPtr.Zero)
            {
                error = $"CreateJobObject a échoué (erreur Windows {Marshal.GetLastWin32Error()})";
                return null;
            }

            var limits = new JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = { LimitFlags = JobObjectLimitKillOnJobClose },
            };
            if (!SetInformationJobObject(handle, ExtendedLimitInformationClass, ref limits, (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
            {
                error = $"SetInformationJobObject a échoué (erreur Windows {Marshal.GetLastWin32Error()})";
                CloseHandle(handle);
                return null;
            }

            error = null;
            return new JobObject(handle);
        }
        catch (Exception ex)
        {
            error = $"Job Object indisponible ({ex.GetType().Name} : {ex.Message})";
            return null;
        }
    }

    public bool TryAssign(Process process, out string? error)
    {
        try
        {
            if (!IsOpen)
            {
                error = "Job Object fermé";
                return false;
            }
            if (!AssignProcessToJobObject(_handle, process.Handle))
            {
                error = $"AssignProcessToJobObject a échoué (erreur Windows {Marshal.GetLastWin32Error()})";
                return false;
            }
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = $"assignation impossible ({ex.GetType().Name} : {ex.Message})";
            return false;
        }
    }

    /// <summary>Ferme le job : tout processus encore dedans est tué par Windows.</summary>
    public void Dispose()
    {
        IntPtr handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
        if (handle != IntPtr.Zero) CloseHandle(handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr job, int informationClass, ref JobObjectExtendedLimitInformation information, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
