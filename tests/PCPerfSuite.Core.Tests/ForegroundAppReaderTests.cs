using PCPerfSuite.Core.Hardware.Displays;
using PCPerfSuite.Core.Processes;

namespace PCPerfSuite.Core.Tests;

/// <summary>Lecteur du premier plan : une lecture par relevé, le processus relu seulement au changement de fenêtre, le
/// shell et PCPerfSuite écartés, les applications du Store rapportées à leur vrai processus.</summary>
public sealed class ForegroundAppReaderTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 20, 0, 0, TimeSpan.Zero);
    private static readonly PixelRect Screen = new(0, 0, 2560, 1440);

    private sealed class FakeWindow
    {
        public uint Pid;
        public string Class = "UnrealWindow";
        public bool Ignored;
        public bool Exists = true;
        public WindowGeometry? Geometry;
        public List<ChildWindowInfo> Children = new();
    }

    private sealed class FakeNative : IForegroundNative
    {
        public readonly Dictionary<IntPtr, FakeWindow> Windows = new();
        public readonly Dictionary<uint, string?> Paths = new();
        public IntPtr Foreground;
        public UserNotificationState State = UserNotificationState.Normal;
        public int PathReads;
        public int GeometryReads;

        public IntPtr ForegroundWindow() => Foreground;
        public bool IsIgnored(IntPtr hwnd) => Windows.TryGetValue(hwnd, out FakeWindow? w) && w.Ignored;
        public bool Exists(IntPtr hwnd) => Windows.TryGetValue(hwnd, out FakeWindow? w) && w.Exists;
        public uint ProcessIdOf(IntPtr hwnd) => Windows.TryGetValue(hwnd, out FakeWindow? w) ? w.Pid : 0;
        public string? ClassNameOf(IntPtr hwnd) => Windows.TryGetValue(hwnd, out FakeWindow? w) ? w.Class : null;

        public string? ImagePathOf(uint processId)
        {
            PathReads++;
            return Paths.GetValueOrDefault(processId);
        }

        public IReadOnlyList<ChildWindowInfo> ChildWindows(IntPtr hwnd)
            => Windows.TryGetValue(hwnd, out FakeWindow? w) ? w.Children : [];

        public WindowGeometry? GeometryOf(IntPtr hwnd)
        {
            GeometryReads++;
            return Windows.TryGetValue(hwnd, out FakeWindow? w) ? w.Geometry : null;
        }

        public UserNotificationState NotificationState() => State;
    }

    private readonly FakeNative _native = new();
    private readonly ForegroundAppReader _reader;

    public ForegroundAppReaderTests()
    {
        _reader = new ForegroundAppReader(_native);
        _native.Windows[1] = new FakeWindow { Pid = 100, Geometry = new WindowGeometry(Screen, Screen, false, false) };
        _native.Paths[100] = @"C:\Jeux\game.exe";
        _native.Windows[2] = new FakeWindow { Pid = 200, Class = "Chrome_WidgetWin_1", Geometry = new WindowGeometry(new PixelRect(0, 0, 1280, 720), Screen, false, false) };
        _native.Paths[200] = @"C:\Programmes\navigateur.exe";
        _native.Windows[9] = new FakeWindow { Pid = 900, Class = "Shell_TrayWnd", Ignored = true };
    }

    [Fact]
    public void L_application_au_premier_plan_est_lue_avec_son_chemin_et_son_plein_ecran()
    {
        _native.Foreground = 1;

        ForegroundApp app = _reader.Poll(T0)!;

        Assert.Equal(@"C:\Jeux\game.exe", app.Path);
        Assert.Equal("game", app.DisplayName);
        Assert.True(app.IsFullscreen);
        Assert.False(app.IsExclusiveFullscreen);
        Assert.False(app.ForegroundIgnored);
    }

    [Fact]
    public void Le_processus_n_est_relu_qu_au_changement_de_fenetre()
    {
        _native.Foreground = 1;
        _reader.Poll(T0);
        _reader.Poll(T0.AddSeconds(1));
        _reader.Poll(T0.AddSeconds(2));
        Assert.Equal(1, _native.PathReads);

        _native.Foreground = 2;
        Assert.Equal(@"C:\Programmes\navigateur.exe", _reader.Poll(T0.AddSeconds(3))!.Path);
        Assert.Equal(2, _native.PathReads);
    }

    [Fact]
    public void Le_plein_ecran_est_reevalue_toutes_les_5_secondes()
    {
        _native.Foreground = 1;
        _reader.Poll(T0);
        _reader.Poll(T0.AddSeconds(4));
        Assert.Equal(1, _native.GeometryReads);

        _native.Windows[1].Geometry = new WindowGeometry(new PixelRect(100, 100, 900, 700), Screen, false, false);
        Assert.False(_reader.Poll(T0.AddSeconds(5))!.IsFullscreen);
        Assert.Equal(2, _native.GeometryReads);
    }

    [Fact]
    public void Le_shell_et_PCPerfSuite_gardent_la_derniere_vraie_application()
    {
        _native.Foreground = 1;
        _reader.Poll(T0);

        _native.Foreground = 9;
        ForegroundApp app = _reader.Poll(T0.AddSeconds(1))!;

        Assert.Equal(@"C:\Jeux\game.exe", app.Path);
        Assert.True(app.ForegroundIgnored);
        Assert.Equal(1, _native.PathReads);
    }

    [Fact]
    public void Revenir_a_la_meme_fenetre_ne_relit_pas_le_processus()
    {
        _native.Foreground = 1;
        _reader.Poll(T0);
        _native.Foreground = 9;
        _reader.Poll(T0.AddSeconds(1));
        _native.Foreground = 1;

        Assert.False(_reader.Poll(T0.AddSeconds(2))!.ForegroundIgnored);
        Assert.Equal(1, _native.PathReads);
    }

    [Fact]
    public void Sans_vraie_application_rien_n_est_rapporte()
    {
        _native.Foreground = 9;
        Assert.Null(_reader.Poll(T0));

        _native.Foreground = IntPtr.Zero;
        Assert.Null(_reader.Poll(T0.AddSeconds(1)));
    }

    [Fact]
    public void Une_application_fermee_pendant_qu_on_est_sur_le_bureau_est_oubliee()
    {
        _native.Foreground = 1;
        _reader.Poll(T0);
        _native.Foreground = 9;
        _native.Windows[1].Exists = false;

        Assert.Null(_reader.Poll(T0.AddSeconds(1)));
    }

    [Fact]
    public void Un_processus_illisible_donne_une_application_sans_chemin()
    {
        _native.Paths[100] = null;
        _native.Foreground = 1;

        ForegroundApp app = _reader.Poll(T0)!;

        Assert.True(app.PathUnavailable);
        Assert.Equal("application inaccessible", app.DisplayName);
    }

    [Fact]
    public void Le_plein_ecran_exclusif_vient_du_shell()
    {
        _native.State = UserNotificationState.RunningD3DFullScreen;
        _native.Windows[1].Geometry = null;
        _native.Foreground = 1;

        ForegroundApp app = _reader.Poll(T0)!;

        Assert.True(app.IsExclusiveFullscreen);
        Assert.True(app.IsFullscreen);
    }

    [Fact]
    public void Une_application_du_Store_est_rapportee_a_son_vrai_processus()
    {
        _native.Windows[5] = new FakeWindow
        {
            Pid = 500,
            Class = "ApplicationFrameWindow",
            Children = [new ChildWindowInfo(6, "ApplicationFrameInputSinkWindow", 500), new ChildWindowInfo(7, "Windows.UI.Core.CoreWindow", 501)],
        };
        _native.Paths[500] = @"C:\Windows\System32\ApplicationFrameHost.exe";
        _native.Paths[501] = @"C:\Program Files\WindowsApps\Editeur.Appli_1.0.0.0_x64__abc\Appli.exe";
        _native.Foreground = 5;

        ForegroundApp app = _reader.Poll(T0)!;

        Assert.Equal(501u, app.ProcessId);
        Assert.EndsWith("Appli.exe", app.Path);
    }

    [Fact]
    public void Sans_CoreWindow_le_cadre_du_Store_reste_le_processus()
        => Assert.Null(ForegroundAppReader.PickStoreAppProcess(500, [new ChildWindowInfo(6, "Windows.UI.Core.CoreWindow", 500)]));

    [Theory]
    [InlineData(0, 0, 2560, 1440, true)]
    [InlineData(-8, -8, 2568, 1448, true)]
    [InlineData(2, 2, 2558, 1438, true)]
    [InlineData(3, 0, 2560, 1440, false)]
    [InlineData(0, 0, 2560, 1400, false)]
    public void Une_fenetre_couvre_son_ecran_a_deux_pixels_pres(int left, int top, int right, int bottom, bool expected)
        => Assert.Equal(expected, ForegroundAppReader.Covers(new PixelRect(left, top, right, bottom), Screen, ForegroundAppReader.FullscreenTolerancePx));

    [Fact]
    public void Un_ecran_secondaire_a_coordonnees_negatives_est_couvert()
    {
        var left = new PixelRect(-1920, 0, 0, 1080);
        Assert.True(ForegroundAppReader.Covers(left, left, 2));
        Assert.False(ForegroundAppReader.Covers(Screen, left, 2));
    }

    [Fact]
    public void Une_fenetre_agrandie_ou_reduite_n_est_pas_en_plein_ecran()
    {
        Assert.False(ForegroundAppReader.IsFullscreen(new WindowGeometry(Screen, Screen, IsMaximized: true, IsMinimized: false)));
        Assert.False(ForegroundAppReader.IsFullscreen(new WindowGeometry(Screen, Screen, IsMaximized: false, IsMinimized: true)));
        Assert.True(ForegroundAppReader.IsFullscreen(new WindowGeometry(Screen, Screen, false, false)));
    }
}
