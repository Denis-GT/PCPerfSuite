using CommunityToolkit.Mvvm.ComponentModel;
using PCPerfSuite.Core.Hardware;
using PCPerfSuite.Core.Overlay;
using PCPerfSuite.Core.PowerSettings;

namespace PCPerfSuite.App.ViewModels;

/// <summary>
/// Overlay en jeu (onglet "Overlay") : pousse CPU/GPU/RAM dans l'overlay de RTSS à chaque relevé du
/// MonitoringViewModel partagé — même schéma que FanCurvesViewModel/GpuControlViewModel. Fonctionne
/// en plein écran exclusif car c'est RTSS (déjà accroché au jeu) qui dessine, pas nous — voir
/// RtssOsdClient pour le détail du mécanisme.
/// </summary>
public sealed partial class OverlayViewModel : ObservableObject, IDisposable
{
    private readonly RtssOsdClient _rtss = new("PCPerfSuite");
    private readonly MonitoringViewModel _monitoring;
    private readonly AppSettings _settings;

    [ObservableProperty] private bool isEnabled;
    [ObservableProperty] private bool showCpu;
    [ObservableProperty] private bool showGpu;
    [ObservableProperty] private bool showRam;
    [ObservableProperty] private bool isRtssDetected;
    [ObservableProperty] private string previewText = "";

    public OverlayViewModel(MonitoringViewModel monitoring)
    {
        _monitoring = monitoring;
        _settings = AppSettingsStore.Load();

        isEnabled = _settings.Overlay.Enabled;
        showCpu = _settings.Overlay.ShowCpu;
        showGpu = _settings.Overlay.ShowGpu;
        showRam = _settings.Overlay.ShowRam;

        _monitoring.SnapshotUpdated += OnSnapshotUpdated;
    }

    partial void OnIsEnabledChanged(bool value)
    {
        Persist();
        if (!value)
        {
            _rtss.Release();
            IsRtssDetected = false;
            PreviewText = "";
        }
    }

    partial void OnShowCpuChanged(bool value) => Persist();
    partial void OnShowGpuChanged(bool value) => Persist();
    partial void OnShowRamChanged(bool value) => Persist();

    private void OnSnapshotUpdated(HardwareSnapshot s)
    {
        if (!IsEnabled) return;

        var lines = new List<string>();
        if (ShowCpu) lines.Add($"CPU {FormatPercent(s.Cpu.LoadPercent)}  {FormatTemp(s.Cpu.PackageTempC)}");
        if (ShowGpu && s.Gpu is { } gpu) lines.Add($"GPU {FormatPercent(gpu.LoadPercent)}  {FormatTemp(gpu.CoreTempC)}");
        if (ShowRam) lines.Add($"RAM {FormatPercent(s.Memory.LoadPercent)}  {(s.Memory.UsedGb is { } used ? $"{used:0.#} Go" : "--")}");

        string text = string.Join("\n", lines);
        PreviewText = text;
        IsRtssDetected = _rtss.TryUpdate(text);
    }

    private static string FormatPercent(float? value) => value is { } v ? $"{v:0}%" : "--";
    private static string FormatTemp(float? value) => value is { } v ? $"{v:0}°C" : "--";

    private void Persist()
    {
        _settings.Overlay.Enabled = IsEnabled;
        _settings.Overlay.ShowCpu = ShowCpu;
        _settings.Overlay.ShowGpu = ShowGpu;
        _settings.Overlay.ShowRam = ShowRam;
        AppSettingsStore.Save(_settings);
    }

    /// <summary>Libère le créneau OSD à la fermeture de l'app, pour ne pas laisser un texte périmé
    /// affiché dans les jeux une fois PCPerfSuite fermé.</summary>
    public void Dispose()
    {
        _monitoring.SnapshotUpdated -= OnSnapshotUpdated;
        _rtss.Dispose();
    }
}
