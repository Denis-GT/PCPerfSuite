using System.Runtime.InteropServices;

namespace PCPerfSuite.Core.Hardware;

/// <summary>
/// Charge CPU totale telle que l'affiche le Gestionnaire des tâches : le compteur de performances Windows
/// "% Processor Utility", qui tient compte de la fréquence réelle des cœurs. Le temps processeur (GetSystemTimes,
/// ou le "CPU Total" de LibreHardwareMonitor) restait à 1-6 % quand le Gestionnaire affichait 30-60 % sur un
/// i5-13500T. Le compteur est ajouté par son nom anglais, donc indépendamment de la langue de Windows.
/// </summary>
internal sealed class CpuLoadSampler : IDisposable
{
    private const string CounterPath = @"\Processor Information(_Total)\% Processor Utility";
    private const uint PdhFmtDouble = 0x00000200;

    private IntPtr _query;
    private readonly IntPtr _counter;

    public CpuLoadSampler()
    {
        if (PdhOpenQueryW(null, IntPtr.Zero, out _query) != 0)
        {
            _query = IntPtr.Zero;
            return;
        }

        if (PdhAddEnglishCounterW(_query, CounterPath, IntPtr.Zero, out _counter) != 0)
        {
            Dispose();
            return;
        }

        // Un compteur de taux se calcule entre deux collectes : celle-ci sert de point de départ au premier relevé.
        PdhCollectQueryData(_query);
    }

    /// <summary>Charge en % depuis l'appel précédent ; null si le compteur est indisponible ou pas encore calculable.</summary>
    public float? Sample()
    {
        if (_query == IntPtr.Zero) return null;
        if (PdhCollectQueryData(_query) != 0) return null;
        if (PdhGetFormattedCounterValue(_counter, PdhFmtDouble, IntPtr.Zero, out PdhFmtCounterValue value) != 0
            || value.CStatus != 0)
        {
            return null;
        }

        // L'utilité dépasse 100 % quand les cœurs tournent au-dessus de leur fréquence nominale : le Gestionnaire
        // des tâches plafonne aussi à 100.
        return (float)Math.Clamp(value.DoubleValue, 0, 100);
    }

    public void Dispose()
    {
        if (_query == IntPtr.Zero) return;
        PdhCloseQuery(_query);
        _query = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFmtCounterValue
    {
        public uint CStatus;
        public double DoubleValue;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(IntPtr query, string fullCounterPath, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll")]
    private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, IntPtr type, out PdhFmtCounterValue value);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);
}
