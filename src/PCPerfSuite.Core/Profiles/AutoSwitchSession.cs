namespace PCPerfSuite.Core.Profiles;

/// <summary>
/// L'état de la bascule automatique pendant une session, en logique pure (heure passée par l'appelant) : ce qui a été
/// traité en dernier (posé, ou adopté après un réglage manuel), le démarrage, le verrou thermique, la pause manuelle et
/// la bascule en cours. Le moteur de l'app (AutoProfileSwitcher) ne fait que relayer les événements ici, puis applique ce
/// que <see cref="Decide"/> demande : tous les enchaînements se testent sans matériel.
/// </summary>
public sealed class AutoSwitchSession
{
    private readonly UsageClassifier _classifier;

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
        DateTimeOffset warmup = now + AutoSwitchPolicy.EnableWarmup;
        if (WarmupUntilUtc < warmup) WarmupUntilUtc = warmup;
    }

    /// <summary>
    /// Réveil : les onglets reposent leur état de démarrage, le groupe posé n'est plus en place et le verdict d'avant ne
    /// vaut plus rien. Une cible adoptée après un réglage manuel est gardée : l'état reposé par les onglets est justement
    /// ce réglage, et seul un changement d'usage rebasculera.
    /// </summary>
    public void OnResume(DateTimeOffset now)
    {
        _classifier.Reset();
        if (!LastHandledAdopted) LastHandled = null;
        WarmupUntilUtc = now + AutoSwitchPolicy.LaunchWarmup;
    }

    /// <summary>Un réglage manuel : pause, et l'usage en cours garde ce réglage (adoption). Vrai si une pause commence
    /// (elle ne courait pas déjà).</summary>
    public bool OnManualWrite(string source, DateTimeOffset now, ProfileGroupsSettings store)
    {
        bool started = !IsManuallyPaused(now);
        ManualPauseUntilUtc = now + AutoSwitchPolicy.ManualPause;
        ManualPauseSource = source;

        if (_classifier.Current is { } verdict && UsageGroupResolver.Resolve(store, verdict.Target).Group is { } group)
        {
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
    }

    public void Lock(string reason) => LockReason = reason;

    public void Unlock() => LockReason = null;

    /// <summary>La décision du moment, d'après le verdict du classifieur et les groupes de la page Profils.</summary>
    public AutoSwitchDecision Decide(bool enabled, DateTimeOffset now, ProfileGroupsSettings store, string? leaseText, bool groupTuning,
        bool pageApplying, out UsageGroupChoice choice)
    {
        UsageVerdict? verdict = _classifier.Current;
        choice = verdict is null ? UsageGroupChoice.None : UsageGroupResolver.Resolve(store, verdict.Target);
        return AutoSwitchPolicy.Decide(new AutoSwitchContext(
            enabled, now, WarmupUntilUtc, verdict, choice, LastHandled, LastSwitchUtc, LockReason, leaseText,
            ManualPauseUntilUtc, ManualPauseSource, groupTuning, Switching || pageApplying));
    }

    /// <summary>Une bascule commence : le délai entre deux bascules part de maintenant, même si elle échoue.</summary>
    public void BeginSwitch(DateTimeOffset now)
    {
        Switching = true;
        LastSwitchUtc = now;
    }

    /// <summary>
    /// Fin d'une bascule. Seule une application qui n'a pas été refusée en bloc tient la cible pour traitée (même si des
    /// parties ont été refusées : on ne réessaie pas en boucle) ; refusée (bail pris entre-temps…) ou en erreur
    /// (<paramref name="report"/> null), elle sera retentée après le délai entre deux bascules.
    /// </summary>
    public void EndSwitch(UsageTarget target, ProfileGroup group, ProfileGroupReport? report)
    {
        Switching = false;
        if (report is null || report.WasRefused) return;

        LastHandled = new AutoSwitchHandled(target, group.Id, group.Revision);
        LastHandledAdopted = false;
    }
}
