namespace PCPerfSuite.Core.Hardware;

/// <summary>
/// Lit un compteur de performances Windows de type taux, ajouté par son nom anglais, donc indépendamment de la
/// langue de Windows. Sert notamment à reproduire le Gestionnaire des tâches, qui pondère le temps processeur par
/// la fréquence réelle des cœurs : "% Processor Utility" pour la charge totale, "% Processor Performance" pour le
/// facteur de fréquence. Le temps processeur brut (GetSystemTimes, le "CPU Total" de LibreHardwareMonitor, ou
/// GetProcessTimes par processus) restait à 1-6 % quand le Gestionnaire affichait 30-60 % sur un i5-13500T.
/// </summary>
internal sealed class PdhCounterSampler : IDisposable
{
    public const string ProcessorUtility = @"\Processor Information(_Total)\% Processor Utility";
    public const string ProcessorPerformance = @"\Processor Information(_Total)\% Processor Performance";

    private IntPtr _query;
    private readonly IntPtr _counter;

    public PdhCounterSampler(string counterPath)
    {
        if (PdhNative.PdhOpenQueryW(null, IntPtr.Zero, out _query) != 0)
        {
            _query = IntPtr.Zero;
            return;
        }

        if (PdhNative.PdhAddEnglishCounterW(_query, counterPath, IntPtr.Zero, out _counter) != 0)
        {
            Dispose();
            return;
        }

        // Un compteur de taux se calcule entre deux collectes : celle-ci sert de point de départ au premier relevé.
        PdhNative.PdhCollectQueryData(_query);
    }

    /// <summary>Valeur moyenne depuis l'appel précédent, non plafonnée (les deux compteurs processeur dépassent
    /// 100 % en turbo) ; null si le compteur est indisponible ou pas encore calculable.</summary>
    public double? Sample()
    {
        if (_query == IntPtr.Zero) return null;
        if (PdhNative.PdhCollectQueryData(_query) != 0) return null;
        if (PdhNative.PdhGetFormattedCounterValue(_counter, PdhNative.FmtDouble, IntPtr.Zero, out PdhNative.FmtCounterValue value) != 0
            || value.CStatus != 0)
        {
            return null;
        }

        return value.DoubleValue;
    }

    public void Dispose()
    {
        if (_query == IntPtr.Zero) return;
        PdhNative.PdhCloseQuery(_query);
        _query = IntPtr.Zero;
    }
}
