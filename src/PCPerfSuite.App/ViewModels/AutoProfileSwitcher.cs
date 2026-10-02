using System.Windows.Threading;
using Microsoft.Win32;
using PCPerfSuite.App.Utils;
using PCPerfSuite.Core.Hardware;
using PCPerfSuite.Core.Hardware.Cpu;
using PCPerfSuite.Core.PowerSettings;
using PCPerfSuite.Core.Processes;
using PCPerfSuite.Core.Profiles;
using PCPerfSuite.Core.SystemInfo;

namespace PCPerfSuite.App.ViewModels;

/// <summary>
/// Bascule automatique de profils selon l'usage (#9, F18) : à chaque relevé, l'application au premier plan, le plein
/// écran, les charges et les FPS RTSS passent par le classifieur (<see cref="UsageClassifier"/>), l'historique les
/// agrège (usage.json), et la politique (<see cref="AutoSwitchPolicy"/>) décide d'appliquer le groupe de l'usage par la
/// page Profils, seule à écrire les groupes. Moteur déterministe et explicable (D3) : aucun LLM ne décide.
///
/// <list type="bullet">
/// <item>Travail O(1) par relevé sur le fil d'interface, dédoublonné par l'heure du relevé ; une charge n'est prise que
/// si son groupe vient d'être relu. Continue fenêtre cachée : inscrit au mode éco (<see cref="IBackgroundSensorConsumer"/>).</item>
/// <item>Une bascule passe par l'orchestrateur de #8 : bail pris le temps d'appliquer, écritures réelles relues avant le
/// rapport, pas d'état de démarrage (rendu à la fermeture, D7 ; le plan d'alimentation, lui, reste), prudence au
/// démarrage (période probatoire, demandeur « bascule-auto »). Jamais de contournement de l'accord de risque CPU ni de la
/// renonciation Intel : les planificateurs refusent, le journal le dit.</item>
/// <item>Verrouillée après une sécurité thermique CPU ou GPU ; en pause sous le bail d'un autre demandeur, pendant le
/// réglage d'un groupe dans un onglet et 10 min après un réglage manuel (l'usage en cours est alors « adopté »).</item>
/// <item>Rien n'est relevé ni enregistré tant qu'elle est désactivée (chemins d'exécutables : données personnelles,
/// jamais au diagnostic ni au journal de session).</item>
/// </list>
/// Arrêtée en tête de MainViewModel.Dispose, avant le relevé et les onglets.
/// </summary>
public sealed class AutoProfileSwitcher : IBackgroundSensorConsumer, IDisposable
{
    public static readonly TimeSpan SaveInterval = TimeSpan.FromMinutes(5);

    /// <summary>Attente maximale de l'enregistrement de l'historique à la fermeture.</summary>
    private static readonly TimeSpan FlushTimeout = TimeSpan.FromSeconds(2);

    private readonly MonitoringViewModel _monitoring;
    private readonly ProfileGroupsViewModel _profiles;
    private readonly CpuControlViewModel _cpu;
    private readonly GpuControlViewModel _gpu;
    private readonly FanCurvesViewModel _fans;
    private readonly TuningLease _lease;
    private readonly TuningStatusViewModel _tuning;
    private readonly CpuControlService _cpuService;
    private readonly GpuControlService _gpuService;
    private readonly bool _hasBattery;
    private readonly Dispatcher _dispatcher;
    private readonly TimeProvider _time = TimeProvider.System;

    private readonly ForegroundAppReader _reader = new();
    private readonly ApplicationPublisherCache _publishers = new();
    private readonly UsageClassifier _classifier = new();
    private readonly UsageHistoryWriter _writer = new(() => AppDataPaths.Current.UsageFile);

    private AutoSwitchSettings _settings;
    private UsageHistory? _history;
    private bool _historyLoading;
    private DateTime _lastCaptured;
    private DateTimeOffset _warmupUntil;
    private DateTimeOffset _lastSaveUtc;
    private DateTimeOffset? _lastSwitchUtc;
    private AutoSwitchHandled? _lastHandled;
    private string? _lockReason;
    private DateTimeOffset? _manualPauseUntil;
    private string? _manualPauseSource;
    private bool _switching;
    private bool _onBattery;
    private bool _tickErrorLogged;
    private bool _disposed;

    public AutoProfileSwitcher(
        MonitoringViewModel monitoring,
        ProfileGroupsViewModel profiles,
        CpuControlViewModel cpu,
        GpuControlViewModel gpu,
        FanCurvesViewModel fans,
        TuningLease lease,
        TuningStatusViewModel tuning,
        CpuControlService cpuService,
        GpuControlService gpuService,
        bool hasBattery)
    {
        _monitoring = monitoring;
        _profiles = profiles;
        _cpu = cpu;
        _gpu = gpu;
        _fans = fans;
        _lease = lease;
        _tuning = tuning;
        _cpuService = cpuService;
        _gpuService = gpuService;
        _hasBattery = hasBattery;
        _dispatcher = Dispatcher.CurrentDispatcher;

        _settings = LoadSettings();
        DateTimeOffset now = _time.GetUtcNow();
        _warmupUntil = now + AutoSwitchPolicy.LaunchWarmup;
        _lastSaveUtc = now;
        Decision = _settings.Enabled
            ? new AutoSwitchDecision(AutoSwitchState.Waiting, "Démarrage : première analyse en cours.")
            : new AutoSwitchDecision(AutoSwitchState.Off, "Bascule automatique désactivée.");

        _monitoring.SnapshotUpdated += OnSnapshot;
        _tuning.ManualWrite += OnManualWrite;
        _profiles.GroupSuspended += OnGroupSuspended;
        _cpuService.EmergencyRestored += OnCpuEmergency;
        _gpuService.ThermalSafety.EmergencyRestored += OnGpuEmergency;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        if (_settings.Enabled) LoadHistory();
    }

    // ---- Ce que la page et le diagnostic lisent ----

    /// <summary>Après chaque changement d'état ou action (fil d'interface).</summary>
    public event Action? Changed;

    /// <summary>Bulle discrète de la zone de notification (titre, texte), sans nom d'application.</summary>
    public event Action<string, string>? Notify;

    public bool IsEnabled => _settings.Enabled;

    /// <summary>Copie des réglages de la bascule.</summary>
    public AutoSwitchSettings Settings => ProfileGroupJson.Clone(_settings);

    public AutoSwitchDecision Decision { get; private set; }

    public UsageVerdict? Verdict => _classifier.Current;

    public UsagePending? Pending => _classifier.Pending;

    /// <summary>L'application au premier plan (page seulement : donnée personnelle).</summary>
    public ForegroundApp? CurrentApp { get; private set; }

    /// <summary>L'historique, null tant qu'il n'est pas chargé (ou fonction désactivée).</summary>
    public UsageHistory? History => _history;

    /// <summary>Ce qui n'allait pas à la lecture d'usage.json, null sinon.</summary>
    public string? HistoryProblem { get; private set; }

    /// <summary>Incidents après une bascule, signalés au lancement de cette session (pour la page).</summary>
    public IReadOnlyList<AutoSwitchJournalEntry> StartupIncidents { get; private set; } = [];

    public string? LockReason => _lockReason;

    public DateTimeOffset? ManualPauseUntil => _manualPauseUntil is { } until && _time.GetUtcNow() < until ? until : null;

    /// <summary>Ce que le diagnostic reprend, sans nom d'application.</summary>
    public AutoSwitchStatus DiagnosticStatus => new(
        _settings.Enabled,
        Decision.State,
        Decision.Text,
        _history?.Journal.LastOrDefault(e => e.Kind is AutoSwitchJournalKinds.Switch or AutoSwitchJournalKinds.Refused),
        _history?.Journal.LastOrDefault(e => e.Kind == AutoSwitchJournalKinds.Incident),
        _settings.Rules?.Count ?? 0,
        _history?.DaysWithData ?? 0);

    public void AddRequiredGroups(ISet<SensorGroup> into)
        => BackgroundSensorNeeds.AddForAutoSwitch(into, _settings.Enabled && !_disposed, _hasBattery, _onBattery,
            CurrentApp is { IsFullscreen: true } || _classifier.Current?.Target.IsGaming == true);

    // ---- Relevé ----

    private void OnSnapshot(HardwareSnapshot snapshot)
    {
        if (_disposed || !_settings.Enabled || snapshot.CapturedAtUtc == _lastCaptured) return;
        _lastCaptured = snapshot.CapturedAtUtc;

        try
        {
            var now = new DateTimeOffset(DateTime.SpecifyKind(snapshot.CapturedAtUtc, DateTimeKind.Utc));
            IReadOnlyCollection<SensorGroup> read = snapshot.GroupsRead;

            CurrentApp = _reader.Poll(now);
            float? cpuLoad = read.Contains(SensorGroup.CpuLoad) ? snapshot.Cpu.LoadPercent : null;
            float? gpuLoad = read.Contains(SensorGroup.Gpu) ? snapshot.Gpu?.LoadPercent : null;
            double? fps = read.Contains(SensorGroup.Fps) ? snapshot.Game?.Fps : null;
            if (snapshot.Battery is { } battery) _onBattery = !battery.PowerOnline;
            else if (!_hasBattery) _onBattery = false;

            UsageRuleTarget? rule = FindRule(CurrentApp?.Path);
            _classifier.Add(new UsageSample(now, CurrentApp?.Path, CurrentApp?.IsFullscreen == true,
                CurrentApp?.IsExclusiveFullscreen == true, cpuLoad, gpuLoad, fps, _onBattery, rule));

            Record(now, snapshot, read, cpuLoad, gpuLoad);
            Evaluate(now);
            if (_history is { IsDirty: true } && now - _lastSaveUtc >= SaveInterval) SaveHistory(now);
        }
        catch (Exception ex)
        {
            // Règle 2 : la bascule ne fait jamais tomber le relevé. Une seule trace par session, pas une par seconde.
            if (!_tickErrorLogged) CrashLog.Record(ex, "bascule automatique : relevé");
            _tickErrorLogged = true;
        }
    }

    private UsageRuleTarget? FindRule(string? appPath)
    {
        if (_settings.Rules is not { Count: > 0 } rules || appPath is null) return null;

        Func<string, string?>? publisherOf = AutoSwitchRules.AnyRequiresPublisher(rules) ? _publishers.PublisherOf : null;
        return AutoSwitchRules.Find(rules, appPath, publisherOf, id => _profiles.Store.Find(id) is not null);
    }

    private void Record(DateTimeOffset now, HardwareSnapshot snapshot, IReadOnlyCollection<SensorGroup> read, float? cpuLoad, float? gpuLoad)
    {
        if (_history is null || _classifier.Current is not { } verdict) return;

        string usage = verdict.Target.Usage
                       ?? (verdict.Target.GroupId is { } id ? _profiles.Store.Find(id)?.Usage : null)
                       ?? "regle";
        float? cpuTemp = read.Contains(SensorGroup.Cpu) ? snapshot.Cpu.PackageTempC : null;
        float? gpuTemp = read.Contains(SensorGroup.Gpu) ? snapshot.Gpu?.CoreTempC : null;
        _history.Record(new UsageObservation(now, usage, cpuLoad, gpuLoad, cpuTemp, gpuTemp, _onBattery, CurrentApp?.Path,
            CurrentApp?.IsFullscreen == true));
    }

    // ---- Décision et bascule ----

    private void Evaluate(DateTimeOffset now)
    {
        UsageVerdict? verdict = _classifier.Current;
        UsageGroupChoice choice = verdict is null ? UsageGroupChoice.None : UsageGroupResolver.Resolve(_profiles.Store, verdict.Target);
        TuningLeaseHolder? holder = _lease.Holder;
        string? leaseText = holder is not null && holder.RequesterId != AutoSwitchRequester.Id
            ? holder.Describe(_lease.UtcNow, _lease.LocalTimeZone)
            : null;

        AutoSwitchDecision decision = AutoSwitchPolicy.Decide(new AutoSwitchContext(
            _settings.Enabled, now, _warmupUntil, verdict, choice, _lastHandled, _lastSwitchUtc, _lockReason, leaseText,
            _manualPauseUntil, _manualPauseSource, _profiles.IsGroupTuning, _switching || _profiles.IsApplying));

        bool changed = decision.State != Decision.State || decision.Text != Decision.Text;
        Decision = decision;
        if (decision.ShouldSwitch) _ = SwitchAsync(decision.Group!, verdict!, now);
        else if (changed) RaiseChanged();
    }

    private async Task SwitchAsync(ProfileGroup group, UsageVerdict verdict, DateTimeOffset now)
    {
        _switching = true;
        _lastSwitchUtc = now;
        var entry = new AutoSwitchJournalEntry
        {
            TimeUtc = now,
            Kind = AutoSwitchJournalKinds.Switch,
            Usage = verdict.Target.Usage ?? group.Usage,
            GroupId = group.Id,
            GroupName = group.Name,
            ReasonKind = verdict.Reason.ToString(),
            Reason = verdict.Rule is { } rule ? $"{verdict.Detail} (règle « {rule.Label} »)" : verdict.Detail,
        };
        RaiseChanged();

        try
        {
            // L'orchestrateur attend les écritures réelles (débouncers vidés, relecture) avant de rendre son rapport.
            ProfileGroupApplyResult? result = await _profiles.ApplyForAutoSwitchAsync(group);
            if (_disposed) return;

            if (result is null)
            {
                entry.Kind = AutoSwitchJournalKinds.Refused;
                entry.NotApplied = ["erreur inattendue pendant l'application"];
            }
            else if (result.Report.WasRefused)
            {
                // Rien de posé (bail pris entre-temps…) : la cible n'est pas tenue pour traitée, elle sera retentée
                // après le délai entre deux bascules.
                entry.Kind = AutoSwitchJournalKinds.Refused;
                entry.NotApplied = [result.Report.Refusal!];
            }
            else
            {
                entry.Kind = result.Report.AnyLanded ? AutoSwitchJournalKinds.Switch : AutoSwitchJournalKinds.Refused;
                entry.NotApplied = AutoSwitchReports.NotApplied(result.Report);
                _lastHandled = new AutoSwitchHandled(verdict.Target, group.Id, group.Revision);
            }

            _history?.AddJournal(entry);
            NotifySwitch(entry);
        }
        catch (Exception ex)
        {
            CrashLog.Record(ex, "bascule automatique : application");
        }
        finally
        {
            _switching = false;
            if (!_disposed)
            {
                SaveHistory(_time.GetUtcNow());
                Evaluate(_time.GetUtcNow());
                RaiseChanged();
            }
        }
    }

    private void NotifySwitch(AutoSwitchJournalEntry entry)
    {
        if (!_settings.NotifyEachSwitch) return;

        string usage = ProfileGroupUsage.Label(entry.Usage) ?? "Règle d'application";
        string text = entry.Kind == AutoSwitchJournalKinds.Switch
            ? $"Groupe « {entry.GroupName} » appliqué ({UsageReasons.Label(entry.ReasonKind)})."
            : $"Groupe « {entry.GroupName} » non appliqué.";
        if (entry.NotApplied is { Count: > 0 } notApplied) text += $" {notApplied.Count} réglage(s) non posé(s) : détail dans Profils › Automatique.";
        RaiseNotify($"Bascule : {usage}", text);
    }

    // ---- Pauses, verrou, incidents, veille ----

    private void OnManualWrite(string source)
    {
        if (_disposed || !_settings.Enabled) return;

        DateTimeOffset now = _time.GetUtcNow();
        bool wasPaused = _manualPauseUntil is { } until && now < until;
        _manualPauseUntil = now + AutoSwitchPolicy.ManualPause;
        _manualPauseSource = source;
        Adopt();

        if (!wasPaused)
        {
            _history?.AddJournal(new AutoSwitchJournalEntry
            {
                TimeUtc = now,
                Kind = AutoSwitchJournalKinds.Pause,
                Reason = $"{source} : pause de {(int)AutoSwitchPolicy.ManualPause.TotalMinutes} min",
            });
        }

        Evaluate(now);
    }

    /// <summary>L'usage en cours garde le réglage de l'utilisateur : son groupe n'est pas réimposé à la fin de la pause,
    /// seul un changement d'usage rebasculera.</summary>
    private void Adopt()
    {
        if (_classifier.Current is not { } verdict) return;
        if (UsageGroupResolver.Resolve(_profiles.Store, verdict.Target).Group is { } group)
        {
            _lastHandled = new AutoSwitchHandled(verdict.Target, group.Id, group.Revision);
        }
    }

    private void OnGroupSuspended(string groupId, string cause)
    {
        if (_disposed || !_settings.Enabled) return;

        string name = _profiles.Store.Find(groupId)?.Name ?? "un groupe";
        _history?.AddJournal(new AutoSwitchJournalEntry
        {
            TimeUtc = _time.GetUtcNow(),
            Kind = AutoSwitchJournalKinds.Incident,
            GroupId = groupId,
            GroupName = name,
            Reason = $"{cause} : groupe suspendu, la bascule n'y reviendra pas d'elle-même",
            Acknowledged = true,
        });
        RaiseNotify("Bascule : groupe suspendu", $"« {name} » : {cause}. La bascule n'y reviendra pas d'elle-même.");
        SaveHistory(_time.GetUtcNow());
        RaiseChanged();
    }

    private void OnCpuEmergency(string message) => OnEmergency($"processeur : {message}");

    private void OnGpuEmergency(string message) => OnEmergency($"carte graphique : {message}");

    /// <summary>Levé sur le fil de la sécurité thermique : la suite passe par l'interface.</summary>
    private void OnEmergency(string reason)
    {
        try
        {
            _dispatcher.BeginInvoke(() =>
            {
                if (_disposed) return;

                _lockReason = reason;
                if (!_settings.Enabled) return;

                DateTimeOffset now = _time.GetUtcNow();
                _history?.AddJournal(new AutoSwitchJournalEntry { TimeUtc = now, Kind = AutoSwitchJournalKinds.Lock, Reason = reason });
                RaiseNotify("Bascule verrouillée", $"Sécurité thermique ({reason}) : plus aucune bascule avant « Déverrouiller » ou la relance de l'app.");
                SaveHistory(now);
                Evaluate(now);
            });
        }
        catch
        {
            // interface déjà fermée
        }
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume) return;

        try
        {
            _dispatcher.BeginInvoke(() =>
            {
                if (_disposed) return;

                // Au réveil, les onglets reposent leur état de démarrage : le groupe posé n'est plus en place. On
                // laisse les onglets passer, puis la bascule repose le groupe de l'usage en cours.
                _lastHandled = null;
                _classifier.ResetWindows();
                _warmupUntil = _time.GetUtcNow() + AutoSwitchPolicy.LaunchWarmup;
                RaiseChanged();
            });
        }
        catch
        {
            // interface déjà fermée
        }
    }

    // ---- Actions de la page ----

    /// <summary>Active ou désactive. À l'activation, les groupes manquants sont générés tout de suite (décision de
    /// Denis) ; ceux qui existent ne sont pas touchés (l'affinage n'est que proposé).</summary>
    public UsageGenerationResult? SetEnabled(bool enabled)
    {
        if (enabled == _settings.Enabled) return null;

        DateTimeOffset now = _time.GetUtcNow();
        UpdateSettings(s => s.Enabled = enabled);
        UsageGenerationResult? generated = null;
        if (enabled)
        {
            if (_warmupUntil < now + AutoSwitchPolicy.EnableWarmup) _warmupUntil = now + AutoSwitchPolicy.EnableWarmup;
            _lastHandled = null;
            generated = Generate(now, onlyMissing: true);
            if (_history is null) LoadHistory();
            Decision = new AutoSwitchDecision(AutoSwitchState.Waiting, "Analyse de l'usage en cours.");
        }
        else
        {
            SaveHistory(now);
            Decision = new AutoSwitchDecision(AutoSwitchState.Off, "Bascule automatique désactivée.");
        }

        RaiseChanged();
        return generated;
    }

    /// <summary>« Régénérer » : met à jour les groupes générés non modifiés à la main, crée ceux qui manquent.</summary>
    public UsageGenerationResult Regenerate() => Generate(_time.GetUtcNow(), onlyMissing: false);

    private UsageGenerationResult Generate(DateTimeOffset now, bool onlyMissing)
    {
        var input = new UsageGenerationInput(
            _cpu.ReadState(),
            _gpu.ReadState(),
            _fans.ReadState(),
            SavedTabTuning.From(AppSettingsStore.Load()),
            usage => _history?.StatsFor(usage) ?? UsageStats.Empty(usage),
            _settings.IncludeBiosFans,
            EmptyUsageOverclockSource.Instance,
            now);
        UsageGenerationResult result = UsageProfileGenerator.Generate(_profiles.Store.Groups, input);
        List<GeneratedUsageGroup> kept = onlyMissing ? result.Groups.Where(g => g.Created).ToList() : result.Groups.ToList();

        if (kept.Count > 0) _profiles.UpsertGenerated(kept.Select(g => g.Group));
        UpdateSettings(s =>
        {
            s.Explanations ??= new Dictionary<string, GeneratedExplanation>();
            foreach (GeneratedUsageGroup group in kept)
            {
                s.Explanations[group.Group.Id] = new GeneratedExplanation
                {
                    Revision = group.Group.Revision,
                    Lines = group.Explanation.ToList(),
                    GeneratedUtc = now,
                };
            }

            // Les explications des groupes supprimés depuis ne servent plus.
            foreach (string id in s.Explanations.Keys.Where(id => _profiles.Store.Find(id) is null).ToList()) s.Explanations.Remove(id);
            if (kept.Count > 0) s.LastGenerationUtc = now;
        });

        if (kept.Count > 0)
        {
            _history?.AddJournal(new AutoSwitchJournalEntry
            {
                TimeUtc = now,
                Kind = AutoSwitchJournalKinds.Generation,
                Reason = $"{kept.Count} groupe(s) générés : {string.Join(", ", kept.Select(g => $"« {g.Group.Name} »"))}",
            });
        }

        // Le groupe régénéré de l'usage en cours sera reposé (sa révision a changé).
        RaiseChanged();
        return result with { Groups = kept };
    }

    /// <summary>« Reprendre » : met fin à la pause après un réglage manuel.</summary>
    public void Resume()
    {
        _manualPauseUntil = null;
        _manualPauseSource = null;
        Evaluate(_time.GetUtcNow());
        RaiseChanged();
    }

    /// <summary>« Déverrouiller » : la bascule reprend après une sécurité thermique. Les planificateurs de #8 refusent
    /// encore toute hausse automatique dans la session.</summary>
    public void Unlock()
    {
        _lockReason = null;
        Evaluate(_time.GetUtcNow());
        RaiseChanged();
    }

    public void SetNotify(bool notify) => UpdateSettings(s => s.NotifyEachSwitch = notify);

    public void SetIncludeBiosFans(bool include) => UpdateSettings(s => s.IncludeBiosFans = include);

    public void AddRule(AutoSwitchRule rule)
    {
        UpdateSettings(s => (s.Rules ??= new List<AutoSwitchRule>()).Add(rule));
        RaiseChanged();
    }

    public void RemoveRule(string ruleId)
    {
        UpdateSettings(s => s.Rules?.RemoveAll(r => string.Equals(r.Id, ruleId, StringComparison.OrdinalIgnoreCase)));
        RaiseChanged();
    }

    /// <summary>« Effacer l'historique » : agrégats, applications vues et journal des bascules.</summary>
    public void ClearHistory()
    {
        _history ??= new UsageHistory();
        _history.Clear();
        SaveHistory(_time.GetUtcNow());
        RaiseChanged();
    }

    // ---- Réglages et historique ----

    private static AutoSwitchSettings LoadSettings()
    {
        AutoSwitchSettings settings = AppSettingsStore.Load().AutoSwitch ?? new AutoSwitchSettings();
        if (settings.Normalize())
        {
            AutoSwitchSettings copy = ProfileGroupJson.Clone(settings);
            AppSettingsStore.Update(s => s.AutoSwitch = copy);
        }

        return settings;
    }

    private void UpdateSettings(Action<AutoSwitchSettings> mutate)
    {
        mutate(_settings);
        AutoSwitchSettings copy = ProfileGroupJson.Clone(_settings);
        AppSettingsStore.Update(s => s.AutoSwitch = copy);
    }

    /// <summary>Lit usage.json hors du fil d'interface (la racine peut être une clé USB), puis signale les incidents
    /// notés au lancement par le gestionnaire de reprise.</summary>
    private void LoadHistory()
    {
        if (_historyLoading || _history is not null) return;
        _historyLoading = true;
        string path = AppDataPaths.Current.UsageFile;
        Task.Run(() => UsageHistoryStore.Read(path)).ContinueWith(task =>
        {
            UsageHistoryRead read = task.IsCompletedSuccessfully ? task.Result : new UsageHistoryRead(new UsageHistoryFile(), "lecture impossible");
            try
            {
                _dispatcher.BeginInvoke(() => OnHistoryRead(read));
            }
            catch
            {
                // interface déjà fermée
            }
        }, TaskScheduler.Default);
    }

    private void OnHistoryRead(UsageHistoryRead read)
    {
        _historyLoading = false;
        if (_disposed) return;

        _history ??= UsageHistory.FromFile(read.File, _time.GetUtcNow());
        HistoryProblem = read.Problem;

        IReadOnlyList<AutoSwitchJournalEntry> incidents = _history.UnacknowledgedIncidents();
        if (incidents.Count > 0)
        {
            StartupIncidents = incidents;
            AutoSwitchJournalEntry last = incidents[^1];
            RaiseNotify("Bascule : groupe suspendu après un incident",
                $"« {last.GroupName ?? "un groupe"} » n'a pas été reposé : {last.Reason}");
            _history.Acknowledge(incidents);
            SaveHistory(_time.GetUtcNow());
        }

        RaiseChanged();
    }

    private void SaveHistory(DateTimeOffset now)
    {
        if (_history is not { IsDirty: true } history) return;

        try
        {
            _writer.Save(UsageHistoryStore.Serialize(history.ToFile()));
            history.MarkSaved();
            _lastSaveUtc = now;
        }
        catch (Exception ex)
        {
            CrashLog.Record(ex, "bascule automatique : enregistrement de l'historique");
        }
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            CrashLog.Record(ex, "bascule automatique : abonné");
        }
    }

    private void RaiseNotify(string title, string text)
    {
        try
        {
            Notify?.Invoke(title, text);
        }
        catch (Exception ex)
        {
            CrashLog.Record(ex, "bascule automatique : notification");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _monitoring.SnapshotUpdated -= OnSnapshot;
        _tuning.ManualWrite -= OnManualWrite;
        _profiles.GroupSuspended -= OnGroupSuspended;
        _cpuService.EmergencyRestored -= OnCpuEmergency;
        _gpuService.ThermalSafety.EmergencyRestored -= OnGpuEmergency;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;

        SaveHistory(_time.GetUtcNow());
        _writer.Flush(FlushTimeout);
    }
}
