using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using PCPerfSuite.Core.Hardware;

namespace PCPerfSuite.Core.Processes;

/// <summary>
/// Relève tous les processus et leur consommation, à la façon du Gestionnaire des tâches, avec des API Win32
/// documentées uniquement. Service à durée de vie longue : il garde d'un relevé à l'autre les compteurs
/// précédents (sans lesquels aucun %CPU ni aucun débit n'est calculable) et les handles déjà ouverts.
///
/// Ce qui a été délibérément ÉCARTÉ, et pourquoi :
/// • NtQuerySystemInformation(SystemProcessInformation) donnerait tout en un appel, mais Microsoft la
///   documente comme interne, susceptible de changer sans préavis, et publie une structure truffée de champs
///   « Reserved » — donc impossible à lire sans deviner. La page renvoie elle-même vers GetProcessMemoryInfo
///   et GetProcessHandleCount, qui sont les API retenues ici.
///   https://learn.microsoft.com/en-us/windows/win32/api/winternl/nf-winternl-ntquerysysteminformation
/// • System.Diagnostics.Process comme socle du relevé : il met ses valeurs en cache (Refresh() obligatoire),
///   lève une exception par processus protégé — des dizaines à chaque tick —, détient un handle jetable par
///   instance, et n'expose pas le working set privé. Il n'est gardé que pour FileVersionInfo.
/// • Le débit réseau par processus : aucune API documentée ne le donne sans passer par une session ETW.
///   GetExtendedTcpTable donne le PID propriétaire d'une connexion, jamais un compteur d'octets.
/// • Le %GPU par processus : le jeu de compteurs « GPU Engine » qu'utilise le Gestionnaire des tâches n'a
///   aucune page de référence chez Microsoft, et la grammaire de ses noms d'instance n'est pas contractuelle.
///
/// SeDebugPrivilege n'est volontairement PAS activé, bien que l'app tourne en administrateur. Ce privilège
/// permettrait de lire le propriétaire des processus système, mais il lèverait du même coup la protection
/// naturelle qui empêche d'ouvrir System et les CSRSS. Le compte de ces processus s'affiche donc « -- »,
/// ce qui est un prix très inférieur au risque.
/// </summary>
public sealed class ProcessService : IDisposable
{
    /// <summary>Ce qu'on retient d'un processus entre deux relevés.</summary>
    private sealed class Tracked
    {
        public IntPtr Handle;
        public DateTime? StartTimeUtc;

        /// <summary>Temps processeur cumulé au relevé précédent, en unités de 100 ns.</summary>
        public long CpuTime100ns;

        /// <summary>Octets d'E/S cumulés au relevé précédent.</summary>
        public ulong IoBytes;

        public bool HasPrevious;

        // Données immuables pour la vie du processus : résolues une fois, à sa première apparition.
        public bool ResolvedOnce;
        public string? ExecutablePath;
        public string? Description;
        public string? Publisher;
        public string? UserName;
        public bool IsSystemAccount;
        public bool IsCritical;
    }

    private readonly Dictionary<int, Tracked> _tracked = new();

    /// <summary>Sérialise tout ce qui touche <see cref="_tracked"/> et les handles qu'il garde ouverts. Le
    /// relevé tourne sur le pool de threads pendant que le thread d'interface peut, lui, appeler
    /// <see cref="ResetCounters"/> (changement d'onglet) ou <see cref="Dispose"/> (fermeture de la fenêtre).
    /// Sans ce verrou, ces deux-là ferment les handles et vident le dictionnaire sous les pieds de la boucle
    /// native : une valeur de handle refermée est aussitôt réattribuable par le noyau à un tout autre objet
    /// du processus, et le dégât ne se verrait pas ici mais ailleurs.</summary>
    private readonly object _gate = new();

    /// <summary>Métadonnées de version par chemin d'exécutable : les dizaines de svchost.exe partagent un
    /// seul fichier, il serait absurde de le relire pour chacun.</summary>
    private readonly Dictionary<string, (string? Description, string? Publisher)> _fileInfoCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Noms de comptes par SID : Translate() peut interroger un contrôleur de domaine, ça n'a rien
    /// à faire dans une boucle qui tourne toutes les deux secondes.</summary>
    private readonly Dictionary<string, (string Name, bool IsSystem)> _accountCache = new(StringComparer.Ordinal);

    private readonly uint _processorCount;
    private readonly string _windowsDirectory;

    // Voir le commentaire sur les capacités « collantes » dans le relevé.
    private bool _everSawIoRate;
    private bool _everSawPrivateWorkingSet;

    /// <summary>Charge totale telle que l'affiche le Gestionnaire des tâches : c'est la référence sur laquelle
    /// le facteur d'échelle est calibré à chaque relevé (voir <see cref="UpdateCpuScale"/>).</summary>
    private readonly PdhCounterSampler _cpuUtility = new(PdhCounterSampler.ProcessorUtility);

    /// <summary>Fréquence réelle des cœurs rapportée à leur fréquence nominale. N'est plus la source
    /// principale du facteur d'échelle, seulement son repli : c'est une moyenne sur TOUS les cœurs, donc
    /// très basse dès qu'une machine se sous-cadence au repos, ce qui divisait par trois ou quatre le %CPU
    /// de chaque processus sur un mini-PC ou un portable.</summary>
    private readonly PdhCounterSampler _cpuPerformance = new(PdhCounterSampler.ProcessorPerformance);

    // Temps système cumulés au relevé précédent, en unités de 100 ns : ils donnent la charge BRUTE de la
    // machine, exactement à l'échelle de la formule par processus, donc le dénominateur de la calibration.
    private long _lastIdle100ns;
    private long _lastKernel100ns;
    private long _lastUser100ns;
    private bool _hasSystemTimes;

    private double _cpuScale = 1.0;
    private CpuScaleSource _cpuScaleSource = CpuScaleSource.Raw;

    private long _lastTimestamp;

    /// <summary>PROCESS_MEMORY_COUNTERS_EX2 n'existe qu'à partir des mises à jour cumulatives de septembre
    /// 2023. Au premier échec on bascule définitivement sur la structure EX, disponible depuis Vista, et le
    /// working set privé devient indisponible (colonne masquée plutôt que remplie de « -- »).</summary>
    private bool _useExtendedMemoryCounters = true;

    private bool _disposed;

    public ProcessService()
    {
        // GetActiveProcessorCount et non Environment.ProcessorCount : ce dernier reflète l'affinité du
        // processus appelant, pas la machine, et fausserait tous les pourcentages si l'app était bridée.
        // System.Environment est qualifié en entier : PCPerfSuite.Core.Environment est un namespace de ce
        // projet, et il masquerait la classe du framework ici.
        uint count = GetActiveProcessorCount(ALL_PROCESSOR_GROUPS);
        _processorCount = count > 0 ? count : (uint)System.Environment.ProcessorCount;
        // Avec le séparateur final : sans lui, "C:\WindowsApps\x.exe" commence par "C:\Windows" et serait
        // classé comme un composant du système.
        _windowsDirectory = System.Environment.GetFolderPath(System.Environment.SpecialFolder.Windows)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    }

    /// <summary>Oublie les compteurs précédents : le prochain relevé n'aura donc ni %CPU ni débit (null, et
    /// surtout pas zéro). À appeler quand le relevé a été interrompu assez longtemps pour qu'un écart
    /// rapporté au temps écoulé n'ait plus aucun sens.</summary>
    public void ResetCounters()
    {
        lock (_gate)
        {
            foreach (Tracked tracked in _tracked.Values)
            {
                CloseIfValid(tracked.Handle);
                // Remis à zéro comme le fait ForgetVanishedProcesses : une valeur de handle déjà fermée ne
                // doit jamais pouvoir être refermée une seconde fois.
                tracked.Handle = IntPtr.Zero;
            }

            _tracked.Clear();
            _lastTimestamp = 0;

            // La calibration se refait elle aussi de zéro : son dénominateur est un écart de temps système,
            // qui n'a pas plus de sens qu'un écart par processus après une interruption.
            _hasSystemTimes = false;
        }
    }

    /// <summary>Relève tous les processus. Appelé depuis un thread d'arrière-plan : il fait des E/S disque
    /// (ressources de version) à la première apparition d'un exécutable.</summary>
    public ProcessSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            return GetSnapshotCore();
        }
    }

    private ProcessSnapshot GetSnapshotCore()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        long start = Stopwatch.GetTimestamp();

        // Un seul horodatage pour tout le relevé : mesurer le temps écoulé processus par processus
        // sous-évaluerait systématiquement ceux balayés en fin de liste.
        double elapsedSeconds = _lastTimestamp == 0
            ? 0
            : Stopwatch.GetElapsedTime(_lastTimestamp, start).TotalSeconds;
        bool isFirstSample = elapsedSeconds <= 0;

        double frequencyFactor = UpdateCpuScale(elapsedSeconds);

        List<ProcessEntry> entries = EnumerateProcesses();
        Dictionary<int, string> windowTitles = CollectWindowTitles();

        var processes = new List<ProcessInfo>(entries.Count);
        var seen = new HashSet<int>(entries.Count);
        int inaccessible = 0;
        // Capacités « collantes » : une fois qu'un débit d'E/S ou un jeu de travail privé a été lu, la
        // colonne reste. Les recalculer à chaque relevé la ferait disparaître dès qu'aucun processus
        // n'écrit — c'est-à-dire clignoter sur une machine au repos.
        bool anyIoRate = _everSawIoRate;
        bool anyPrivateWorkingSet = _everSawPrivateWorkingSet;

        foreach (ProcessEntry entry in entries)
        {
            seen.Add(entry.Pid);

            if (!_tracked.TryGetValue(entry.Pid, out Tracked? tracked))
            {
                tracked = new Tracked();
                _tracked[entry.Pid] = tracked;
            }

            EnsureHandle(tracked, entry.Pid);
            bool accessible = tracked.Handle != IntPtr.Zero;
            if (!accessible) inaccessible++;

            DateTime? startTime = null;
            double? cpuPercent = null;
            long? workingSet = null;
            long? privateWorkingSet = null;
            long? committed = null;
            double? ioRate = null;

            // Capturé une fois pour toute l'itération, et relu par le bloc E/S plus bas : « ce processus
            // avait-il déjà une ligne de base AVANT ce relevé ? ». Interroger tracked.HasPrevious là-bas
            // donnait toujours vrai, puisque le bloc CPU vient de le lever — un processus apparu en cours de
            // session affichait alors toutes ses E/S depuis son lancement divisées par un seul intervalle,
            // soit un pic de plusieurs centaines de Mo/s qui le propulsait en tête du tri.
            bool hadPrevious = false;

            if (accessible && GetProcessTimes(tracked.Handle, out long creation, out _, out long kernel, out long user))
            {
                startTime = SafeFromFileTimeUtc(creation);

                // Un PID réattribué se repère à une heure de démarrage différente : sans ce contrôle, l'écart
                // de temps processeur du défunt serait imputé à son successeur et produirait un pic absurde.
                if (tracked.HasPrevious && tracked.StartTimeUtc != startTime)
                {
                    tracked.HasPrevious = false;
                    tracked.ResolvedOnce = false;
                }
                tracked.StartTimeUtc = startTime;
                hadPrevious = tracked.HasPrevious;

                long cpuTime = kernel + user;
                if (hadPrevious && !isFirstSample)
                {
                    long delta = cpuTime - tracked.CpuTime100ns;
                    // Le temps processeur ne recule pas : un écart négatif ne peut venir que d'un PID
                    // réattribué qui aurait échappé au contrôle ci-dessus.
                    if (delta > 0)
                    {
                        // 1 unité = 100 ns, donc 10 000 000 unités de temps processeur disponibles par
                        // seconde et par cœur. Diviser par le nombre de processeurs logiques donne la part de
                        // la machine entière. Le Gestionnaire des tâches compte en cycles, donc en tenant
                        // compte de la fréquence réelle : d'où le facteur d'échelle, mesuré par
                        // UpdateCpuScale plutôt que supposé.
                        double percent = delta / (elapsedSeconds * _processorCount * 100_000.0) * frequencyFactor;
                        cpuPercent = Math.Clamp(percent, 0, 100);
                    }
                    else
                    {
                        cpuPercent = 0;
                    }
                }

                tracked.CpuTime100ns = cpuTime;
                // Vrai dès le premier relevé : la ligne de base vient d'être enregistrée, elle est donc
                // exploitable au relevé suivant. La condition « !isFirstSample » ci-dessus suffit déjà à
                // empêcher tout calcul sur un intervalle nul ; l'écrire aussi ici retardait la première
                // valeur d'un relevé de plus, et la colonne de tri par défaut restait vide deux tours.
                tracked.HasPrevious = true;
            }

            if (accessible && TryReadMemory(tracked.Handle, out long ws, out long? pws, out long commit))
            {
                workingSet = ws;
                privateWorkingSet = pws;
                committed = commit;
                if (pws is not null) anyPrivateWorkingSet = true;
            }

            if (accessible && GetProcessIoCounters(tracked.Handle, out IO_COUNTERS io))
            {
                // Toutes les E/S du processus, pas seulement le disque : fichiers, réseau, tubes et console.
                // C'est ce que compte la colonne du Gestionnaire des tâches, d'où le libellé « E/S ».
                ulong total = io.ReadTransferCount + io.WriteTransferCount;
                if (hadPrevious && !isFirstSample && total >= tracked.IoBytes)
                {
                    ioRate = (total - tracked.IoBytes) / elapsedSeconds;
                    anyIoRate = true;
                }
                tracked.IoBytes = total;
            }

            ResolveImmutableData(tracked, entry.Pid, accessible);

            windowTitles.TryGetValue(entry.Pid, out string? windowTitle);

            processes.Add(new ProcessInfo
            {
                Identity = new ProcessIdentity(entry.Pid, startTime ?? tracked.StartTimeUtc),
                ParentPid = entry.ParentPid,
                Name = entry.Name,
                Description = tracked.Description,
                Publisher = tracked.Publisher,
                ExecutablePath = tracked.ExecutablePath,
                WindowTitle = windowTitle,
                UserName = tracked.UserName,
                Kind = ClassifyKind(windowTitle, tracked),
                IsCritical = tracked.IsCritical,
                CpuPercent = cpuPercent,
                WorkingSetBytes = workingSet,
                PrivateWorkingSetBytes = privateWorkingSet,
                CommittedBytes = committed,
                IoBytesPerSecond = ioRate,
                ThreadCount = entry.ThreadCount,
                IsAccessible = accessible,
            });
        }

        ForgetVanishedProcesses(seen);
        _lastTimestamp = start;
        _everSawIoRate = anyIoRate;
        _everSawPrivateWorkingSet = anyPrivateWorkingSet;

        return new ProcessSnapshot
        {
            Processes = processes,
            Capabilities = new ProcessCapabilities
            {
                HasPrivateWorkingSet = anyPrivateWorkingSet,
                HasIoRate = anyIoRate,
            },
            ReadDuration = Stopwatch.GetElapsedTime(start),
            InaccessibleCount = inaccessible,
            IsFirstSample = isFirstSample,
            CpuScaleFactor = frequencyFactor,
            CpuScaleSource = _cpuScaleSource,
        };
    }

    /// <summary>
    /// Met à jour le facteur qui convertit le temps processeur brut en ce que le Gestionnaire des tâches
    /// affiche, et le renvoie.
    ///
    /// Windows compte le temps processeur en durée, indépendamment de la vitesse à laquelle les cœurs
    /// tournaient pendant cette durée. Le Gestionnaire des tâches, lui, compte en cycles : un cœur à 800 MHz
    /// occupé une seconde n'y vaut pas un cœur à 4 GHz occupé une seconde. D'où un facteur d'échelle.
    ///
    /// Ce facteur était auparavant lu sur « % Processor Performance », la fréquence moyenne de TOUS les
    /// cœurs rapportée à la fréquence nominale. C'était une hypothèse sur la machine, et elle est fausse
    /// dans les deux sens : sur un PC qui se sous-cadence au repos — mini-PC, portable, processeur basse
    /// consommation — elle vaut 25 à 40 %, et divisait donc par trois ou quatre le %CPU de chaque processus ;
    /// quand le compteur manque, elle vaut 1, et les valeurs sont deux à trois fois trop basses.
    ///
    /// On le MESURE désormais : « % Processor Utility » est exactement la charge totale qu'affiche le
    /// Gestionnaire des tâches, et GetSystemTimes donne la charge brute de la même machine sur le même
    /// intervalle, à la même échelle que la formule par processus. Leur rapport est le facteur cherché, et
    /// par construction la somme de nos lignes suit le total de Windows, à n'importe quelle fréquence.
    /// https://learn.microsoft.com/en-us/windows/win32/api/sysinfoapi/nf-sysinfoapi-getsystemtimes
    /// </summary>
    /// <param name="elapsedSeconds">Durée écoulée depuis le relevé précédent ; 0 au tout premier.</param>
    private double UpdateCpuScale(double elapsedSeconds)
    {
        // Toujours échantillonner, même au premier relevé : ces deux compteurs sont des compteurs de taux,
        // ils n'ont de valeur qu'à partir de leur deuxième collecte. Sauter un tour retarderait d'autant.
        double? utility = _cpuUtility.Sample();
        double? performance = _cpuPerformance.Sample();

        double? rawTotalPercent = null;
        if (GetSystemTimes(out long idle, out long kernel, out long user))
        {
            if (_hasSystemTimes && elapsedSeconds > 0)
            {
                // kernel CONTIENT idle : le temps disponible est donc kernel + user, et le temps occupé
                // kernel + user - idle. Le rapport des deux est un pourcentage déjà normalisé par le nombre
                // de cœurs, sans avoir à le réintroduire.
                double total = (double)(kernel - _lastKernel100ns) + (user - _lastUser100ns);
                double busy = total - (idle - _lastIdle100ns);
                if (total > 0 && busy >= 0) rawTotalPercent = busy / total * 100.0;
            }

            _lastIdle100ns = idle;
            _lastKernel100ns = kernel;
            _lastUser100ns = user;
            _hasSystemTimes = true;
        }

        // Sous ce seuil, le rapport de deux petits nombres n'est que du bruit : une machine au repos ferait
        // sauter le facteur d'un relevé à l'autre. On garde alors le dernier facteur mesuré, qui reste
        // valable — c'est bien le but d'une calibration que de survivre aux moments où l'on ne mesure rien.
        const double CalibrationFloorPercent = 2.0;

        if (utility is { } u && u > 0 && rawTotalPercent is { } raw && raw >= CalibrationFloorPercent)
        {
            // Bornes larges : elles n'existent que pour qu'une lecture aberrante d'un compteur ne rende pas
            // la colonne absurde, pas pour corriger une mesure.
            double measured = Math.Clamp(u / raw, 0.2, 5.0);

            // Lissage : le facteur doit suivre les changements de fréquence, pas les sauts d'un seul relevé.
            _cpuScale = _cpuScaleSource == CpuScaleSource.Calibrated
                ? _cpuScale + 0.35 * (measured - _cpuScale)
                : measured;
            _cpuScaleSource = CpuScaleSource.Calibrated;
            return _cpuScale;
        }

        // Déjà calibré au moins une fois : le dernier facteur mesuré vaut mieux que n'importe quel repli.
        if (_cpuScaleSource == CpuScaleSource.Calibrated) return _cpuScale;

        if (performance is { } p && p > 0)
        {
            _cpuScale = p / 100.0;
            _cpuScaleSource = CpuScaleSource.Performance;
            return _cpuScale;
        }

        _cpuScale = 1.0;
        _cpuScaleSource = CpuScaleSource.Raw;
        return _cpuScale;
    }

    /// <summary>
    /// Termine un processus, après avoir revérifié qu'il s'agit bien de celui qui était affiché. L'appel est
    /// asynchrone côté Windows (« it initiates termination and returns immediately ») : on attend donc la
    /// disparition effective avant d'annoncer un succès, plutôt que de se fier au code de retour.
    /// https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-terminateprocess
    /// </summary>
    public TerminateResult Terminate(ProcessIdentity identity)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!identity.IsComplete)
        {
            return new TerminateResult(TerminateFailure.IdentityMismatch,
                "L'heure de démarrage de ce processus n'a pas pu être lue : impossible de garantir qu'il "
                + "s'agit toujours du même.");
        }

        // Handle ouvert pour l'occasion, avec le droit de terminaison : celui du relevé est volontairement
        // limité à la lecture et n'est jamais porteur de PROCESS_TERMINATE.
        IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_TERMINATE | SYNCHRONIZE,
            false, (uint)identity.Pid);
        if (handle == IntPtr.Zero)
        {
            int error = Marshal.GetLastPInvokeError();
            return error switch
            {
                ERROR_INVALID_PARAMETER => new TerminateResult(TerminateFailure.AlreadyExited),
                ERROR_ACCESS_DENIED => new TerminateResult(TerminateFailure.AccessDenied),
                _ => new TerminateResult(TerminateFailure.Other, new Win32Exception(error).Message),
            };
        }

        try
        {
            if (!GetProcessTimes(handle, out long creation, out _, out _, out _))
            {
                return new TerminateResult(TerminateFailure.Other,
                    new Win32Exception(Marshal.GetLastPInvokeError()).Message);
            }

            if (SafeFromFileTimeUtc(creation) != identity.StartTimeUtc)
            {
                return new TerminateResult(TerminateFailure.IdentityMismatch);
            }

            if (!TerminateProcess(handle, 1))
            {
                int error = Marshal.GetLastPInvokeError();

                // Un processus déjà mort renvoie ERROR_ACCESS_DENIED : son handle est alors signalé, ce qui
                // distingue le « trop tard » du « pas le droit ».
                if (WaitForSingleObject(handle, 0) == WAIT_OBJECT_0)
                {
                    return new TerminateResult(TerminateFailure.AlreadyExited);
                }

                return error == ERROR_ACCESS_DENIED
                    ? new TerminateResult(TerminateFailure.AccessDenied)
                    : new TerminateResult(TerminateFailure.Other, new Win32Exception(error).Message);
            }

            return WaitForSingleObject(handle, TerminateWaitMs) == WAIT_OBJECT_0
                ? TerminateResult.Ok
                : new TerminateResult(TerminateFailure.StillRunning);
        }
        finally
        {
            CloseIfValid(handle);
        }
    }

    public void Dispose()
    {
        // Sous le même verrou que le relevé : à la fermeture de la fenêtre, un relevé peut très bien être en
        // vol sur le pool. Le thread d'interface attend ici qu'il se termine — quelques centaines de
        // millisecondes au pire — plutôt que de lui fermer ses handles en pleine boucle.
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;

            foreach (Tracked tracked in _tracked.Values)
            {
                CloseIfValid(tracked.Handle);
                tracked.Handle = IntPtr.Zero;
            }
            _tracked.Clear();
            _cpuUtility.Dispose();
            _cpuPerformance.Dispose();
        }
    }

    // ----- Énumération -----

    private readonly record struct ProcessEntry(int Pid, int ParentPid, int ThreadCount, string Name);

    /// <summary>Instantané Toolhelp : c'est la seule source documentée qui donne, en un seul appel et sans
    /// ouvrir chaque processus, le PID parent et le nombre de threads.</summary>
    private static List<ProcessEntry> EnumerateProcesses()
    {
        var entries = new List<ProcessEntry>(400);

        IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == InvalidHandleValue) return entries;

        try
        {
            // dwSize doit être renseigné avant le premier appel, sinon Process32FirstW échoue (la doc le dit
            // explicitement).
            var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (!Process32FirstW(snapshot, ref entry)) return entries;

            do
            {
                entries.Add(new ProcessEntry(
                    (int)entry.th32ProcessID,
                    (int)entry.th32ParentProcessID,
                    (int)entry.cntThreads,
                    entry.szExeFile));
            }
            while (Process32NextW(snapshot, ref entry));
        }
        finally
        {
            CloseIfValid(snapshot);
        }

        return entries;
    }

    /// <summary>Table PID → titre de la fenêtre principale, construite en un seul balayage. Ne retient que
    /// les fenêtres de premier plan visibles et non possédées : une boîte de dialogue ou une fenêtre outil
    /// ferait passer pour « application » un processus qui n'en est pas une.</summary>
    private static Dictionary<int, string> CollectWindowTitles()
    {
        var titles = new Dictionary<int, string>();

        bool Callback(IntPtr window, IntPtr _)
        {
            if (!IsWindowVisible(window)) return true;
            if (GetWindow(window, GW_OWNER) != IntPtr.Zero) return true;

            int length = GetWindowTextLength(window);
            if (length <= 0) return true;

            var buffer = new char[length + 1];
            int written = GetWindowText(window, buffer, buffer.Length);
            if (written <= 0) return true;

            GetWindowThreadProcessId(window, out uint pid);
            if (pid == 0) return true;

            // La première fenêtre rencontrée fait foi : Windows les énumère de la plus en avant à la plus en
            // arrière, donc c'est celle que l'utilisateur considère comme la fenêtre du programme.
            titles.TryAdd((int)pid, new string(buffer, 0, written));
            return true;
        }

        var callback = new EnumWindowsProc(Callback);
        EnumWindows(callback, IntPtr.Zero);

        // GetWindowTextW est explicitement conçu pour ne pas bloquer sur un processus tiers qui ne répond
        // pas — contrairement à un SendMessage(WM_GETTEXT), qu'il ne faut surtout pas utiliser ici.
        GC.KeepAlive(callback);
        return titles;
    }

    // ----- Lectures par processus -----

    private void EnsureHandle(Tracked tracked, int pid)
    {
        if (tracked.Handle != IntPtr.Zero) return;

        // PROCESS_QUERY_LIMITED_INFORMATION (et jamais PROCESS_QUERY_INFORMATION) : c'est le droit
        // volontairement restreint que Windows accorde aussi vers les processus protégés, et il suffit à
        // GetProcessTimes, GetProcessMemoryInfo, GetProcessIoCounters et IsProcessCritical.
        // Idle (PID 0) et System/CSRSS restent inaccessibles par construction : leur ligne s'affiche vide.
        tracked.Handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
    }

    private bool TryReadMemory(IntPtr handle, out long workingSet, out long? privateWorkingSet, out long committed)
    {
        workingSet = 0;
        privateWorkingSet = null;
        committed = 0;

        if (_useExtendedMemoryCounters)
        {
            var counters = new PROCESS_MEMORY_COUNTERS_EX2 { cb = (uint)Marshal.SizeOf<PROCESS_MEMORY_COUNTERS_EX2>() };
            if (GetProcessMemoryInfoEx2(handle, ref counters, counters.cb))
            {
                workingSet = (long)counters.WorkingSetSize;
                privateWorkingSet = (long)counters.PrivateWorkingSetSize;
                committed = (long)counters.PrivateUsage;
                return true;
            }

            // Un refus peut venir du processus lui-même (déjà mort) comme de la structure trop récente pour
            // ce Windows. On ne bascule définitivement que sur le second cas, que Windows signale par un
            // paramètre invalide.
            if (Marshal.GetLastPInvokeError() == ERROR_INVALID_PARAMETER)
            {
                _useExtendedMemoryCounters = false;
            }
        }

        var legacy = new PROCESS_MEMORY_COUNTERS_EX { cb = (uint)Marshal.SizeOf<PROCESS_MEMORY_COUNTERS_EX>() };
        if (!GetProcessMemoryInfoEx(handle, ref legacy, legacy.cb)) return false;

        workingSet = (long)legacy.WorkingSetSize;
        committed = (long)legacy.PrivateUsage;
        return true;
    }

    /// <summary>Chemin, métadonnées, compte propriétaire et drapeau « critique » : tout cela ne change pas de
    /// la vie du processus, et se résout donc une seule fois, à sa première apparition.</summary>
    private void ResolveImmutableData(Tracked tracked, int pid, bool accessible)
    {
        if (tracked.ResolvedOnce) return;

        // Un processus tout juste lancé peut n'être pas encore ouvrable. Marquer la résolution comme
        // faite avant d'y arriver le condamnerait à rester sans chemin, sans éditeur et sans compte pour
        // toute sa vie, même une fois devenu lisible : on réessaiera au relevé suivant.
        if (!accessible) return;

        tracked.ResolvedOnce = true;

        tracked.ExecutablePath = TryGetImagePath(tracked.Handle);
        if (tracked.ExecutablePath is { Length: > 0 } path)
        {
            (tracked.Description, tracked.Publisher) = GetFileMetadata(path);
        }

        if (IsProcessCritical(tracked.Handle, out bool critical))
        {
            tracked.IsCritical = critical;
        }

        (tracked.UserName, tracked.IsSystemAccount) = TryGetOwner(tracked.Handle);

        _ = pid;
    }

    private static string? TryGetImagePath(IntPtr handle)
    {
        // Le tampon est dimensionné pour les chemins longs, et lpdwSize se compte en CARACTÈRES, pas en octets.
        var buffer = new char[32768];
        uint size = (uint)buffer.Length;
        return QueryFullProcessImageName(handle, 0, buffer, ref size) && size > 0
            ? new string(buffer, 0, (int)size)
            : null;
    }

    private (string? Description, string? Publisher) GetFileMetadata(string path)
    {
        if (_fileInfoCache.TryGetValue(path, out (string? Description, string? Publisher) cached)) return cached;

        (string? Description, string? Publisher) result = (null, null);
        try
        {
            FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);
            string? description = string.IsNullOrWhiteSpace(info.FileDescription) ? info.ProductName : info.FileDescription;
            result = (Blank(description), Blank(info.CompanyName));
        }
        catch (FileNotFoundException) { /* l'exécutable a été supprimé ou déplacé depuis le lancement */ }
        catch (IOException) { /* fichier verrouillé ou volume indisponible */ }
        catch (UnauthorizedAccessException) { /* lecture refusée malgré l'élévation */ }

        _fileInfoCache[path] = result;
        return result;

        static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>Compte propriétaire, et si c'est un compte système. Échoue silencieusement pour les processus
    /// dont le jeton n'est pas lisible : SeDebugPrivilege n'est pas activé, c'est assumé (voir l'en-tête).</summary>
    private (string? Name, bool IsSystem) TryGetOwner(IntPtr processHandle)
    {
        if (!OpenProcessToken(processHandle, TOKEN_QUERY, out IntPtr token)) return (null, false);

        IntPtr buffer = IntPtr.Zero;
        try
        {
            GetTokenInformation(token, TokenUser, IntPtr.Zero, 0, out uint needed);
            if (needed == 0) return (null, false);

            buffer = Marshal.AllocHGlobal((int)needed);
            if (!GetTokenInformation(token, TokenUser, buffer, needed, out _)) return (null, false);

            // TOKEN_USER ne contient qu'un SID_AND_ATTRIBUTES, dont le premier champ est le pointeur de SID :
            // il est donc en tête du tampon, et SecurityIdentifier sait partir directement de ce pointeur.
            var sid = new SecurityIdentifier(Marshal.ReadIntPtr(buffer));
            string key = sid.Value;
            if (_accountCache.TryGetValue(key, out (string Name, bool IsSystem) cached)) return cached;

            bool isSystem = sid.IsWellKnown(WellKnownSidType.LocalSystemSid)
                            || sid.IsWellKnown(WellKnownSidType.LocalServiceSid)
                            || sid.IsWellKnown(WellKnownSidType.NetworkServiceSid);

            string name;
            try
            {
                name = sid.Translate(typeof(NTAccount)).Value;
            }
            catch (IdentityNotMappedException)
            {
                // Compte d'un domaine injoignable ou SID sans correspondance : la forme SDDL vaut mieux que rien.
                name = key;
            }
            catch (SystemException)
            {
                name = key;
            }

            (string Name, bool IsSystem) resolved = (name, isSystem);
            _accountCache[key] = resolved;
            return resolved;
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            CloseIfValid(token);
        }
    }

    /// <summary>
    /// Classement Application / Windows / Arrière-plan. Windows n'expose aucune API pour ça : c'est une
    /// heuristique, et elle est assumée comme telle. Une fenêtre principale visible fait l'application ;
    /// à défaut, un exécutable du dossier Windows ou un compte système fait le composant Windows.
    /// Le nom du fichier n'entre jamais en ligne de compte : il s'usurpe trivialement.
    /// </summary>
    private ProcessKind ClassifyKind(string? windowTitle, Tracked tracked)
    {
        if (!string.IsNullOrEmpty(windowTitle)) return ProcessKind.Application;

        if (tracked.IsSystemAccount) return ProcessKind.Windows;

        if (tracked.ExecutablePath is { Length: > 0 } path
            && path.StartsWith(_windowsDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return ProcessKind.Windows;
        }

        return ProcessKind.Background;
    }

    private void ForgetVanishedProcesses(HashSet<int> seen)
    {
        List<int>? gone = null;
        foreach ((int pid, Tracked tracked) in _tracked)
        {
            if (seen.Contains(pid)) continue;

            CloseIfValid(tracked.Handle);
            tracked.Handle = IntPtr.Zero;
            (gone ??= new List<int>()).Add(pid);
        }

        if (gone is null) return;
        foreach (int pid in gone) _tracked.Remove(pid);
    }

    /// <summary>Une heure de création hors plage ne doit pas faire tomber tout le relevé : certains
    /// pseudo-processus renvoient zéro.</summary>
    private static DateTime? SafeFromFileTimeUtc(long fileTime)
    {
        if (fileTime <= 0) return null;

        try
        {
            return DateTime.FromFileTimeUtc(fileTime);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static void CloseIfValid(IntPtr handle)
    {
        if (handle != IntPtr.Zero && handle != InvalidHandleValue) CloseHandle(handle);
    }

    // ----- Interop -----
    //
    // Toutes les structures ci-dessous sont recopiées des en-têtes du SDK Windows 10.0.26100 et suivent
    // l'alignement naturel du 64 bits (le projet est en AnyCPU et tourne en 64 bits). Aucune n'est soumise à
    // une directive de packing dans ces en-têtes. SIZE_T se marshale en nuint : le remplacer par uint
    // casserait silencieusement la disposition en 64 bits.

    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private const uint PROCESS_TERMINATE = 0x0001;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint SYNCHRONIZE = 0x00100000;
    private const uint TOKEN_QUERY = 0x0008;
    private const ushort ALL_PROCESSOR_GROUPS = 0xFFFF;
    private const uint GW_OWNER = 4;

    /// <summary>TOKEN_INFORMATION_CLASS.TokenUser, seule valeur de l'énumération que la documentation écrit
    /// explicitement (= 1) ; vérifiée dans winnt.h du SDK 10.0.26100.</summary>
    private const int TokenUser = 1;

    private const uint WAIT_OBJECT_0 = 0x00000000;
    private const int ERROR_ACCESS_DENIED = 5;
    private const int ERROR_INVALID_PARAMETER = 87;

    /// <summary>Délai laissé au système pour faire disparaître le processus avant de dire qu'il est toujours
    /// là. TerminateProcess ne fait qu'amorcer la terminaison et rend la main aussitôt.</summary>
    private const uint TerminateWaitMs = 3000;

    private static readonly IntPtr InvalidHandleValue = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;

        // ULONG_PTR : 8 octets en 64 bits. Le déclarer en uint décalerait tout ce qui suit, notamment le PID
        // parent et le nombre de threads.
        public UIntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_MEMORY_COUNTERS_EX
    {
        public uint cb;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
        public nuint PrivateUsage;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_MEMORY_COUNTERS_EX2
    {
        public uint cb;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
        public nuint PrivateUsage;
        public nuint PrivateWorkingSetSize;
        public ulong SharedCommitUsage;
    }

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    // Les FILETIME sont reçus en long : ce sont deux DWORD contigus, soit exactement un entier 64 bits en
    // little-endian, et c'est un compte de tranches de 100 ns.
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(IntPtr process, out long creationTime, out long exitTime,
        out long kernelTime, out long userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(IntPtr process, out IO_COUNTERS counters);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessCritical(IntPtr process, [MarshalAs(UnmanagedType.Bool)] out bool critical);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetActiveProcessorCount(ushort groupNumber);

    /// <summary>Temps système cumulés, en unités de 100 ns. Attention : <c>kernel</c> INCLUT <c>idle</c>,
    /// ce que la documentation précise explicitement.</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, [Out] char[] exeName, ref uint size);

    [DllImport("psapi.dll", EntryPoint = "GetProcessMemoryInfo", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessMemoryInfoEx(IntPtr process, ref PROCESS_MEMORY_COUNTERS_EX counters, uint size);

    [DllImport("psapi.dll", EntryPoint = "GetProcessMemoryInfo", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessMemoryInfoEx2(IntPtr process, ref PROCESS_MEMORY_COUNTERS_EX2 counters, uint size);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(IntPtr token, int informationClass, IntPtr information,
        uint informationLength, out uint returnLength);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint command);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(IntPtr window, [Out] char[] text, int maxCount);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextLengthW", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr window);
}
