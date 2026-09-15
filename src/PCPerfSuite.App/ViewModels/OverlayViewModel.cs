using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PCPerfSuite.App.Metrics;
using PCPerfSuite.App.Overlay;
using PCPerfSuite.App.Views;
using PCPerfSuite.Core.Overlay;
using PCPerfSuite.Core.PowerSettings;

namespace PCPerfSuite.App.ViewModels;

/// <summary>
/// Overlay en jeu (onglet "Overlay") : met en forme les métriques choisies (même catalogue que "Mes
/// métriques") à chaque relevé du MonitoringViewModel partagé, puis les affiche par deux canaux au
/// choix, cumulables :
///
/// - RTSS : le texte est poussé dans la mémoire partagée de RivaTuner Statistics Server, qui le dessine
///   lui-même dans le jeu (y compris en plein écran exclusif). Couleurs et taille passent par ses
///   balises de mise en forme ; la police, elle, est celle configurée dans RTSS.
/// - Fenêtre PCPerfSuite : une fenêtre transparente toujours au-dessus, dessinée par l'app, donc
///   police/taille/couleurs/position entièrement libres. Fonctionne en jeu fenêtré ou sans bordure.
///
/// La cadence d'affichage est indépendante de celle du monitoring (elle ne peut pas être plus rapide,
/// puisque les valeurs viennent de là, mais elle peut être plus lente pour un texte plus lisible).
/// </summary>
public sealed partial class OverlayViewModel : ObservableObject, IDisposable
{
    private readonly RtssOsdClient _rtss = new("PCPerfSuite");
    private readonly MonitoringViewModel _monitoring;
    private MetricSample? _lastSample;
    private long _lastRenderTick;
    private OverlayWindow? _window;

    /// <summary>Tolérance sur la cadence : sans elle, une cadence d'overlay égale à celle du monitoring
    /// raterait un relevé sur deux à cause de la gigue du timer.</summary>
    private const double RateTolerance = 0.9;

    [ObservableProperty] private bool isEnabled;
    [ObservableProperty] private bool useRtss;
    [ObservableProperty] private bool useWindow;
    [ObservableProperty] private bool oneLinePerMetric;
    [ObservableProperty] private bool isRtssDetected;

    private int _refreshMs = 1000;

    /// <summary>Cadence de l'overlay, saisie librement en millisecondes. Elle ne peut pas descendre
    /// sous celle du Monitoring (les valeurs en viennent), mais elle peut être plus lente.</summary>
    public int RefreshMs
    {
        get => _refreshMs;
        set
        {
            int clamped = RefreshRates.Clamp(value);
            bool changed = SetProperty(ref _refreshMs, clamped);

            if (clamped != value) OnPropertyChanged(nameof(RefreshMs));
            if (!changed) return;

            // Repart de zéro pour que la nouvelle cadence s'applique dès le prochain relevé.
            _lastRenderTick = 0;
            Persist();
        }
    }

    public string RefreshHint => RefreshRates.Hint;

    public MetricSelectionViewModel Metrics { get; }
    public OverlayAppearanceViewModel Appearance { get; }

    /// <summary>Lignes prêtes à afficher, partagées par l'aperçu de l'onglet et la fenêtre d'overlay.</summary>
    public ObservableCollection<OverlayLine> Lines { get; } = new();

    /// <summary>Signalé quand la géométrie de l'overlay change (ancrage, marges, police) : la fenêtre
    /// se replace.</summary>
    public event Action? LayoutChanged;

    public OverlayViewModel(MonitoringViewModel monitoring)
    {
        _monitoring = monitoring;
        OverlaySettings settings = AppSettingsStore.Load().Overlay;

        isEnabled = settings.Enabled;
        useRtss = settings.UseRtss;
        useWindow = settings.UseWindow;
        oneLinePerMetric = settings.OneLinePerMetric;
        _refreshMs = RefreshRates.Clamp(settings.RefreshMs);

        Metrics = new MetricSelectionViewModel(settings.MetricIds ?? LegacyMetricIds(settings));
        Metrics.SelectionChanged += OnDisplayOptionsChanged;

        Appearance = new OverlayAppearanceViewModel(
            settings.Appearance ?? new OverlayAppearanceSettings(),
            OnDisplayOptionsChanged,
            () => LayoutChanged?.Invoke());

        _monitoring.MetricsUpdated += OnMetricsUpdated;
    }

    /// <summary>Réglages d'avant la sélection libre : reprend ce qu'affichaient les anciens interrupteurs
    /// CPU/GPU/RAM (tous cochés par défaut).</summary>
    private static List<string> LegacyMetricIds(OverlaySettings settings)
    {
        var ids = new List<string>();
        if (settings.ShowCpu ?? true) ids.AddRange(new[] { "cpu.load", "cpu.temp.package" });
        if (settings.ShowGpu ?? true) ids.AddRange(new[] { "gpu.load", "gpu.temp.core" });
        if (settings.ShowRam ?? true) ids.AddRange(new[] { "ram.load", "ram.used" });
        return ids;
    }

    partial void OnIsEnabledChanged(bool value)
    {
        Persist();
        if (value)
        {
            Render();
        }
        else
        {
            StopOutputs();
        }
    }

    partial void OnUseRtssChanged(bool value)
    {
        if (!value)
        {
            _rtss.Release();
            IsRtssDetected = false;
        }
        OnDisplayOptionsChanged();
    }

    partial void OnUseWindowChanged(bool value) => OnDisplayOptionsChanged();

    partial void OnOneLinePerMetricChanged(bool value) => OnDisplayOptionsChanged();

    private void OnDisplayOptionsChanged()
    {
        Persist();
        Render();
    }

    private void OnMetricsUpdated(MetricSample sample)
    {
        _lastSample = sample;

        long now = Environment.TickCount64;
        if (now - _lastRenderTick < RefreshMs * RateTolerance) return;
        _lastRenderTick = now;

        Render();
    }

    /// <summary>Recompose les lignes (toujours, pour que l'aperçu reste vivant même overlay éteint) puis
    /// les pousse vers RTSS et/ou la fenêtre si l'overlay est activé.</summary>
    private void Render()
    {
        if (_lastSample is null) return;

        List<OverlayLine> lines = OverlayComposer.Compose(
            _lastSample, Metrics.Selected, OneLinePerMetric, Appearance.BuildColorScheme());

        Lines.Clear();
        foreach (OverlayLine line in lines) Lines.Add(line);

        if (!IsEnabled)
        {
            StopOutputs();
            return;
        }

        if (UseRtss)
        {
            string text = OverlayComposer.ToRtssText(lines, Appearance.SendColorsToRtss, Appearance.RtssSizePercent);
            IsRtssDetected = _rtss.TryUpdate(text);
        }

        SyncWindow();
    }

    private void StopOutputs()
    {
        _rtss.Release();
        IsRtssDetected = false;
        CloseWindow();
    }

    private void SyncWindow()
    {
        if (!IsEnabled || !UseWindow)
        {
            CloseWindow();
            return;
        }

        if (_window is not null) return;

        _window = new OverlayWindow { DataContext = this };
        _window.Show();
    }

    private void CloseWindow()
    {
        if (_window is null) return;

        OverlayWindow window = _window;
        _window = null;
        window.Close();
    }

    private void Persist()
    {
        // Relit le fichier plutôt que de garder une copie : le Monitoring enregistre aussi ses réglages.
        AppSettings settings = AppSettingsStore.Load();
        settings.Overlay.Enabled = IsEnabled;
        settings.Overlay.UseRtss = UseRtss;
        settings.Overlay.UseWindow = UseWindow;
        settings.Overlay.OneLinePerMetric = OneLinePerMetric;
        settings.Overlay.RefreshMs = RefreshMs;
        settings.Overlay.MetricIds = Metrics.SelectedIds;

        OverlayAppearanceSettings appearance = settings.Overlay.Appearance ?? new OverlayAppearanceSettings();
        Appearance.WriteTo(appearance);
        settings.Overlay.Appearance = appearance;

        settings.Overlay.ShowCpu = null;
        settings.Overlay.ShowGpu = null;
        settings.Overlay.ShowRam = null;
        AppSettingsStore.Save(settings);
    }

    /// <summary>Libère le créneau OSD et ferme la fenêtre à la fermeture de l'app, pour ne pas laisser un
    /// texte périmé affiché dans les jeux une fois PCPerfSuite fermé.</summary>
    public void Dispose()
    {
        _monitoring.MetricsUpdated -= OnMetricsUpdated;
        CloseWindow();
        _rtss.Dispose();
    }
}
