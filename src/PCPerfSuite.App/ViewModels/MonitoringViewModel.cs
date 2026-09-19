using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCPerfSuite.App.Metrics;
using PCPerfSuite.App.Utils;
using PCPerfSuite.Core.Hardware;
using PCPerfSuite.Core.PowerSettings;

namespace PCPerfSuite.App.ViewModels;

/// <summary>Ligne de "Disques" : occupation, usure et test de santé. Débits et température sont des capteurs
/// comme les autres, affichables en tuiles graphiques.</summary>
public sealed partial class DiskItemViewModel : ObservableObject
{
    private readonly DiskHealthService _health;

    public string Identifier { get; }

    [ObservableProperty] private string name = "…";
    [ObservableProperty] private double usedPercent;
    [ObservableProperty] private double? remainingLifePercent;

    [ObservableProperty] private bool isTesting;
    [ObservableProperty] private DiskHealthStatus? healthStatus;
    [ObservableProperty] private string? healthSummary;

    public string RemainingLifeDisplay => RemainingLifePercent is { } l ? $"{l:0}%" : "--";
    public string TestButtonLabel => IsTesting ? "Test en cours…" : "Tester l'état";

    public DiskItemViewModel(DiskHealthService health, string identifier)
    {
        _health = health;
        Identifier = identifier;
    }

    public void Apply(DiskSnapshot s)
    {
        Name = s.Name;
        UsedPercent = s.UsedPercent ?? 0;
        RemainingLifePercent = s.RemainingLifePercent;
    }

    partial void OnRemainingLifePercentChanged(double? value) => OnPropertyChanged(nameof(RemainingLifeDisplay));
    partial void OnIsTestingChanged(bool value) => OnPropertyChanged(nameof(TestButtonLabel));

    [RelayCommand]
    private async Task TestAsync()
    {
        IsTesting = true;
        HealthSummary = null;
        try
        {
            DiskHealthReport report = await _health.CheckAsync(Name);
            HealthStatus = report.Status;
            HealthSummary = BuildSummary(report);
        }
        catch (Exception ex)
        {
            HealthStatus = DiskHealthStatus.Unknown;
            HealthSummary = $"Échec du test : {ex.Message}";
        }
        finally
        {
            IsTesting = false;
        }
    }

    private static string BuildSummary(DiskHealthReport r)
    {
        if (r.ErrorMessage is not null) return r.ErrorMessage;

        string headline = r.Status switch
        {
            DiskHealthStatus.Healthy => "Sain (Windows Storage Management)",
            DiskHealthStatus.Warning => "Attention",
            DiskHealthStatus.Unhealthy => "Défaillance imminente — sauvegarde recommandée",
            _ => "État inconnu",
        };

        var details = new List<string>();
        if (r.PowerOnHours is { } hours) details.Add($"{hours} h sous tension");
        if (r.WearPercent is { } wear) details.Add($"{wear:0}% d'usure");
        if (r.ReadErrorsTotal is { } re && r.WriteErrorsTotal is { } we && re + we > 0)
            details.Add($"{re + we} erreur(s) E/S cumulée(s)");

        return details.Count > 0 ? $"{headline} — {string.Join(", ", details)}" : headline;
    }
}

/// <summary>Carte "Batterie" : ce qui ne mérite pas de courbe parce que ça ne bouge qu'au fil des mois (état de
/// santé, capacités, cycles) ou qui décrit la batterie. Les valeurs temps réel sont des métriques graphiques.</summary>
public sealed partial class BatteryInfoViewModel : ObservableObject
{
    [ObservableProperty] private bool isPresent;
    [ObservableProperty] private string name = "Batterie";
    [ObservableProperty] private double healthPercent;
    [ObservableProperty] private string healthDisplay = "--";
    [ObservableProperty] private string stateDisplay = "--";
    [ObservableProperty] private string designCapacityDisplay = "--";
    [ObservableProperty] private string fullChargeCapacityDisplay = "--";
    [ObservableProperty] private string cycleCountDisplay = "--";
    [ObservableProperty] private string detailsDisplay = "";

    public void Apply(BatterySnapshot? b)
    {
        IsPresent = b is not null;
        if (b is null) return;

        Name = b.BatteryCount > 1 ? $"{b.BatteryCount} batteries" : b.Name ?? "Batterie";
        HealthPercent = b.HealthPercent ?? 0;
        HealthDisplay = b.HealthPercent is { } h ? $"{h:0}%" : "--";
        StateDisplay = (b.PowerOnline, b.Charging, b.Discharging) switch
        {
            (_, true, _) => "En charge",
            (_, _, true) => "Sur batterie",
            (true, _, _) => "Sur secteur, batterie pleine ou charge en pause",
            _ => "Inconnu",
        };
        DesignCapacityDisplay = Capacity(b, b.DesignMWh);
        FullChargeCapacityDisplay = Capacity(b, b.FullChargeMWh);
        CycleCountDisplay = b.CycleCount is { } c ? c.ToString(CultureInfo.CurrentCulture) : "non communiqué";
        DetailsDisplay = string.Join(" · ", new[] { b.Manufacturer, b.Chemistry }.Where(x => !string.IsNullOrEmpty(x)));
    }

    /// <summary>"90 005 mWh · 6 087 mAh" : les mWh du pilote, et leur équivalent en mAh à la tension actuelle.</summary>
    private static string Capacity(BatterySnapshot b, double? mWh)
    {
        if (mWh is not { } energy) return "--";
        string text = $"{energy.ToString("N0", CultureInfo.CurrentCulture)} mWh";
        return b.ToMah(energy) is { } mah ? $"{text} · {mah.ToString("N0", CultureInfo.CurrentCulture)} mAh" : text;
    }
}

/// <summary>Tuile graphique d'un capteur : valeur courante et courbe de son historique, dans la couleur de sa
/// catégorie. Mise à jour en place à chaque relevé plutôt que recréée (pas de clignotement).</summary>
public sealed partial class MetricTileViewModel : ObservableObject
{
    public MetricDefinition Definition { get; }
    public string Label => Definition.Label;
    public string CategoryName => Definition.Category.Name;

    /// <summary>Historique tenu par le MonitoringViewModel pour tous les capteurs, affichés ou non.</summary>
    public SampleHistory History { get; }

    public Brush LineBrush { get; }
    public Brush FillBrush { get; }

    public bool AutoScale => Definition.GraphMaximum is null;
    public double GraphMaximum => Definition.GraphMaximum ?? 100;
    public double MinimumScale => Definition.GraphMinimumScale;

    /// <summary>Valeur signée (charge/décharge) : courbe de part et d'autre d'un axe à zéro.</summary>
    public bool CenterZero => Definition.IsSigned;

    [ObservableProperty] private string displayValue = "--";
    [ObservableProperty] private string unit = "";

    /// <summary>Pourquoi la valeur n'est pas un nombre (conso totale "secteur"), null sinon.</summary>
    [ObservableProperty] private string? note;

    /// <summary>Min, moyenne et max depuis le démarrage ou la dernière remise à zéro, affichés en petit face
    /// à la valeur en direct. Sans unité quand elle est la même que celle de la valeur en direct (juste à
    /// côté) ; répétée sinon, pour un débit dont l'échelle (o/s, Ko/s, Mo/s...) diffère de celle du direct.</summary>
    [ObservableProperty] private string minimumDisplay = "--";
    [ObservableProperty] private string averageDisplay = "--";
    [ObservableProperty] private string maximumDisplay = "--";

    /// <summary>Met en forme un point de la courbe comme la valeur courante de la tuile — même format, même
    /// unité. C'est ce que le repère du graphique affiche au clic, d'où l'impossibilité qu'il contredise le
    /// chiffre affiché juste au-dessus.</summary>
    public Func<double, string> FormatSample { get; }

    /// <summary>Met en forme une valeur d'historique exactement comme la valeur courante de la tuile.</summary>
    private readonly Func<double, MetricReading> _format;

    public MetricTileViewModel(MetricDefinition definition, SampleHistory history)
    {
        Definition = definition;
        History = history;

        MetricReading Read(double value) => definition.FormatNumber?.Invoke(value)
            ?? new MetricReading(value, value.ToString("0.##", CultureInfo.CurrentCulture), "");

        FormatSample = value => Read(value).Text;
        _format = Read;

        var color = (Color)ColorConverter.ConvertFromString(definition.Category.DefaultColor);
        LineBrush = Frozen(new SolidColorBrush(color));
        FillBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x30, color.R, color.G, color.B)));
    }

    public void Apply(MetricSample sample)
    {
        MetricReading reading = Definition.Read(sample);
        DisplayValue = reading.Value;
        Unit = reading.Unit;
        Note = reading.Note;
        RefreshStats();
    }

    /// <summary>Recopie les statistiques de l'historique dans les trois libellés. Appelée à chaque relevé et
    /// à la remise à zéro, pour que l'affichage suive sans attendre le relevé suivant.</summary>
    public void RefreshStats()
    {
        RunningStats stats = History.Stats;
        MinimumDisplay = FormatStat(stats.HasValue, stats.Minimum);
        AverageDisplay = FormatStat(stats.HasValue, stats.Average);
        MaximumDisplay = FormatStat(stats.HasValue, stats.Maximum);
    }

    /// <summary>Bare pour une unité fixe (déjà celle de la valeur en direct juste à côté) ; sinon avec son
    /// unité, car un débit change d'échelle (o/s, Ko/s, Mo/s...) et taire l'unité tromperait sur la grandeur.</summary>
    private string FormatStat(bool hasValue, double value)
    {
        if (!hasValue) return "--";

        MetricReading reading = _format(value);
        return reading.Unit == Unit ? reading.Value : reading.Text;
    }

    private static Brush Frozen(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }
}

/// <summary>Ligne de "Cadence des capteurs" : relecture automatique selon le coût mesuré, ou cadence imposée.</summary>
public sealed partial class SensorGroupCadenceViewModel : ObservableObject
{
    private readonly HardwareMonitorService _hardware;
    private readonly Action _onIntervalChanged;
    private readonly bool _initialized;

    public SensorGroup Group { get; }
    public string Name { get; }
    public string Description { get; }

    [ObservableProperty] private bool isAuto;

    /// <summary>Ce qui se passe réellement pour ce groupe, en clair.</summary>
    [ObservableProperty] private string statusText = "…";

    /// <summary>Pourquoi : coût mesuré de la lecture, ou effet de l'intervalle fixe.</summary>
    [ObservableProperty] private string reasonText = "Mesure du coût de lecture en cours…";

    private int _manualMs;
    private int _lastRefreshMs = 1000;

    /// <summary>Cadence imposée hors automatique, bornée comme l'actualisation (et ramenée dans ces bornes à l'affichage).</summary>
    public int ManualMs
    {
        get => _manualMs;
        set
        {
            int clamped = RefreshRates.Clamp(value);
            bool changed = SetProperty(ref _manualMs, clamped);
            if (clamped != value) OnPropertyChanged(nameof(ManualMs));
            if (changed && !IsAuto) ApplyAndSave();
        }
    }

    /// <param name="onIntervalChanged">Appelé quand l'intervalle du groupe change, pour recaler le timer du Monitoring.</param>
    public SensorGroupCadenceViewModel(HardwareMonitorService hardware, SensorGroup group, int? savedManualMs, Action onIntervalChanged)
    {
        _hardware = hardware;
        _onIntervalChanged = onIntervalChanged;
        Group = group;
        (Name, Description) = group switch
        {
            SensorGroup.CpuLoad => ("Charge CPU", "Utilisation totale, comme le Gestionnaire des tâches"),
            SensorGroup.Cpu => ("CPU", "Température, puissance, fréquence, charge par cœur"),
            SensorGroup.Gpu => ("GPU", "Charge, températures, puissance, VRAM, ventilateur"),
            SensorGroup.Memory => ("Mémoire vive", "Utilisation"),
            SensorGroup.Motherboard => ("Carte mère", "Températures, tensions, ventilateurs"),
            SensorGroup.Storage => ("Disques", "Débits, température, espace utilisé"),
            SensorGroup.Network => ("Réseau", "Débits de toutes les cartes"),
            SensorGroup.Fps => ("FPS (RTSS)", "FPS, temps de frame et 1 % low du jeu au premier plan"),
            SensorGroup.Battery => ("Batterie / alimentation", "Charge, décharge, capacité, conso totale"),
            _ => (group.ToString(), ""),
        };

        _manualMs = RefreshRates.Clamp(savedManualMs ?? 1000);
        IsAuto = savedManualMs is null;
        _hardware.SetManualInterval(group, IsAuto ? null : TimeSpan.FromMilliseconds(_manualMs));
        _initialized = true;
    }

    partial void OnIsAutoChanged(bool value)
    {
        if (_initialized) ApplyAndSave();
    }

    public void Apply(SensorGroupReadStatus status, int refreshMs)
    {
        _lastRefreshMs = refreshMs;

        double intervalMs = status.Interval.TotalMilliseconds;
        string? cost = status.AverageReadDuration is { } duration ? $"{duration.TotalMilliseconds:0.0} ms" : null;

        StatusText = intervalMs >= 1000 ? $"Relu toutes les {intervalMs / 1000:0.#} s" : $"Relu toutes les {intervalMs:0} ms";

        ReasonText = (IsAuto, status.IsSpacedOut, cost) switch
        {
            (true, _, null) => $"Suit l'actualisation ({refreshMs} ms) · mesure du coût de lecture en cours…",
            (true, false, _) => $"Lecture rapide ({cost}) : suit l'actualisation ({refreshMs} ms).",
            (true, true, _) => $"Lecture coûteuse ({cost}) : espacée pour ne pas y passer plus de " +
                               $"{HardwareMonitorService.AutoReadBudget * 100:0} % du temps.",
            (false, _, null) => "Fréquence propre à ce groupe.",
            (false, _, _) => $"Fréquence propre à ce groupe · lecture {cost}.",
        };
    }

    private void ApplyAndSave()
    {
        TimeSpan? manual = IsAuto ? null : TimeSpan.FromMilliseconds(ManualMs);
        _hardware.SetManualInterval(Group, manual);

        AppSettings settings = AppSettingsStore.Load();
        if (manual is null) settings.SensorGroupIntervalsMs.Remove(Group.ToString());
        else settings.SensorGroupIntervalsMs[Group.ToString()] = ManualMs;
        AppSettingsStore.Save(settings);

        // Le texte et le timer suivent tout de suite le réglage, sans attendre le prochain relevé.
        Apply(_hardware.GetGroupStatus(Group), _lastRefreshMs);
        _onIntervalChanged();
    }
}

/// <summary>Ligne du tableau "Temps de lecture des capteurs" : dernière durée, moyenne et pire cas
/// sur les <see cref="Window"/> dernières lectures.</summary>
public sealed partial class ReadTimingViewModel : ObservableObject
{
    public const int Window = 100;

    private readonly SampleHistory _durationsMs = new(Window);

    public string Key { get; }
    public string Name { get; }

    [ObservableProperty] private string lastDisplay = "--";
    [ObservableProperty] private string averageDisplay = "--";
    [ObservableProperty] private string maxDisplay = "--";

    public ReadTimingViewModel(string key, string name)
    {
        Key = key;
        Name = name;
    }

    public void Record(TimeSpan duration)
    {
        _durationsMs.Push(duration.TotalMilliseconds);
        LastDisplay = Format(duration.TotalMilliseconds);
        AverageDisplay = Format(_durationsMs.Average() ?? 0);
        MaxDisplay = Format(_durationsMs.Max());
    }

    private static string Format(double ms) => $"{ms:0.00} ms";
}

public sealed partial class MonitoringViewModel : ObservableObject, IDisposable
{
    private readonly HardwareMonitorService _hardware;
    private readonly DiskHealthService _diskHealth = new();
    private readonly Dispatcher _dispatcher;
    private readonly SamplingLoop _loop;
    private readonly Dictionary<string, SampleHistory> _histories = new();
    private MetricSample? _lastSample;
    private long _tickIntervalTicks; // Lu par le thread des relevés : un long, lu et écrit avec Interlocked.

    /// <summary>1 tant qu'un relevé attend d'être appliqué par l'interface (lu et écrit depuis deux threads).</summary>
    private int _applyPending;

    public event Action<HardwareSnapshot>? SnapshotUpdated;

    /// <summary>Même relevé que SnapshotUpdated, complété des FPS RTSS et de l'heure : ce que lit le catalogue de métriques.</summary>
    public event Action<MetricSample>? MetricsUpdated;

    private int _refreshMs = 1000;

    /// <summary>Cadence de rafraîchissement, saisie librement en millisecondes. La valeur est ramenée
    /// dans les bornes acceptables et l'affichage suit (taper 10 affiche 100, le minimum retenu).</summary>
    public int RefreshMs
    {
        get => _refreshMs;
        set
        {
            int clamped = RefreshRates.Clamp(value);
            bool changed = SetProperty(ref _refreshMs, clamped);

            // Saisie hors bornes : on renotifie pour que le champ affiche la valeur réellement retenue.
            if (clamped != value) OnPropertyChanged(nameof(RefreshMs));
            if (!changed) return;

            _hardware.SetBaseInterval(TimeSpan.FromMilliseconds(clamped));

            AppSettings settings = AppSettingsStore.Load();
            settings.MonitoringRefreshMs = clamped;
            AppSettingsStore.Save(settings);

            // L'actualisation globale fait référence : sa valeur est recopiée dans la cadence imposée de chaque
            // groupe, celle qu'appliquent les groupes hors automatique.
            foreach (SensorGroupCadenceViewModel cadence in SensorCadences)
            {
                cadence.ManualMs = clamped;
                cadence.Apply(_hardware.GetGroupStatus(cadence.Group), clamped);
            }
            OnPropertyChanged(nameof(SensorCadencesHint));
            UpdateTimerInterval();
        }
    }

    public string RefreshHint => RefreshRates.Hint;

    /// <summary>Capteurs proposés en tuiles graphiques : le catalogue partagé avec l'overlay (sauf ce qui n'a pas
    /// de courbe), plus les capteurs propres à la machine ajoutés au fil des relevés.</summary>
    public MetricSelectionViewModel MyMetrics { get; }
    public ObservableCollection<MetricTileViewModel> MyMetricTiles { get; } = new();
    [ObservableProperty] private bool isCustomizingMyMetrics;

    public ObservableCollectionEx<DiskItemViewModel> Disks { get; } = new();

    /// <summary>Infos batterie sans courbe (santé, capacités, cycles) ; carte masquée sans batterie.</summary>
    public BatteryInfoViewModel Battery { get; } = new();

    [ObservableProperty] private string? errorMessage;

    /// <summary>Ticks ignorés parce que la lecture précédente n'était pas terminée.</summary>
    [ObservableProperty] private int skippedTicks;

    public ObservableCollection<ReadTimingViewModel> ReadTimings { get; } = new();

    public string ReadTimingsHint =>
        $"Moyenne et max sur les {ReadTimingViewModel.Window} dernières lectures. Si le max du relevé complet dépasse " +
        "l'actualisation, des ticks sont sautés. Chaque groupe de capteurs n'est relu qu'à sa propre cadence : " +
        "le max du relevé complet correspond aux ticks où les groupes coûteux le sont.";

    /// <summary>Un groupe par ligne, dans l'ordre de <see cref="SensorGroup"/>.</summary>
    public IReadOnlyList<SensorGroupCadenceViewModel> SensorCadences { get; }

    [ObservableProperty] private bool isCustomizingCadences;

    public string SensorCadencesHint =>
        $"Chaque groupe est relu à sa propre fréquence, et sa courbe n'avance qu'à ce rythme. En Auto, il suit l'actualisation " +
        $"({RefreshMs} ms), sauf si sa lecture coûte cher : il est alors espacé pour ne pas y passer plus de " +
        $"{HardwareMonitorService.AutoReadBudget * 100:0} % du temps. Sans Auto, choisis son intervalle, plus court ou plus " +
        "long que l'actualisation. Changer l'actualisation remet tous les intervalles choisis à sa valeur.";

    public MonitoringViewModel(HardwareMonitorService hardware)
    {
        _hardware = hardware;

        AppSettings settings = AppSettingsStore.Load();
        _refreshMs = RefreshRates.Clamp(settings.MonitoringRefreshMs);
        hardware.SetBaseInterval(TimeSpan.FromMilliseconds(_refreshMs));

        // Avant le premier relevé, pour que les cadences imposées s'appliquent dès le départ.
        SensorCadences = Enum.GetValues<SensorGroup>()
            .Select(group => new SensorGroupCadenceViewModel(hardware, group,
                settings.SensorGroupIntervalsMs.TryGetValue(group.ToString(), out int ms) ? ms : null,
                UpdateTimerInterval))
            .ToArray();

        // Première ouverture des tuiles graphiques : ce qu'affichaient les anciennes cartes, plus les tuiles
        // "Mes métriques" déjà choisies.
        IEnumerable<string> sensorIds = settings.MonitoringSensorIds
            ?? MetricCatalog.DefaultMonitoringIds.Union(settings.MonitoringMetricIds ?? Enumerable.Empty<string>());
        MyMetrics = new MetricSelectionViewModel(sensorIds, MetricCatalog.All.Where(m => m.HasGraph));
        MyMetrics.SelectionChanged += OnMyMetricsSelectionChanged;
        SyncMyMetricTiles();

        // Relevés sur un thread dédié, à échéances fixes : la cadence ne dépend plus de l'occupation de l'interface.
        _dispatcher = Dispatcher.CurrentDispatcher;
        _tickIntervalTicks = ComputeTickInterval().Ticks;
        _loop = new SamplingLoop(() => TickInterval, OnSamplingTick);
        _loop.Start();
    }

    private void OnMyMetricsSelectionChanged()
    {
        SyncMyMetricTiles();

        AppSettings settings = AppSettingsStore.Load();
        settings.MonitoringSensorIds = MyMetrics.SelectedIds;
        AppSettingsStore.Save(settings);
    }

    private void SyncMyMetricTiles()
    {
        // Réconciliation plutôt que reconstruction : les tuiles déjà affichées gardent leur instance.
        IReadOnlyList<MetricDefinition> selected = MyMetrics.Selected;

        for (int i = MyMetricTiles.Count - 1; i >= 0; i--)
        {
            if (!selected.Contains(MyMetricTiles[i].Definition))
            {
                MyMetricTiles.RemoveAt(i);
            }
        }

        // Les tuiles restantes sont déjà dans l'ordre d'affichage : il suffit d'insérer les nouvelles à leur rang.
        for (int i = 0; i < selected.Count; i++)
        {
            if (i < MyMetricTiles.Count && MyMetricTiles[i].Definition == selected[i]) continue;

            var tile = new MetricTileViewModel(selected[i], GetHistory(selected[i].Id));
            if (_lastSample is not null) tile.Apply(_lastSample);
            MyMetricTiles.Insert(i, tile);
        }
    }

    /// <summary>Durée d'un tick de relevé : chaque groupe est relu tous les N ticks. L'overlay s'en sert pour
    /// n'afficher qu'un relevé sur N, à intervalle régulier.</summary>
    public TimeSpan TickInterval => TimeSpan.FromTicks(Interlocked.Read(ref _tickIntervalTicks));

    private TimeSpan ComputeTickInterval()
        => TimeSpan.FromMilliseconds(Math.Max(RefreshRates.MinMs, _hardware.TickInterval.TotalMilliseconds));

    private void UpdateTimerInterval()
    {
        TimeSpan interval = ComputeTickInterval();
        // Changer la durée du tick fait repartir le décompte : seulement quand la valeur change vraiment.
        if (interval == TickInterval) return;

        Interlocked.Exchange(ref _tickIntervalTicks, interval.Ticks);
        _loop.IntervalChanged();
    }

    private SampleHistory GetHistory(string metricId)
    {
        if (!_histories.TryGetValue(metricId, out SampleHistory? history))
        {
            history = new SampleHistory();
            _histories[metricId] = history;
        }
        return history;
    }

    /// <summary>
    /// Tick de la boucle de relevé, sur son thread. Les lectures se font ici, l'application à l'interface est
    /// ensuite postée au thread de l'interface. Si l'interface n'a pas encore appliqué le relevé précédent, ce tick
    /// est sauté : empiler les relevés lui ferait accumuler un retard qu'elle ne rattraperait jamais.
    /// </summary>
    private void OnSamplingTick(SamplingTick tick)
    {
        int skipped = tick.Skipped;

        if (Interlocked.CompareExchange(ref _applyPending, 1, 0) != 0)
        {
            _dispatcher.InvokeAsync(() => SkippedTicks += skipped + 1);
            return;
        }

        HardwareSnapshot snapshot;
        try
        {
            snapshot = _hardware.GetSnapshot(tick.Epoch, tick.Index);
        }
        catch (Exception ex)
        {
            _dispatcher.InvokeAsync(() =>
            {
                Interlocked.Exchange(ref _applyPending, 0);
                SkippedTicks += skipped;
                ErrorMessage = $"Lecture des capteurs impossible : {ex.Message}";
            });
            return;
        }

        _dispatcher.InvokeAsync(() =>
        {
            try
            {
                SkippedTicks += skipped;
                ApplyTick(snapshot);
            }
            finally
            {
                Interlocked.Exchange(ref _applyPending, 0);
            }
        }, DispatcherPriority.Normal);
    }

    private void ApplyTick(HardwareSnapshot snapshot)
    {
        try
        {
            long applyStart = Stopwatch.GetTimestamp();
            Apply(snapshot);
            TimeSpan applyDuration = Stopwatch.GetElapsedTime(applyStart);
            RecordReadTimings(snapshot, applyDuration);
            foreach (SensorGroupReadStatus status in snapshot.GroupStatuses)
            {
                SensorCadences.FirstOrDefault(c => c.Group == status.Group)?.Apply(status, RefreshMs);
            }
            ErrorMessage = null;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Lecture des capteurs impossible : {ex.Message}";
        }
    }

    private void RecordReadTimings(HardwareSnapshot snapshot, TimeSpan applyDuration)
    {
        foreach (HardwareReadTiming timing in snapshot.ReadTimings)
        {
            ReadTimingRow(timing.Identifier, timing.Name).Record(timing.Duration);
        }

        ReadTimingRow("snapshot", "Relevé complet (GetSnapshot)").Record(snapshot.ReadDuration);

        // Sur le thread de l'interface : tuiles, historiques des graphiques et tous les abonnés
        // (onglet GPU, courbes de ventilateurs, overlay). Le dessin qui suit n'est pas compté.
        ReadTimingRow("ui", "Interface et abonnés (Apply)").Record(applyDuration);
    }

    private ReadTimingViewModel ReadTimingRow(string key, string name)
    {
        ReadTimingViewModel? row = ReadTimings.FirstOrDefault(r => r.Key == key);
        if (row is null)
        {
            row = new ReadTimingViewModel(key, name);
            ReadTimings.Add(row);
        }
        return row;
    }

    /// <summary>Repart de zéro pour les min, moyennes et max de tous les capteurs d'un seul geste, par exemple
    /// avant un benchmark. Les courbes, elles, continuent sans coupure : le repère épinglé sur l'une d'elles
    /// reste valable.</summary>
    [RelayCommand]
    private void ResetMetricStats()
    {
        foreach (SampleHistory history in _histories.Values)
        {
            history.Stats.Reset();
        }

        // Les tuiles n'écoutent pas l'historique : on les rafraîchit ici plutôt que d'attendre le relevé suivant,
        // qui peut être à une seconde ou plus selon l'actualisation.
        foreach (MetricTileViewModel tile in MyMetricTiles)
        {
            tile.RefreshStats();
        }
    }

    /// <summary>Repart de zéro, par exemple pour mesurer après avoir changé l'actualisation.</summary>
    [RelayCommand]
    private void ResetReadTimings()
    {
        ReadTimings.Clear();
        SkippedTicks = 0;
    }

    private void Apply(HardwareSnapshot s)
    {
        ApplyDisks(s.Disks);
        Battery.Apply(s.Battery);

        var sample = new MetricSample { Hardware = s, Game = s.Game, LocalTime = DateTime.Now };
        _lastSample = sample;

        // Capteurs propres à la machine, découverts au fil des relevés (un disque branché en cours de route...).
        MyMetrics.AddDefinitions(MonitoringSensorCatalog.FromSnapshot(s));
        MyMetrics.AddDefinitions(PowerMetricCatalog.FromSnapshot(s));

        // Historique tenu pour tous les capteurs, affichés ou non : une tuile qu'on active a déjà sa courbe. Un point
        // n'est ajouté que quand le groupe du capteur vient d'être relu, pour que chaque courbe avance à sa propre fréquence.
        foreach (MetricDefinition definition in MyMetrics.Definitions)
        {
            if (definition.ReadGroup is { } group && !s.GroupsRead.Contains(group)) continue;
            // L'heure du relevé plutôt que celle de l'instant : une seule lecture d'horloge pour la centaine
            // de capteurs, et un repère de graphique qui date le point à l'instant où il a été mesuré.
            GetHistory(definition.Id).Push(definition.Read(sample).Number, sample.LocalTime);
        }

        foreach (MetricTileViewModel tile in MyMetricTiles)
        {
            tile.Apply(sample);
        }

        SnapshotUpdated?.Invoke(s);
        MetricsUpdated?.Invoke(sample);
    }

    private void ApplyDisks(IReadOnlyList<DiskSnapshot> snapshots)
    {
        // Reconciliation par identifiant (pas un ReplaceAll aveugle) pour ne pas effacer un test
        // de santé en cours/terminé sur une carte à chaque tick de rafraîchissement.
        for (int i = Disks.Count - 1; i >= 0; i--)
        {
            if (snapshots.All(s => s.Identifier != Disks[i].Identifier))
            {
                Disks.RemoveAt(i);
            }
        }

        foreach (DiskSnapshot snap in snapshots)
        {
            DiskItemViewModel? existing = Disks.FirstOrDefault(d => d.Identifier == snap.Identifier);
            if (existing is null)
            {
                var item = new DiskItemViewModel(_diskHealth, snap.Identifier);
                item.Apply(snap);
                Disks.Add(item);
            }
            else
            {
                existing.Apply(snap);
            }
        }
    }

    public void Dispose()
    {
        _loop.Dispose();
    }
}
