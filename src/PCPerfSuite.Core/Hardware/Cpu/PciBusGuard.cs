namespace PCPerfSuite.Core.Hardware.Cpu;

/// <summary>
/// Prend le mutex système du bus PCI le temps d'un échange avec le SMU d'AMD, pour ne pas croiser le fer avec
/// LibreHardwareMonitor, HWiNFO, Ryzen Master ou un autre outil qui lirait la même boîte aux lettres : sans lui, deux
/// lecteurs s'écrasent mutuellement leurs adresses. Le délai d'attente dépend de l'appelant : court pour le relevé,
/// qui ne doit jamais attendre, plus long pour un réglage.
/// </summary>
internal sealed class PciBusGuard : IDisposable
{
    /// <summary>Mutex partagé par tous les outils qui parlent au SMU.</summary>
    public const string MutexName = @"Global\Access_PCI";

    private readonly Mutex? _mutex;

    public PciBusGuard(TimeSpan timeout)
    {
        try
        {
            _mutex = new Mutex(false, MutexName);
            IsHeld = _mutex.WaitOne(timeout, false);
        }
        catch (AbandonedMutexException)
        {
            // Un autre outil a planté en le détenant. .NET signale l'abandon par une exception, mais l'attente a bien
            // réussi : le mutex est à nous, et c'est à nous de le relâcher. Le compter comme un échec le laisserait
            // abandonné à son tour, et l'outil suivant recevrait la même exception, en chaîne.
            IsHeld = true;
        }
        catch
        {
            // Mutex inaccessible (droits) : on continue sans, comme le fait LibreHardwareMonitor — le risque est une
            // lecture incohérente, pas une écriture ratée.
            IsHeld = false;
            IsUnavailable = true;
        }
    }

    /// <summary>Le mutex est pris : aucun autre outil ne parle au SMU pendant l'échange.</summary>
    public bool IsHeld { get; }

    /// <summary>Le mutex n'a pas pu être ouvert du tout (et non pas : il est tenu ailleurs).</summary>
    public bool IsUnavailable { get; }

    public void Dispose()
    {
        try
        {
            if (IsHeld) _mutex?.ReleaseMutex();
            _mutex?.Dispose();
        }
        catch
        {
            // best-effort
        }
    }
}
