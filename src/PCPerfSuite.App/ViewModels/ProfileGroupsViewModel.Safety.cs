using System.Windows.Threading;
using Microsoft.Win32;
using PCPerfSuite.App.Utils;
using PCPerfSuite.Core.Hardware;
using PCPerfSuite.Core.Hardware.Cpu;
using PCPerfSuite.Core.PowerSettings;
using PCPerfSuite.Core.Profiles;
using PCPerfSuite.Core.Safety.Events;

namespace PCPerfSuite.App.ViewModels;

/// <summary>
/// Prudence des groupes de profils, à part pour ne pas grossir la page : la période probatoire de 30 min ouverte par une
/// application qui relève l'OC GPU ou les watts (<see cref="ProfileGroupProbation"/>). Pendant ce temps, le journal
/// Système est lu chaque minute, hors du fil d'interface : un TDR échoue la période, suspend le groupe, décoche
/// « Appliquer au démarrage » pour ce qu'il avait relevé, et prévient. Une sécurité thermique l'échoue aussi. Au terme,
/// la période est close « terminée », ou « non vérifiée » si le journal Système est resté illisible (la page le dit). La
/// décision de chaque minute est dans <see cref="ProbationWatch"/>. Aucune remise d'origine automatique : la page propose
/// un bouton.
/// </summary>
public sealed partial class ProfileGroupsViewModel
{
    private static readonly TimeSpan ProbationTick = TimeSpan.FromMinutes(1);

    /// <summary>Au lancement, les watts sont reposés après le délai des curseurs : la vérification « rien de relevé »
    /// attend un peu.</summary>
    private static readonly TimeSpan StartupCheckDelay = TimeSpan.FromSeconds(10);

    private Dispatcher? _dispatcher;
    private DispatcherTimer? _probationTimer;
    private DispatcherTimer? _startupCheck;
    private CpuControlService? _cpuService;
    private GpuControlService? _gpuService;
    private bool _readingEvents;
    private bool _disposed;

    /// <summary>TDR vu pendant la période probatoire de cette session.</summary>
    private ProfileGroupWarning? _sessionWarning;

    /// <summary>Un groupe vient d'être suspendu pendant la session (TDR pendant sa période probatoire), avec la cause :
    /// la bascule automatique (#9) le note à son journal et prévient. Sur le fil d'interface.</summary>
    public event Action<string, string>? GroupSuspended;

    private ProfileGroupProbation Probation => _applier.Probation;

    private void InitializeSafety(CpuControlService cpuService, GpuControlService gpuService)
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _cpuService = cpuService;
        _gpuService = gpuService;
        cpuService.EmergencyRestored += OnEmergencyRestored;
        gpuService.ThermalSafety.EmergencyRestored += OnEmergencyRestored;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        _probationTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = ProbationTick };
        _probationTimer.Tick += (_, _) => OnProbationTick();
        _probationTimer.Start();

        // Une période ouverte au lancement (les onglets reposent l'état risqué du groupe actif) : si rien n'est relevé
        // une fois les onglets passés, elle n'a plus d'objet.
        if (Probation.Current is { Action: ProfileGroupProbation.StartupAction })
        {
            _startupCheck = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = StartupCheckDelay };
            _startupCheck.Tick += (_, _) =>
            {
                _startupCheck?.Stop();
                if (Probation.Current is { Action: ProfileGroupProbation.StartupAction }) Probation.AfterApplication(_applier.AnyRaised);
            };
            _startupCheck.Start();
        }
    }

    /// <summary>Levé sur le fil de la sécurité thermique (relevé ou minuterie) : la suite passe par l'interface.</summary>
    private void OnEmergencyRestored(string message)
    {
        if (_dispatcher is not { } dispatcher) return;

        try
        {
            dispatcher.BeginInvoke(() =>
            {
                if (_disposed || Probation.Current is null) return;
                Probation.Fail($"sécurité thermique : {message}");
            });
        }
        catch
        {
            // interface déjà fermée
        }
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume || _dispatcher is not { } dispatcher) return;

        // Le firmware a pu rétablir ses limites : jamais « conforme » avant la relecture des onglets (HardwareResynced).
        // Priorité Send : passer avant les relectures des onglets, déjà dans la file (l'onglet Processeur s'abonne avant
        // la page), sinon ce texte écraserait leur résultat.
        try
        {
            dispatcher.InvokeAsync(() =>
            {
                if (_disposed || ActiveText is null) return;
                ConformityText = "À revérifier : une veille vient d'avoir lieu, les onglets relisent le matériel.";
            }, DispatcherPriority.Send);
        }
        catch
        {
            // interface déjà fermée
        }
    }

    private void OnProbationTick()
    {
        if (_disposed || _readingEvents || Probation.Current is not { } current) return;

        if (!ProbationWatch.NeedsEvents(current))
        {
            // Sans OC GPU, un TDR ne lui est pas imputé : seul le terme compte.
            Act(current, ProbationWatch.Decide(current, _time.GetUtcNow(), null));
            return;
        }

        _readingEvents = true;
        _ = Task.Run(() => SystemEventReader.ReadSince(current.SinceUtc, [SystemEventKind.DisplayDriverReset]))
            .ContinueWith(task =>
            {
                _readingEvents = false;
                if (_disposed || !ReferenceEquals(Probation.Current, current)) return;

                SystemEventReadResult? read = task.IsCompletedSuccessfully ? task.Result : null;
                Act(current, ProbationWatch.Decide(current, _time.GetUtcNow(), read));
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void Act(ProbationInfo current, ProbationTickDecision decision)
    {
        switch (decision.Action)
        {
            case ProbationTickAction.Close:
                Probation.Close();
                break;
            case ProbationTickAction.CloseUnverified:
                OnUnverified(current, decision.Why ?? ProbationWatch.ReadFailed);
                break;
            case ProbationTickAction.Tdr when decision.Tdr is { } tdr:
                OnTdr(current, tdr);
                break;
        }
    }

    /// <summary>Terme atteint sans avoir pu lire le journal Système : la période est close « non vérifiée », et la page
    /// le dit plutôt que de laisser croire que tout s'est bien passé.</summary>
    private void OnUnverified(ProbationInfo current, string why)
    {
        Probation.CloseUnverified(why);
        string groupName = _store.Find(current.GpuOwner?.GroupId ?? current.GroupId) is { } group ? $"du groupe « {group.Name} »" : "du groupe";
        _sessionWarning = new ProfileGroupWarning(null,
            $"La période d'essai de 30 min {groupName} n'a pas pu être vérifiée : {why}. "
            + "Un pilote graphique relancé (TDR) pendant ce temps n'a pas pu être vu. Si l'écran a clignoté ou si un jeu a planté, "
            + "réduis l'overclock avant de réappliquer ce groupe.",
            OffersGpuRestore: true);

        if (_loaded) Refresh();
    }

    /// <summary>TDR pendant la période : échec, le groupe à qui revient l'OC GPU est suspendu, cases décochées pour ce
    /// qu'il avait relevé. C'est le groupe de la période, ou celui dont elle avait repris l'OC encore en place.</summary>
    private void OnTdr(ProbationInfo current, Incident tdr)
    {
        int minutes = (int)Math.Max(0, Math.Round((tdr.LoggedUtc - current.SinceUtc).TotalMinutes));
        string cause = $"pilote graphique relancé (TDR) {minutes} min après l'application";
        Probation.Fail(cause);

        ProbationCarry owner = current.GpuRaised
            ? new ProbationCarry(current.GroupId, current.GpuRaised, current.WattsRaised, current.MadeStartupState)
            : current.Carried ?? new ProbationCarry(current.GroupId, false, current.WattsRaised, current.MadeStartupState);

        // Comme au lancement (ProfileGroupRecoveryHandler) : la case est décochée si le groupe avait fait l'état de
        // démarrage, ou si ce qu'il avait relevé est exactement ce que l'onglet reposera (groupe de jeu généré par la
        // bascule automatique).
        AppSettings saved = AppSettingsStore.Load();
        ProfileGroup? applied = _store.Find(owner.GroupId);
        bool gpuIsSaved = ProfileGroupStartupCheck.MatchesSavedGpu(applied?.Gpu, saved.Gpu);
        bool wattsAreSaved = ProfileGroupStartupCheck.MatchesSavedWatts(applied?.Cpu, saved.Cpu);

        bool gpuUnchecked = false, cpuUnchecked = false;
        if ((owner.MadeStartupState || gpuIsSaved) && owner.GpuRaised && _gpu.ApplyOverclockAtStartup)
        {
            _gpu.ApplyOverclockAtStartup = false;
            gpuUnchecked = true;
        }

        if ((owner.MadeStartupState || wattsAreSaved) && owner.WattsRaised && _cpu.ApplyAtStartup)
        {
            _cpu.ApplyAtStartup = false;
            cpuUnchecked = true;
        }

        string groupName = "le groupe";
        if (applied is { } group)
        {
            groupName = $"le groupe « {group.Name} »";
            _store.Suspensions[group.Id] = new ProfileGroupSuspension
            {
                SinceUtc = _time.GetUtcNow(),
                Cause = cause,
                CpuStartupUnchecked = cpuUnchecked,
                GpuStartupUnchecked = gpuUnchecked,
                Acknowledged = true,
            };
            Persist();

            try
            {
                GroupSuspended?.Invoke(group.Id, cause);
            }
            catch (Exception ex)
            {
                // Un abonné en échec ne doit pas empêcher l'avertissement de la page.
                CrashLog.Record(ex, "groupe suspendu : abonné");
            }
        }

        var tabs = new List<string>();
        if (cpuUnchecked) tabs.Add("Processeur");
        if (gpuUnchecked) tabs.Add("GPU");
        string startup = tabs.Count == 0 ? "" : $" « Appliquer au démarrage » a été décoché : {string.Join(" et ", tabs)}.";
        _sessionWarning = new ProfileGroupWarning(null,
            $"Le pilote graphique a été relancé (TDR) {minutes} min après l'application de {groupName} : il est suspendu.{startup} "
            + "L'overclock est peut-être encore en place : « Rendre le GPU d'origine » le retire.",
            OffersGpuRestore: true);

        if (_loaded) Refresh();
    }

    private void DisposeSafety()
    {
        _disposed = true;
        _probationTimer?.Stop();
        _startupCheck?.Stop();
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        if (_cpuService is { } cpu) cpu.EmergencyRestored -= OnEmergencyRestored;
        if (_gpuService is { } gpu) gpu.ThermalSafety.EmergencyRestored -= OnEmergencyRestored;

        // Fermeture propre : un incident survenu une fois l'app fermée ne lui est pas imputé. Jamais Dispose, qui
        // vaudrait échec.
        Probation.Close();
    }
}
