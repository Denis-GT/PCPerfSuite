using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PCPerfSuite.Core.Hardware;

/// <summary>Tick du relevé. <see cref="Epoch"/> change avec la durée du tick, et <see cref="Index"/> repart alors de zéro.</summary>
/// <param name="Skipped">Ticks sautés juste avant celui-ci, parce que le précédent a débordé sur leur échéance.</param>
public readonly record struct SamplingTick(long Epoch, long Index, int Skipped);

/// <summary>
/// Boucle de relevé à cadence fixe, sur un thread dédié. Elle vise des échéances absolues (t0 + k × intervalle)
/// plutôt qu'un délai à partir du tick précédent : un tick en retard ne décale pas les suivants, et un tick qui
/// déborde sur l'échéance suivante la fait sauter au lieu d'empiler du retard. Elle ne dépend pas non plus de
/// l'occupation du thread de l'interface, contrairement à un DispatcherTimer.
/// </summary>
public sealed class SamplingLoop : IDisposable
{
    private readonly Func<TimeSpan> _getInterval;
    private readonly Action<SamplingTick> _onTick;
    private readonly CancellationTokenSource _cts = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;

    /// <param name="getInterval">Durée du tick, relue avant chaque attente.</param>
    /// <param name="onTick">Appelé sur le thread de la boucle ; une exception y est ignorée pour ne pas arrêter les relevés.</param>
    public SamplingLoop(Func<TimeSpan> getInterval, Action<SamplingTick> onTick)
    {
        _getInterval = getInterval;
        _onTick = onTick;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "PCPerfSuite sampling",
            Priority = ThreadPriority.AboveNormal,
        };
    }

    public void Start() => _thread.Start();

    /// <summary>À appeler quand la durée du tick change : l'attente en cours est interrompue pour appliquer
    /// tout de suite la nouvelle cadence (sinon, passer d'une minute à une seconde attendrait la minute).</summary>
    public void IntervalChanged()
    {
        if (_cts.IsCancellationRequested) return;

        // Le test ci-dessus et ce Set ne sont pas indivisibles : l'arrêt peut s'intercaler entre les deux
        // et libérer le handle sous nos pieds. Un réglage modifié à l'instant précis de la fermeture n'a
        // aucun intérêt, donc on laisse simplement tomber.
        try { _wake.Set(); }
        catch (ObjectDisposedException) { /* boucle déjà arrêtée */ }
    }

    private void Run()
    {
        using var waiter = new PreciseWaiter();
        WaitHandle[] interrupts = { _wake, _cts.Token.WaitHandle };

        long epoch = 0;
        long index = 0;
        int skipped = 0;
        TimeSpan interval = Sanitize(_getInterval());
        long origin = Stopwatch.GetTimestamp();

        while (!_cts.IsCancellationRequested)
        {
            TimeSpan current = Sanitize(_getInterval());
            if (current != interval)
            {
                interval = current;
                epoch++;
                index = 0;
                skipped = 0;
                origin = Stopwatch.GetTimestamp();
            }

            long intervalTicks = ToStopwatchTicks(interval);
            long deadline = origin + index * intervalTicks;

            // Réveillé avant l'échéance (réglage modifié ou arrêt) : on réévalue la cadence.
            if (!waiter.WaitUntil(deadline, interrupts)) continue;

            try
            {
                _onTick(new SamplingTick(epoch, index, skipped));
            }
            catch
            {
                // L'appelant gère ses erreurs ; la boucle, elle, ne doit jamais s'arrêter.
            }

            // Prochaine échéance encore à venir : celles déjà dépassées sont sautées.
            long elapsed = Stopwatch.GetTimestamp() - origin;
            long next = Math.Max(index + 1, elapsed / intervalTicks + 1);
            skipped = (int)(next - index - 1);
            index = next;
        }
    }

    private static TimeSpan Sanitize(TimeSpan interval)
        => interval < TimeSpan.FromMilliseconds(10) ? TimeSpan.FromMilliseconds(10) : interval;

    private static long ToStopwatchTicks(TimeSpan interval)
        => (long)(interval.TotalSeconds * Stopwatch.Frequency);

    /// <summary>Délai laissé au relevé en cours pour finir avant qu'on abandonne. Un groupe coûteux ou un
    /// pilote qui traîne peut dépasser deux secondes ; au-delà, c'est qu'il ne rendra pas la main.</summary>
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    public void Dispose()
    {
        _cts.Cancel();

        if (_thread.IsAlive && !_thread.Join(StopTimeout))
        {
            // Le thread garde ses handles : les libérer sous lui ferait planter la boucle. Ce qu'il lit,
            // lui, est protégé par le verrou de HardwareMonitorService, qui attend la fin du relevé avant
            // de fermer quoi que ce soit — on peut donc l'abandonner sans danger, mais pas sans le dire.
            Debug.WriteLine($"SamplingLoop : relevé toujours en cours après {StopTimeout.TotalSeconds:0} s, thread abandonné.");
            return;
        }

        _cts.Dispose();
        _wake.Dispose();
    }

    /// <summary>
    /// Attente jusqu'à un instant précis. Le minuteur haute résolution de Windows (Windows 10 1803 et plus) tient la
    /// milliseconde ; les attentes classiques, elles, s'arrondissent au quantum de l'ordonnanceur (~15,6 ms), ce qui
    /// se voit à l'écran comme une cadence irrégulière.
    /// </summary>
    private sealed class PreciseWaiter : IDisposable
    {
        private const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x00000002;
        private const uint TIMER_ALL_ACCESS = 0x001F0003;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeWaitHandle CreateWaitableTimerExW(IntPtr lpTimerAttributes, string? lpTimerName, uint dwFlags, uint dwDesiredAccess);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWaitableTimer(SafeWaitHandle hTimer, in long lpDueTime, int lPeriod, IntPtr pfnCompletionRoutine,
            IntPtr lpArgToCompletionRoutine, [MarshalAs(UnmanagedType.Bool)] bool fResume);

        private readonly TimerWaitHandle? _timer;

        public PreciseWaiter()
        {
            try
            {
                SafeWaitHandle handle = CreateWaitableTimerExW(IntPtr.Zero, null, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
                if (handle.IsInvalid) handle.Dispose();
                else _timer = new TimerWaitHandle(handle);
            }
            catch
            {
                // Pas de minuteur haute résolution : attente classique, un peu moins régulière.
            }
        }

        /// <summary>Vrai à l'échéance, faux si l'un des <paramref name="interrupts"/> a été signalé avant.</summary>
        public bool WaitUntil(long deadlineTimestamp, WaitHandle[] interrupts)
        {
            long remaining = deadlineTimestamp - Stopwatch.GetTimestamp();
            if (remaining <= 0) return true;

            // Unités de 100 ns, valeur négative : échéance relative.
            long relative = -(long)(remaining * (10_000_000.0 / Stopwatch.Frequency));

            if (_timer is not null && SetWaitableTimer(_timer.SafeWaitHandle, relative, 0, IntPtr.Zero, IntPtr.Zero, false))
            {
                var handles = new WaitHandle[interrupts.Length + 1];
                handles[0] = _timer;
                interrupts.CopyTo(handles, 1);
                return WaitHandle.WaitAny(handles) == 0;
            }

            int milliseconds = (int)Math.Ceiling(remaining * 1000.0 / Stopwatch.Frequency);
            return WaitHandle.WaitAny(interrupts, milliseconds) == WaitHandle.WaitTimeout;
        }

        public void Dispose() => _timer?.Dispose();

        private sealed class TimerWaitHandle : WaitHandle
        {
            public TimerWaitHandle(SafeWaitHandle handle) => SafeWaitHandle = handle;
        }
    }
}
