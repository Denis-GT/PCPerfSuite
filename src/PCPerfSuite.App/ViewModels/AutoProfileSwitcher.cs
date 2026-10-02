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

    /// <summary>Délai avant de relire un usage.json qui n'a pas pu être lu (verrouillé, clé USB pas prête).</summary>
    public static readonly TimeSpan HistoryRetry = TimeSpan.FromMinutes(5);

    /// <summary>En mode éco, intervalle entre deux relevés des charges pour la bascule.</summary>
    public static readonly TimeSpan EcoSampleInterval = TimeSpan.FromSeconds(5);

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
    private readonly UsageHistoryWriter _writer = new(() => AppDataPaths.Current.UsageFile);

    /// <summary>L'état de la bascule (dernière cible traitée, adoption, démarrage, verrou, pause), en logique pure.</summary>
    private readonly AutoSwitchSession _session;

    private AutoSwitchSettings _settings;
    private UsageHistory? _history;
    private bool _historyLoading;
    private string? _historyReadProblem;
    private DateTimeOffset _historyRetryUtc;
    private DateTime _lastCaptured;
    private DateTimeOffset _lastSaveUtc;
    private bool _onBattery;
    private double _lastFps;
    private bool _gpuLoadEverRead;
    private bool _rtssFpsSeen;
    private DateTimeOffset _lastEcoSampleUtc;

    /// <summary>Règle trouvée pour l'application au premier plan, recalculée seulement quand l'application, les règles, les
    /// groupes ou un éditeur vérifié changent.</summary>
    private (string? Path, int Settings, int Groups, int Publishers, UsageRuleTarget? Rule) _ruleCache;
    private int _settingsVersion;
    private int _publishersVersion;
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
        _session = new AutoSwitchSession(new UsageClassifier(), now);
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
        _publishers.Resolved += () => Interlocked.Increment(ref _publishersVersion);

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

    private UsageClassifier Classifier => _session.Classifier;

    public UsageVerdict? Verdict => Classifier.Current;

    public UsagePending? Pending => Classifier.Pending;

    /// <summary>L'application au premier plan (page seulement : donnée personnelle).</summary>
    public ForegroundApp? CurrentApp { get; private set; }

    /// <summary>L'historique, null tant qu'il n'est pas chargé (ou fonction désactivée).</summary>
    public UsageHistory? History => _history;

    /// <summary>Ce qui ne va pas avec usage.json (lecture, ou dernière écriture), null sinon.</summary>
    public string? HistoryProblem => _historyReadProblem ?? _writer.LastError;

    /// <summary>Incidents après une bascule, signalés au lancement de cette session (pour la page).</summary>
    public IReadOnlyList<AutoSwitchJournalEntry> StartupIncidents { get; private set; } = [];

    public string? LockReason => _session.LockReason;

    public DateTimeOffset? ManualPauseUntil => _session.IsManuallyPaused(_time.GetUtcNow()) ? _session.ManualPauseUntilUtc : null;

    /// <summary>Les signaux de détection qui manquent sur ce PC (règle 3 : dire pourquoi), null tant que rien n'a été relevé.
    /// Sans charge GPU ni RTSS, un jeu en fenêtre ou en plein écran sans bord n'est pas reconnu.</summary>
    public string? SignalsText => _lastCaptured == default ? null : UsageReasons.DescribeSignals(_gpuLoadEverRead, _rtssFpsSeen);

    /// <summary>Pourquoi la dernière génération a échoué (activation ou « Régénérer »), null sinon.</summary>
    public string? GenerationProblem { get; private set; }

    /// <summary>Ce que le diagnostic reprend, sans nom d'application.</summary>
    public AutoSwitchStatus DiagnosticStatus => new(
        _settings.Enabled,
        Decision.State,
        Decision.Text,
        _history?.Journal.LastOrDefault(e => e.Kind is AutoSwitchJournalKinds.Switch or AutoSwitchJournalKinds.Refused),
        _history?.Journal.LastOrDefault(e => e.Kind == AutoSwitchJournalKinds.Incident),
        _settings.Rules?.Count ?? 0,
        _history?.DaysWithData ?? 0,
        HistoryProblem,
        SignalsText);

    /// <summary>
    /// Fenêtre cachée, la bascule n'a pas besoin d'un relevé par seconde (ses délais vont de 30 s à 2 min) : elle ne
    /// demande ses groupes que toutes les <see cref="EcoSampleInterval"/>, pour que le relevé retombe entre-temps sur son
    /// rythme de repos si personne d'autre n'a besoin de capteurs. Sur batterie, le GPU n'est interrogé que si quelque chose
    /// ressemble déjà à un jeu (RTSS, plein écran exclusif, jeu reconnu) : une vidéo en plein écran ne le réveille pas.
    /// </summary>
    public void AddRequiredGroups(ISet<SensorGroup> into)
    {
        if (_monitoring.IsBackgroundMode && _time.GetUtcNow() - _lastEcoSampleUtc < EcoSampleInterval) return;

        bool looksLikeGame = CurrentApp is { IsExclusiveFullscreen: true } || _lastFps > 0 || Classifier.Current?.Target.IsGaming == true;
        BackgroundSensorNeeds.AddForAutoSwitch(into, _settings.Enabled && !_disposed, _hasBattery, _onBattery, looksLikeGame);
    }

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
            if (read.Contains(SensorGroup.Fps)) _lastFps = fps ?? 0;
            if (gpuLoad is not null) _gpuLoadEverRead = true;
            if (fps > 0) _rtssFpsSeen = true;
            if (_monitoring.IsBackgroundMode && read.Contains(SensorGroup.CpuLoad)) _lastEcoSampleUtc = _time.GetUtcNow();
            if (snapshot.Battery is { } battery) _onBattery = !battery.PowerOnline;
            else if (!_hasBattery) _onBattery = false;

            UsageRuleTarget? rule = FindRule(CurrentApp?.Path);
            Classifier.Add(new UsageSample(now, CurrentApp?.Path, CurrentApp?.IsFullscreen == true,
                CurrentApp?.IsExclusiveFullscreen == true, cpuLoad, gpuLoad, fps, _onBattery, rule));

            Record(now, snapshot, read, cpuLoad, gpuLoad);
            Evaluate(now);
            if (_history is { IsDirty: true } && now - _lastSaveUtc >= SaveInterval) SaveHistory(now);
            if (_history is null && _historyReadProblem is not null && now >= _historyRetryUtc) LoadHistory();
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

        int groups = _profiles.Store.Groups.Count;
        int publishers = Volatile.Read(ref _publishersVersion);
        if (_ruleCache.Path == appPath && _ruleCache.Settings == _settingsVersion && _ruleCache.Groups == groups
            && _ruleCache.Publishers == publishers)
        {
            return _ruleCache.Rule;
        }

        Func<string, string?>? publisherOf = AutoSwitchRules.AnyRequiresPublisher(rules) ? _publishers.PublisherOf : null;
        UsageRuleTarget? rule = AutoSwitchRules.Find(rules, appPath, publisherOf, id => _profiles.Store.Find(id) is not null);
        // Clé prise avant la recherche : un éditeur vérifié pendant ce temps change la version, et la règle sera recherchée
        // de nouveau au relevé suivant.
        _ruleCache = (appPath, _settingsVersion, groups, publishers, rule);
        return rule;
    }

    private void Record(DateTimeOffset now, HardwareSnapshot snapshot, IReadOnlyCollection<SensorGroup> read, float? cpuLoad, float? gpuLoad)
    {
        if (_history is null || Classifier.Current is not { } verdict) return;

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
        TuningLeaseHolder? holder = _lease.Holder;
        string? leaseText = holder is not null && holder.RequesterId != AutoSwitchRequester.Id
            ? holder.Describe(_lease.UtcNow, _lease.LocalTimeZone)
            : null;

        AutoSwitchDecision decision = _session.Decide(_settings.Enabled, now, _profiles.Store, leaseText, _profiles.IsGroupTuning,
            _profiles.IsApplying, out _);

        bool changed = decision.State != Decision.State || decision.Text != Decision.Text;
        Decision = decision;
        if (decision.ShouldSwitch && Classifier.Current is { } verdict) _ = SwitchAsync(decision.Group!, verdict, now);
        else if (changed) RaiseChanged();
    }

    private async Task SwitchAsync(ProfileGroup group, UsageVerdict verdict, DateTimeOffset now)
    {
        _session.BeginSwitch(now);
        ProfileGroupReport? report = null;
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
            report = result?.Report;
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
            // Une application refusée en bloc (bail pris entre-temps) ou en erreur sera retentée après le délai.
            _session.EndSwitch(verdict.Target, group, report);
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
        if (_session.OnManualWrite(source, now, _profiles.Store))
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

                _session.Lock(reason);
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

                // Au réveil, les onglets reposent leur état de démarrage : le groupe posé n'est plus en place, et le
                // verdict d'avant la veille ne vaut plus rien. On laisse les onglets passer, puis la bascule repose le
                // groupe de l'usage constaté (une adoption après un réglage manuel est gardée).
                _session.OnResume(_time.GetUtcNow());
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
            // Le classifieur ne tournait plus : son verdict d'avant ne vaut rien, et la première analyse a le temps de
            // reconnaître un jeu déjà lancé avant toute bascule.
            _session.OnEnabled(now);
            Decision = new AutoSwitchDecision(AutoSwitchState.Waiting, "Analyse de l'usage en cours.");
            if (_history is null) LoadHistory();
            generated = TryGenerate(now, onlyMissing: true);
        }
        else
        {
            SaveHistory(now);
            Decision = new AutoSwitchDecision(AutoSwitchState.Off, "Bascule automatique désactivée.");
        }

        RaiseChanged();
        return generated;
    }

    /// <summary>« Régénérer » : met à jour les groupes générés non modifiés à la main, crée ceux qui manquent. Null si la
    /// génération a échoué (<see cref="GenerationProblem"/>).</summary>
    public UsageGenerationResult? Regenerate() => TryGenerate(_time.GetUtcNow(), onlyMissing: false);

    /// <summary>Une génération qui échoue (lecture d'un onglet…) ne doit empêcher ni l'activation ni l'historique.</summary>
    private UsageGenerationResult? TryGenerate(DateTimeOffset now, bool onlyMissing)
    {
        try
        {
            UsageGenerationResult result = Generate(now, onlyMissing);
            GenerationProblem = null;
            return result;
        }
        catch (Exception ex)
        {
            CrashLog.Record(ex, "bascule automatique : génération des groupes");
            GenerationProblem = $"groupes non générés : erreur inattendue ({ex.GetType().Name})";
            RaiseChanged();
            return null;
        }
    }

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
        _session.EndManualPause();
        Evaluate(_time.GetUtcNow());
        RaiseChanged();
    }

    /// <summary>« Déverrouiller » : la bascule reprend après une sécurité thermique. Les planificateurs de #8 refusent
    /// encore toute hausse automatique dans la session.</summary>
    public void Unlock()
    {
        _session.Unlock();
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
        _historyReadProblem = UsageHistoryStore.DeleteCorruptCopy(AppDataPaths.Current.UsageFile);
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
        _settingsVersion++;
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
            UsageHistoryRead read = task.IsCompletedSuccessfully ? task.Result : new UsageHistoryRead(new UsageHistoryFile(), "lecture impossible", Failed: true);
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

        if (read.Failed)
        {
            // Fichier intact mais illisible pour l'instant : on ne relève ni n'enregistre rien, sinon le premier enregistrement
            // écraserait 30 jours d'historique. Nouvel essai plus tard.
            _historyReadProblem = $"{read.Problem} : historique ni relevé ni enregistré, nouvel essai dans {(int)HistoryRetry.TotalMinutes} min";
            _historyRetryUtc = _time.GetUtcNow() + HistoryRetry;
            RaiseChanged();
            return;
        }

        _history ??= UsageHistory.FromFile(read.File, _time.GetUtcNow());
        _historyReadProblem = read.Problem;

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
            // La rétention de 30 jours vaut aussi pour une app qui reste ouverte des semaines (veille chaque soir).
            history.Prune(now);
            _writer.Save(history.Serialize());
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

    /// <summary>
    /// Arrêt, en tête de la fermeture : plus aucune bascule ni relevé, et l'historique part à l'écrivain sans attendre (le
    /// retour des ventilateurs, de l'OC et des watts passe avant). L'attente de l'écriture est dans <see cref="Dispose"/>,
    /// appelé en dernier.
    /// </summary>
    public void Stop()
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
    }

    /// <summary>Fin de la fermeture : attend au plus <see cref="FlushTimeout"/> que l'historique soit écrit.</summary>
    public void Dispose()
    {
        Stop();
        _writer.Flush(FlushTimeout);
    }
}
