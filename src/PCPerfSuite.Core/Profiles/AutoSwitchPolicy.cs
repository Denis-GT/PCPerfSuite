using System.Globalization;

namespace PCPerfSuite.Core.Profiles;

/// <summary>Ce que la bascule a traité en dernier : la cible et la version du groupe posé (ou adopté après un réglage
/// manuel). Une bascule n'a lieu que si la cible du moment en diffère.</summary>
public sealed record AutoSwitchHandled(UsageTarget Target, string GroupId, int Revision);

/// <summary>État de la bascule, pour la page et le diagnostic.</summary>
public enum AutoSwitchState
{
    Off,

    /// <summary>Verrouillée après une sécurité thermique, jusqu'à « Déverrouiller » ou la relance de l'app.</summary>
    Locked,

    /// <summary>Une application de groupe est en cours (page ou bascule).</summary>
    Busy,

    /// <summary>En pause : bail tenu par un autre demandeur, réglage manuel récent, réglage d'un groupe dans un onglet.</summary>
    Paused,

    /// <summary>Attend quelque chose (démarrage, groupe, délai entre deux bascules).</summary>
    Waiting,

    /// <summary>Le groupe de l'usage en cours est en place.</summary>
    Idle,

    /// <summary>Basculer maintenant.</summary>
    Switch,
}

/// <summary>Tout ce que la politique regarde, relu à chaque relevé.</summary>
/// <param name="WarmupUntilUtc">Pas de bascule avant : laisse les onglets reposer leur état au lancement (fans, watts).</param>
/// <param name="LockReason">Sécurité thermique de la session, null sans.</param>
/// <param name="LeaseText">« Réglages pilotés par le bench… » quand un autre demandeur tient le bail, null sinon.</param>
/// <param name="ManualPauseUntilUtc">Fin de la pause après un réglage manuel.</param>
/// <param name="GroupTuning">Un groupe est en cours de réglage dans un onglet (« Régler dans l'onglet »).</param>
/// <param name="Applying">La page ou la bascule applique un groupe.</param>
public sealed record AutoSwitchContext(
    bool Enabled,
    DateTimeOffset NowUtc,
    DateTimeOffset WarmupUntilUtc,
    UsageVerdict? Verdict,
    UsageGroupChoice Choice,
    AutoSwitchHandled? LastHandled,
    DateTimeOffset? LastSwitchUtc,
    string? LockReason,
    string? LeaseText,
    DateTimeOffset? ManualPauseUntilUtc,
    string? ManualPauseSource,
    bool GroupTuning,
    bool Applying);

/// <summary>La décision : l'état, ce qu'on en dit (sans nom d'application), et le groupe à poser pour une bascule.</summary>
public sealed record AutoSwitchDecision(AutoSwitchState State, string Text, ProfileGroup? Group = null)
{
    public bool ShouldSwitch => State == AutoSwitchState.Switch && Group is not null;
}

/// <summary>
/// Décide, en logique pure, si la bascule automatique pose un groupe maintenant. Dans l'ordre :
/// <list type="number">
/// <item>désactivée ;</item>
/// <item>verrouillée après une sécurité thermique CPU ou GPU dans la session (en plus, les planificateurs de #8 refusent
/// toute hausse automatique après une sécurité) ;</item>
/// <item>une application de groupe en cours ;</item>
/// <item>en pause tant qu'un autre demandeur tient le bail de réglage (bench, vérification ou recherche d'OC) : c'est le
/// seul moyen pour une autre fonction de suspendre la bascule ;</item>
/// <item>en pause pendant le réglage d'un groupe dans un onglet, et pendant <see cref="ManualPause"/> après un réglage
/// manuel (pause propre à la bascule) ;</item>
/// <item>attente du démarrage, d'un verdict, d'un groupe pour l'usage, d'un groupe non suspendu après un incident ;</item>
/// <item>rien à faire si le groupe de la cible, dans sa version actuelle, est celui traité en dernier ;</item>
/// <item>au plus une bascule toutes les <see cref="MinInterval"/>.</item>
/// </list>
/// Délais expérimentaux.
/// </summary>
public static class AutoSwitchPolicy
{
    public static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan ManualPause = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan LaunchWarmup = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan EnableWarmup = TimeSpan.FromSeconds(5);

    public static AutoSwitchDecision Decide(AutoSwitchContext c)
    {
        if (!c.Enabled) return new AutoSwitchDecision(AutoSwitchState.Off, "Bascule automatique désactivée.");
        if (c.LockReason is { } lockReason)
        {
            return new AutoSwitchDecision(AutoSwitchState.Locked,
                $"Verrouillée après une sécurité thermique ({lockReason}) : aucune bascule jusqu'à « Déverrouiller » ou la relance de l'app.");
        }

        if (c.Applying) return new AutoSwitchDecision(AutoSwitchState.Busy, "Application d'un groupe en cours.");
        if (c.LeaseText is { } lease) return new AutoSwitchDecision(AutoSwitchState.Paused, $"En pause : {Lower(lease)}");
        if (c.GroupTuning) return new AutoSwitchDecision(AutoSwitchState.Paused, "En pause : un groupe est en cours de réglage dans un onglet.");
        if (c.ManualPauseUntilUtc is { } until && c.NowUtc < until)
        {
            string source = string.IsNullOrWhiteSpace(c.ManualPauseSource) ? "réglage manuel" : c.ManualPauseSource!;
            return new AutoSwitchDecision(AutoSwitchState.Paused,
                $"En pause jusqu'à {Clock(until)} : {source}. L'usage en cours garde ce réglage ; un changement d'usage après la pause rebasculera.");
        }

        if (c.NowUtc < c.WarmupUntilUtc) return new AutoSwitchDecision(AutoSwitchState.Waiting, $"Démarrage : première analyse jusqu'à {Clock(c.WarmupUntilUtc)}.");
        if (c.Verdict is not { } verdict) return new AutoSwitchDecision(AutoSwitchState.Waiting, "Analyse de l'usage en cours.");

        string usage = TargetLabel(verdict.Target);
        if (c.Choice.Group is not { } group)
        {
            return new AutoSwitchDecision(AutoSwitchState.Waiting,
                $"{usage} : aucun groupe pour cet usage. « Générer les groupes », ou donne cet usage à un groupe dans « Groupes ».");
        }

        if (c.Choice.Suspended)
        {
            string why = c.Choice.SuspendedBy is { } by && by != group.Name
                ? $"porte le même réglage relevé que « {by} », suspendu après un incident"
                : "est suspendu après un incident";
            return new AutoSwitchDecision(AutoSwitchState.Waiting,
                $"{usage} : le groupe « {group.Name} » {why} ; aucune bascule vers lui tant que la suspension n'est pas levée.");
        }

        if (c.LastHandled is { } handled && handled.Target == verdict.Target
            && string.Equals(handled.GroupId, group.Id, StringComparison.OrdinalIgnoreCase) && handled.Revision == group.Revision)
        {
            return new AutoSwitchDecision(AutoSwitchState.Idle, $"{usage} : groupe « {group.Name} » en place.");
        }

        if (c.LastSwitchUtc is { } last && c.NowUtc >= last && c.NowUtc - last < MinInterval)
        {
            return new AutoSwitchDecision(AutoSwitchState.Waiting,
                $"{usage} détecté : bascule vers « {group.Name} » possible à {Clock(last + MinInterval)} (au plus une toutes les {(int)MinInterval.TotalMinutes} min).");
        }

        return new AutoSwitchDecision(AutoSwitchState.Switch, $"{usage} : bascule vers « {group.Name} ».", group);
    }

    /// <summary>« Jeu exigeant », ou « groupe d'une règle ».</summary>
    public static string TargetLabel(UsageTarget target)
        => ProfileGroupUsage.Label(target.Usage) ?? (target.GroupId is not null ? "Règle d'application" : "Usage inconnu");

    private static string Clock(DateTimeOffset utc) => utc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    private static string Lower(string text)
    {
        string trimmed = text.Trim();
        return trimmed.Length == 0 ? trimmed : char.ToLowerInvariant(trimmed[0]) + trimmed[1..];
    }
}

/// <summary>Ce que le journal des bascules retient d'un rapport d'application.</summary>
public static class AutoSwitchReports
{
    /// <summary>Les réglages refusés ou laissés de côté, par dimension, avec leur raison (« Carte graphique : overclock
    /// non posé, … ») ; les rapports des planificateurs ne nomment aucune application.</summary>
    public static List<string> NotApplied(ProfileGroupReport report)
        => report.Dimensions
            .SelectMany(d => d.Items
                .Where(i => i.Status is ReportItemStatus.Refused or ReportItemStatus.Ignored)
                .Select(i => $"{DimensionReport.Title(d.Dimension)} : {i.Text}"))
            .ToList();
}

/// <summary>Libellés des raisons du classifieur, sans nom d'application (diagnostic, notification).</summary>
public static class UsageReasons
{
    public static string Label(UsageReasonKind reason) => reason switch
    {
        UsageReasonKind.NoGame => "aucun signe de jeu",
        UsageReasonKind.Rtss => "images mesurées par RTSS",
        UsageReasonKind.ExclusiveFullscreen => "plein écran exclusif",
        UsageReasonKind.BorderlessFullscreen => "plein écran sans bord, GPU chargé",
        UsageReasonKind.GpuLoad => "GPU chargé longtemps",
        UsageReasonKind.StickyFullscreen => "jeu resté au premier plan en plein écran",
        UsageReasonKind.CpuLoad => "processeur chargé longtemps",
        UsageReasonKind.Rule => "règle d'application",
        UsageReasonKind.BatteryCap => "sur batterie",
        _ => "raison inconnue",
    };

    /// <summary>Le libellé d'une raison enregistrée en chaîne (journal), « raison inconnue » si elle ne se lit pas.</summary>
    public static string Label(string? reason)
        => Enum.TryParse(reason, ignoreCase: false, out UsageReasonKind kind) && Enum.IsDefined(kind) ? Label(kind) : "raison inconnue";
}
