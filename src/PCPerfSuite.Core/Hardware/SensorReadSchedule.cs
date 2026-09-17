namespace PCPerfSuite.Core.Hardware;

/// <summary>Groupes de capteurs relus chacun à sa propre cadence.</summary>
public enum SensorGroup
{
    /// <summary>Charge CPU totale (compteur Windows), séparée du reste du CPU qui coûte bien plus cher à lire.</summary>
    CpuLoad,
    Cpu,
    Gpu,
    Memory,
    Motherboard,
    Storage,
    Network,

    /// <summary>FPS et temps de frame lus dans la mémoire partagée de RTSS.</summary>
    Fps,
}

/// <summary>Cadence d'un groupe de capteurs au moment d'un relevé, pour l'afficher et la régler.</summary>
public sealed class SensorGroupReadStatus
{
    public SensorGroup Group { get; init; }

    /// <summary>Cadence imposée par l'utilisateur, null en automatique.</summary>
    public TimeSpan? ManualInterval { get; init; }

    /// <summary>Cadence appliquée : un nombre entier de ticks du relevé, le plus proche de la cadence voulue
    /// (l'imposée, ou en automatique la cadence de base, allongée si la lecture coûte cher).</summary>
    public TimeSpan Interval { get; init; }

    /// <summary>Vrai quand l'automatique a espacé le groupe au-delà de la cadence de base à cause de son coût.</summary>
    public bool IsSpacedOut { get; init; }

    /// <summary>Durée moyenne d'une lecture du groupe, null tant qu'aucune n'a été mesurée.</summary>
    public TimeSpan? AverageReadDuration { get; init; }
}

/// <summary>
/// Échéancier d'un groupe de capteurs, compté en ticks du relevé : le groupe est relu tous les N ticks, N entier.
/// Tous les groupes changent ainsi au même instant et à un rythme régulier. Avec une cadence en millisecondes qui
/// ne tombe pas juste sur le tick, la gigue du timer faisait relire un groupe tantôt au tick suivant, tantôt à
/// celui d'après, et les valeurs affichées avançaient par à-coups.
///
/// En automatique, la cadence découle du coût moyen d'une lecture : le groupe ne doit pas passer plus de
/// <see cref="HardwareMonitorService.AutoReadBudget"/> du temps à se lire. Le réglage manuel peut arriver depuis
/// le thread de l'interface pendant un relevé.
/// </summary>
internal sealed class SensorReadSchedule
{
    /// <summary>Poids d'une nouvelle mesure dans la moyenne glissante : assez pour suivre un changement
    /// de coût en quelques lectures, sans qu'un pic isolé ne fasse bondir la cadence.</summary>
    private const double AverageSmoothing = 0.3;

    /// <summary>Hystérésis de la cadence automatique : un coût qui oscille autour d'une limite ne la fait pas
    /// alterner entre deux nombres de ticks.</summary>
    private const double AutoHysteresis = 0.1;

    private readonly object _sync = new();
    private TimeSpan _baseInterval = TimeSpan.FromSeconds(1);
    private TimeSpan? _manualInterval;
    private double? _averageReadMs;
    private int _readCount;
    private int _autoTicks = 1;
    private long _lastReadEpoch = -1;
    private long _lastReadTick;

    public SensorReadSchedule(SensorGroup group) => Group = group;

    public SensorGroup Group { get; }

    public TimeSpan? ManualInterval
    {
        get { lock (_sync) return _manualInterval; }
        set { lock (_sync) _manualInterval = value; }
    }

    /// <summary>Cadence d'un groupe en automatique dont la lecture ne coûte pas cher : l'actualisation globale.</summary>
    public TimeSpan BaseInterval
    {
        get { lock (_sync) return _baseInterval; }
        set { lock (_sync) _baseInterval = value; }
    }

    /// <summary>Intervalle voulu hors coût : l'imposé, sinon la cadence de base. Le tick du relevé est le plus
    /// court de ces intervalles, l'automatique ne pouvant que les allonger.</summary>
    public TimeSpan RequestedInterval
    {
        get { lock (_sync) return _manualInterval ?? _baseInterval; }
    }

    /// <param name="epoch">Change avec la durée du tick : les numéros de tick repartent alors de zéro et le
    /// groupe est relu tout de suite.</param>
    public bool IsDue(long epoch, long tick, TimeSpan tickInterval)
    {
        lock (_sync)
        {
            return _readCount == 0
                || epoch != _lastReadEpoch
                || tick - _lastReadTick >= PeriodTicks(tickInterval);
        }
    }

    public void RecordRead(long epoch, long tick, TimeSpan duration, TimeSpan tickInterval)
    {
        lock (_sync)
        {
            _lastReadEpoch = epoch;
            _lastReadTick = tick;
            _readCount++;

            // La toute première lecture initialise les pilotes et coûte bien plus que les suivantes.
            if (_readCount == 1) return;

            double ms = duration.TotalMilliseconds;
            _averageReadMs = _averageReadMs is { } average ? average + AverageSmoothing * (ms - average) : ms;
            UpdateAutoTicks(tickInterval);
        }
    }

    public SensorGroupReadStatus GetStatus(TimeSpan tickInterval)
    {
        lock (_sync)
        {
            int ticks = PeriodTicks(tickInterval);
            return new SensorGroupReadStatus
            {
                Group = Group,
                ManualInterval = _manualInterval,
                Interval = tickInterval * ticks,
                IsSpacedOut = _manualInterval is null && ticks > RoundTicks(_baseInterval, tickInterval),
                AverageReadDuration = _averageReadMs is { } average ? TimeSpan.FromMilliseconds(average) : null,
            };
        }
    }

    /// <summary>À appeler sous le verrou.</summary>
    private int PeriodTicks(TimeSpan tickInterval)
    {
        if (_manualInterval is { } manual) return RoundTicks(manual, tickInterval);
        return Math.Max(RoundTicks(_baseInterval, tickInterval), _autoTicks);
    }

    private static int RoundTicks(TimeSpan interval, TimeSpan tickInterval)
        => Math.Max(1, (int)Math.Round(interval.TotalMilliseconds / Math.Max(1, tickInterval.TotalMilliseconds)));

    /// <summary>Nombre de ticks qu'impose le coût de lecture, arrondi au-dessus, avec hystérésis. À appeler sous le verrou.</summary>
    private void UpdateAutoTicks(TimeSpan tickInterval)
    {
        if (_averageReadMs is not { } average) return;

        double costMs = Math.Min(average / HardwareMonitorService.AutoReadBudget,
            HardwareMonitorService.MaxAutoInterval.TotalMilliseconds);
        double needed = costMs / Math.Max(1, tickInterval.TotalMilliseconds);

        if (needed > _autoTicks * (1 + AutoHysteresis) || needed < (_autoTicks - 1) * (1 - AutoHysteresis))
        {
            _autoTicks = Math.Max(1, (int)Math.Ceiling(needed));
        }
    }
}
