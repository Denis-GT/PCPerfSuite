namespace PCPerfSuite.Core.Profiles;

/// <summary>
/// L'état de la bascule automatique pendant une session, en logique pure (heure passée par l'appelant) : ce qui a été
/// traité en dernier (posé, ou adopté après un réglage manuel), le démarrage, le verrou thermique, la pause manuelle et
/// la bascule en cours. Le moteur de l'app (AutoProfileSwitcher) ne fait que relayer les événements ici, puis applique ce
/// que <see cref="Decide"/> demande : tous les enchaînements se testent sans matériel.
/// </summary>
public sealed class AutoSwitchSession
{
    /// <summary>Après autant d'applications en erreur de suite pour la même cible, elle est tenue pour traitée : on ne
    /// réessaie pas toutes les 2 min (écritures, journal, bulle) un groupe que l'application n'arrive pas à poser.</summary>
    public const int MaxFailedAttempts = 2;

    private readonly UsageClassifier _classifier;

    /// <summary>L'adoption a eu lieu sur un groupe posé par la bascule, donc transitoire : au réveil, les onglets reposent
    /// leur état de démarrage à la place, et l'adoption ne vaut plus.</summary>
    private bool _adoptedOverSwitch;

    /// <summary>
    /// Le sens du changement d'usage déjà en attente au moment du réglage manuel : -1 s'il descendait (sortie d'un jeu,
    /// qui passe par paliers d'exigeant à léger puis à la bureautique), +1 s'il montait, 0 sans changement en attente. Les
    /// paliers qui le prolongent pendant la pause sont adoptés : l'utilisateur réglait pour ce qui venait, pas pour ce qui
    /// finissait. Un changement dans l'autre sens (un jeu lancé pendant la pause) rebasculera, lui, à la fin de la pause.
    /// </summary>
    private int _adoptDirection;

    private UsageTarget? _failedTarget;
    private int _failedAttempts;

    public AutoSwitchSession(UsageClassifier classifier, DateTimeOffset launchUtc)
    {
        _classifier = classifier;
        WarmupUntilUtc = launchUtc + AutoSwitchPolicy.LaunchWarmup;
    }

    public UsageClassifier Classifier => _classifier;

    /// <summary>Pas de bascule avant : les onglets reposent leur état au lancement ou au réveil.</summary>
    public DateTimeOffset WarmupUntilUtc { get; private set; }

    public AutoSwitchHandled? LastHandled { get; private set; }

    /// <summary>La dernière cible traitée a été adoptée après un réglage manuel (rien n'a été posé par la bascule).</summary>
    public bool LastHandledAdopted { get; private set; }

    public DateTimeOffset? LastSwitchUtc { get; private set; }

    public string? LockReason { get; private set; }

    public DateTimeOffset? ManualPauseUntilUtc { get; private set; }

    public string? ManualPauseSource { get; private set; }

    public bool Switching { get; private set; }

    /// <summary>Vrai tant que la pause après un réglage manuel court.</summary>
    public bool IsManuallyPaused(DateTimeOffset now) => ManualPauseUntilUtc is { } until && now < until;

    /// <summary>Activation : rien de ce qui précède ne vaut (le classifieur ne tournait plus), et la première analyse a
    /// le temps de reconnaître un jeu déjà lancé avant toute bascule.</summary>
    public void OnEnabled(DateTimeOffset now)
    {
        _classifier.Reset();
        LastHandled = null;
        LastHandledAdopted = false;
        _adoptedOverSwitch = false;
        _adoptDirection = 0;
        DateTimeOffset warmup = now + AutoSwitchPolicy.EnableWarmup;
        if (WarmupUntilUtc < warmup) WarmupUntilUtc = warmup;
    }

    /// <summary>
    /// Réveil : les onglets reposent leur état de démarrage, le groupe posé n'est plus en place et le verdict d'avant ne
    /// vaut plus rien. Une cible adoptée après un réglage manuel est gardée si l'état reposé par les onglets est ce
    /// réglage ; pas si l'adoption s'est faite sur un groupe que la bascule avait posé sans en faire l'état de démarrage :
    /// les onglets reposent alors autre chose, et la bascule doit reposer le groupe de l'usage.
    /// </summary>
    public void OnResume(DateTimeOffset now)
    {
        _classifier.Reset();
        if (!LastHandledAdopted || _adoptedOverSwitch)
        {
            LastHandled = null;
            LastHandledAdopted = false;
            _adoptedOverSwitch = false;
        }

        _adoptDirection = 0;
        WarmupUntilUtc = now + AutoSwitchPolicy.LaunchWarmup;
    }

    /// <summary>Un réglage manuel : pause, et l'usage en cours garde ce réglage (adoption). Vrai si une pause commence
    /// (elle ne courait pas déjà).</summary>
    /// <param name="makesStartupState">Un groupe appliqué à la main en état de démarrage : les onglets le reposeront
    /// eux-mêmes au réveil, l'adoption reste donc valable même faite sur un groupe posé par la bascule.</param>
    public bool OnManualWrite(string source, DateTimeOffset now, ProfileGroupsSettings store, bool makesStartupState = false)
    {
        bool started = !IsManuallyPaused(now);
        ManualPauseUntilUtc = now + AutoSwitchPolicy.ManualPause;
        ManualPauseSource = source;
        _adoptDirection = _classifier.Pending is { } pending && _classifier.Current is { } current
                          && Rank(pending.Target) is { } to && Rank(current.Target) is { } from
            ? Math.Sign(to - from)
            : 0;

        if (_classifier.Current is { } verdict && UsageGroupResolver.Resolve(store, verdict.Target).Group is { } group)
        {
            // Sur un groupe posé par la bascule (transitoire), ou sur une adoption qui l'était déjà.
            _adoptedOverSwitch = !makesStartupState && LastHandled is not null && (!LastHandledAdopted || _adoptedOverSwitch);
            LastHandled = new AutoSwitchHandled(verdict.Target, group.Id, group.Revision);
            LastHandledAdopted = true;
        }

        return started;
    }

    /// <summary>« Reprendre » : fin de la pause manuelle.</summary>
    public void EndManualPause()
    {
        ManualPauseUntilUtc = null;
        ManualPauseSource = null;
        _adoptDirection = 0;
    }

    public void Lock(string reason) => LockReason = reason;

    public void Unlock() => LockReason = null;

    /// <summary>
    /// Après chaque relevé passé au classifieur, avant la décision : le changement d'usage qui était en cours au moment du
    /// réglage manuel se confirme pendant la pause, palier par palier dans le même sens ; le réglage lui revient, au lieu
    /// d'être écrasé à la fin de la pause alors que l'usage n'a pas changé depuis.
    /// </summary>
    public void OnVerdict(DateTimeOffset now, ProfileGroupsSettings store)
    {
        if (_adoptDirection == 0 || !LastHandledAdopted || !IsManuallyPaused(now)) return;
        if (_classifier.Current is not { } confirmed || LastHandled is not { } adoptedBefore || confirmed.Target == adoptedBefore.Target) return;
        if (Rank(confirmed.Target) is not { } rankNow || Rank(adoptedBefore.Target) is not { } rankBefore
            || Math.Sign(rankNow - rankBefore) != _adoptDirection) return;

        if (UsageGroupResolver.Resolve(store, confirmed.Target).Group is { } adopted)
        {
            LastHandled = new AutoSwitchHandled(confirmed.Target, adopted.Id, adopted.Revision);
        }
    }

    /// <summary>La décision du moment, d'après le verdict du classifieur et les groupes de la page Profils ; ne change pas
    /// l'état de la session.</summary>
    public AutoSwitchDecision Decide(bool enabled, DateTimeOffset now, ProfileGroupsSettings store, string? leaseText, bool groupTuning,
        bool pageApplying, out UsageGroupChoice choice)
    {
        UsageVerdict? verdict = _classifier.Current;
        choice = verdict is null ? UsageGroupChoice.None : UsageGroupResolver.Resolve(store, verdict.Target);
        return AutoSwitchPolicy.Decide(new AutoSwitchContext(
            enabled, now, WarmupUntilUtc, verdict, choice, LastHandled, LastSwitchUtc, LockReason, leaseText,
            ManualPauseUntilUtc, ManualPauseSource, groupTuning, Switching || pageApplying, LastHandledAdopted));
    }

    /// <summary>Une bascule commence : le délai entre deux bascules part de maintenant, même si elle échoue.</summary>
    public void BeginSwitch(DateTimeOffset now)
    {
        Switching = true;
        LastSwitchUtc = now;
    }

    /// <summary>
    /// Fin d'une bascule. Seule une application qui n'a pas été refusée en bloc tient la cible pour traitée (même si des
    /// parties ont été refusées : on ne réessaie pas en boucle) ; refusée (bail pris entre-temps…), elle sera retentée
    /// après le délai entre deux bascules. En erreur (<paramref name="report"/> null), elle est retentée une fois, puis
    /// tenue pour traitée (<see cref="MaxFailedAttempts"/>) jusqu'au prochain changement d'usage. Vrai si elle sera
    /// retentée.
    /// </summary>
    public bool EndSwitch(UsageTarget target, ProfileGroup group, ProfileGroupReport? report)
    {
        Switching = false;
        if (report is null)
        {
            _failedAttempts = _failedTarget == target ? _failedAttempts + 1 : 1;
            _failedTarget = target;
            if (_failedAttempts < MaxFailedAttempts) return true;
        }
        else
        {
            _failedTarget = null;
            _failedAttempts = 0;
            if (report.WasRefused) return true;
        }

        LastHandled = new AutoSwitchHandled(target, group.Id, group.Revision, Failed: report is null);
        LastHandledAdopted = false;
        _adoptedOverSwitch = false;
        _adoptDirection = 0;
        return false;
    }

    /// <summary>L'exigence d'un usage : bureautique, jeu léger, jeu exigeant ; null pour une règle vers un groupe.</summary>
    private static int? Rank(UsageTarget target) => target.Usage switch
    {
        ProfileGroupUsage.Office => 0,
        ProfileGroupUsage.LightGaming => 1,
        ProfileGroupUsage.HeavyGaming => 2,
        _ => null,
    };
}
