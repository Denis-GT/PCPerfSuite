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

    private Dispatcher? _dispatcher;
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
        UpdateThermalSafetyText();
    }

    /// <summary>Overclock relevé par l'app : la sécurité thermique doit continuer de lire la température du GPU, même
    /// fenêtre cachée.</summary>
    public void AddRequiredGroups(ISet<SensorGroup> into)
    {
        if (IsAvailable && _gpuControl.ThermalSafety.IsArmed) into.Add(SensorGroup.Gpu);
    }

    private void NoteSafety(HardwareSnapshot snapshot)
    {
        // Relevé sans GPU (capteur absent, groupe en pause) : null, que la sécurité traite comme « non lue ».
        _gpuControl.ThermalSafety.Note(DateTimeOffset.UtcNow, snapshot.Gpu?.CoreTempC, snapshot.Gpu?.HotSpotTempC);
        if (!_monitoring.IsBackgroundMode) UpdateThermalSafetyText();
    }

    private void UpdateThermalSafetyText()
    {
        GpuThermalSafety safety = _gpuControl.ThermalSafety;
        ThermalLimit core = GpuThermalSafety.CoreLimit;
        ThermalLimit hotSpot = GpuThermalSafety.HotSpotLimit;

        ThermalSafetyText = (safety.IsArmed, safety.CoreEverRead || safety.HotSpotEverRead) switch
        {
            (false, _) => "Sécurité thermique : en attente, aucun réglage relevé par l'app.",
            (true, true) => $"Sécurité thermique : armée. La carte revient d'origine si le cœur reste à {core.ThresholdC:0} °C ou plus " +
                            $"pendant {core.Delay.TotalSeconds:0} s (point chaud : {hotSpot.ThresholdC:0} °C, s'il est lu).",
            _ => "Sécurité thermique : armée, mais ce PC ne fournit pas la température du GPU : elle ne peut rien surveiller.",
        };
    }

    private void OnEmergencyRestored(string message)
    {
        _emergencyThisSession = true;

        // Une consigne de curseur encore en attente remettrait l'overclock juste après.
        CancelPendingApplies();

        LoadPowerLimit();
        LoadOverclock();
        OverclockStatus = message;
        UpdateThermalSafetyText();
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
        if (!ShouldReapplyAtStartup(saved))
        {
            OverclockStatus = "Réveil de veille : réglages relus sur la carte.";
        }
        else if (_emergencyThisSession)
        {
            OverclockStatus = "Réveil de veille : réglages relus sur la carte. L'overclock enregistré n'est pas réappliqué : " +
                              "la sécurité thermique l'a retiré pendant cette session.";
        }
        else
        {
            ReapplySaved(saved, "Réveil de veille : réglages enregistrés réappliqués.");
        }

        UpdateThermalSafetyText();
    }

    private void DisposeSafety()
    {
        _disposed = true;
        Interlocked.Exchange(ref _resumeDelay, null)?.Cancel();
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _gpuControl.ThermalSafety.EmergencyRestored -= OnEmergencyRestored;
    }
}
