using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCPerfSuite.App.Utils;
using PCPerfSuite.Core.Hardware.Cpu;
using PCPerfSuite.Core.Hardware.Cpu.CoreParking;
using PCPerfSuite.Core.SystemChanges;
using PCPerfSuite.Core.SystemInfo;

namespace PCPerfSuite.App.ViewModels;

/// <summary>
/// Sous-onglet Processeur › Cœurs : la charge et l'état parqué de chaque cœur logique, groupés comme le matériel
/// (cache L3, puis classe P/E, puis cœur et ses fils SMT), et les réglages du parking du plan actif, restaurables.
///
/// Lecture sans administrateur, hors du thread d'interface, une fois par seconde, et seulement tant que le sous-onglet
/// est affiché et la fenêtre visible (<see cref="IPageLifecycle"/>, posé par la page Processeur). L'écriture du parking
/// demande l'administrateur ; elle reste en place après la fermeture de l'app (D7).
/// </summary>
public sealed partial class CoreParkingViewModel : ObservableObject, IPageLifecycle, IDisposable
{
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(1);

    private readonly CoreParkingService _service;
    private readonly CpuPlatform _platform;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly ParkedShareWindow _parkedWindow = new(capacity: 5);
    private readonly Debouncer _writeDebounce = new();
    private readonly Dictionary<LogicalProcessorId, CoreThreadViewModel> _threads = new();

    private CpuTopology? _topology;
    private bool _isDualCcdX3D;
    private bool _loadRequested;
    private CancellationTokenSource? _sampling;

    public CoreParkingViewModel(CoreParkingService service, CpuPlatform platform)
    {
        _service = service;
        _platform = platform;
    }

    /// <summary>Posé par la page Processeur : page affichée, fenêtre visible, et ce sous-onglet choisi.</summary>
    [ObservableProperty] private bool isPageShown;

    [ObservableProperty] private bool isLoading = true;

    public ObservableCollection<CoreClusterViewModel> Clusters { get; } = new();

    [ObservableProperty] private string summary = "Analyse…";

    /// <summary>Pourquoi le visuel manque (CPU sets illisibles), null quand il s'affiche.</summary>
    [ObservableProperty] private string? topologyUnavailable;

    [ObservableProperty] private string parkedSummary = "--";

    /// <summary>D'où vient l'état parqué quand le compteur « Parking Status » manque, ou pourquoi il manque.</summary>
    [ObservableProperty] private string? activityNote;

    /// <summary>Règle 6 : ce qui n'a pas été vérifié sur une vraie machine dans cette topologie.</summary>
    [ObservableProperty] private string? experimentalNotice;

    /// <summary>Ryzen X3D à deux CCD : le pilote AMD parque volontairement un CCD en jeu.</summary>
    [ObservableProperty] private string? vCacheNotice;

    public ObservableCollection<CoreParkingSettingViewModel> Settings { get; } = new();
    public ObservableCollection<CoreParkingSettingViewModel> AdvancedSettings { get; } = new();

    [ObservableProperty] private bool showAdvanced;
    [ObservableProperty] private string planText = "Plan actif : --";
    [ObservableProperty] private string? status;
    [ObservableProperty] private string? settingsUnavailable;

    public bool HasAdvanced => AdvancedSettings.Count > 0;

    /// <summary>Le plan d'alimentation ne s'écrit qu'en administrateur ; la lecture et le visuel marchent sans.</summary>
    public bool CanEdit { get; } = ElevationHelper.IsAdministrator();

    public string? ElevationMessage => CanEdit
        ? null
        : "Lecture seule : relance PCPerfSuite en administrateur pour modifier le parking. Le visuel par cœur marche sans.";

    public bool ShowBatteryColumn => _service.HasBattery;

    partial void OnIsPageShownChanged(bool value)
    {
        if (!value)
        {
            StopSampling();
            return;
        }

        if (!_loadRequested)
        {
            _loadRequested = true;
            _ = LoadSafelyAsync();
            return;
        }

        // Le plan a pu changer depuis (Optimisation Windows, powercfg, outil du fabricant) : on relit à chaque retour.
        RefreshSettings();
        StartSampling();
    }

    /// <summary>Une erreur inattendue ne laisse pas l'onglet sur « Analyse… » sans un mot (règle 3).</summary>
    private async Task LoadSafelyAsync()
    {
        try
        {
            await LoadAsync();
        }
        catch (Exception ex)
        {
            CrashLog.Record(ex, "chargement de Processeur › Cœurs");
            Summary = "N/D";
            TopologyUnavailable = $"Lecture impossible ({ex.GetType().Name} : {ex.Message}).";
            IsLoading = false;
        }
    }

    /// <summary>Topologie, caches et réglages se lisent hors du thread d'interface, à la première ouverture.</summary>
    private async Task LoadAsync()
    {
        (CpuTopologyRead topology, bool vcacheDriver) = await Task.Run(() => (CpuTopology.Read(), AmdVCacheDriver.IsInstalled()));

        if (topology.Topology is { } read)
        {
            _topology = read;
            _isDualCcdX3D = CoreTopologySupport.IsDualCcdX3D(_platform.Vendor, read, vcacheDriver);
            BuildMap(read);
            Summary = CoreMapBuilder.Summary(read);

            CoreTopologyAssessment assessment = CoreTopologySupport.Assess(_platform.Vendor, _platform.Family, _platform.Model, read);
            ExperimentalNotice = assessment.IsVerified
                ? null
                : $"Expérimental : {string.Join(" ; ", assessment.ExperimentalReasons)}.";
            VCacheNotice = _isDualCcdX3D
                ? "Ryzen X3D à deux CCD : " + (vcacheDriver
                    ? "le pilote AMD 3D V-Cache est installé. En jeu, il parque volontairement le CCD sans V-Cache pour garder le jeu sur celui qui en a : des cœurs parqués sont alors normaux."
                    : "le pilote AMD 3D V-Cache n'est pas installé (pilote chipset AMD). C'est lui qui garde les jeux sur le CCD avec V-Cache.")
                : null;
        }
        else
        {
            Summary = "N/D";
            TopologyUnavailable = $"N/D : {topology.Problem?.Reason ?? "topologie illisible"}.";
        }

        LoadSettings();
        IsLoading = false;

        if (IsPageShown) StartSampling();
    }

    private void BuildMap(CpuTopology topology)
    {
        Clusters.Clear();
        _threads.Clear();
        foreach (CoreClusterViewModel cluster in CoreMapBuilder.Build(topology, _platform))
        {
            Clusters.Add(cluster);
            foreach (CoreThreadViewModel thread in cluster.Classes.SelectMany(c => c.Cores).SelectMany(c => c.Threads))
            {
                _threads[thread.Id] = thread;
            }
        }
    }

    /// <summary>Un réglage que ce Windows ne connaît pas est absent de la liste : l'afficher inerte n'aiderait personne.</summary>
    private void LoadSettings()
    {
        Settings.Clear();
        AdvancedSettings.Clear();

        IReadOnlyList<CoreParkingSetting> available = CoreParkingCatalog.For(_topology?.IsHybrid ?? _service.IsHybrid);
        foreach (CoreParkingSetting setting in available)
        {
            if (_service.Read(setting) is not { } value) continue;

            var row = new CoreParkingSettingViewModel(_service, setting, value, _writeDebounce, message => Status = message);
            (setting.IsAdvanced ? AdvancedSettings : Settings).Add(row);
        }

        OnPropertyChanged(nameof(HasAdvanced));
        SettingsUnavailable = Settings.Count == 0
            ? "N/D : ce Windows n'expose pas les réglages du parking des cœurs dans son plan d'alimentation."
            : null;
        UpdatePlanText();
    }

    private void RefreshSettings()
    {
        foreach (CoreParkingSettingViewModel row in Settings.Concat(AdvancedSettings))
        {
            if (_service.Read(row.Setting) is { } value) row.SetSilently(value);
            row.UpdateOrigin();
        }

        UpdatePlanText();
    }

    private void UpdatePlanText()
    {
        string? name = _service.ActiveScheme() is { } scheme ? _service.PlanName(scheme) : null;
        PlanText = name is null ? "Plan actif : nom illisible" : $"Plan actif : « {name} »";
    }

    // ============ Relevé ============

    private void StartSampling()
    {
        if (_sampling is not null || _topology is null) return;

        var cts = new CancellationTokenSource();
        _sampling = cts;
        _parkedWindow.Clear();
        _ = Task.Run(() => SampleLoopAsync(cts.Token));
    }

    private void StopSampling()
    {
        _sampling?.Cancel();
        _sampling?.Dispose();
        _sampling = null;
    }

    /// <summary>Une requête PDH pour toute la durée de l'affichage, créée et fermée sur ce même fil de fond. Le
    /// premier relevé arrive une seconde après l'ouverture : avant, les compteurs de taux n'ont rien à moyenner.</summary>
    private async Task SampleLoopAsync(CancellationToken token)
    {
        try
        {
            using var reader = new CoreActivityReader();
            string? note = reader.QueryUnavailable is { } problem
                ? $"Charge par cœur : N/D, {problem.Reason}."
                : !reader.HasUtility ? "Charge par cœur : N/D, le compteur « % Processor Utility » manque sur ce Windows."
                : !reader.HasParkingStatus ? "Le compteur « Parking Status » manque sur ce Windows : l'état parqué vient des CPU sets, relus chaque seconde."
                : null;
            Post(token, () => ActivityNote = note);

            using var timer = new PeriodicTimer(SampleInterval);
            while (await timer.WaitForNextTickAsync(token))
            {
                CoreActivitySample? sample = reader.Sample();
                IReadOnlyDictionary<LogicalProcessorId, bool>? cpuSetParked = reader.HasParkingStatus ? null : CpuTopology.ReadParkedFlags();
                Post(token, () => Apply(sample, cpuSetParked));
            }
        }
        catch (OperationCanceledException)
        {
            // Sous-onglet quitté ou fenêtre cachée.
        }
        catch (Exception ex)
        {
            CrashLog.Record(ex, "relevé par cœur");
            Post(CancellationToken.None, () => ActivityNote = $"Relevé par cœur interrompu ({ex.Message}).");
        }
    }

    private void Post(CancellationToken token, Action action)
        => _dispatcher.InvokeAsync(() =>
        {
            if (!token.IsCancellationRequested) action();
        });

    private void Apply(CoreActivitySample? sample, IReadOnlyDictionary<LogicalProcessorId, bool>? cpuSetParked)
    {
        if (sample is null && cpuSetParked is null) return;

        var parkedNow = new Dictionary<LogicalProcessorId, bool>();
        foreach (LogicalProcessorId id in _threads.Keys)
        {
            bool? parked = null;
            if (sample is not null && sample.Processors.TryGetValue(id, out CoreActivity? activity)) parked = activity.IsParked;
            if (parked is null && cpuSetParked is not null && cpuSetParked.TryGetValue(id, out bool fromCpuSets)) parked = fromCpuSets;
            if (parked is { } value) parkedNow[id] = value;
        }

        _parkedWindow.Add(parkedNow);

        foreach ((LogicalProcessorId id, CoreThreadViewModel thread) in _threads)
        {
            CoreActivity? activity = sample?.Processors.GetValueOrDefault(id);
            thread.Update(activity?.UtilityPercent, parkedNow.TryGetValue(id, out bool p) ? p : null,
                _parkedWindow.Share(id), activity?.PerformancePercent, _parkedWindow.Capacity);
        }

        ParkedSummary = parkedNow.Count == 0
            ? "État parqué : N/D"
            : $"Parqués en ce moment : {parkedNow.Count(p => p.Value)} fils sur {_threads.Count}";
    }

    // ============ Préréglages ============

    [RelayCommand]
    private void ApplyAllCoresActive()
    {
        if (!CanEdit) return;

        if (_isDualCcdX3D)
        {
            MessageBoxResult answer = ShowMessage(
                "Activer tous les cœurs sur ce Ryzen X3D à deux CCD ?\n\n"
                + "En jeu, le pilote AMD 3D V-Cache parque volontairement le CCD sans V-Cache, pour que le jeu reste sur celui "
                + "qui en a. Empêcher tout parking le laisse déborder sur l'autre CCD : on perd probablement des images "
                + "(constaté par la communauté, non documenté par AMD).\n\n"
                + "Le processeur chauffera et consommera aussi plus au repos. « Windows (origine) » rend les valeurs d'avant.",
                MessageBoxImage.Warning, MessageBoxButton.YesNo, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;
        }

        ApplyPreset(CoreParkingPreset.AllCoresActive, "Tous les cœurs actifs");
    }

    [RelayCommand]
    private void ApplyEconomy()
    {
        if (CanEdit) ApplyPreset(CoreParkingPreset.Economy, "Économie");
    }

    /// <summary>Le même chemin que « Tout rétablir » du registre des modifications.</summary>
    [RelayCommand]
    private void RestoreOrigin()
    {
        if (!CanEdit) return;

        CancelPendingWrites();
        SystemRestoreResult result = _service.RestoreAll();
        RefreshSettings();

        Status = result.Status switch
        {
            SystemRestoreStatus.NothingToRestore => result.Message ?? "Rien à rendre : PCPerfSuite n'a pas modifié le parking de ce PC.",
            SystemRestoreStatus.Restored => "Valeurs d'origine rendues au plan d'alimentation.",
            _ => $"{result.Message} Encore modifié : {string.Join(", ", result.NotRestored.Select(c => c.Title))}.",
        };
    }

    private void ApplyPreset(CoreParkingPreset preset, string name)
    {
        CancelPendingWrites();

        IReadOnlyList<CoreParkingTarget> targets = CoreParkingPresets.Targets(
            preset, Settings.Select(s => s.Setting).ToList(), _service.Read, _service.Origin, _service.HasBattery);
        if (targets.Count == 0)
        {
            Status = $"« {name} » est déjà en place.";
            return;
        }

        CoreParkingWriteResult result = _service.Write(targets);
        RefreshSettings();

        List<CoreParkingApplied> refused = result.Applied.Where(a => !a.Matches).ToList();
        string message = result.Error ?? (refused.Count > 0
            ? $"« {name} » : Windows n'a pas retenu {string.Join(", ", refused.Select(r => r.Setting.LabelFor(_service.IsHybrid)))} (réglage piloté par le fabricant du PC ?)."
            : $"« {name} » appliqué au plan actif.");
        Status = result.Note is { } note ? $"{note} {message}" : message;
    }

    /// <summary>Un curseur encore en attente réécrirait sa valeur juste après le préréglage.</summary>
    private void CancelPendingWrites()
    {
        foreach (CoreParkingSettingViewModel row in Settings.Concat(AdvancedSettings)) _writeDebounce.Cancel(row.Setting.Id);
    }

    private static MessageBoxResult ShowMessage(string text, MessageBoxImage image, MessageBoxButton buttons, MessageBoxResult defaultResult)
    {
        Window? owner = Application.Current?.MainWindow;
        return owner is null
            ? MessageBox.Show(text, "Parking des cœurs", buttons, image, defaultResult)
            : MessageBox.Show(owner, text, "Parking des cœurs", buttons, image, defaultResult);
    }

    /// <summary>Un réglage encore en attente est écrit avant de partir ; le parking, lui, reste en place (D7).</summary>
    public void Dispose()
    {
        StopSampling();
        _writeDebounce.Flush();
    }
}
