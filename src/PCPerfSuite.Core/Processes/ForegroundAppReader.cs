using PCPerfSuite.Core.Hardware.Displays;

namespace PCPerfSuite.Core.Processes;

/// <summary>
/// L'application de l'utilisateur au premier plan, telle que la voit <see cref="ForegroundAppReader"/>.
/// </summary>
/// <param name="ProcessId">Processus de la fenêtre (de la vraie application pour une application du Store).</param>
/// <param name="Path">Chemin complet normalisé de l'exécutable ; null s'il n'a pas pu être lu (processus protégé,
/// application du Store suspendue) : seules les charges servent alors, et aucune règle ne peut correspondre.</param>
/// <param name="IsFullscreen">La fenêtre couvre tout son écran (plein écran exclusif ou fenêtré sans bord).</param>
/// <param name="IsExclusiveFullscreen">Windows signale une application Direct3D en plein écran exclusif.</param>
/// <param name="ForegroundIgnored">La fenêtre au premier plan est celle de PCPerfSuite ou du shell (barre des tâches,
/// bureau, Alt+Tab) : l'application donnée est la dernière vraie, toujours ouverte.</param>
public sealed record ForegroundApp(
    uint ProcessId,
    string? Path,
    bool IsFullscreen,
    bool IsExclusiveFullscreen,
    bool ForegroundIgnored)
{
    public bool PathUnavailable => Path is null;

    /// <summary>Nom affiché (« game »), ou « application inaccessible ».</summary>
    public string DisplayName => Path is { } path ? ApplicationPaths.DisplayName(path) : "application inaccessible";
}

/// <summary>Une fenêtre enfant : sa classe et son processus.</summary>
internal readonly record struct ChildWindowInfo(IntPtr Handle, string? ClassName, uint ProcessId);

/// <summary>Le cadre d'une fenêtre (sans ses bordures invisibles) et les bornes de son écran, en pixels physiques.</summary>
internal readonly record struct WindowGeometry(PixelRect Window, PixelRect Monitor, bool IsMaximized, bool IsMinimized);

/// <summary>État de l'utilisateur selon le shell (SHQueryUserNotificationState).</summary>
internal enum UserNotificationState
{
    Unknown,
    Normal,

    /// <summary>Une application en plein écran (QUNS_BUSY) : jeu sans bord, vidéo, présentation…</summary>
    Busy,

    /// <summary>Une application Direct3D en plein écran exclusif (QUNS_RUNNING_D3D_FULL_SCREEN).</summary>
    RunningD3DFullScreen,

    /// <summary>Mode présentation, écran verrouillé, application Store en plein écran…</summary>
    Other,
}

/// <summary>Les appels Windows du lecteur, isolés pour tester sa logique sans fenêtre.</summary>
internal interface IForegroundNative
{
    IntPtr ForegroundWindow();

    /// <summary>Fenêtre de PCPerfSuite ou du shell (<see cref="DisplayTopology.IsIgnoredForegroundWindow(IntPtr)"/>).</summary>
    bool IsIgnored(IntPtr hwnd);

    bool Exists(IntPtr hwnd);

    uint ProcessIdOf(IntPtr hwnd);

    string? ClassNameOf(IntPtr hwnd);

    /// <summary>Chemin de l'exécutable, null s'il est illisible. Le handle est refermé aussitôt.</summary>
    string? ImagePathOf(uint processId);

    IReadOnlyList<ChildWindowInfo> ChildWindows(IntPtr hwnd);

    WindowGeometry? GeometryOf(IntPtr hwnd);

    UserNotificationState NotificationState();
}

/// <summary>
/// Lecteur léger de l'application au premier plan, sans ProcessService (trop coûteux en permanence) : brique partagée
/// par la bascule automatique (#9), les limites par processus (#21) et la préférence de GPU (#22).
///
/// À chaque appel de <see cref="Poll"/> (un relevé), une seule lecture : la fenêtre au premier plan. Le processus n'est
/// relu que quand elle change, par un handle PROCESS_QUERY_LIMITED_INFORMATION refermé aussitôt, et le plein écran est
/// réévalué au changement puis toutes les <see cref="FullscreenRecheck"/>. Les fenêtres de PCPerfSuite et du shell sont
/// écartées comme pour l'overlay (#3) : passer sur la barre des tâches ou dans PCPerfSuite ne change pas l'application
/// en cours. Une application du Store (ApplicationFrameHost) est rapportée à son vrai processus. À appeler depuis un seul
/// fil (celui de l'interface) ; ne lève jamais.
/// </summary>
public sealed class ForegroundAppReader
{
    public static readonly TimeSpan FullscreenRecheck = TimeSpan.FromSeconds(5);

    /// <summary>Écart toléré entre le cadre d'une fenêtre plein écran et les bornes de son écran, en pixels.</summary>
    public const int FullscreenTolerancePx = 2;

    private const string FrameHostClass = "ApplicationFrameWindow";
    private const string CoreWindowClass = "Windows.UI.Core.CoreWindow";

    private readonly IForegroundNative _native;

    private IntPtr _seenHwnd;
    private bool _seenIgnored = true;

    private IntPtr _appHwnd;
    private uint _appPid;
    private string? _appPath;
    private bool _hasApp;

    private DateTimeOffset? _fullscreenCheckedUtc;
    private bool _fullscreen;
    private bool _exclusive;

    public ForegroundAppReader() : this(new Win32ForegroundNative())
    {
    }

    internal ForegroundAppReader(IForegroundNative native) => _native = native;

    /// <summary>L'application de l'utilisateur au premier plan, null s'il n'y en a pas (bureau seul, écran verrouillé,
    /// dernière application fermée).</summary>
    public ForegroundApp? Poll(DateTimeOffset now)
    {
        try
        {
            IntPtr hwnd = _native.ForegroundWindow();
            if (hwnd != _seenHwnd)
            {
                _seenHwnd = hwnd;
                _seenIgnored = hwnd == IntPtr.Zero || _native.IsIgnored(hwnd);
                if (!_seenIgnored && hwnd != _appHwnd) Adopt(hwnd);
            }

            if (!_hasApp) return null;

            // La dernière vraie application a été fermée pendant qu'on était sur le bureau ou dans PCPerfSuite.
            if (!_native.Exists(_appHwnd))
            {
                Forget();
                return null;
            }

            if (_fullscreenCheckedUtc is not { } checkedUtc || now - checkedUtc >= FullscreenRecheck || now < checkedUtc)
            {
                _fullscreenCheckedUtc = now;
                _fullscreen = _native.GeometryOf(_appHwnd) is { } geometry && IsFullscreen(geometry);
                _exclusive = !_seenIgnored && _native.NotificationState() == UserNotificationState.RunningD3DFullScreen;
            }

            return new ForegroundApp(_appPid, _appPath, _fullscreen || _exclusive, _exclusive, _seenIgnored);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void Adopt(IntPtr hwnd)
    {
        uint pid = _native.ProcessIdOf(hwnd);
        if (string.Equals(_native.ClassNameOf(hwnd), FrameHostClass, StringComparison.Ordinal))
        {
            pid = PickStoreAppProcess(pid, _native.ChildWindows(hwnd)) ?? pid;
        }

        _appHwnd = hwnd;
        _appPid = pid;
        _appPath = pid == 0 ? null : ApplicationPaths.Normalize(_native.ImagePathOf(pid));
        _hasApp = true;
        _fullscreenCheckedUtc = null;
    }

    private void Forget()
    {
        _hasApp = false;
        _appHwnd = IntPtr.Zero;
        _appPid = 0;
        _appPath = null;
        _fullscreenCheckedUtc = null;
        _fullscreen = _exclusive = false;
    }

    /// <summary>
    /// Une application du Store s'affiche dans un cadre d'ApplicationFrameHost : son vrai processus est celui de la
    /// fenêtre CoreWindow enfant qui n'appartient pas au cadre. Null si elle manque (application suspendue).
    /// </summary>
    internal static uint? PickStoreAppProcess(uint frameProcessId, IReadOnlyList<ChildWindowInfo> children)
        => children
            .Where(c => string.Equals(c.ClassName, CoreWindowClass, StringComparison.Ordinal) && c.ProcessId != 0 && c.ProcessId != frameProcessId)
            .Select(c => (uint?)c.ProcessId)
            .FirstOrDefault();

    /// <summary>Plein écran : la fenêtre, ni réduite ni simplement agrandie, couvre tout son écran (tolérance de
    /// <see cref="FullscreenTolerancePx"/>). Une fenêtre agrandie couvre la zone de travail, voire tout l'écran quand la
    /// barre des tâches se masque : ce n'est pas un plein écran.</summary>
    internal static bool IsFullscreen(WindowGeometry geometry)
        => !geometry.IsMinimized && !geometry.IsMaximized && Covers(geometry.Window, geometry.Monitor, FullscreenTolerancePx);

    /// <summary>Vrai si <paramref name="window"/> couvre <paramref name="monitor"/> (à <paramref name="tolerance"/> près).</summary>
    public static bool Covers(PixelRect window, PixelRect monitor, int tolerance)
        => monitor.Width > 0 && monitor.Height > 0
           && window.Left <= monitor.Left + tolerance
           && window.Top <= monitor.Top + tolerance
           && window.Right >= monitor.Right - tolerance
           && window.Bottom >= monitor.Bottom - tolerance;
}
