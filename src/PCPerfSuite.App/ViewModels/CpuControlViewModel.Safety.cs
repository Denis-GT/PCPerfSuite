using System.Windows.Threading;
using Microsoft.Win32;
using PCPerfSuite.Core.Hardware.Cpu;
using PCPerfSuite.Core.PowerSettings;
using PCPerfSuite.Core.Safety;

namespace PCPerfSuite.App.ViewModels;

/// <summary>
/// Garde-fous de l'onglet Processeur, à part comme ceux de l'onglet GPU (GpuControlViewModel.Safety.cs) : retour
/// d'origine d'office par la sécurité thermique, et relecture des limites au réveil de veille.
/// </summary>
public sealed partial class CpuControlViewModel
{
    private Dispatcher? _dispatcher;
    private bool _disposed;

    /// <summary>Vrai dès que la sécurité thermique a rendu les limites d'origine pendant cette session : le réveil ne
    /// réapplique plus les limites enregistrées.</summary>
    private bool _emergencyThisSession;

    private void InitializeSafety()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _cpu.EmergencyRestored += OnEmergencyRestored;

        // Une veille S3 réinitialise les limites MSR/SMU au firmware : sans relecture, l'onglet continuait d'afficher
        // la limite posée avant la veille comme si elle tenait toujours.
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    private void OnEmergencyRestored(string message)
    {
        _emergencyThisSession = true;
        _applyDebounce.Cancel(PowerLimitKey);

        if (_cpu.ReadPowerLimits() is { } snapshot)
        {
            _suppressApply = true;
            SustainedWatts = snapshot.SustainedWatts;
            BurstWatts = snapshot.BurstWatts ?? snapshot.SustainedWatts;
            _suppressApply = false;
        }

        // Sans cela, le prochain lancement reposerait les limites qui viennent de faire chauffer le processeur. Seule
        // la case est enregistrée : les limites enregistrées ne sont pas écrasées par les limites d'origine relues.
        if (ApplyAtStartup)
        {
            _suppressApply = true;
            ApplyAtStartup = false;
            _suppressApply = false;
            AppSettingsStore.Update(settings => settings.Cpu.ApplyAtStartup = false);
            message += " « Appliquer au démarrage » a été décoché : ces limites ne seront pas reposées au prochain lancement.";
        }

        Status = message;
    }

    /// <summary>Levé sur le fil de SystemEvents : la relecture touche l'interface, elle passe par son fil.</summary>
    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume || _dispatcher is not { } dispatcher) return;

        try { dispatcher.InvokeAsync(OnResumed); }
        catch { /* interface déjà fermée */ }
    }

    /// <summary>
    /// Au réveil, les limites sont toujours relues : le firmware a pu reposer les siennes, et l'onglet ne doit pas
    /// afficher une valeur qui ne tient plus. Elles ne sont réappliquées que si « Appliquer au démarrage » est coché,
    /// qu'elles sont modifiables, que l'avertissement a été accepté, et que la sécurité thermique ne les a pas
    /// retirées pendant la session (<see cref="TuningResume.Decide"/>).
    /// </summary>
    private void OnResumed()
    {
        if (_disposed) return;

        _applyDebounce.Cancel(PowerLimitKey);

        // La veille ne compte pas comme chaleur tenue, et une limite rendue au firmware ne justifie plus la sécurité.
        _cpu.RefreshAfterResume();

        if (_cpu.ReadPowerLimits() is { } snapshot)
        {
            _suppressApply = true;
            SustainedWatts = snapshot.SustainedWatts;
            BurstWatts = snapshot.BurstWatts ?? snapshot.SustainedWatts;
            _suppressApply = false;

            Status = $"Réveil de veille : limites relues, {snapshot.SustainedWatts:0} W en soutenu"
                     + (snapshot.BurstWatts is { } burst ? $", {burst:0} W en pointe." : ".");
        }

        switch (TuningResume.Decide(ApplyAtStartup, sameHardware: true, IsPowerLimitAvailable && RiskAccepted, _emergencyThisSession))
        {
            case ResumeAction.SkipAfterEmergency:
                Status += " Les limites enregistrées ne sont pas réappliquées : la sécurité thermique les a retirées pendant cette session.";
                return;
            case ResumeAction.ReadOnly:
                return;
        }

        AppSettings settings = AppSettingsStore.Load();
        if (settings.Cpu.SustainedWatts is not { } storedSustained) return;

        _suppressApply = true;
        SustainedWatts = Math.Clamp(storedSustained, MinWatts, MaxWatts);
        BurstWatts = Math.Clamp(settings.Cpu.BurstWatts ?? storedSustained, MinWatts, MaxWatts);
        _suppressApply = false;

        ApplyNow();
    }

    private void DisposeSafety()
    {
        _disposed = true;
        _cpu.EmergencyRestored -= OnEmergencyRestored;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
    }
}
