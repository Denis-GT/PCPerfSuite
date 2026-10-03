using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCPerfSuite.App.Utils;
using PCPerfSuite.Core.Benchmark;
using PCPerfSuite.Core.Benchmark.Disk;
using PCPerfSuite.Core.Benchmark.Memory;
using PCPerfSuite.Core.Benchmark.Protocol;
using PCPerfSuite.Core.Benchmark.Results;
using PCPerfSuite.Core.Benchmark.Session;
using PCPerfSuite.Core.Benchmark.Worker;
using PCPerfSuite.Core.Compatibility;
using PCPerfSuite.Core.Hardware;
using PCPerfSuite.Core.Hardware.Cpu;
using PCPerfSuite.Core.PowerSettings;
using PCPerfSuite.Core.Profiles;
using PCPerfSuite.Core.Safety;
using PCPerfSuite.Core.SystemInfo;

namespace PCPerfSuite.App.ViewModels;

/// <summary>Un test cochable de la page Bench, avec sa raison quand il est indisponible sur ce PC.</summary>
public sealed partial class BenchTestItemViewModel : ObservableObject
{
    public BenchTestItemViewModel(BenchTestKind kind)
    {
        Kind = kind;
        Title = BenchTestKinds.Title(kind);
    }

    public BenchTestKind Kind { get; }

    public string Title { get; }

    public bool IsDisk => Kind == BenchTestKind.Disk;

    [ObservableProperty] private bool isChecked;

    [ObservableProperty] private bool isAvailable = true;

    /// <summary>Pourquoi le test est indisponible (règle 3), null quand il l'est.</summary>
    [ObservableProperty] private string? reason;

    [ObservableProperty] private string estimateText = "";

    public event Action? CheckedChanged;

    partial void OnIsCheckedChanged(bool value) => CheckedChanged?.Invoke();
}

/// <summary>Un volume de la liste déroulante du test disque.</summary>
public sealed class BenchVolumeItemViewModel
{
    public BenchVolumeItemViewModel(BenchVolume volume)
    {
        Volume = volume;
        Label = volume.Unavailable is { } problem ? $"{volume.Describe()} — N/D : {problem.Reason}" : volume.Describe();
    }

    public BenchVolume Volume { get; }

    /// <summary>Texte de la liste déroulante (gabarit LabelOptionTemplate).</summary>
    public string Label { get; }

    public bool IsEligible => Volume.IsEligible;
}

/// <summary>Carte de résultat d'un test : points, mesures en unités physiques, et ce que les capteurs ont vu.</summary>
public sealed class BenchResultCardViewModel
{
    public BenchResultCardViewModel(BenchTestResult test)
    {
        Title = test.Title;
        PointsText = test.Points is { } points
            ? $"{points.ToString("N0", CultureInfo.CurrentCulture)} pts"
            : !test.Succeeded ? "échec" : test.ChecksumMismatch ? "sans points : erreur de calcul" : !test.IsComparable ? "sans points : chemin de calcul non comparable" : "sans points";
        StatusText = test.Succeeded
            ? $"{BenchPlanner.DescribeDuration(test.DurationSeconds)}" + (test.IsUnstable ? " · mesures instables" : "")
            : test.Error ?? "échec";
        Lines = test.Measurements.Select(Describe).ToList();
        var details = new List<string> { test.Cadence, test.Throttle };
        if (test.MaxCpuTempC is { } max) details.Add($"processeur jusqu'à {max:0} °C");
        if (test.IdleReturnNote is { } idle) details.Add($"repos : {idle}");
        details.AddRange(test.Notes.Select(n => $"{n.Key} : {n.Value}"));
        DetailText = string.Join(" · ", details);
        Warning = test.StopReason is not null ? test.StopDetail ?? "arrêt de sécurité"
            : test.ChecksumMismatch ? "Un noyau a rendu un résultat différent de l'étalon : erreur de calcul (matériel instable ?)."
            : test.IsUnstable ? "Passes trop dispersées (coefficient de variation > 3 %) : activité en arrière-plan ou bridage qui oscille." : null;
    }

    public string Title { get; }

    public string PointsText { get; }

    public string StatusText { get; }

    public IReadOnlyList<string> Lines { get; }

    public string DetailText { get; }

    public string? Warning { get; }

    private static string Describe(BenchMeasurement m)
    {
        string value = m.Median >= 100 ? m.Median.ToString("N0", CultureInfo.CurrentCulture) : m.Median.ToString("0.#", CultureInfo.CurrentCulture);
        string spread = m.Values.Count > 1 ? $" (CV {m.Cv.ToString("P1", CultureInfo.CurrentCulture)}{(m.IsUnstable ? ", instable" : "")})" : "";
        return $"{m.Label} : {value} {m.Unit}{spread}";
    }
}

/// <summary>Une session passée, pour l'historique.</summary>
public sealed class BenchHistoryItemViewModel
{
    public BenchHistoryItemViewModel(BenchSessionResult session)
    {
        Session = session;
        Text = $"{session.StartedUtc.ToLocalTime():dd/MM/yyyy HH:mm} · {session.Summary()}";
        var notes = new List<string>();
        if (!session.IsRepresentative) notes.Add("sur batterie : non représentatif");
        if (session.BenchVersion != BenchVersion.Bench) notes.Add($"bench v{session.BenchVersion} : points non comparables à la v{BenchVersion.Bench}");
        Note = notes.Count == 0 ? null : string.Join(" · ", notes);
    }

    public BenchSessionResult Session { get; }

    public string Text { get; }

    public string? Note { get; }
}

/// <summary>
/// Sous-onglet « Bench » (#10) : tests cochables indépendamment avec leur raison, volume et taille du test disque,
/// durée estimée, confirmation « Non » par défaut (D6), progression et courbes en direct, cartes de résultat et
/// historique. Charge à la première ouverture ; jamais de lancement automatique. Pendant une session, il déclare ses
/// groupes de capteurs au mode éco (<see cref="IBackgroundSensorConsumer"/>) : la sécurité continue fenêtre cachée.
/// Tout le matériel passe par <see cref="BenchSession"/> et ses ports ; ici, l'interface et les réglages.
/// </summary>
public sealed partial class BenchViewModel : ObservableObject, IPageLifecycle, IBackgroundSensorConsumer, IDisposable
{
    private static readonly SensorGroup[] RequiredGroups = [SensorGroup.Cpu, SensorGroup.CpuLoad, SensorGroup.Motherboard, SensorGroup.Battery];
    private static readonly TimeSpan CadenceInterval = TimeSpan.FromMilliseconds(250);
    private const double IdleReturnEstimateSeconds = 20;

    private readonly HardwareMonitorService _hardware;
    private readonly MonitoringViewModel _monitoring;
    private readonly CpuControlService _cpuControl;
    private readonly GpuControlService _gpuControl;
    private readonly FanCurvesViewModel _fans;
    private readonly TuningLease _lease;
    private readonly StartupRecoveryReport _recovery;
    private readonly BackgroundLoadWindow _backgroundLoad = new();
    private CpuTopology? _topology;
    private BenchThermalLimits _thermal = BenchThermalPolicy.Resolve(null, null, null);
    private MemoryBenchSizes? _memorySizes;
    private IReadOnlyList<BenchVolume> _volumes = [];
    private string? _workerProblem;
    private bool _loadRequested;
    private bool _loading;
    private bool _disposed;
    private CancellationTokenSource? _runCancel;
    private volatile bool _running;
    private BenchDiagnosticStatus? _diagnostic;

    public BenchViewModel(HardwareMonitorService hardware, MonitoringViewModel monitoring, CpuControlService cpuControl, GpuControlService gpuControl,
        FanCurvesViewModel fans, TuningLease lease, TuningStatusViewModel tuning, StartupRecoveryReport recovery, BenchResultStore? store = null)
    {
        _hardware = hardware;
        _monitoring = monitoring;
        _cpuControl = cpuControl;
        _gpuControl = gpuControl;
        _fans = fans;
        _lease = lease;
        Tuning = tuning;
        _recovery = recovery;
        Store = store ?? new BenchResultStore();

        foreach (BenchTestKind kind in BenchTestKinds.All)
        {
            var item = new BenchTestItemViewModel(kind) { IsChecked = true };
            item.CheckedChanged += OnSelectionChanged;
            Tests.Add(item);
        }

        RecoveryNote = recovery.Handlers.FirstOrDefault(h => h.HandlerId == BenchVersion.Requester)?.Note;
        _monitoring.SnapshotUpdated += OnSnapshot;
    }

    public TuningStatusViewModel Tuning { get; }

    public BenchResultStore Store { get; }

    /// <summary>Pour la ligne « Bench » du diagnostic : null tant que la page n'a pas été ouverte.</summary>
    public BenchDiagnosticStatus? DiagnosticStatus => _diagnostic;

    public ObservableCollection<BenchTestItemViewModel> Tests { get; } = new();

    public ObservableCollection<BenchVolumeItemViewModel> Volumes { get; } = new();

    public ObservableCollection<BenchResultCardViewModel> Results { get; } = new();

    public ObservableCollection<BenchHistoryItemViewModel> History { get; } = new();

    public SampleHistory TempHistory { get; } = new(300);

    public SampleHistory PowerHistory { get; } = new(300);

    public SampleHistory ClockHistory { get; } = new(300);

    [ObservableProperty] private bool isPageShown;

    [ObservableProperty] private BenchVolumeItemViewModel? selectedVolume;

    [ObservableProperty] private int diskFileSizeMb = (int)(DiskTestFile.DefaultFileBytes / DiskTestFile.Mebibyte);

    [ObservableProperty] private bool sustainedEnabled = true;

    /// <summary>Toute la fonction indisponible (worker non lançable, batterie trop faible) : message adapté au PC.</summary>
    [ObservableProperty] private string? unavailableMessage;

    [ObservableProperty] private string? recoveryNote;

    [ObservableProperty] private string preconditionText = "";

    [ObservableProperty] private string estimatedDurationText = "";

    [ObservableProperty] private string? status;

    /// <summary>Démarrer en dépend : sans cette notification, le bouton resterait grisé après le chargement.</summary>
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(StartCommand))] private bool isLoaded;

    [ObservableProperty] private bool isRunning;

    [ObservableProperty] private string phase = "";

    [ObservableProperty] private double percent;

    [ObservableProperty] private string? detail;

    [ObservableProperty] private string? liveValueText;

    [ObservableProperty] private string liveSensorsText = "";

    [ObservableProperty] private string? historyStatus;

    public bool HasResults => Results.Count > 0;

    public bool HasHistory => History.Count > 0;

    public string DiskSizeText => DiskFileSizeMb >= 1024 && DiskFileSizeMb % 1024 == 0 ? $"{DiskFileSizeMb / 1024} Go" : $"{DiskFileSizeMb} Mo";

    public string DiskWriteText => $"jusqu'à {DiskBenchPlan.DefaultWriteBudgetFactor * DiskFileSizeMb / 1024.0:0.#} Go écrits, fichier supprimé à la fin";

    public string ThermalText => $"Arrêt de sécurité : {_thermal.Describe()} ; ventilateur CPU à l'arrêt ; batterie sous {BenchPreconditions.BatteryStopPercent:0} %.";

    // ---- Cycle de vie ----

    partial void OnIsPageShownChanged(bool value)
    {
        if (!value || _loadRequested) return;
        _loadRequested = true;
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        _loading = true;
        Status = "Analyse de ce PC…";
        try
        {
            BenchSettings settings = AppSettingsStore.Load().Bench ?? new BenchSettings();
            IReadOnlyList<BenchSessionResult> history = [];
            int unreadable = 0;
            await Task.Run(() =>
            {
                _topology = CpuTopology.Read().Topology;
                _memorySizes = MemoryBenchSizing.ReadCurrent(_topology);
                _volumes = BenchVolumeReader.Read();
                _workerProblem = BenchWorkerLauncher.FindExecutable(out string? reason) is null ? reason : null;
                history = Store.LoadAll(out unreadable);
            });
            if (_disposed) return;

            _thermal = BenchThermalPolicy.Resolve(_cpuControl.Platform, _topology, _hardware.LastSnapshot?.CpuThrottle?.TjMaxC);
            OnPropertyChanged(nameof(ThermalText));

            Volumes.Clear();
            foreach (BenchVolume volume in _volumes) Volumes.Add(new BenchVolumeItemViewModel(volume));
            SelectedVolume = Volumes.FirstOrDefault(v => v.IsEligible && settings.DiskVolume is { } letter && string.Equals(v.Volume.DriveLetter, letter, StringComparison.OrdinalIgnoreCase))
                ?? Volumes.FirstOrDefault(v => v.IsEligible && v.Volume.IsSystem)
                ?? Volumes.FirstOrDefault(v => v.IsEligible)
                ?? Volumes.FirstOrDefault();

            DiskFileSizeMb = (int)(DiskTestFile.ClampFileBytes((long)settings.DiskFileSizeMb * DiskTestFile.Mebibyte) / DiskTestFile.Mebibyte);
            SustainedEnabled = settings.SustainedEnabled;
            if (settings.SelectedTests is { } selected)
            {
                foreach (BenchTestItemViewModel item in Tests) item.IsChecked = selected.Contains(BenchTestKinds.Key(item.Kind));
            }

            History.Clear();
            foreach (BenchSessionResult session in history) History.Add(new BenchHistoryItemViewModel(session));
            OnPropertyChanged(nameof(HasHistory));
            HistoryStatus = unreadable > 0 ? $"{unreadable} fichier(s) de session illisible(s), ignoré(s)." : null;

            EvaluatePreconditions();
            UpdateEstimate();
            Status = null;
            IsLoaded = true;
        }
        catch (Exception ex)
        {
            Status = $"Analyse impossible : {ex.Message}";
            CrashLog.Record(ex, "page Bench");
        }
        finally
        {
            _loading = false;
        }
    }

    // ---- Réglages ----

    private void OnSelectionChanged()
    {
        UpdateEstimate();
        SaveSettings();
    }

    partial void OnSelectedVolumeChanged(BenchVolumeItemViewModel? value)
    {
        if (_loading) return;
        EvaluatePreconditions();
        UpdateEstimate();
        SaveSettings();
    }

    partial void OnDiskFileSizeMbChanged(int value)
    {
        OnPropertyChanged(nameof(DiskSizeText));
        OnPropertyChanged(nameof(DiskWriteText));
        if (_loading) return;
        EvaluatePreconditions();
        UpdateEstimate();
        SaveSettings();
    }

    partial void OnSustainedEnabledChanged(bool value)
    {
        if (_loading) return;
        UpdateEstimate();
        SaveSettings();
    }

    [RelayCommand]
    private void IncreaseDiskSize() => DiskFileSizeMb = (int)(DiskTestFile.ClampFileBytes(((long)DiskFileSizeMb + 256) * DiskTestFile.Mebibyte) / DiskTestFile.Mebibyte);

    [RelayCommand]
    private void DecreaseDiskSize() => DiskFileSizeMb = (int)(DiskTestFile.ClampFileBytes(((long)DiskFileSizeMb - 256) * DiskTestFile.Mebibyte) / DiskTestFile.Mebibyte);

    private void SaveSettings()
    {
        if (_loading || !IsLoaded) return;
        List<string> selected = Tests.Where(t => t.IsChecked).Select(t => BenchTestKinds.Key(t.Kind)).ToList();
        string? volume = SelectedVolume?.Volume.DriveLetter;
        int size = DiskFileSizeMb;
        bool sustained = SustainedEnabled;
        AppSettingsStore.Update(settings =>
        {
            settings.Bench ??= new BenchSettings();
            settings.Bench.SelectedTests = selected;
            settings.Bench.DiskVolume = volume;
            settings.Bench.DiskFileSizeMb = size;
            settings.Bench.SustainedEnabled = sustained;
        });
    }

    // ---- Préconditions et estimation ----

    private BenchPreconditionReport EvaluatePreconditions()
    {
        BenchVolume? volume = SelectedVolume?.Volume;
        long fileBytes = (long)DiskFileSizeMb * DiskTestFile.Mebibyte;
        Unavailable? diskProblem = volume is null
            ? new Unavailable(UnavailableCause.HardwareOrDriver, "aucun volume local éligible (NTFS, ReFS ou exFAT)")
            : volume.Unavailable ?? (volume.FreeBytes < DiskTestFile.RequiredFreeBytes(fileBytes)
                ? new Unavailable(UnavailableCause.HardwareOrDriver, $"pas assez d'espace libre sur {volume.DriveLetter} : il faut {DiskTestFile.RequiredFreeBytes(fileBytes) / DiskTestFile.Mebibyte} Mo")
                : null);

        HardwareSnapshot? snapshot = _hardware.LastSnapshot;
        BenchPreconditionReport report = BenchPreconditions.Evaluate(new BenchPreconditionInputs(
            snapshot?.Battery?.PowerOnline,
            BenchSafetySample.BatteryPercentOf(snapshot?.Battery),
            _backgroundLoad,
            _workerProblem,
            _memorySizes?.BandwidthUnavailable,
            _memorySizes?.LatencyUnavailable,
            diskProblem));

        foreach (BenchTestItemViewModel item in Tests)
        {
            item.IsAvailable = report.IsAvailable(item.Kind);
            item.Reason = report.For(item.Kind)?.Reason;
        }
        PreconditionText = string.Join(" · ", report.Notes);
        UnavailableMessage = report.AnyAvailable ? null : $"Le bench n'est pas disponible sur ce PC pour l'instant : {report.For(BenchTestKind.CpuMono)?.Reason}.";
        _diagnostic = new BenchDiagnosticStatus(report.PerTest, _workerProblem, _thermal.Describe(), _volumes.Count(v => v.IsEligible), report.Notes);
        StartCommand.NotifyCanExecuteChanged();
        return report;
    }

    private void UpdateEstimate()
    {
        var options = new BenchPlanOptions(SustainedEnabled, (long)DiskFileSizeMb * DiskTestFile.Mebibyte, SelectedVolume?.Volume.IsRotational ?? false);
        double total = 0;
        int count = 0;
        foreach (BenchTestItemViewModel item in Tests)
        {
            double seconds = BenchPlanner.EstimateSeconds(item.Kind, options);
            item.EstimateText = $"≈ {BenchPlanner.DescribeDuration(seconds)}";
            if (item.IsChecked && item.IsAvailable)
            {
                total += seconds;
                count++;
            }
        }
        if (count > 1) total += (count - 1) * IdleReturnEstimateSeconds;
        EstimatedDurationText = count == 0 ? "Aucun test coché." : $"Durée estimée : {BenchPlanner.DescribeDuration(total)} pour {count} test{(count > 1 ? "s" : "")} (retour au repos compris).";
    }

    // ---- Relevés ----

    private void OnSnapshot(HardwareSnapshot snapshot)
    {
        if (!IsPageShown && !_running) return;
        // Heure monotone : un changement d'heure de Windows ne doit ni vider ni étirer la fenêtre de 10 s.
        _backgroundLoad.Note(LivenessWatch.MonotonicNow(), snapshot.Cpu.LoadPercent);
        if (!_running) return;

        float? temp = snapshot.Cpu.PackageTempC ?? snapshot.Cpu.MaxCoreTempC;
        TempHistory.Push(temp);
        PowerHistory.Push(snapshot.Cpu.PowerWatts);
        ClockHistory.Push(snapshot.Cpu.MaxClockMhz);
        var parts = new List<string>();
        if (temp is { } t) parts.Add($"{t:0} °C");
        if (snapshot.Cpu.PowerWatts is { } w) parts.Add($"{w:0} W");
        if (snapshot.Cpu.MaxClockMhz is { } mhz) parts.Add($"{mhz:0} MHz");
        if (snapshot.CpuThrottle is { } throttle && (throttle.Thermal == true || throttle.PowerLimit == true || throttle.Prochot == true)) parts.Add("bridage");
        LiveSensorsText = parts.Count == 0 ? "capteurs CPU non lus (pilote PawnIO absent ?)" : string.Join(" · ", parts);
    }

    public void AddRequiredGroups(ISet<SensorGroup> into)
    {
        if (!_running) return;
        foreach (SensorGroup group in RequiredGroups) into.Add(group);
    }

    // ---- Session ----

    private bool CanStart() => IsLoaded && !IsRunning && UnavailableMessage is null;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        BenchPreconditionReport report = EvaluatePreconditions();
        List<BenchTestItemViewModel> selected = Tests.Where(t => t.IsChecked && t.IsAvailable).ToList();
        if (selected.Count == 0)
        {
            Status = "Coche au moins un test disponible.";
            return;
        }

        // Décision D6 : avertissement à chaque fois, « Non » par défaut.
        if (ShowMessage(BuildConfirmation(selected, report), MessageBoxImage.Warning, MessageBoxButton.YesNo, MessageBoxResult.No) != MessageBoxResult.Yes) return;

        // Dossier sécurisé, espace libre relu, ancien fichier supprimé : des E/S (un disque externe peut mettre des
        // secondes à se réveiller), donc hors du fil d'interface.
        DiskTestFilePlacement? diskPlacement = null;
        if (selected.Any(t => t.IsDisk) && SelectedVolume?.Volume is { } diskVolume)
        {
            long fileBytes = (long)DiskFileSizeMb * DiskTestFile.Mebibyte;
            Status = "Préparation du fichier de test disque…";
            diskPlacement = await Task.Run(() => DiskTestFile.Prepare(diskVolume, fileBytes));
            Status = null;
        }

        List<BenchTestPlan> plans = BuildPlans(selected, diskPlacement, out List<string> skipped);
        if (plans.Count == 0)
        {
            Status = skipped.Count > 0 ? string.Join(" ; ", skipped) : "Aucun test à passer.";
            return;
        }
        SaveSettings();

        Results.Clear();
        OnPropertyChanged(nameof(HasResults));
        TempHistory.Clear();
        PowerHistory.Clear();
        ClockHistory.Clear();
        _runCancel = new CancellationTokenSource();
        _running = true;
        IsRunning = true;
        Phase = "démarrage";
        Percent = 0;
        Detail = skipped.Count > 0 ? string.Join(" ; ", skipped) : null;
        Status = null;
        StopCommand.NotifyCanExecuteChanged();
        StartCommand.NotifyCanExecuteChanged();

        try
        {
            var plan = new BenchSessionPlan { Tests = plans, Thermal = _thermal };
            var session = new BenchSession(new BenchSessionPorts
            {
                StartWorker = StartWorkerAsync,
                Lease = _lease,
                Journal = SessionJournal.Current,
                SubscribeSnapshots = handler =>
                {
                    _monitoring.SnapshotUpdated += handler;
                    return new ActionDisposable(() => _monitoring.SnapshotUpdated -= handler);
                },
                RequestCadence = () => _hardware.RequestCadence(BenchVersion.Requester, CadenceInterval, RequiredGroups),
                RaisePriority = () => ProcessPriorityScope.Raise(),
                LastSnapshot = () => _hardware.LastSnapshot,
            });
            var progress = new Progress<BenchSessionProgress>(OnProgress);
            BenchSessionOutcome outcome = await session.RunAsync(plan, p => ((IProgress<BenchSessionProgress>)progress).Report(p), _runCancel.Token);

            if (outcome.LeaseRefusal is { } refusal)
            {
                Status = $"Bench refusé : {refusal}";
                return;
            }

            Phase = "enregistrement";
            string? fansText = DescribeFans();
            CpuTopology? topology = _topology;
            BenchContext context = await Task.Run(() => BenchContextReader.Read(new BenchContextSources
            {
                CpuPowerLimits = _cpuControl.ReadPowerLimits,
                GpuControl = _gpuControl.GetSnapshot,
                LastSnapshot = () => _hardware.LastSnapshot,
                FansDescription = fansText,
                Platform = _cpuControl.Platform,
                Topology = topology,
            }));
            BenchSessionResult result = BenchSessionResult.From(outcome, context, report, _thermal);
            string? saveError = null;
            string? path = await Task.Run(() => Store.Save(result, out saveError));

            foreach (BenchTestResult test in result.Tests) Results.Add(new BenchResultCardViewModel(test));
            OnPropertyChanged(nameof(HasResults));
            History.Insert(0, new BenchHistoryItemViewModel(result));
            OnPropertyChanged(nameof(HasHistory));

            var status = new List<string>();
            if (outcome.StoppedBy is { } reason) status.Add($"{BenchSafetyMonitor.Label(reason)} : la session s'est arrêtée d'elle-même.");
            else if (outcome.Cancelled) status.Add("Bench arrêté à ta demande.");
            else status.Add("Bench terminé.");
            if (!report.IsRepresentative) status.Add("Sur batterie : résultat non représentatif.");
            status.Add(path is null ? $"Session non enregistrée : {saveError}" : "Session enregistrée dans le dossier bench.");
            Status = string.Join(" ", status);
        }
        catch (Exception ex)
        {
            Status = $"Le bench a échoué : {ex.Message}";
            CrashLog.Record(ex, "bench");
        }
        finally
        {
            _running = false;
            IsRunning = false;
            Phase = "";
            Detail = null;
            LiveValueText = null;
            _runCancel?.Dispose();
            _runCancel = null;
            StopCommand.NotifyCanExecuteChanged();
            StartCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanStop() => IsRunning;

    /// <summary>Toujours visible ; actif pendant une session. Demande l'arrêt au worker, qui rend un résultat partiel.</summary>
    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop()
    {
        _runCancel?.Cancel();
        Phase = "arrêt…";
    }

    private static async Task<IBenchWorker> StartWorkerAsync(Action<string>? log, CancellationToken cancel)
        => await BenchWorkerLauncher.StartAsync(log, cancel);

    private void OnProgress(BenchSessionProgress p)
    {
        string test = p.Kind is { } kind ? BenchTestKinds.Title(kind) : "";
        Phase = p.TestCount > 1 ? $"Test {p.TestIndex + 1}/{p.TestCount} · {test} · {p.Phase}" : $"{test} · {p.Phase}";
        Percent = p.Percent;
        Detail = p.Detail;
        LiveValueText = p.Value is { } value ? $"{(value >= 100 ? value.ToString("N0", CultureInfo.CurrentCulture) : value.ToString("0.#", CultureInfo.CurrentCulture))} {p.Unit}" : null;
    }

    private List<BenchTestPlan> BuildPlans(List<BenchTestItemViewModel> selected, DiskTestFilePlacement? diskPlacement, out List<string> skipped)
    {
        skipped = new List<string>();
        var plans = new List<BenchTestPlan>();
        int processors = Environment.ProcessorCount;
        foreach (BenchTestItemViewModel item in selected)
        {
            switch (item.Kind)
            {
                case BenchTestKind.CpuMono:
                case BenchTestKind.CpuMulti:
                {
                    BenchJobRequest request = BenchPlanner.CpuRequest(item.Kind, _topology, SustainedEnabled, processors);
                    plans.Add(new BenchTestPlan(item.Kind, request, BenchPlanner.JournalValues(request)));
                    break;
                }
                case BenchTestKind.RamBandwidth:
                case BenchTestKind.RamLatency:
                {
                    if (_memorySizes is null) break;
                    BenchJobRequest request = BenchPlanner.RamRequest(item.Kind, _memorySizes, _topology, processors);
                    plans.Add(new BenchTestPlan(item.Kind, request, BenchPlanner.JournalValues(request)));
                    break;
                }
                case BenchTestKind.Disk:
                {
                    if (SelectedVolume?.Volume is not { } volume || diskPlacement is not { } placement) break;
                    long fileBytes = (long)DiskFileSizeMb * DiskTestFile.Mebibyte;
                    if (!placement.IsReady)
                    {
                        skipped.Add($"Disque non passé : {placement.Problem?.Reason}");
                        break;
                    }
                    BenchJobRequest request = BenchPlanner.DiskRequest(placement.Path!, volume, fileBytes);
                    plans.Add(new BenchTestPlan(item.Kind, request, BenchPlanner.JournalValues(request, volume)));
                    break;
                }
            }
        }
        return plans;
    }

    private string BuildConfirmation(List<BenchTestItemViewModel> selected, BenchPreconditionReport report)
    {
        var options = new BenchPlanOptions(SustainedEnabled, (long)DiskFileSizeMb * DiskTestFile.Mebibyte, SelectedVolume?.Volume.IsRotational ?? false);
        double seconds = selected.Sum(t => BenchPlanner.EstimateSeconds(t.Kind, options)) + Math.Max(0, selected.Count - 1) * IdleReturnEstimateSeconds;
        var lines = new List<string>
        {
            $"Le bench va charger ce PC pendant environ {BenchPlanner.DescribeDuration(seconds)} :",
        };
        lines.AddRange(selected.Select(t => $"• {t.Title}"));
        if (selected.Any(t => t.IsDisk) && SelectedVolume?.Volume is { } volume)
        {
            lines.Add($"• Test disque sur le volume {volume.DriveLetter} ({DiskTestFile.FileName}) : {DiskWriteText}.");
        }
        lines.Add("");
        lines.Add("Pendant la mesure, les réglages processeur, carte graphique et ventilation sont figés, le PC peut chauffer et devenir peu réactif. Le bench s'arrête de lui-même si le processeur reste à son seuil, si son ventilateur s'arrête ou si la batterie passe sous 30 %.");
        if (report.OnBattery) lines.Add("Sur batterie : le résultat ne sera pas représentatif.");
        if (!_backgroundLoad.IsQuiet && _backgroundLoad.AveragePercent is not null) lines.Add("Une activité tourne en arrière-plan : les passes risquent d'être instables.");
        lines.Add("");
        lines.Add("Lancer le bench ?");
        return string.Join("\n", lines);
    }

    private string? DescribeFans()
    {
        try
        {
            if (MachineInfo.Current.SoftwareFanControlRefused) return "ventilation : contrôleur du PC (portable ou châssis indéterminé, lecture seule)";
            int curves = _fans.Fans.Count(f => f.Mode == FanControlMode.Curve);
            int manual = _fans.Fans.Count(f => f.Mode == FanControlMode.Manual);
            int auto = _fans.Fans.Count - curves - manual;
            return $"ventilateurs : {curves} en courbe PCPerfSuite, {manual} en manuel, {auto} laissés au BIOS";
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static MessageBoxResult ShowMessage(string text, MessageBoxImage image, MessageBoxButton buttons, MessageBoxResult defaultResult)
    {
        Window? owner = Application.Current?.MainWindow;
        return owner is null
            ? MessageBox.Show(text, "PCPerfSuite", buttons, image, defaultResult)
            : MessageBox.Show(owner, text, "PCPerfSuite", buttons, image, defaultResult);
    }

    public void Dispose()
    {
        _disposed = true;
        _monitoring.SnapshotUpdated -= OnSnapshot;
        _runCancel?.Cancel();
    }

    private sealed class ActionDisposable(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
