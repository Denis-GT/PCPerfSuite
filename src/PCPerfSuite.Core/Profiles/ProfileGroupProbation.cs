using PCPerfSuite.Core.Safety;

namespace PCPerfSuite.Core.Profiles;

/// <summary>La période probatoire en cours : quel groupe, depuis quand, et ce qu'il a relevé.</summary>
public sealed record ProbationInfo(
    string GroupId, string Action, DateTimeOffset SinceUtc, DateTimeOffset DeadlineUtc, bool GpuRaised, bool WattsRaised, bool MadeStartupState);

/// <summary>
/// Prudence au démarrage (#8) : chaque application d'un groupe qui relève l'OC GPU ou les watts au-delà de l'origine
/// ouvre une opération du journal de session (<see cref="SessionJournal"/>), avant la première écriture. Elle reste
/// ouverte pendant <see cref="Window"/> : si un arrêt anormal, un écran bleu ou un TDR survient d'ici là, le lancement
/// suivant la trouve encore « en cours », et <see cref="ProfileGroupRecoveryHandler"/> empêche le groupe d'être
/// réappliqué. Passé ce délai sans incident, elle est close « terminée ».
///
/// Une seule à la fois : un autre groupe qui relève quelque chose clôt la précédente et ouvre la sienne ; un groupe qui
/// ne relève rien ne la clôt que si plus rien ne reste relevé. La fermeture propre de l'app la clôt aussi : un incident
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
    /// sur le disque : l'appelant ne doit alors pas poser ce qui est risqué (docs/decisions.md, journal de session).
    /// </summary>
    /// <param name="requesterId">Demandeur en kebab-case (<see cref="RequesterKey"/>) ; null : non noté.</param>
    public bool Begin(string groupId, string action, bool gpuRaised, bool wattsRaised, bool madeStartupState, string? requesterId = null)
    {
        Close();

        var values = new Dictionary<string, string>
        {
            [GroupKey] = groupId,
            [GpuKey] = gpuRaised ? "oui" : "non",
            [WattsKey] = wattsRaised ? "oui" : "non",
            [StartupStateKey] = madeStartupState ? "oui" : "non",
        };
        if (!string.IsNullOrWhiteSpace(requesterId)) values[RequesterKey] = requesterId;

        SessionOperation operation = _journal.Begin(Component, action, values);

        if (!operation.IsDurable)
        {
            // Rien sur le disque : il n'y a rien à clore non plus.
            LastProblem = _journal.LastWriteError ?? "journal de session inaccessible";
            return false;
        }

        LastProblem = null;
        DateTimeOffset now = _time.GetUtcNow();
        _open = operation;
        Current = new ProbationInfo(groupId, action, now, now + Window, gpuRaised, wattsRaised, madeStartupState);
        return true;
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

    /// <summary>Clôt la période « échouée » : sécurité thermique, TDR vu pendant la session.</summary>
    public void Fail(string cause)
    {
        SessionOperation? open = _open;
        _open = null;
        Current = null;
        open?.Fail(cause);
    }
}
