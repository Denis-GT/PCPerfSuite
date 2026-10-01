using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Win32;
using PCPerfSuite.Core.Hardware;
using PCPerfSuite.Core.PowerSettings;
using PCPerfSuite.Core.Safety;

namespace PCPerfSuite.App.ViewModels;

/// <summary>
/// Garde-fous de l'onglet GPU, à part pour ne pas grossir le reste de la classe : sécurité thermique nourrie à chaque
/// relevé (fenêtre cachée comprise) et relecture de la carte au réveil de veille.
/// </summary>
public sealed partial class GpuControlViewModel
{
    /// <summary>Au réveil, le pilote graphique n'est pas toujours prêt : relire tout de suite rendrait des valeurs
    /// fausses, ou un refus.</summary>
    private static readonly TimeSpan ResumeDelay = TimeSpan.FromSeconds(5);

    /// <summary>Plus aucun relevé depuis ce délai (lecture du GPU qui lève ou bloque après un TDR) : la sécurité reçoit
    /// un relevé vide, pour conclure à la perte de température au lieu de rester aveugle et « armée ».</summary>
    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(5);

    private Dispatcher? _dispatcher;
    private DispatcherTimer? _watchdog;

    /// <summary>Distingue une veille (à oublier) d'une panne de relevé (perte de température).</summary>
    private readonly ReadingWatchdog _readings = new(WatchdogInterval, MonotonicNow());

    private CancellationTokenSource? _resumeDelay;
    private bool _disposed;

    /// <summary>Vrai dès que la sécurité thermique a retiré l'overclock pendant cette session : le réveil ne le
    /// réapplique plus, même avec « Appliquer au démarrage ».</summary>
    private bool _emergencyThisSession;

    /// <summary>État de la sécurité thermique, affiché sous les réglages.</summary>
    [ObservableProperty] private string thermalSafetyText = "";

    private void InitializeSafety()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _gpuControl.ThermalSafety.EmergencyRestored += OnEmergencyRestored;

        // Une veille peut rendre la carte à ses réglages d'origine (ou non : cela dépend du pilote) : sans relecture,
        // l'onglet affichait encore l'overclock d'avant.
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        _watchdog = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = WatchdogInterval };
        _watchdog.Tick += OnWatchdogTick;
        _watchdog.Start();

        UpdateThermalSafetyText();
    }

    private void OnWatchdogTick(object? sender, EventArgs e)
    {
        DateTimeOffset now = MonotonicNow();
        switch (_readings.OnTick(now, _monitoring.TickInterval))
        {
            case WatchdogAction.Restart:
                _gpuControl.ThermalSafety.Restart();
                break;
            case WatchdogAction.CheckLoss when IsAvailable:
                _gpuControl.ThermalSafety.NoteNoReading(now);
                break;
        }
    }

    /// <summary>Heure monotone pour les délais de la sécurité : insensible à un recalage de l'horloge Windows. Elle
    /// avance pendant la veille, d'où la détection du silence ci-dessus.</summary>
    private static DateTimeOffset MonotonicNow() => DateTimeOffset.UnixEpoch.AddMilliseconds(Environment.TickCount64);

    /// <summary>Overclock relevé par l'app : la sécurité thermique doit continuer de lire la température du GPU, même
    /// fenêtre cachée.</summary>
    public void AddRequiredGroups(ISet<SensorGroup> into)
    {
        if (IsAvailable && _gpuControl.ThermalSafety.IsArmed) into.Add(SensorGroup.Gpu);
    }

    private void NoteSafety(HardwareSnapshot snapshot)
    {
        // Premier relevé après une veille, parfois avant le chien de garde et avant l'événement Resume : repartir de zéro.
        DateTimeOffset now = MonotonicNow();
        if (_readings.OnReading(now, _monitoring.TickInterval) == WatchdogAction.Restart) _gpuControl.ThermalSafety.Restart();

        // Relevé sans GPU (capteur absent, groupe en pause) : null, que la sécurité traite comme « non lue ».
        _gpuControl.ThermalSafety.Note(now, snapshot.Gpu?.CoreTempC, snapshot.Gpu?.HotSpotTempC);
        if (!_monitoring.IsBackgroundMode) UpdateThermalSafetyText();
    }

    private void UpdateThermalSafetyText()
    {
        GpuThermalSafety safety = _gpuControl.ThermalSafety;
        ThermalLimit core = GpuThermalSafety.CoreLimit;
        ThermalLimit hotSpot = GpuThermalSafety.HotSpotLimit;

        bool everRead = safety.CoreEverRead || safety.HotSpotEverRead;
        ThermalSafetyText = (safety.IsArmed, everRead, safety.State) switch
        {
            (false, _, _) => "Sécurité thermique : en attente, aucun réglage relevé par l'app.",
            (true, false, _) => "Sécurité thermique : armée, mais ce PC ne fournit pas la température du GPU : elle ne peut rien surveiller.",
            (true, true, ThermalState.NotMonitorable) => "Sécurité thermique : armée, en attente d'une température du GPU.",
            _ => $"Sécurité thermique : armée. La carte revient d'origine si le cœur reste à {core.ThresholdC:0} °C ou plus " +
                 $"pendant {core.Delay.TotalSeconds:0} s (point chaud : {hotSpot.ThresholdC:0} °C, s'il est lu).",
        };
    }

    private void OnEmergencyRestored(string message)
    {
        _emergencyThisSession = true;

        // Une consigne de curseur encore en attente remettrait l'overclock juste après.
        CancelPendingApplies();

        LoadPowerLimit();
        LoadOverclock();

        // Sans cela, le prochain lancement remettrait l'overclock qui vient de faire chauffer la carte. Seule la case
        // est enregistrée : les réglages enregistrés ne sont pas écrasés par les valeurs d'origine relues.
        if (ApplyOverclockAtStartup)
        {
            _suppressApply = true;
            ApplyOverclockAtStartup = false;
            _suppressApply = false;
            AppSettingsStore.Update(settings => settings.Gpu.ApplyOverclockAtStartup = false);
            message += " « Appliquer au démarrage » a été décoché : cet overclock ne sera pas remis au prochain lancement.";
        }

        OverclockStatus = message;
        UpdateThermalSafetyText();
        RaiseHardwareResynced();
    }

    private void CancelPendingApplies()
    {
        _applyDebounce.Cancel(PowerLimitKey);
        _applyDebounce.Cancel(TemperatureLimitKey);
        _applyDebounce.Cancel(VoltageKey);
        _applyDebounce.Cancel(ClockOffsetsKey);
    }

    /// <summary>Levé sur le fil de SystemEvents : la suite passe par le fil de l'interface, après un court délai.</summary>
    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume || !IsAvailable || _dispatcher is not { } dispatcher) return;

        // Tout de suite : la durée de la veille ne doit compter ni comme chaleur tenue ni comme perte de température.
        try { dispatcher.InvokeAsync(RestartSafetyAfterSleep); }
        catch { /* interface déjà fermée */ }

        var delay = new CancellationTokenSource();
        Interlocked.Exchange(ref _resumeDelay, delay)?.Cancel();

        _ = Task.Delay(ResumeDelay, delay.Token).ContinueWith(
            task =>
            {
                if (task.IsCanceled) return;
                try { dispatcher.InvokeAsync(OnResumed); }
                catch { /* interface déjà fermée */ }
            },
            CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    /// <summary>
    /// Relit la carte au réveil et affiche ses vraies valeurs, puis ne réapplique les réglages enregistrés que si
    /// « Appliquer au démarrage » est coché, pour cette carte, et que la sécurité thermique ne les a pas retirés
    /// pendant la session.
    /// </summary>
    private void OnResumed()
    {
        if (_disposed || !IsAvailable) return;

        CancelPendingApplies();
        LoadPowerLimit();
        LoadOverclock();
        _gpuControl.RefreshArming();

        GpuControlSettings saved = AppSettingsStore.Load().Gpu;
        switch (TuningResume.Decide(ApplyOverclockAtStartup, SettingsMatchCurrentGpu(saved), CanOverclock, _emergencyThisSession))
        {
            // Sous le bail d'un autre (bench, recherche d'OC), c'est à lui de reposer ce qu'il veut : on relit seulement.
            case ResumeAction.Reapply when Tuning.ManualWriteRefusal() is { } refusal:
                OverclockStatus = $"Réveil de veille : réglages relus sur la carte. Réapplication différée : {refusal}";
                break;
            case ResumeAction.Reapply:
                // L'état de démarrage revient : un groupe appliqué sans l'être n'est plus en place.
                _overclockTransient = false;
                UpdateKeepOverclockOnExit();
                ReapplySaved(saved, "Réveil de veille : réglages enregistrés réappliqués.");
                break;
            case ResumeAction.SkipAfterEmergency:
                OverclockStatus = "Réveil de veille : réglages relus sur la carte. L'overclock enregistré n'est pas réappliqué : " +
                                  "la sécurité thermique l'a retiré pendant cette session.";
                break;
            default:
                OverclockStatus = "Réveil de veille : réglages relus sur la carte.";
                break;
        }

        UpdateThermalSafetyText();
        RaiseHardwareResynced();
    }

    private void RestartSafetyAfterSleep()
    {
        if (_disposed) return;

        _readings.Restarted(MonotonicNow());
        _gpuControl.ThermalSafety.Restart();
    }

    private void DisposeSafety()
    {
        _disposed = true;
        if (_watchdog is { } watchdog)
        {
            watchdog.Stop();
            watchdog.Tick -= OnWatchdogTick;
        }

        Interlocked.Exchange(ref _resumeDelay, null)?.Cancel();
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _gpuControl.ThermalSafety.EmergencyRestored -= OnEmergencyRestored;
    }
}
