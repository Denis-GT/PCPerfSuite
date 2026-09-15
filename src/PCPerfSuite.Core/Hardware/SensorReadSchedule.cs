using System.Diagnostics;

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

    /// <summary>Cadence appliquée : l'imposée, ou en automatique la cadence de base, allongée si la lecture coûte cher.</summary>
    public TimeSpan Interval { get; init; }

    /// <summary>Durée moyenne d'une lecture du groupe, null tant qu'aucune n'a été mesurée.</summary>
    public TimeSpan? AverageReadDuration { get; init; }
}

/// <summary>
/// Échéancier d'un groupe de capteurs. En automatique, la cadence découle du coût moyen d'une lecture : le
/// groupe ne doit pas passer plus de <see cref="HardwareMonitorService.AutoReadBudget"/> du temps à se lire.
/// Les lectures chères sont ainsi espacées sans ralentir celles qui ne coûtent rien, et ça s'ajuste tout seul
/// d'une machine à l'autre. Le réglage manuel peut arriver depuis le thread de l'interface pendant un relevé.
/// </summary>
internal sealed class SensorReadSchedule
{
    /// <summary>Poids d'une nouvelle mesure dans la moyenne glissante : assez pour suivre un changement
    /// de coût en quelques lectures, sans qu'un pic isolé ne fasse bondir la cadence.</summary>
    private const double AverageSmoothing = 0.3;

    /// <summary>La cadence automatique est arrondie au pas supérieur pour ne pas trembler à chaque lecture.</summary>
    private const double AutoIntervalStepMs = 50;

    private readonly object _sync = new();
    private TimeSpan _baseInterval = TimeSpan.FromSeconds(1);
    private TimeSpan? _manualInterval;
    private double? _averageReadMs;
    private int _readCount;
    private long _lastReadTimestamp;

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

    public bool IsDue(long timestamp)
    {
        lock (_sync)
        {
            // Marge de 10 % : le timer de l'interface n'est pas exact à la milliseconde, et sans marge un relevé
            // arrivé à 998 ms pour une cadence d'1 s repousserait la relecture d'un tick entier.
            return _readCount == 0
                || Stopwatch.GetElapsedTime(_lastReadTimestamp, timestamp) >= CurrentInterval() * 0.9;
        }
    }

    public void RecordRead(long timestamp, TimeSpan duration)
    {
        lock (_sync)
        {
            _lastReadTimestamp = timestamp;
            _readCount++;

            // La toute première lecture initialise les pilotes et coûte bien plus que les suivantes.
            if (_readCount == 1) return;

            double ms = duration.TotalMilliseconds;
            _averageReadMs = _averageReadMs is { } average ? average + AverageSmoothing * (ms - average) : ms;
        }
    }

    public SensorGroupReadStatus GetStatus()
    {
        lock (_sync)
        {
            return new SensorGroupReadStatus
            {
                Group = Group,
                ManualInterval = _manualInterval,
                Interval = CurrentInterval(),
                AverageReadDuration = _averageReadMs is { } average ? TimeSpan.FromMilliseconds(average) : null,
            };
        }
    }

    /// <summary>À appeler sous le verrou.</summary>
    private TimeSpan CurrentInterval()
    {
        if (_manualInterval is { } manual) return manual;
        if (_averageReadMs is not { } average) return _baseInterval;

        double ms = Math.Ceiling(average / HardwareMonitorService.AutoReadBudget / AutoIntervalStepMs) * AutoIntervalStepMs;
        var costInterval = TimeSpan.FromMilliseconds(Math.Min(ms, HardwareMonitorService.MaxAutoInterval.TotalMilliseconds));
        return costInterval > _baseInterval ? costInterval : _baseInterval;
    }
}
