using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCPerfSuite.App.Metrics;
using PCPerfSuite.App.Utils;
using PCPerfSuite.Core.Hardware;
using PCPerfSuite.Core.Overlay;
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

    [ObservableProperty] private string displayValue = "--";
    [ObservableProperty] private string unit = "";

    public MetricTileViewModel(MetricDefinition definition, SampleHistory history)
    {
        Definition = definition;
        History = history;

        var color = (Color)ColorConverter.ConvertFromString(definition.Category.DefaultColor);
        LineBrush = Frozen(new SolidColorBrush(color));
        FillBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x30, color.R, color.G, color.B)));
    }

    public void Apply(MetricSample sample)
    {
        MetricReading reading = Definition.Read(sample);
        DisplayValue = reading.Value;
        Unit = reading.Unit;
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
    private readonly bool _initialized;

    public SensorGroup Group { get; }
    public string Name { get; }
    public string Description { get; }

    [ObservableProperty] private bool isAuto;
    [ObservableProperty] private string cadenceDisplay = "--";
    [ObservableProperty] private string averageCostDisplay = "--";

    private int _manualMs;

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

    public SensorGroupCadenceViewModel(HardwareMonitorService hardware, SensorGroup group, int? savedManualMs)
    {
        _hardware = hardware;
        Group = group;
        (Name, Description) = group switch
        {
            SensorGroup.Cpu => ("CPU", "Température, puissance, fréquence (la charge suit chaque relevé)"),
            SensorGroup.Gpu => ("GPU", "Charge, températures, puissance, VRAM, ventilateur"),
            SensorGroup.Memory => ("Mémoire vive", "Utilisation"),
            SensorGroup.Motherboard => ("Carte mère", "Températures, tensions, ventilateurs"),
            SensorGroup.Storage => ("Disques", "Débits, température, vie restante"),
            _ => ("Réseau", "Débits de toutes les cartes"),
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
        // Même marge de 10 % que l'échéancier : une cadence à peine plus lente que l'actualisation revient
        // à relire le groupe à chaque relevé.
        double intervalMs = status.Interval.TotalMilliseconds;
        CadenceDisplay = intervalMs * 0.9 <= refreshMs ? "à chaque relevé"
            : intervalMs >= 1000 ? $"toutes les {intervalMs / 1000:0.#} s"
            : $"toutes les {intervalMs:0} ms";
        AverageCostDisplay = status.AverageReadDuration is { } cost ? $"{cost.TotalMilliseconds:0.00} ms" : "--";
    }

    private void ApplyAndSave()
    {
        TimeSpan? manual = IsAuto ? null : TimeSpan.FromMilliseconds(ManualMs);
        _hardware.SetManualInterval(Group, manual);

        AppSettings settings = AppSettingsStore.Load();
        if (manual is null) settings.SensorGroupIntervalsMs.Remove(Group.ToString());
        else settings.SensorGroupIntervalsMs[Group.ToString()] = ManualMs;
        AppSettingsStore.Save(settings);
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
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<string, SampleHistory> _histories = new();
    private MetricSample? _lastSample;
    private bool _isRefreshing;

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

            _timer.Interval = TimeSpan.FromMilliseconds(clamped);

            AppSettings settings = AppSettingsStore.Load();
            settings.MonitoringRefreshMs = clamped;
            AppSettingsStore.Save(settings);

            // L'actualisation globale fait référence : sa valeur est recopiée dans la cadence imposée de chaque
            // groupe, celle qu'appliquent les groupes hors automatique.
            foreach (SensorGroupCadenceViewModel cadence in SensorCadences)
            {
                cadence.ManualMs = clamped;
            }
        }
    }

    public string RefreshHint => RefreshRates.Hint;

    /// <summary>Capteurs proposés en tuiles graphiques : le catalogue partagé avec l'overlay (sauf ce qui n'a pas
    /// de courbe), plus les capteurs propres à la machine ajoutés au fil des relevés.</summary>
    public MetricSelectionViewModel MyMetrics { get; }
    public ObservableCollection<MetricTileViewModel> MyMetricTiles { get; } = new();
    [ObservableProperty] private bool isCustomizingMyMetrics;

    public ObservableCollectionEx<DiskItemViewModel> Disks { get; } = new();

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
        $"En automatique, un groupe est relu d'autant moins souvent que sa lecture coûte cher : elle ne doit pas occuper " +
        $"plus de {HardwareMonitorService.AutoReadBudget * 100:0} % du temps (au plus toutes les " +
        $"{HardwareMonitorService.MaxAutoInterval.TotalSeconds:0} s). La charge CPU et les FPS sont lus à chaque relevé. " +
        "Changer l'actualisation recopie sa valeur dans la cadence imposée de chaque groupe.";

    public MonitoringViewModel(HardwareMonitorService hardware)
    {
        _hardware = hardware;

        AppSettings settings = AppSettingsStore.Load();
        _refreshMs = RefreshRates.Clamp(settings.MonitoringRefreshMs);

        // Avant le premier relevé, pour que les cadences imposées s'appliquent dès le départ.
        SensorCadences = Enum.GetValues<SensorGroup>()
            .Select(group => new SensorGroupCadenceViewModel(hardware, group,
                settings.SensorGroupIntervalsMs.TryGetValue(group.ToString(), out int ms) ? ms : null))
            .ToArray();

        // Première ouverture des tuiles graphiques : ce qu'affichaient les anciennes cartes, plus les tuiles
        // "Mes métriques" déjà choisies.
        IEnumerable<string> sensorIds = settings.MonitoringSensorIds
            ?? MetricCatalog.DefaultMonitoringIds.Union(settings.MonitoringMetricIds ?? Enumerable.Empty<string>());
        MyMetrics = new MetricSelectionViewModel(sensorIds, MetricCatalog.All.Where(m => m.HasGraph));
        MyMetrics.SelectionChanged += OnMyMetricsSelectionChanged;
        SyncMyMetricTiles();

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(_refreshMs),
        };
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();

        _ = RefreshAsync();
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

    private SampleHistory GetHistory(string metricId)
    {
        if (!_histories.TryGetValue(metricId, out SampleHistory? history))
        {
            history = new SampleHistory();
            _histories[metricId] = history;
        }
        return history;
    }

    private async Task RefreshAsync()
    {
        // Le timer continue de sonner pendant la lecture. Si la précédente n'est pas terminée, on saute ce
        // tick : sinon les lectures s'empilent en parallèle sur LibreHardwareMonitor, qui n'est pas prévu
        // pour ça, et l'interface accumule un retard qu'elle ne rattrape jamais.
        if (_isRefreshing)
        {
            SkippedTicks++;
            return;
        }

        _isRefreshing = true;
        try
        {
            (HardwareSnapshot snapshot, RtssFrameStats? game, TimeSpan rtssDuration) = await Task.Run(() =>
            {
                HardwareSnapshot hardware = _hardware.GetSnapshot();
                long rtssStart = Stopwatch.GetTimestamp();
                RtssFrameStats? frames = RtssFrameStatsReader.TryReadForeground();
                return (hardware, frames, Stopwatch.GetElapsedTime(rtssStart));
            });
            long applyStart = Stopwatch.GetTimestamp();
            Apply(snapshot, game);
            TimeSpan applyDuration = Stopwatch.GetElapsedTime(applyStart);
            RecordReadTimings(snapshot, rtssDuration, applyDuration);
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
        finally
        {
            _isRefreshing = false;
        }
    }

    private void RecordReadTimings(HardwareSnapshot snapshot, TimeSpan rtssDuration, TimeSpan applyDuration)
    {
        foreach (HardwareReadTiming timing in snapshot.ReadTimings)
        {
            ReadTimingRow(timing.Identifier, timing.Name).Record(timing.Duration);
        }

        ReadTimingRow("snapshot", "Relevé complet (GetSnapshot)").Record(snapshot.ReadDuration);
        ReadTimingRow("rtss", "FPS (RTSS)").Record(rtssDuration);

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

    /// <summary>Repart de zéro, par exemple pour mesurer après avoir changé l'actualisation.</summary>
    [RelayCommand]
    private void ResetReadTimings()
    {
        ReadTimings.Clear();
        SkippedTicks = 0;
    }

    private void Apply(HardwareSnapshot s, RtssFrameStats? game)
    {
        ApplyDisks(s.Disks);

        var sample = new MetricSample { Hardware = s, Game = game, LocalTime = DateTime.Now };
        _lastSample = sample;

        // Capteurs propres à la machine, découverts au fil des relevés (un disque branché en cours de route...).
        MyMetrics.AddDefinitions(MonitoringSensorCatalog.FromSnapshot(s));

        // Historique tenu pour tous les capteurs, affichés ou non : une tuile qu'on active a déjà sa courbe.
        foreach (MetricDefinition definition in MyMetrics.Definitions)
        {
            GetHistory(definition.Id).Push(definition.Read(sample).Number);
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
        _timer.Stop();
    }
}
