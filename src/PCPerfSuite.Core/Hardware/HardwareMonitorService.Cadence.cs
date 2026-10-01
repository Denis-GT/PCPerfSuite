namespace PCPerfSuite.Core.Hardware;

/// <summary>Cadence du relevé : cadence de base, cadence imposée par groupe, mode éco et baux de cadence.</summary>
public sealed partial class HardwareMonitorService
{
    /// <summary>Tick le plus court du relevé, en millisecondes (voir <see cref="MinTickInterval"/>).</summary>
    public const int MinTickMilliseconds = 100;

    /// <summary>Tick le plus court du relevé : plus vite n'apporte rien de lisible, alors que la lecture complète du CPU
    /// par LibreHardwareMonitor coûte déjà 30 à 80 ms. Le relevé et l'échéancier l'appliquent tous deux, sinon les
    /// cadences des groupes seraient calculées sur un tick que la boucle ne tient pas.</summary>
    public static readonly TimeSpan MinTickInterval = TimeSpan.FromMilliseconds(MinTickMilliseconds);

    /// <summary>Tick du relevé quand plus aucun groupe n'est à relire (mode éco sans overlay ni courbe de
    /// ventilateur) : de quoi s'apercevoir qu'un groupe redevient nécessaire, pour presque rien.</summary>
    public static readonly TimeSpan IdleTickInterval = TimeSpan.FromSeconds(5);

    private readonly CadenceLeases _leases = new();

    /// <summary>Levé quand un bail de cadence est pris ou libéré (depuis le thread du demandeur) : le relevé doit
    /// recalculer son tick.</summary>
    public event Action? CadenceChanged;

    /// <summary>Baux de cadence en cours, pour le diagnostic.</summary>
    public IReadOnlyList<CadenceLeaseInfo> ActiveCadenceLeases => _leases.Active;

    /// <summary>
    /// Bail de cadence : relit <paramref name="groups"/> (null : tous) au moins toutes les <paramref name="interval"/>
    /// (100 ms au plus vite) le temps d'une mesure, fenêtre cachée comprise, puis rend d'elle-même la cadence d'avant à
    /// sa libération. Le bail le plus rapide l'emporte, sur la cadence imposée par l'utilisateur comme sur le plafond du
    /// GPU, sans les modifier. Le demandeur est un nom court (« bench CPU »), affiché dans le diagnostic.
    /// </summary>
    public IDisposable RequestCadence(string requester, TimeSpan interval, IReadOnlyCollection<SensorGroup>? groups = null)
        => _leases.Acquire(requester, interval, groups);

    /// <summary>Vrai tant qu'un bail couvre ce groupe (lectures détaillées réservées aux mesures : MSR par cœur…).</summary>
    public bool IsCadenceLeased(SensorGroup group) => _leases.IsLeased(group);

    /// <summary>Un bail pris pendant qu'un autre se libère, sur deux threads : sans ce verrou, l'un recopierait son calcul
    /// par-dessus celui de l'autre, et un bail actif resterait sans effet.</summary>
    private readonly object _leaseApplyGate = new();

    private void OnLeasesChanged()
    {
        lock (_leaseApplyGate)
        {
            foreach (SensorReadSchedule schedule in _schedules)
            {
                schedule.LeaseInterval = _leases.IntervalFor(schedule.Group);
            }
        }
        CadenceChanged?.Invoke();
    }

    /// <summary>Impose une cadence de relecture à un groupe, ou null pour la cadence automatique
    /// (déduite du coût mesuré). Peut être appelé pendant un relevé en cours.</summary>
    public void SetManualInterval(SensorGroup group, TimeSpan? interval)
        => _schedules[(int)group].ManualInterval = interval;

    /// <summary>Cadence actuelle d'un groupe, sans attendre le prochain relevé (après un changement de réglage).</summary>
    public SensorGroupReadStatus GetGroupStatus(SensorGroup group) => _schedules[(int)group].GetStatus(TickInterval);

    /// <summary>Cadence de base, l'actualisation globale : celle d'un groupe en automatique dont la lecture ne coûte pas cher.</summary>
    public void SetBaseInterval(TimeSpan interval)
    {
        foreach (SensorReadSchedule schedule in _schedules)
        {
            schedule.BaseInterval = interval;
        }
    }

    /// <summary>Rythme auquel appeler GetSnapshot : le plus court des intervalles voulus (actualisation, cadence
    /// imposée ou bail), jamais sous <see cref="MinTickInterval"/>. Les cadences de tous les groupes en sont des
    /// multiples entiers ; l'automatique ne fait que les allonger, ce tick ne dépend donc pas du coût mesuré et reste
    /// stable. Les groupes en pause ne comptent pas ; s'ils le sont tous, le relevé ne tourne plus qu'au ralenti
    /// (<see cref="IdleTickInterval"/>).</summary>
    public TimeSpan TickInterval => ComputeTickInterval(_schedules);

    internal static TimeSpan ComputeTickInterval(IEnumerable<SensorReadSchedule> schedules)
    {
        TimeSpan? shortest = null;
        foreach (SensorReadSchedule schedule in schedules)
        {
            if (schedule.IsEffectivelySuspended) continue;
            TimeSpan requested = schedule.RequestedInterval;
            if (shortest is null || requested < shortest) shortest = requested;
        }

        if (shortest is not { } tick) return IdleTickInterval;
        return tick < MinTickInterval ? MinTickInterval : tick;
    }

    /// <summary>
    /// Mode éco : seuls les groupes de <paramref name="groups"/> restent relus, les autres sont mis en pause sans
    /// rien perdre de leur réglage (un groupe sous bail reste relu). Null revient au relevé complet ; les groupes en
    /// pause sont alors relus dès le tick suivant. Peut être appelé pendant un relevé en cours : il prend effet au suivant.
    /// </summary>
    public void SetBackgroundGroups(IReadOnlyCollection<SensorGroup>? groups)
    {
        foreach (SensorReadSchedule schedule in _schedules)
        {
            schedule.IsSuspended = groups is not null && !groups.Contains(schedule.Group);
        }
    }

    private bool IsSuspended(SensorGroup group) => _schedules[(int)group].IsEffectivelySuspended;
}
