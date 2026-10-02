using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCPerfSuite.Core.Processes;
using PCPerfSuite.Core.Profiles;

namespace PCPerfSuite.App.ViewModels;

/// <summary>Un usage et son groupe, dans le sous-onglet Automatique.</summary>
public sealed partial class AutoUsageGroupItem : ObservableObject
{
    public AutoUsageGroupItem(string usage) => Usage = usage;

    public string Usage { get; }

    public string UsageLabel => ProfileGroupUsage.Label(Usage) ?? Usage;

    [ObservableProperty] private string? groupId;
    [ObservableProperty] private string groupName = "aucun groupe";
    [ObservableProperty] private string? badge;
    [ObservableProperty] private string explanation = "";
    [ObservableProperty] private string? suspensionText;
    [ObservableProperty] private string? suspendedGroupId;
    [ObservableProperty] private bool isConfirmingLift;

    public bool HasGroup => GroupId is not null;

    public bool IsSuspended => SuspensionText is not null;

    partial void OnGroupIdChanged(string? value) => OnPropertyChanged(nameof(HasGroup));

    partial void OnSuspensionTextChanged(string? value) => OnPropertyChanged(nameof(IsSuspended));
}

/// <summary>Une règle par application, telle qu'affichée.</summary>
public sealed record AutoRuleItem(string Id, string Label, string Match, string Target);

/// <summary>Une cible possible d'une règle : un usage, ou un groupe précis.</summary>
public sealed record RuleTargetChoice(string Label, string? Usage, string? GroupId);

/// <summary>Une application vue au premier plan, proposée pour une règle.</summary>
public sealed record SeenAppChoice(string Path, string Label);

/// <summary>
/// Sous-onglet « Automatique » de la page Profils (#9) : interrupteur, état de la bascule, les trois groupes (modifiés par
/// « Modifier » de la page Groupes, régénérés sur demande), règles par application, journal des bascules et historique.
/// Toute la logique vit dans <see cref="AutoProfileSwitcher"/> et le Core ; ici, l'affichage et les commandes. Relevé en
/// direct seulement page affichée (<see cref="IPageLifecycle"/>, posé par la page Profils).
/// </summary>
public sealed partial class AutoProfilesViewModel : ObservableObject, IPageLifecycle, IDisposable
{
    private const int JournalLines = 20;
    private const int SeenAppsShown = 30;

    private readonly AutoProfileSwitcher _switcher;
    private readonly ProfileGroupsViewModel _profiles;
    private readonly DispatcherTimer _timer;
    private bool _syncing;
    private bool _restoringSelection;
    private int _ticks;

    /// <summary>Rafraîchissement complet toutes les 15 s (minuterie d'une seconde).</summary>
    private const int FullRefreshTicks = 15;

    public AutoProfilesViewModel(AutoProfileSwitcher switcher, ProfileGroupsViewModel profiles)
    {
        _switcher = switcher;
        _profiles = profiles;
        foreach (string usage in ProfileGroupUsage.All) UsageGroups.Add(new AutoUsageGroupItem(usage));

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => OnTick();
        _switcher.Changed += OnSwitcherChanged;
        SyncSettings(_switcher.Settings);
    }

    [ObservableProperty] private bool isPageShown;

    // ---- Réglages ----

    [ObservableProperty] private bool isEnabled;
    [ObservableProperty] private bool notifyEachSwitch;
    [ObservableProperty] private bool includeBiosFans;

    // ---- État ----

    [ObservableProperty] private string stateText = "";
    [ObservableProperty] private bool isPaused;
    [ObservableProperty] private bool isLocked;
    [ObservableProperty] private string? usageText;
    [ObservableProperty] private string? pendingText;
    [ObservableProperty] private string? foregroundText;
    [ObservableProperty] private string? signalsText;
    [ObservableProperty] private string? incidentText;
    [ObservableProperty] private string? status;

    public ObservableCollection<AutoUsageGroupItem> UsageGroups { get; } = new();

    [ObservableProperty] private string? refinementText;
    [ObservableProperty] private string? historyText;
    [ObservableProperty] private bool isConfirmingClear;

    public ObservableCollection<string> Journal { get; } = new();

    // ---- Règles ----

    public ObservableCollection<AutoRuleItem> Rules { get; } = new();

    public ObservableCollection<SeenAppChoice> SeenApps { get; } = new();

    public ObservableCollection<RuleTargetChoice> TargetChoices { get; } = new();

    [ObservableProperty] private SeenAppChoice? selectedSeenApp;
    [ObservableProperty] private string newRulePath = "";
    [ObservableProperty] private bool newRuleNameOnly;
    [ObservableProperty] private bool newRuleRequirePublisher;
    [ObservableProperty] private RuleTargetChoice? newRuleTarget;
    [ObservableProperty] private bool isAddingRule;

    /// <summary>Ce qui se passe à la fermeture et à la première bascule (D7).</summary>
    public string ClosingNote =>
        "Une bascule n'est jamais l'état de démarrage : à la fermeture, l'overclock, les watts et les ventilateurs sont rendus "
        + "d'origine, et le lancement suivant reprend l'état enregistré dans les onglets ; la première bascule le remplace pour la "
        + "session. Les réglages du plan d'alimentation (préférence performance/économie) sont permanents : à la fermeture, le PC "
        + "reste sur ceux du dernier groupe posé, et « Tout rétablir » les rend.";

    partial void OnIsPageShownChanged(bool value)
    {
        if (value)
        {
            Refresh();
            _timer.Start();
        }
        else
        {
            _timer.Stop();
        }
    }

    partial void OnIsEnabledChanged(bool value)
    {
        if (_syncing) return;

        try
        {
            UsageGenerationResult? generated = _switcher.SetEnabled(value);
            Status = value
                ? generated is { Groups.Count: > 0 } g
                    ? $"Bascule activée : {g.Groups.Count} groupe(s) générés, modifiables par « Modifier »."
                    : _switcher.GenerationProblem is { } problem ? $"Bascule activée, mais {problem}." : "Bascule activée."
                : "Bascule désactivée : le groupe en place le reste jusqu'à la fermeture ; plus rien n'est relevé. L'historique déjà relevé reste sur ce PC (30 jours au plus) : « Effacer l'historique » le supprime.";
        }
        catch (Exception ex)
        {
            Status = $"Changement impossible ({ex.GetType().Name}).";
        }

        Refresh();
    }

    partial void OnNotifyEachSwitchChanged(bool value)
    {
        if (!_syncing) _switcher.SetNotify(value);
    }

    partial void OnIncludeBiosFansChanged(bool value)
    {
        if (!_syncing) _switcher.SetIncludeBiosFans(value);
    }

    partial void OnSelectedSeenAppChanged(SeenAppChoice? value)
    {
        // Liste reconstruite : la sélection est seulement remise, le chemin choisi entre-temps (« Parcourir… ») reste.
        if (value is null || _restoringSelection) return;
        NewRulePath = value.Path;
    }

    partial void OnNewRulePathChanged(string value) => NewRuleNameOnly = ApplicationPaths.SuggestsNameMode(value);

    private void OnSwitcherChanged()
    {
        if (IsPageShown) Refresh();
    }

    /// <summary>Chaque seconde, l'état et l'usage (compte à rebours d'un changement) ; le reste (groupes, règles, historique,
    /// qui ne bougent guère) toutes les <see cref="FullRefreshTicks"/> secondes, ou à chaque changement de la bascule.</summary>
    private void OnTick()
    {
        if (++_ticks % FullRefreshTicks == 0)
        {
            Refresh();
            return;
        }

        try
        {
            RefreshState();
            RefreshUsage();
        }
        catch (Exception ex)
        {
            Status = $"Affichage incomplet ({ex.GetType().Name}).";
        }
    }

    private void SyncSettings(AutoSwitchSettings settings)
    {
        _syncing = true;
        IsEnabled = settings.Enabled;
        NotifyEachSwitch = settings.NotifyEachSwitch;
        IncludeBiosFans = settings.IncludeBiosFans;
        _syncing = false;
    }

    /// <summary>Tout l'affichage, depuis la bascule et la page Profils.</summary>
    private void Refresh()
    {
        try
        {
            AutoSwitchSettings settings = _switcher.Settings;
            SyncSettings(settings);
            RefreshState();

            RefreshUsage();
            RefreshGroups(settings);
            RefreshRules(settings);
            RefreshHistory(settings);
        }
        catch (Exception ex)
        {
            Status = $"Affichage incomplet ({ex.GetType().Name}).";
        }
    }

    private void RefreshState()
    {
        AutoSwitchDecision decision = _switcher.Decision;
        StateText = decision.Text;
        IsPaused = decision.State == AutoSwitchState.Paused && _switcher.ManualPauseUntil is not null;
        IsLocked = decision.State == AutoSwitchState.Locked;
    }

    private void RefreshUsage()
    {
        if (!_switcher.IsEnabled)
        {
            UsageText = PendingText = ForegroundText = SignalsText = null;
            return;
        }

        UsageText = _switcher.Verdict is { } verdict
            ? $"Usage détecté : {AutoSwitchPolicy.TargetLabel(verdict.Target)} depuis {verdict.SinceUtc.ToLocalTime():HH:mm} — {verdict.Detail}."
            : "Usage : analyse en cours.";

        PendingText = _switcher.Pending is { } pending
            ? $"Changement en vue : {AutoSwitchPolicy.TargetLabel(pending.Target)} ({pending.Detail}), confirmé vers {pending.DueUtc.ToLocalTime():HH:mm:ss} s'il se maintient."
            : null;

        SignalsText = _switcher.SignalsText;
        ForegroundText = _switcher.CurrentApp is { } app
            ? $"Au premier plan : {app.DisplayName}{(app.IsFullscreen ? ", en plein écran" : "")}{(app.ForegroundIgnored ? " (PCPerfSuite ou le bureau par-dessus)" : "")}"
              + (app.PathUnavailableReason is { } why ? $" ({why})." : ".")
            : "Au premier plan : aucune application.";

        IncidentText = _switcher.StartupIncidents.Count == 0
            ? null
            : string.Join(" ", _switcher.StartupIncidents.Select(i => $"« {i.GroupName ?? "un groupe"} » n'a pas été reposé au lancement : {i.Reason}."));
    }

    private void RefreshGroups(AutoSwitchSettings settings)
    {
        ProfileGroupsSettings store = _profiles.Store;
        foreach (AutoUsageGroupItem item in UsageGroups)
        {
            UsageGroupChoice choice = UsageGroupResolver.Resolve(store, new UsageTarget(item.Usage, null));
            ProfileGroup? group = choice.Group;
            item.GroupId = group?.Id;
            item.GroupName = group?.Name ?? "aucun groupe";
            item.Badge = group is null ? null
                : !group.IsGenerated ? "fait à la main"
                : group.EditedByUser ? "généré, modifié à la main"
                : "généré";
            if (choice.Candidates > 1) item.Badge += $" · {choice.Candidates} groupes pour cet usage, celui-ci est retenu";

            // Suspendu lui-même, ou porteur du même réglage relevé qu'un groupe suspendu (les deux groupes de jeu générés
            // reprennent le même overclock) : lever la suspension vise le groupe suspendu.
            ProfileGroup? blocker = group is null ? null : UsageGroupResolver.SuspendedBy(store, group);
            item.SuspendedGroupId = blocker?.Id;
            item.SuspensionText = blocker is not null && store.Suspensions.TryGetValue(blocker.Id, out ProfileGroupSuspension? suspension)
                ? ReferenceEquals(blocker, group)
                    ? $"Suspendu depuis le {suspension.SinceUtc.ToLocalTime():dd/MM à HH:mm} : {suspension.Cause}. La bascule n'y revient pas d'elle-même."
                    : $"Porte le même réglage relevé que « {blocker.Name} », suspendu depuis le {suspension.SinceUtc.ToLocalTime():dd/MM à HH:mm} : {suspension.Cause}. La bascule ne le pose pas."
                : null;
            if (!item.IsSuspended) item.IsConfirmingLift = false;

            item.Explanation = group is null
                ? "Aucun groupe pour cet usage : « Générer les groupes », ou donne cet usage à un groupe dans « Groupes »."
                : settings.Explanations?.TryGetValue(group.Id, out GeneratedExplanation? explanation) == true && explanation.Lines is { Count: > 0 } lines
                    ? string.Join(Environment.NewLine, lines)
                      + (explanation.Revision != group.Revision ? $"{Environment.NewLine}Modifié depuis la génération : voir son détail dans « Groupes »." : "")
                    : "Groupe sans explication générée : son détail est dans « Groupes ».";
        }

        var targets = new List<RuleTargetChoice>();
        targets.AddRange(ProfileGroupUsage.All.Select(u => new RuleTargetChoice($"Usage : {ProfileGroupUsage.Label(u)}", u, null)));
        targets.AddRange(store.Groups.Where(g => !g.IsEmpty).Select(g => new RuleTargetChoice($"Groupe : {g.Name}", null, g.Id)));
        if (!targets.SequenceEqual(TargetChoices))
        {
            RuleTargetChoice? selected = NewRuleTarget;
            TargetChoices.Clear();
            foreach (RuleTargetChoice target in targets) TargetChoices.Add(target);
            NewRuleTarget = TargetChoices.FirstOrDefault(t => t == selected) ?? TargetChoices.FirstOrDefault(t => t.Usage == ProfileGroupUsage.HeavyGaming);
        }
    }

    private void RefreshRules(AutoSwitchSettings settings)
    {
        ProfileGroupsSettings store = _profiles.Store;
        var rules = (settings.Rules ?? []).Select(rule => new AutoRuleItem(
            rule.Id,
            rule.DisplayLabel + (rule.Enabled ? "" : " (désactivée)"),
            rule.Match?.Describe() ?? "règle illisible",
            rule.GroupId is { } id
                ? store.Find(id) is { } group ? $"groupe « {group.Name} »" : "groupe supprimé : règle sans effet"
                : ProfileGroupUsage.Label(rule.Usage) ?? "usage inconnu : règle sans effet")).ToList();
        if (!rules.SequenceEqual(Rules))
        {
            Rules.Clear();
            foreach (AutoRuleItem rule in rules) Rules.Add(rule);
        }

        if (_switcher.History is not { } history) return;
        // Reconstruite seulement quand les applications changent (pas à chaque seconde de plus), pour ne pas bouger sous la
        // souris ; les durées affichées sont celles de la dernière reconstruction.
        List<UsageAppRecord> recent = history.Apps.OrderByDescending(a => a.LastSeenUtc).Take(SeenAppsShown).ToList();
        if (!recent.Select(a => a.Path).OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .SequenceEqual(SeenApps.Select(a => a.Path).OrderBy(p => p, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase))
        {
            SeenAppChoice? selected = SelectedSeenApp;
            _restoringSelection = true;
            try
            {
                SeenApps.Clear();
                foreach (UsageAppRecord app in recent)
                {
                    SeenApps.Add(new SeenAppChoice(app.Path,
                        $"{ApplicationPaths.DisplayName(app.Path)} — {Duration(app.TotalSeconds)} vue, dont {Duration(app.FullscreenSeconds)} en plein écran"));
                }

                SelectedSeenApp = SeenApps.FirstOrDefault(a => string.Equals(a.Path, selected?.Path, StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                _restoringSelection = false;
            }
        }
    }

    private void RefreshHistory(AutoSwitchSettings settings)
    {
        if (_switcher.History is not { } history)
        {
            HistoryText = !_switcher.IsEnabled ? "Historique : rien n'est relevé tant que la bascule est désactivée ; ce qui l'a été avant reste sur ce PC 30 jours au plus, « Effacer l'historique » le supprime."
                : _switcher.HistoryProblem is { } loadProblem ? $"Historique : usage.json {loadProblem}."
                : "Historique : chargement…";
            RefinementText = null;
            if (Journal.Count > 0) Journal.Clear();
            return;
        }

        IEnumerable<string> parts = ProfileGroupUsage.All
            .Select(history.StatsFor)
            .Where(s => s.Seconds > 0)
            .Select(s => $"{ProfileGroupUsage.Label(s.Usage)} {Duration(s.Seconds)}"
                         + (s.CpuTempP95 is { } cpu ? $", processeur ≤ {cpu} °C 95 % du temps" : "")
                         + (s.GpuTempP95 is { } gpu ? $", carte ≤ {gpu} °C" : ""));
        string summary = string.Join(" ; ", parts);
        HistoryText = $"Historique ({history.DaysWithData} jour(s) sur 30) : {(summary.Length == 0 ? "rien encore" : summary)}."
                      + (_switcher.HistoryProblem is { } problem ? $" (usage.json : {problem}.)" : "");

        DateTimeOffset now = DateTimeOffset.UtcNow;
        bool stale = settings.LastGenerationUtc is not { } last || now - last >= TimeSpan.FromDays(3);
        RefinementText = stale && history.DaysWithData >= 3
            ? $"{history.DaysWithData} jours d'historique : « Régénérer » affinerait les courbes d'après les températures relevées (les groupes modifiés à la main ne bougent pas)."
            : null;

        var lines = history.Journal.Reverse().Take(JournalLines).Select(DescribeEntry).ToList();
        if (!lines.SequenceEqual(Journal))
        {
            Journal.Clear();
            foreach (string line in lines) Journal.Add(line);
        }
    }

    private static string DescribeEntry(AutoSwitchJournalEntry entry)
    {
        string when = entry.TimeUtc.ToLocalTime().ToString("dd/MM HH:mm", CultureInfo.InvariantCulture);
        string usage = ProfileGroupUsage.Label(entry.Usage) ?? "règle";
        string group = string.IsNullOrWhiteSpace(entry.GroupName) ? "" : $" (« {entry.GroupName} »)";
        string text = entry.Kind switch
        {
            AutoSwitchJournalKinds.Switch => $"Bascule → {usage}{group} : {entry.Reason}",
            AutoSwitchJournalKinds.Refused => $"Bascule non appliquée → {usage}{group} : {entry.Reason}",
            AutoSwitchJournalKinds.Incident => $"Incident{group} : {entry.Reason}",
            AutoSwitchJournalKinds.Pause => $"Pause : {entry.Reason}",
            AutoSwitchJournalKinds.Lock => $"Verrouillée : {entry.Reason}",
            AutoSwitchJournalKinds.Generation => $"Génération : {entry.Reason}",
            _ => $"{entry.Kind} : {entry.Reason}",
        };
        if (entry.NotApplied is { Count: > 0 } notApplied) text += $" · Non appliqué : {string.Join(" ; ", notApplied)}";
        return $"{when} · {text}";
    }

    private static string Duration(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours} h {span.Minutes:00}";
        return span.TotalMinutes >= 1 ? $"{(int)span.TotalMinutes} min" : $"{(int)span.TotalSeconds} s";
    }

    // ---- Commandes ----

    [RelayCommand]
    private void Regenerate()
    {
        try
        {
            if (_switcher.Regenerate() is not { } result)
            {
                Status = $"Régénération impossible : {_switcher.GenerationProblem ?? "erreur inattendue"}.";
                Refresh();
                return;
            }

            var parts = new List<string> { result.Groups.Count == 0 ? "aucun groupe généré" : $"{result.Groups.Count} groupe(s) générés ou mis à jour" };
            parts.AddRange(result.Skipped.Select(s => $"{ProfileGroupUsage.Label(s.Usage)} : {s.Reason}"));
            Status = $"Régénération : {string.Join(" ; ", parts)}.";
        }
        catch (Exception ex)
        {
            Status = $"Régénération impossible ({ex.GetType().Name}).";
        }

        Refresh();
    }

    [RelayCommand]
    private void Resume()
    {
        _switcher.Resume();
        Refresh();
    }

    [RelayCommand]
    private void Unlock()
    {
        _switcher.Unlock();
        Status = "Bascule déverrouillée. Dans cette session, toute hausse automatique (watts, overclock) reste refusée.";
        Refresh();
    }

    [RelayCommand]
    private void Edit(AutoUsageGroupItem? item)
    {
        if (item?.GroupId is { } id) _profiles.OpenEditor(id);
    }

    /// <summary>Lever une suspension est une action risquée (D6) : confirmation à chaque fois, « Non » par défaut.</summary>
    [RelayCommand]
    private void AskLiftSuspension(AutoUsageGroupItem? item)
    {
        if (item is not null) item.IsConfirmingLift = true;
    }

    [RelayCommand]
    private void CancelLiftSuspension(AutoUsageGroupItem? item)
    {
        if (item is not null) item.IsConfirmingLift = false;
    }

    [RelayCommand]
    private void ConfirmLiftSuspension(AutoUsageGroupItem? item)
    {
        if (item?.SuspendedGroupId is not { } id) return;

        item.IsConfirmingLift = false;
        string name = _profiles.Store.Find(id)?.Name ?? item.GroupName;
        Status = _profiles.LiftSuspension(id)
            ? $"Suspension de « {name} » levée : la bascule pourra de nouveau poser ses réglages."
            : "Ce groupe n'était plus suspendu.";
        Refresh();
    }

    [RelayCommand]
    private void Browse()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choisir l'exécutable de l'application",
            Filter = "Applications (*.exe)|*.exe",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() == true) NewRulePath = dialog.FileName;
    }

    [RelayCommand]
    private async Task AddRule()
    {
        if (IsAddingRule) return;

        if (ApplicationPaths.Normalize(NewRulePath) is not { } path)
        {
            Status = "Choisis une application vue, ou « Parcourir… » jusqu'à son exécutable (chemin complet).";
            return;
        }

        if (NewRuleTarget is not { } target)
        {
            Status = "Choisis l'usage ou le groupe visé par la règle.";
            return;
        }

        IsAddingRule = true;
        try
        {
            string? publisher = null;
            if (NewRuleRequirePublisher)
            {
                // La vérification hache tout l'exécutable : hors du fil d'interface.
                publisher = await Task.Run(() => ApplicationPublisherCache.ReadPublisher(path));
                if (publisher is null)
                {
                    Status = "Cet exécutable n'a pas de signature valide : impossible d'exiger son éditeur. Décoche la case, ou choisis-en un autre.";
                    return;
                }
            }

            ApplicationMatch match = ApplicationMatch.For(path, NewRuleNameOnly, publisher)!;
            var rule = new AutoSwitchRule
            {
                Match = match,
                Usage = target.Usage,
                GroupId = target.GroupId,
                Label = ApplicationPaths.DisplayName(path),
            };
            _switcher.AddRule(rule);
            Status = $"Règle ajoutée : {rule.DisplayLabel} → {(target.GroupId is null ? ProfileGroupUsage.Label(target.Usage) : target.Label)}.";
            NewRulePath = "";
            NewRuleRequirePublisher = false;
            SelectedSeenApp = null;
        }
        catch (Exception ex)
        {
            Status = $"Règle non ajoutée ({ex.GetType().Name}).";
        }
        finally
        {
            IsAddingRule = false;
            Refresh();
        }
    }

    [RelayCommand]
    private void RemoveRule(AutoRuleItem? item)
    {
        if (item is null) return;
        _switcher.RemoveRule(item.Id);
        Status = $"Règle « {item.Label} » retirée.";
        Refresh();
    }

    [RelayCommand]
    private void AskClearHistory() => IsConfirmingClear = true;

    [RelayCommand]
    private void CancelClearHistory() => IsConfirmingClear = false;

    [RelayCommand]
    private void ConfirmClearHistory()
    {
        IsConfirmingClear = false;
        _switcher.ClearHistory();
        Status = "Historique effacé : temps par usage, températures, applications vues et journal des bascules.";
        Refresh();
    }

    public void Dispose()
    {
        _timer.Stop();
        _switcher.Changed -= OnSwitcherChanged;
    }
}
