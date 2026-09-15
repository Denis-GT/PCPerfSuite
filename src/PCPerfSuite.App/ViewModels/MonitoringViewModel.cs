using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCPerfSuite.App.Metrics;
using PCPerfSuite.App.Utils;
using PCPerfSuite.Core.Hardware;
using PCPerfSuite.Core.Overlay;
using PCPerfSuite.Core.PowerSettings;

namespace PCPerfSuite.App.ViewModels;

public sealed partial class DiskItemViewModel : ObservableObject
{
    private readonly DiskHealthService _health;

    public string Identifier { get; }

    [ObservableProperty] private string name = "…";
    [ObservableProperty] private double usedPercent;
    [ObservableProperty] private double? readRateBytesPerSecond;
    [ObservableProperty] private double? writeRateBytesPerSecond;
    [ObservableProperty] private double? temperatureC;
    [ObservableProperty] private double? remainingLifePercent;

    public SampleHistory ReadHistory { get; } = new();
    public SampleHistory WriteHistory { get; } = new();

    [ObservableProperty] private bool isTesting;
    [ObservableProperty] private DiskHealthStatus? healthStatus;
    [ObservableProperty] private string? healthSummary;

    public string ReadRateDisplay => ByteFormatter.FormatRate(ReadRateBytesPerSecond);
    public string WriteRateDisplay => ByteFormatter.FormatRate(WriteRateBytesPerSecond);
    public string TemperatureDisplay => TemperatureC is { } t ? $"{t:0.#} °C" : "--";
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
        ReadRateBytesPerSecond = s.ReadRateBytesPerSecond;
        WriteRateBytesPerSecond = s.WriteRateBytesPerSecond;
        TemperatureC = s.TemperatureC;
        RemainingLifePercent = s.RemainingLifePercent;
        ReadHistory.Push(s.ReadRateBytesPerSecond);
        WriteHistory.Push(s.WriteRateBytesPerSecond);
    }

    partial void OnReadRateBytesPerSecondChanged(double? value) => OnPropertyChanged(nameof(ReadRateDisplay));
    partial void OnWriteRateBytesPerSecondChanged(double? value) => OnPropertyChanged(nameof(WriteRateDisplay));
    partial void OnTemperatureCChanged(double? value) => OnPropertyChanged(nameof(TemperatureDisplay));
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

/// <summary>Tuile de "Mes métriques", mise à jour en place à chaque relevé plutôt que recréée (pas de clignotement).</summary>
public sealed partial class MetricTileViewModel : ObservableObject
{
    public MetricDefinition Definition { get; }
    public string Label => Definition.Label;
    public string CategoryName => Definition.Category.Name;
    public bool IsPercent => Definition.IsPercent;

    [ObservableProperty] private string displayValue = "--";
    [ObservableProperty] private string unit = "";
    [ObservableProperty] private double percent;

    public MetricTileViewModel(MetricDefinition definition) => Definition = definition;

    public void Apply(MetricSample sample)
    {
        MetricReading reading = Definition.Read(sample);
        DisplayValue = reading.Value;
        Unit = reading.Unit;
        Percent = reading.Number ?? 0;
    }
}

/// <summary>Ligne de "Ventilateurs détectés", mise à jour en place pour que son graphique garde son historique.</summary>
public sealed partial class FanItemViewModel : ObservableObject
{
    public string SensorId { get; }
    public SampleHistory RpmHistory { get; } = new();

    [ObservableProperty] private string sensorName = "";
    [ObservableProperty] private string hardwareName = "";
    [ObservableProperty] private double? rpm;
    [ObservableProperty] private double? percentControl;

    public FanItemViewModel(string sensorId) => SensorId = sensorId;

    public void Apply(FanReading reading)
    {
        SensorName = reading.SensorName;
        HardwareName = reading.HardwareName;
        Rpm = reading.Rpm;
        PercentControl = reading.PercentControl;
        RpmHistory.Push(reading.Rpm);
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
        }
    }

    public string RefreshHint => RefreshRates.Hint;

    public MetricSelectionViewModel MyMetrics { get; }
    public ObservableCollection<MetricTileViewModel> MyMetricTiles { get; } = new();
    [ObservableProperty] private bool isCustomizingMyMetrics;

    [ObservableProperty] private string cpuName = "…";
    [ObservableProperty] private double cpuLoad;
    [ObservableProperty] private double? cpuTemp;
    [ObservableProperty] private double? cpuPower;
    [ObservableProperty] private double? cpuClock;

    [ObservableProperty] private bool hasGpu;
    [ObservableProperty] private string gpuName = "…";
    [ObservableProperty] private string gpuVendor = "";
    [ObservableProperty] private double gpuLoad;
    [ObservableProperty] private double? gpuTemp;
    [ObservableProperty] private double? gpuHotspot;
    [ObservableProperty] private double? gpuCoreClock;
    [ObservableProperty] private double? gpuMemClock;
    [ObservableProperty] private double? gpuPower;
    [ObservableProperty] private double? gpuVramUsed;
    [ObservableProperty] private double? gpuVramTotal;
    [ObservableProperty] private double gpuVramLoad;
    [ObservableProperty] private double? gpuFanRpm;

    [ObservableProperty] private double memLoad;
    [ObservableProperty] private double? memUsedGb;
    [ObservableProperty] private double? memTotalGb;

    [ObservableProperty] private string motherboardName = "…";
    [ObservableProperty] private string motherboardTempLabel = "Température système";
    [ObservableProperty] private double? motherboardTemp;
    [ObservableProperty] private string motherboardVrmLabel = "VRM";
    [ObservableProperty] private double? motherboardVrmTemp;

    [ObservableProperty] private double? netDownload;
    [ObservableProperty] private double? netUpload;

    public string NetDownloadDisplay => ByteFormatter.FormatRate(NetDownload);
    public string NetUploadDisplay => ByteFormatter.FormatRate(NetUpload);

    partial void OnNetDownloadChanged(double? value) => OnPropertyChanged(nameof(NetDownloadDisplay));
    partial void OnNetUploadChanged(double? value) => OnPropertyChanged(nameof(NetUploadDisplay));

    // Historiques des graphiques, tenus ici plutôt que dans les Sparkline pour ne pas dépendre de la vue.
    public SampleHistory CpuLoadHistory { get; } = new();
    public SampleHistory CpuTempHistory { get; } = new();
    public SampleHistory CpuPowerHistory { get; } = new();
    public SampleHistory GpuLoadHistory { get; } = new();
    public SampleHistory GpuTempHistory { get; } = new();
    public SampleHistory GpuPowerHistory { get; } = new();
    public SampleHistory MemLoadHistory { get; } = new();
    public SampleHistory MotherboardTempHistory { get; } = new();
    public SampleHistory MotherboardVrmTempHistory { get; } = new();
    public SampleHistory NetDownloadHistory { get; } = new();
    public SampleHistory NetUploadHistory { get; } = new();

    public ObservableCollectionEx<FanItemViewModel> Fans { get; } = new();
    public ObservableCollectionEx<SensorReading> MotherboardOtherTemps { get; } = new();
    public ObservableCollectionEx<SensorReading> MotherboardVoltages { get; } = new();
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

    public string SensorCadencesHint =>
        $"En automatique, un groupe est relu d'autant moins souvent que sa lecture coûte cher : elle ne doit pas occuper " +
        $"plus de {HardwareMonitorService.AutoReadBudget * 100:0} % du temps (au plus toutes les " +
        $"{HardwareMonitorService.MaxAutoInterval.TotalSeconds:0} s). La charge CPU et les FPS sont lus à chaque relevé.";

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

        MyMetrics = new MetricSelectionViewModel(settings.MonitoringMetricIds ?? MetricCatalog.DefaultMonitoringIds);
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
        settings.MonitoringMetricIds = MyMetrics.SelectedIds;
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

        // Les tuiles restantes sont déjà dans l'ordre du catalogue : il suffit d'insérer les nouvelles à leur rang.
        for (int i = 0; i < selected.Count; i++)
        {
            if (i < MyMetricTiles.Count && MyMetricTiles[i].Definition == selected[i]) continue;

            var tile = new MetricTileViewModel(selected[i]);
            if (_lastSample is not null) tile.Apply(_lastSample);
            MyMetricTiles.Insert(i, tile);
        }
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

        // Sur le thread de l'interface : valeurs affichées, historiques des graphiques et tous les abonnés
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
        CpuName = s.Cpu.Name;
        CpuLoad = s.Cpu.LoadPercent ?? 0;
        CpuTemp = s.Cpu.PackageTempC;
        CpuPower = s.Cpu.PowerWatts;
        CpuClock = s.Cpu.MaxClockMhz;
        CpuLoadHistory.Push(CpuLoad);
        CpuTempHistory.Push(CpuTemp);
        CpuPowerHistory.Push(CpuPower);

        HasGpu = s.Gpu is not null;
        if (s.Gpu is { } gpu)
        {
            GpuName = gpu.Name;
            GpuVendor = gpu.Vendor;
            GpuLoad = gpu.LoadPercent ?? 0;
            GpuTemp = gpu.CoreTempC;
            GpuHotspot = gpu.HotSpotTempC;
            GpuCoreClock = gpu.CoreClockMhz;
            GpuMemClock = gpu.MemoryClockMhz;
            GpuPower = gpu.PowerWatts;
            GpuVramUsed = gpu.VramUsedMb;
            GpuVramTotal = gpu.VramTotalMb;
            GpuVramLoad = gpu.VramUsedMb is { } used && gpu.VramTotalMb is { } total and > 0
                ? used / total * 100 : 0;
            GpuFanRpm = gpu.FanRpm;
            GpuLoadHistory.Push(GpuLoad);
            GpuTempHistory.Push(GpuTemp);
            GpuPowerHistory.Push(GpuPower);
        }

        MemLoad = s.Memory.LoadPercent ?? 0;
        MemUsedGb = s.Memory.UsedGb;
        MemTotalGb = s.Memory.TotalGb;
        MemLoadHistory.Push(MemLoad);

        MotherboardName = s.Motherboard.Name;
        MotherboardTempLabel = s.Motherboard.SystemTempLabel;
        MotherboardTemp = s.Motherboard.SystemTempC;
        MotherboardVrmLabel = s.Motherboard.VrmTempLabel;
        MotherboardVrmTemp = s.Motherboard.VrmTempC;
        MotherboardTempHistory.Push(MotherboardTemp);
        MotherboardVrmTempHistory.Push(MotherboardVrmTemp);

        NetDownload = s.Network.DownloadBytesPerSecond;
        NetUpload = s.Network.UploadBytesPerSecond;
        NetDownloadHistory.Push(NetDownload);
        NetUploadHistory.Push(NetUpload);
        MotherboardOtherTemps.ReplaceAll(s.Motherboard.OtherTemperatures);
        MotherboardVoltages.ReplaceAll(s.Motherboard.Voltages);

        ApplyFans(s.Fans);
        ApplyDisks(s.Disks);

        var sample = new MetricSample { Hardware = s, Game = game, LocalTime = DateTime.Now };
        _lastSample = sample;
        foreach (MetricTileViewModel tile in MyMetricTiles)
        {
            tile.Apply(sample);
        }

        SnapshotUpdated?.Invoke(s);
        MetricsUpdated?.Invoke(sample);
    }

    private void ApplyFans(IReadOnlyList<FanReading> readings)
    {
        // Réconciliation par capteur, comme les disques : une ligne recréée à chaque tick perdrait son historique.
        for (int i = Fans.Count - 1; i >= 0; i--)
        {
            if (readings.All(r => r.SensorId != Fans[i].SensorId))
            {
                Fans.RemoveAt(i);
            }
        }

        foreach (FanReading reading in readings)
        {
            FanItemViewModel? existing = Fans.FirstOrDefault(f => f.SensorId == reading.SensorId);
            if (existing is null)
            {
                existing = new FanItemViewModel(reading.SensorId);
                Fans.Add(existing);
            }
            existing.Apply(reading);
        }
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
