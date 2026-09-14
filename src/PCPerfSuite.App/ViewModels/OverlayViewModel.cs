using CommunityToolkit.Mvvm.ComponentModel;
using PCPerfSuite.App.Metrics;
using PCPerfSuite.Core.Overlay;
using PCPerfSuite.Core.PowerSettings;

namespace PCPerfSuite.App.ViewModels;

/// <summary>
/// Overlay en jeu (onglet "Overlay") : pousse les métriques choisies (même catalogue que "Mes métriques")
/// dans l'overlay de RTSS à chaque relevé du MonitoringViewModel partagé — même schéma que
/// FanCurvesViewModel. Fonctionne en plein écran exclusif car c'est RTSS (déjà accroché au jeu) qui
/// dessine, pas nous — voir RtssOsdClient pour le détail du mécanisme.
/// </summary>
public sealed partial class OverlayViewModel : ObservableObject, IDisposable
{
    private readonly RtssOsdClient _rtss = new("PCPerfSuite");
    private readonly MonitoringViewModel _monitoring;
    private MetricSample? _lastSample;

    [ObservableProperty] private bool isEnabled;
    [ObservableProperty] private bool oneLinePerMetric;
    [ObservableProperty] private bool isRtssDetected;
    [ObservableProperty] private string previewText = "";

    public MetricSelectionViewModel Metrics { get; }

    public OverlayViewModel(MonitoringViewModel monitoring)
    {
        _monitoring = monitoring;
        OverlaySettings settings = AppSettingsStore.Load().Overlay;

        isEnabled = settings.Enabled;
        oneLinePerMetric = settings.OneLinePerMetric;
        Metrics = new MetricSelectionViewModel(settings.MetricIds ?? LegacyMetricIds(settings));
        Metrics.SelectionChanged += OnDisplayOptionsChanged;

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
            _rtss.Release();
            IsRtssDetected = false;
            PreviewText = "";
        }
    }

    partial void OnOneLinePerMetricChanged(bool value) => OnDisplayOptionsChanged();

    private void OnDisplayOptionsChanged()
    {
        Persist();
        Render();
    }

    private void OnMetricsUpdated(MetricSample sample)
    {
        _lastSample = sample;
        Render();
    }

    private void Render()
    {
        if (!IsEnabled || _lastSample is null) return;

        string text = BuildOsdText(_lastSample);
        PreviewText = text;
        IsRtssDetected = _rtss.TryUpdate(text);
    }

    private string BuildOsdText(MetricSample sample)
    {
        IReadOnlyList<MetricDefinition> selected = Metrics.Selected;

        IEnumerable<string> lines = OneLinePerMetric
            ? selected.Select(m => $"{m.Category.OsdLabel} {m.OsdLabel}  {m.Read(sample).Text}")
            // Façon Afterburner : une ligne par catégorie, ex. "GPU  45%  62°C  180 W".
            : selected.GroupBy(m => m.Category)
                .Select(g => $"{g.Key.OsdLabel}  {string.Join("  ", g.Select(m => m.Read(sample).Text))}");

        return string.Join("\n", lines);
    }

    private void Persist()
    {
        // Relit le fichier plutôt que de garder une copie : le Monitoring enregistre aussi ses réglages.
        AppSettings settings = AppSettingsStore.Load();
        settings.Overlay.Enabled = IsEnabled;
        settings.Overlay.OneLinePerMetric = OneLinePerMetric;
        settings.Overlay.MetricIds = Metrics.SelectedIds;
        settings.Overlay.ShowCpu = null;
        settings.Overlay.ShowGpu = null;
        settings.Overlay.ShowRam = null;
        AppSettingsStore.Save(settings);
    }

    /// <summary>Libère le créneau OSD à la fermeture de l'app, pour ne pas laisser un texte périmé
    /// affiché dans les jeux une fois PCPerfSuite fermé.</summary>
    public void Dispose()
    {
        _monitoring.MetricsUpdated -= OnMetricsUpdated;
        _rtss.Dispose();
    }
}
