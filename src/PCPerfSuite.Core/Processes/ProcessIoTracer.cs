using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using PCPerfSuite.Core.SystemInfo;

namespace PCPerfSuite.Core.Processes;

/// <summary>Où en est la trace qui compte les octets de disque et de réseau par processus.</summary>
public enum IoTraceState
{
    /// <summary>Pas démarrée : l'onglet Processus n'est pas affiché, ou l'affichage est figé.</summary>
    Stopped,

    Running,

    /// <summary>Windows réserve les traces noyau aux administrateurs : c'est la limite du lancement de l'app,
    /// pas celle du PC.</summary>
    NeedsAdministrator,

    /// <summary>La session n'a pas pu démarrer (nombre maximal de sessions atteint, service ETW arrêté,
    /// stratégie de sécurité…) ou s'est arrêtée d'elle-même.</summary>
    Failed,
}

/// <summary>
/// Compte les octets de disque et de réseau de chaque processus, à partir d'une session ETW noyau : c'est la
/// source que le Gestionnaire des tâches et le Moniteur de ressources utilisent, et la seule qui sépare le
/// disque physique du reste des entrées/sorties (le compteur de GetProcessIoCounters inclut le cache, les
/// tubes et les fichiers déjà en mémoire, d'où des débits sans rapport avec le « Disque » du Gestionnaire).
///
/// Best-effort comme tout ce qui touche au matériel : aucune méthode publique ne lève. Quand la trace ne peut
/// pas démarrer, <see cref="State"/> et <see cref="Detail"/> disent pourquoi, et l'onglet retombe sur les
/// compteurs classiques en le disant.
///
/// La session est nommée et propre à l'app : elle ne touche pas au « NT Kernel Logger », que d'autres outils
/// (PerfView, WPR) utilisent. Elle n'existe que le temps où l'onglet est affiché.
/// </summary>
public sealed class ProcessIoTracer : IDisposable
{
    private const string SessionName = "PCPerfSuite-ProcessIo";

    /// <summary>Intervalle minimal entre deux relectures de la table des threads : chaque événement disque
    /// d'un thread inconnu la réclamerait sinon, et un instantané des threads coûte quelques millisecondes.</summary>
    private static readonly long ThreadScanIntervalTicks = Stopwatch.Frequency;

    private sealed class Totals
    {
        public long Disk;
        public long Network;
    }

    private readonly ConcurrentDictionary<int, Totals> _totals = new();

    /// <summary>Thread → processus propriétaire. Un événement disque nomme le thread qui a émis l'E/S, pas son
    /// processus. N'est lue et écrite que par le thread de la trace (et avant son démarrage).</summary>
    private Dictionary<int, int> _threadOwners = new();

    private long _lastThreadScan;

    private readonly object _lifecycle = new();
    private TraceEventSession? _session;
    private Thread? _thread;
    private long _diskEvents;
    private long _networkEvents;
    private volatile IoTraceState _state = IoTraceState.Stopped;
    private volatile string? _detail;

    public IoTraceState State => _state;

    /// <summary>Pourquoi la trace n'est pas active, en une phrase pour l'utilisateur. Null quand elle l'est.</summary>
    public string? Detail => _detail;

    /// <summary>Événements reçus depuis le démarrage, pour le diagnostic : une trace « active » qui ne voit
    /// jamais passer d'événement disque est le signe d'un attribution qui ne marche pas sur ce PC.</summary>
    public long DiskEventCount => Interlocked.Read(ref _diskEvents);

    public long NetworkEventCount => Interlocked.Read(ref _networkEvents);

    /// <summary>Démarre la trace. Renvoie vrai si elle tourne (déjà ou maintenant).</summary>
    public bool Start()
    {
        lock (_lifecycle)
        {
            if (_state == IoTraceState.Running) return true;

            try
            {
                if (!ElevationHelper.IsAdministrator())
                {
                    _state = IoTraceState.NeedsAdministrator;
                    _detail = "PCPerfSuite n'est pas lancé en administrateur : Windows réserve les traces noyau "
                              + "(ETW) aux administrateurs.";
                    return false;
                }

                StopOrphanSession();
                RefreshThreadOwners();
                Interlocked.Exchange(ref _diskEvents, 0);
                Interlocked.Exchange(ref _networkEvents, 0);
                _totals.Clear();

                var session = new TraceEventSession(SessionName) { StopOnDispose = true };
                try
                {
                    session.EnableKernelProvider(
                        KernelTraceEventParser.Keywords.DiskIO | KernelTraceEventParser.Keywords.NetworkTCPIP);

                    KernelTraceEventParser kernel = session.Source.Kernel;
                    kernel.DiskIORead += OnDisk;
                    kernel.DiskIOWrite += OnDisk;
                    kernel.TcpIpSend += data => AddNetwork(data.ProcessID, data.size);
                    kernel.TcpIpRecv += data => AddNetwork(data.ProcessID, data.size);
                    kernel.TcpIpSendIPV6 += data => AddNetwork(data.ProcessID, data.size);
                    kernel.TcpIpRecvIPV6 += data => AddNetwork(data.ProcessID, data.size);
                    kernel.UdpIpSend += data => AddNetwork(data.ProcessID, data.size);
                    kernel.UdpIpRecv += data => AddNetwork(data.ProcessID, data.size);
                    kernel.UdpIpSendIPV6 += data => AddNetwork(data.ProcessID, data.size);
                    kernel.UdpIpRecvIPV6 += data => AddNetwork(data.ProcessID, data.size);
                }
                catch
                {
                    session.Dispose();
                    throw;
                }

                _session = session;
                _thread = new Thread(() => Pump(session)) { IsBackground = true, Name = "PCPerfSuite ETW" };
                _thread.Start();

                _state = IoTraceState.Running;
                _detail = null;
                return true;
            }
            catch (Exception ex)
            {
                _session = null;
                _thread = null;
                _state = IoTraceState.Failed;
                _detail = $"La trace noyau (ETW) n'a pas pu démarrer : {ex.Message}";
                return false;
            }
        }
    }

    /// <summary>Arrête la trace et oublie les compteurs. Sans effet si elle n'est pas démarrée.</summary>
    public void Stop()
    {
        TraceEventSession? session;
        Thread? thread;
        lock (_lifecycle)
        {
            session = _session;
            thread = _thread;
            _session = null;
            _thread = null;

            // Une trace en échec garde son état : c'est ce que le diagnostic doit encore pouvoir expliquer.
            if (_state == IoTraceState.Running) _state = IoTraceState.Stopped;
        }

        try
        {
            session?.Stop(noThrow: true);
        }
        catch
        {
            // Best-effort : Dispose ci-dessous referme de toute façon la session.
        }

        // Le thread de la trace se termine dès que la session s'arrête. Le délai borne l'attente si Windows
        // tarde à la fermer ; il reste alors un thread d'arrière-plan, qui n'empêche pas l'app de quitter.
        thread?.Join(TimeSpan.FromSeconds(2));

        try
        {
            session?.Dispose();
        }
        catch
        {
            // Idem.
        }

        _totals.Clear();
    }

    /// <summary>Octets cumulés depuis le démarrage de la trace. Faux si elle ne tourne pas ; un PID dont rien
    /// n'est passé vaut zéro, ce qui est une vraie valeur — « ce processus n'a rien lu ni écrit ».</summary>
    public bool TryGetTotals(int pid, out ulong diskBytes, out ulong networkBytes)
    {
        diskBytes = 0;
        networkBytes = 0;
        if (_state != IoTraceState.Running) return false;

        if (_totals.TryGetValue(pid, out Totals? totals))
        {
            diskBytes = (ulong)Interlocked.Read(ref totals.Disk);
            networkBytes = (ulong)Interlocked.Read(ref totals.Network);
        }

        return true;
    }

    /// <summary>Un processus a disparu : ses compteurs ne serviront plus, et son PID sera réattribué.</summary>
    public void Forget(int pid) => _totals.TryRemove(pid, out _);

    public void Dispose() => Stop();

    // ----- Thread de la trace -----

    private void Pump(TraceEventSession session)
    {
        try
        {
            // Bloque jusqu'à l'arrêt de la session.
            session.Source.Process();
        }
        catch (Exception ex)
        {
            OnPumpFailed(session, ex.Message);
            return;
        }

        // Une session arrêtée de l'extérieur (un autre outil qui liste et ferme les sessions ETW) rend la
        // main sans erreur : ce n'est pas un arrêt demandé par Stop(), qui a déjà retiré _session.
        OnPumpFailed(session, "la session a été arrêtée par un autre programme");
    }

    private void OnPumpFailed(TraceEventSession session, string reason)
    {
        lock (_lifecycle)
        {
            if (!ReferenceEquals(_session, session)) return;

            _session = null;
            _thread = null;
            _state = IoTraceState.Failed;
            _detail = $"La trace noyau (ETW) s'est arrêtée : {reason}.";
        }

        try
        {
            session.Dispose();
        }
        catch
        {
            // Best-effort.
        }
    }

    // ----- Événements -----

    private void OnDisk(Microsoft.Diagnostics.Tracing.Parsers.Kernel.DiskIOTraceData data)
    {
        try
        {
            Interlocked.Increment(ref _diskEvents);
            int size = data.TransferSize;
            if (size <= 0) return;

            int pid = ResolveIssuingProcess(data.ThreadID, data.ProcessID);
            if (pid > 0) Interlocked.Add(ref TotalsFor(pid).Disk, size);
        }
        catch
        {
            // Un événement illisible n'arrête pas la trace.
        }
    }

    private void AddNetwork(int pid, int size)
    {
        try
        {
            Interlocked.Increment(ref _networkEvents);
            if (pid > 0 && size > 0) Interlocked.Add(ref TotalsFor(pid).Network, size);
        }
        catch
        {
            // Idem.
        }
    }

    private Totals TotalsFor(int pid) => _totals.GetOrAdd(pid, static _ => new Totals());

    /// <summary>Le processus qui a émis une E/S disque. L'événement nomme un thread ; on en déduit le
    /// processus par l'instantané des threads. Faute de mieux, le processus de l'en-tête de l'événement.</summary>
    private int ResolveIssuingProcess(int threadId, int headerProcessId)
    {
        if (_threadOwners.TryGetValue(threadId, out int owner)) return owner;

        // Thread créé depuis la dernière lecture : on relit, mais pas plus d'une fois par seconde.
        long now = Stopwatch.GetTimestamp();
        if (now - _lastThreadScan >= ThreadScanIntervalTicks)
        {
            RefreshThreadOwners();
            if (_threadOwners.TryGetValue(threadId, out owner)) return owner;
        }

        return headerProcessId;
    }

    private void RefreshThreadOwners()
    {
        _lastThreadScan = Stopwatch.GetTimestamp();

        IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
        if (snapshot == InvalidHandleValue) return;

        try
        {
            var owners = new Dictionary<int, int>(_threadOwners.Count + 64);
            var entry = new THREADENTRY32 { dwSize = (uint)Marshal.SizeOf<THREADENTRY32>() };
            if (!Thread32First(snapshot, ref entry)) return;

            do
            {
                owners[(int)entry.th32ThreadID] = (int)entry.th32OwnerProcessID;
            }
            while (Thread32Next(snapshot, ref entry));

            _threadOwners = owners;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    /// <summary>Une session du même nom laissée par une exécution précédente qui n'a pas pu la fermer
    /// (plantage, arrêt brutal) : elle occuperait un des emplacements de sessions système, en nombre limité.</summary>
    private static void StopOrphanSession()
    {
        try
        {
            if (!TraceEventSession.GetActiveSessionNames().Contains(SessionName)) return;

            using TraceEventSession? orphan = TraceEventSession.GetActiveSession(SessionName);
            orphan?.Stop(noThrow: true);
        }
        catch
        {
            // Si elle ne se ferme pas, le démarrage échouera et le dira.
        }
    }

    // ----- Interop -----

    private const uint TH32CS_SNAPTHREAD = 0x00000004;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct THREADENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ThreadID;
        public uint th32OwnerProcessID;
        public int tpBasePri;
        public int tpDeltaPri;
        public uint dwFlags;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Thread32First(IntPtr snapshot, ref THREADENTRY32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Thread32Next(IntPtr snapshot, ref THREADENTRY32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
