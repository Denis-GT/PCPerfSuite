using System.Runtime.InteropServices;

namespace PCPerfSuite.Core.Hardware;

/// <summary>
/// Charge CPU totale entre deux appels, calculée à partir des temps cumulés que Windows tient pour
/// l'ensemble des processeurs logiques (GetSystemTimes), comme le Gestionnaire des tâches. Quasi gratuit :
/// la charge suit ainsi chaque relevé sans relire tout le CPU via LibreHardwareMonitor.
/// </summary>
internal sealed class CpuLoadSampler
{
    private long _lastIdle;
    private long _lastKernel;
    private long _lastUser;
    private bool _hasPrevious;

    /// <summary>Charge en % depuis l'appel précédent ; null au premier appel ou si Windows ne répond pas.</summary>
    public float? Sample()
    {
        if (!GetSystemTimes(out long idle, out long kernel, out long user)) return null;

        float? load = null;
        // Le temps noyau inclut le temps d'inactivité : le temps écoulé total est donc noyau + utilisateur.
        long total = (kernel - _lastKernel) + (user - _lastUser);
        if (_hasPrevious && total > 0)
        {
            long busy = total - (idle - _lastIdle);
            load = (float)Math.Clamp(100.0 * busy / total, 0, 100);
        }

        _lastIdle = idle;
        _lastKernel = kernel;
        _lastUser = user;
        _hasPrevious = true;
        return load;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);
}
