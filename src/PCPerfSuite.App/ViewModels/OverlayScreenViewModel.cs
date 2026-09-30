using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PCPerfSuite.App.Views;
using PCPerfSuite.Core.Hardware.Displays;
using PCPerfSuite.Core.Overlay;
using PCPerfSuite.Core.PowerSettings;

namespace PCPerfSuite.App.ViewModels;

/// <summary>Un mode de la liste « Écran » de l'overlay.</summary>
public sealed record OverlayScreenModeOption(OverlayScreenMode Mode, string Title, string Description);

/// <summary>Un écran de la liste, avec le numéro qu'affiche « Identifier ».</summary>
public sealed record OverlayDisplayOption(int Number, string Label, DisplayMonitor Monitor)
{
    public string Title => $"{Number} — {Label}";
}

/// <summary>
/// Écran de l'overlay fenêtre : écran principal (défaut), écran choisi, ou écran du jeu (fenêtre au premier plan).
/// L'ancrage et les marges, eux, restent ceux d'OverlayAppearanceViewModel, communs à tous les écrans.
///
/// La liste des écrans est relue à l'ouverture de l'onglet, à chaque changement de configuration (branchement,
/// résolution, écran principal) et avant « Identifier ». Un écran choisi qui manque fait retomber l'overlay sur l'écran
/// principal, avec un message, jusqu'à ce qu'il revienne.
/// </summary>
public sealed partial class OverlayScreenViewModel : ObservableObject, IPageLifecycle, IDisposable
{
    private static readonly IReadOnlyDictionary<string, string> NoSerials = new Dictionary<string, string>();

    private readonly Action _onChanged;
    private readonly Action _onTargetMoved;
    private readonly Dispatcher _dispatcher;

    private DisplayTopologySnapshot? _snapshot;
    private IReadOnlyDictionary<string, string> _serialHashes = NoSerials;
    private DisplayIdentity? _saved;
    private IntPtr _gameMonitor;
    private DisplayMonitor? _lastTarget;
    private CancellationTokenSource? _serialRead;
    private bool _syncing;
    private bool _pageShown;

    public IReadOnlyList<OverlayScreenModeOption> Modes { get; } =
    [
        new(OverlayScreenMode.Primary, "Écran principal", "L'écran principal de Windows."),
        new(OverlayScreenMode.Fixed, "Cet écran", "L'écran choisi dans la liste, retrouvé même s'il change de connecteur."),
        new(OverlayScreenMode.Game, "Écran du jeu", "L'écran de la fenêtre au premier plan. La barre des tâches, le bureau et PCPerfSuite ne comptent pas."),
    ];

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsFixedMode))] private OverlayScreenModeOption selectedMode;

    public ObservableCollection<OverlayDisplayOption> Displays { get; } = new();

    [ObservableProperty] private OverlayDisplayOption? selectedDisplay;

    /// <summary>Ce qu'il faut dire de l'écran retenu (écran choisi débranché, liste illisible) ; null si rien.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasStatusMessage))] private string? statusMessage;

    public bool IsFixedMode => SelectedMode.Mode == OverlayScreenMode.Fixed;

    public bool HasStatusMessage => StatusMessage is not null;

    /// <param name="onChanged">Choix de l'utilisateur : enregistrer, puis replacer la fenêtre.</param>
    /// <param name="onTargetMoved">L'écran retenu a changé sans action de l'utilisateur (branchement, jeu sur un autre
    /// écran) : replacer seulement.</param>
    public OverlayScreenViewModel(OverlayAppearanceSettings settings, Action onChanged, Action onTargetMoved)
    {
        _onChanged = onChanged;
        _onTargetMoved = onTargetMoved;
        _dispatcher = Dispatcher.CurrentDispatcher;

        OverlayScreenMode mode = OverlayScreenChoice.ParseMode(settings.ScreenMode);
        selectedMode = Modes.First(m => m.Mode == mode);
        _saved = settings.Screen;

        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    public bool IsPageShown
    {
        set
        {
            if (value == _pageShown) return;
            _pageShown = value;
            if (!value) return;

            RefreshTopology();
            ReadSerialsInBackground();
        }
    }

    /// <summary>Écran où placer l'overlay maintenant ; null si aucun écran n'a pu être lu (la fenêtre garde alors
    /// l'ancien placement sur l'écran principal).</summary>
    public DisplayMonitor? ResolveTarget()
    {
        if (_snapshot is null) RefreshTopology();
        return UpdateStatus();
    }

    /// <summary>Relit les écrans (rapide) : après un changement de configuration ou d'échelle. Les HMONITOR ne
    /// survivent pas à un branchement, l'instantané précédent ne vaut plus.</summary>
    public void RefreshTopology()
    {
        _snapshot = DisplayTopology.Read();
        RebuildDisplays();
        UpdateStatus();
        if (HasIdenticalScreens(_snapshot)) ReadSerialsInBackground();
    }

    /// <summary>Mode « écran du jeu » : note la fenêtre passée au premier plan. Vrai si l'overlay doit changer d'écran.
    /// Les fenêtres du shell et de PCPerfSuite sont ignorées : un clic sur la barre des tâches ne fait rien bouger.</summary>
    public bool NoteForeground(IntPtr hwnd)
    {
        if (SelectedMode.Mode != OverlayScreenMode.Game) return false;
        if (DisplayTopology.IsIgnoredForegroundWindow(hwnd)) return false;

        IntPtr monitor = DisplayTopology.MonitorFromWindow(hwnd);
        if (monitor == IntPtr.Zero || monitor == _gameMonitor) return false;

        _gameMonitor = monitor;
        if (_snapshot is null || DisplayTopology.FindMonitor(_snapshot, monitor) is null) RefreshTopology();
        return true;
    }

    public void WriteTo(OverlayAppearanceSettings settings)
    {
        settings.ScreenMode = OverlayScreenChoice.ToSetting(SelectedMode.Mode);
        settings.Screen = _saved;
    }

    /// <summary>Affiche un grand numéro sur chaque écran, celui de la liste.</summary>
    [RelayCommand]
    private void Identify()
    {
        RefreshTopology();
        foreach (OverlayDisplayOption option in Displays) DisplayIdentifyWindow.ShowOn(option.Monitor, option.Number);
    }

    partial void OnSelectedModeChanged(OverlayScreenModeOption value)
    {
        if (_syncing) return;

        if (value.Mode == OverlayScreenMode.Fixed && _saved is null)
        {
            // Premier passage en « Cet écran » : l'écran où est l'overlay aujourd'hui, pour que rien ne bouge.
            OverlayDisplayOption? current = Displays.FirstOrDefault(d => d.Monitor == _lastTarget) ?? Displays.FirstOrDefault();
            if (current is not null) _saved = DisplayIdentity.FromMonitor(current.Monitor, _serialHashes, current.Number);
            SyncSelection();
        }

        UpdateStatus();
        _onChanged();
    }

    partial void OnSelectedDisplayChanged(OverlayDisplayOption? value)
    {
        if (_syncing || value is null) return;

        _saved = DisplayIdentity.FromMonitor(value.Monitor, _serialHashes, value.Number);
        UpdateStatus();
        _onChanged();
    }

    private DisplayMonitor? UpdateStatus()
    {
        OverlayScreenTarget target = OverlayScreenChoice.Resolve(SelectedMode.Mode, _saved, _snapshot ?? DisplayTopologySnapshot.Empty,
            _serialHashes, _snapshot is null ? null : DisplayTopology.FindMonitor(_snapshot, _gameMonitor));

        StatusMessage = target.Message;
        _lastTarget = target.Monitor;
        return target.Monitor;
    }

    private void RebuildDisplays()
    {
        _syncing = true;
        try
        {
            Displays.Clear();
            IReadOnlyList<DisplayMonitor> monitors = _snapshot?.Monitors ?? Array.Empty<DisplayMonitor>();
            for (int i = 0; i < monitors.Count; i++)
            {
                Displays.Add(new OverlayDisplayOption(i + 1, DisplayNames.Label(monitors[i], i + 1), monitors[i]));
            }
        }
        finally
        {
            _syncing = false;
        }

        SyncSelection();
    }

    /// <summary>Sélectionne dans la liste l'écran enregistré, sans que ce soit pris pour un choix de l'utilisateur.
    /// Rien de sélectionné quand il est débranché : le message dit pourquoi.</summary>
    private void SyncSelection()
    {
        DisplayMonitor? saved = _saved is null || _snapshot is null
            ? null
            : DisplayIdentityResolver.Resolve(_saved, _snapshot, _serialHashes).Monitor;

        _syncing = true;
        try
        {
            SelectedDisplay = Displays.FirstOrDefault(d => d.Monitor == saved);
        }
        finally
        {
            _syncing = false;
        }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
        => _dispatcher.BeginInvoke(() =>
        {
            RefreshTopology();
            _onTargetMoved();
        });

    /// <summary>Deux écrans du même modèle : seul leur numéro de série les départage.</summary>
    private static bool HasIdenticalScreens(DisplayTopologySnapshot snapshot)
        => snapshot.Monitors
            .SelectMany(m => m.Targets)
            .Where(t => t.HasEdid)
            .GroupBy(t => (t.EdidManufacturerId, t.EdidProductCodeId))
            .Any(g => g.Count() > 1);

    /// <summary>Numéros de série par WMI, hors du thread d'interface. Au retour, l'écran enregistré garde l'empreinte
    /// du sien (pour le retrouver entre deux écrans identiques), et l'overlay change d'écran si la résolution change.</summary>
    private async void ReadSerialsInBackground()
    {
        _serialRead?.Cancel();
        var cancellation = new CancellationTokenSource();
        _serialRead = cancellation;

        List<string> paths = (_snapshot?.Monitors ?? Array.Empty<DisplayMonitor>())
            .SelectMany(m => m.Targets)
            .Select(t => t.DevicePath)
            .OfType<string>()
            .ToList();
        if (paths.Count == 0) return;

        try
        {
            IReadOnlyDictionary<string, string> hashes = await Task.Run(() => MonitorSerials.ReadHashes(paths, cancellation.Token));
            if (cancellation.IsCancellationRequested) return;

            _serialHashes = hashes;
            DisplayMonitor? before = _lastTarget;
            SyncSelection();
            DisplayMonitor? after = UpdateStatus();

            if (RememberSerial()) _onChanged();
            else if (after != before) _onTargetMoved();
        }
        catch
        {
            // WMI muet : deux écrans identiques ne seront pas départagés, le message le dira.
        }
    }

    /// <summary>Ajoute à l'écran enregistré l'empreinte de son numéro de série, s'il est branché et qu'elle manque.</summary>
    private bool RememberSerial()
    {
        if (_saved is null || _snapshot is null || !string.IsNullOrEmpty(_saved.SerialHash)) return false;
        if (DisplayIdentityResolver.Resolve(_saved, _snapshot, _serialHashes).Monitor is not { } monitor) return false;
        if (monitor.Targets.FirstOrDefault()?.DevicePath is not { } path || !_serialHashes.TryGetValue(path, out string? hash)) return false;

        _saved.SerialHash = hash;
        return true;
    }

    public void Dispose()
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _serialRead?.Cancel();
    }
}
