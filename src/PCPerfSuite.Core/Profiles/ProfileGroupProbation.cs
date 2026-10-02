using PCPerfSuite.Core.Safety;

namespace PCPerfSuite.Core.Profiles;

/// <summary>La période probatoire en cours : quel groupe, depuis quand, et ce qu'il a relevé.</summary>
/// <param name="Carried">Ce qu'un groupe précédent avait relevé et qui reste en place, repris de la période que celle-ci a
/// remplacée ; null si rien.</param>
public sealed record ProbationInfo(
    string GroupId, string Action, DateTimeOffset SinceUtc, DateTimeOffset DeadlineUtc, bool GpuRaised, bool WattsRaised, bool MadeStartupState,
    ProbationCarry? Carried = null)
{
    /// <summary>Un OC GPU est surveillé : celui du groupe, ou celui d'un groupe précédent encore en place.</summary>
    public bool WatchesGpu => GpuRaised || Carried?.GpuRaised == true;

    /// <summary>Le groupe à qui revient l'OC GPU surveillé, null si aucun.</summary>
    public ProbationCarry? GpuOwner
        => GpuRaised ? new ProbationCarry(GroupId, true, false, MadeStartupState)
            : Carried is { GpuRaised: true } carried ? carried with { WattsRaised = false } : null;

    /// <summary>Le groupe à qui reviennent les watts relevés surveillés, null si aucun.</summary>
    public ProbationCarry? WattsOwner
        => WattsRaised ? new ProbationCarry(GroupId, false, true, MadeStartupState)
            : Carried is { WattsRaised: true } carried ? carried with { GpuRaised = false } : null;
}

/// <summary>
/// Ce qu'un groupe a relevé et qui reste en place quand un autre groupe ouvre sa période : le groupe à qui l'imputer,
/// et s'il en avait fait l'état de démarrage. Sans cela, appliquer un groupe qui relève les watts clôturerait la période
/// d'un OC GPU toujours posé, qui ne serait plus surveillé ni en session ni au lancement.
/// </summary>
public sealed record ProbationCarry(string GroupId, bool GpuRaised, bool WattsRaised, bool MadeStartupState);

/// <summary>
/// Prudence au démarrage (#8) : chaque application d'un groupe qui relève l'OC GPU ou les watts au-delà de l'origine
/// ouvre une opération du journal de session (<see cref="SessionJournal"/>), avant la première écriture. Elle reste
/// ouverte pendant <see cref="Window"/> : si un arrêt anormal, un écran bleu ou un TDR survient d'ici là, le lancement
/// suivant la trouve encore « en cours », et <see cref="ProfileGroupRecoveryHandler"/> empêche le groupe d'être
/// réappliqué. Passé ce délai sans incident, elle est close « terminée ».
///
/// Une seule à la fois : un autre groupe qui relève quelque chose clôt la précédente et ouvre la sienne, en reprenant ce
/// que la précédente surveillait et qu'il ne règle pas (<see cref="ProbationCarry"/>) ; un groupe qui ne relève rien ne
/// la clôt que si plus rien ne reste relevé. La fermeture propre de l'app la clôt aussi : un incident
/// survenu une fois l'app fermée ne lui est pas imputé. Jamais <c>Dispose</c> pour une fin normale : ce serait un échec.
/// </summary>
public sealed class ProfileGroupProbation
{
    public const string Component = "groupe-profils";
    public const string ApplyAction = "application";
    public const string StartupAction = "demarrage";

    public const string GroupKey = "groupe";
    public const string GpuKey = "gpu-oc";
    public const string WattsKey = "watts";
    public const string StartupStateKey = "etat-demarrage";

    /// <summary>Qui a appliqué le groupe (« manuel », « onglet », « bascule-auto »…) : la bascule automatique (#9)
    /// reconnaît ainsi au lancement les incidents qui ont suivi une de ses bascules. Absent d'une ligne écrite avant #9,
    /// et de celle de l'état reposé au lancement.</summary>
    public const string RequesterKey = "demandeur";

    /// <summary>Ce qui est repris d'un groupe précédent (<see cref="ProbationCarry"/>). Absent d'une ligne qui ne reprend
    /// rien, et de celles écrites avant.</summary>
    public const string CarriedGroupKey = "repris-groupe";
    public const string CarriedGpuKey = "repris-gpu-oc";
    public const string CarriedWattsKey = "repris-watts";
    public const string CarriedStartupStateKey = "repris-etat-demarrage";

    /// <summary>Délai après lequel un incident n'est plus imputé au groupe (décision de Denis, prompt #8).</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(30);

    private readonly SessionJournal _journal;
    private readonly TimeProvider _time;
    private SessionOperation? _open;

    public ProfileGroupProbation(SessionJournal journal, TimeProvider time)
    {
        _journal = journal;
        _time = time;
    }

    /// <summary>La période en cours, null si aucune.</summary>
    public ProbationInfo? Current { get; private set; }

    /// <summary>Raison de la dernière ouverture ratée (journal inaccessible), null sinon.</summary>
    public string? LastProblem { get; private set; }

    /// <summary>
    /// Ouvre la période d'une application risquée, en clôturant la précédente. Faux si la ligne n'a pas pu être écrite
    /// sur le disque : l'appelant ne doit alors pas poser ce qui est risqué (docs/decisions.md, journal de session), et la
    /// période en cours reste ouverte, puisque ce qu'elle surveille est toujours en place.
    /// </summary>
    /// <param name="requesterId">Demandeur en kebab-case (<see cref="RequesterKey"/>) ; null : non noté.</param>
    /// <param name="carried">Ce qui est repris de la période remplacée (<see cref="CarryOver"/>) ; null si rien.</param>
    public bool Begin(string groupId, string action, bool gpuRaised, bool wattsRaised, bool madeStartupState, string? requesterId = null,
        ProbationCarry? carried = null)
    {
        var values = new Dictionary<string, string>
        {
            [GroupKey] = groupId,
            [GpuKey] = YesNo(gpuRaised),
            [WattsKey] = YesNo(wattsRaised),
            [StartupStateKey] = YesNo(madeStartupState),
        };
        if (!string.IsNullOrWhiteSpace(requesterId)) values[RequesterKey] = requesterId;
        if (carried is not null)
        {
            values[CarriedGroupKey] = carried.GroupId;
            values[CarriedGpuKey] = YesNo(carried.GpuRaised);
            values[CarriedWattsKey] = YesNo(carried.WattsRaised);
            values[CarriedStartupStateKey] = YesNo(carried.MadeStartupState);
        }

        SessionOperation operation = _journal.Begin(Component, action, values);

        if (!operation.IsDurable)
        {
            // Rien sur le disque : il n'y a rien à clore non plus.
            LastProblem = _journal.LastWriteError ?? "journal de session inaccessible";
            return false;
        }

        Close();
        LastProblem = null;
        DateTimeOffset now = _time.GetUtcNow();
        _open = operation;
        Current = new ProbationInfo(groupId, action, now, now + Window, gpuRaised, wattsRaised, madeStartupState, carried);
        return true;
    }

    /// <summary>
    /// Ce que la période en cours surveille et que la nouvelle application ne règle pas, alors que c'est encore relevé :
    /// la nouvelle période le reprend, avec le groupe à qui il revient. La nouvelle application relève au moins une des
    /// deux dimensions (sinon elle n'ouvre pas de période) : il en reste au plus une à reprendre.
    /// </summary>
    /// <param name="touchesGpu">Le nouveau groupe règle la carte graphique : ce qui y est posé devient le sien.</param>
    /// <param name="touchesWatts">Le nouveau groupe règle les watts.</param>
    public ProbationCarry? CarryOver(bool touchesGpu, bool touchesWatts, bool gpuRaisedNow, bool wattsRaisedNow)
    {
        if (Current is not { } current) return null;
        if (!touchesGpu && gpuRaisedNow && current.GpuOwner is { } gpu) return gpu;
        if (!touchesWatts && wattsRaisedNow && current.WattsOwner is { } watts) return watts;
        return null;
    }

    /// <summary>Après l'application : si la relecture ne montre plus rien de relevé (tout refusé, rogné à l'origine), la
    /// période n'a plus d'objet.</summary>
    public void AfterApplication(bool raisedNow)
    {
        if (!raisedNow) Close();
    }

    /// <summary>Un groupe qui ne relève rien vient d'être appliqué : la période se clôt si plus rien ne reste relevé
    /// (le groupe précédent avait de l'OC GPU, le nouveau n'y touche pas : elle continue).</summary>
    public void NoteReplaced(bool stillRaised)
    {
        if (!stillRaised) Close();
    }

    /// <summary>Vrai quand la période est arrivée à son terme : l'appelant fait sa dernière vérification (TDR) puis
    /// <see cref="Close"/> ou <see cref="Fail"/>.</summary>
    public bool IsDue(DateTimeOffset now) => Current is { } current && now >= current.DeadlineUtc;

    /// <summary>Clôt la période « terminée » : délai écoulé, plus rien de relevé, ou fermeture propre de l'app.</summary>
    public void Close()
    {
        SessionOperation? open = _open;
        _open = null;
        Current = null;
        open?.Complete();
    }

    /// <summary>
    /// Clôt la période arrivée à son terme sans qu'on ait pu la vérifier : le journal Système est resté illisible, aucun
    /// TDR n'a donc pu être vu. Notée « échouée » avec cette raison, pas « terminée » : rien ne dit qu'elle s'est bien
    /// passée. Le groupe n'est pas suspendu pour autant, faute d'incident constaté.
    /// </summary>
    public void CloseUnverified(string why) => Fail($"non vérifiée : {why}");

    /// <summary>Clôt la période « échouée » : sécurité thermique, TDR vu pendant la session.</summary>
    public void Fail(string cause)
    {
        SessionOperation? open = _open;
        _open = null;
        Current = null;
        open?.Fail(cause);
    }

    private static string YesNo(bool value) => value ? "oui" : "non";
}
