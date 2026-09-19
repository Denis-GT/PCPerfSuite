using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PCPerfSuite.App.Interop;

/// <summary>
/// Icône dans la zone de notification (près de l'horloge), via Shell_NotifyIcon. Permet de laisser
/// PCPerfSuite tourner fenêtre masquée : le monitoring, les courbes de ventilation et l'overlay
/// continuent pendant que la fenêtre n'encombre plus la barre des tâches.
///
/// La classe ne connaît que Win32 : elle signale les clics (<see cref="Activated"/>,
/// <see cref="MenuRequested"/>) et laisse la fenêtre décider quoi en faire.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_MODIFY = 0x00000001;
    private const uint NIM_DELETE = 0x00000002;
    private const uint NIM_SETVERSION = 0x00000004;

    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;
    private const uint NIF_INFO = 0x00000010;
    private const uint NIF_SHOWTIP = 0x00000080;

    private const uint NOTIFYICON_VERSION_4 = 4;

    /// <summary>Message de rappel choisi pour nos clics. La plage WM_APP est réservée aux applications.</summary>
    private const int WM_TRAYCALLBACK = 0x8000 + 1;

    // Reçus tels quels dans lParam en protocole historique...
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_CONTEXTMENU = 0x007B;

    // ...et remplacés par ceux-ci en version 4.
    private const int NIN_SELECT = 0x0400;
    private const int NIN_KEYSELECT = 0x0401;

    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_POPUP = unchecked((int)0x80000000);

    private const uint IMAGE_ICON = 1;
    private const uint LR_DEFAULTCOLOR = 0x00000000;
    private const uint LR_SHARED = 0x00008000;
    private const uint LR_DEFAULTSIZE = 0x00000040;

    private const int SM_CXSMICON = 49;
    private const int SM_CYSMICON = 50;

    /// <summary>Ordinal donné par le compilateur au groupe d'icônes issu de &lt;ApplicationIcon&gt;.
    /// C'est aussi la valeur de IDI_APPLICATION côté système, d'où son usage dans les deux replis.</summary>
    private const int ICON_ORDINAL = 32512;

    private const uint MSGFLT_ALLOW = 1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATAW data);

    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string name);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, uint message, uint action, IntPtr changeInfo);

    [DllImport("user32.dll", EntryPoint = "LoadImageW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImage(IntPtr instance, IntPtr name, uint type, int cx, int cy, uint load);

    [DllImport("shell32.dll", EntryPoint = "ExtractIconExW", CharSet = CharSet.Unicode)]
    private static extern int ExtractIconEx(string file, int index, IntPtr[]? large, IntPtr[]? small, int count);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    /// <summary>Clic gauche (ou activation au clavier) sur l'icône.</summary>
    public event Action? Activated;

    /// <summary>Clic droit sur l'icône. Le point est en pixels écran, pas en unités WPF.</summary>
    public event Action<Point>? MenuRequested;

    private readonly string _tooltip;
    private readonly uint _taskbarCreated;

    // Gardé en champ : sans référence, le ramasse-miettes libérerait le délégué appelé par Windows.
    private readonly HwndSourceHook _hook;

    private HwndSource? _source;
    private IntPtr _icon;
    private bool _iconIsShared;
    private bool _useVersion4;
    private bool _added;
    private bool _disposed;

    /// <summary>HWND de la fenêtre cachée qui reçoit les clics : à passer au premier plan avant
    /// d'ouvrir un menu contextuel, sinon Windows ne le referme pas quand on clique ailleurs.</summary>
    public IntPtr Handle => _source?.Handle ?? IntPtr.Zero;

    /// <summary>Faux si la fenêtre cachée ou l'inscription auprès du shell a échoué : appelant ne doit
    /// alors pas compter sur l'icône pour rouvrir l'app (masquer la fenêtre la rendrait irrécupérable).</summary>
    public bool IsAvailable => _source != null && _added;

    /// <summary>À créer sur le thread de l'interface : Windows rappelle l'icône par la boucle de messages.</summary>
    public TrayIcon(string tooltip)
    {
        _tooltip = tooltip;
        _hook = WndProc;
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");

        // Fenêtre cachée dédiée plutôt que le HWND de la fenêtre principale : celle-ci est justement
        // masquée la plupart du temps, et la passer au premier plan pour afficher le menu de l'icône
        // la ferait réapparaître dans la barre des tâches. Volontairement une fenêtre de premier
        // niveau invisible et non une fenêtre "message-only" (HWND_MESSAGE) : ces dernières ne
        // reçoivent pas les messages diffusés, or TaskbarCreated en est un.
        HwndSourceParameters parameters = new("PCPerfSuiteTray")
        {
            Width = 0,
            Height = 0,
            PositionX = 0,
            PositionY = 0,
            WindowStyle = WS_POPUP,
            ExtendedWindowStyle = WS_EX_TOOLWINDOW,
        };

        try
        {
            _source = new HwndSource(parameters);
            _source.AddHook(_hook);
        }
        catch
        {
            // Sans fenêtre cachée, l'icône ne peut pas s'inscrire : IsAvailable reste faux et
            // MainWindow ferme normalement au lieu de se masquer vers une icône inexistante.
            return;
        }

        AllowMessagesFromShell();
        _icon = LoadTrayIcon();
        Add();
    }

    /// <summary>L'app tourne en administrateur (app.manifest) alors que l'Explorateur, lui, tourne en
    /// intégrité moyenne : sans ces autorisations, Windows filtre les messages qu'il nous envoie
    /// au-dessus de WM_USER, et les clics sur l'icône n'arriveraient jamais.</summary>
    private void AllowMessagesFromShell()
    {
        try
        {
            ChangeWindowMessageFilterEx(Handle, _taskbarCreated, MSGFLT_ALLOW, IntPtr.Zero);
            ChangeWindowMessageFilterEx(Handle, (uint)WM_TRAYCALLBACK, MSGFLT_ALLOW, IntPtr.Zero);
        }
        catch
        {
            // Hors session élevée, le filtre ne s'applique pas : l'icône fonctionne quand même.
        }
    }

    /// <summary>Icône de l'exe (celle de &lt;ApplicationIcon&gt;), à la taille attendue par la zone de
    /// notification — Windows choisit alors la bonne trame du .ico au lieu de réduire la plus grande.</summary>
    private IntPtr LoadTrayIcon()
    {
        int width = GetSystemMetrics(SM_CXSMICON);
        int height = GetSystemMetrics(SM_CYSMICON);

        try
        {
            IntPtr icon = LoadImage(GetModuleHandle(null), new IntPtr(ICON_ORDINAL), IMAGE_ICON, width, height, LR_DEFAULTCOLOR);
            if (icon != IntPtr.Zero) return icon;

            // Même icône, retrouvée par le chemin de l'exe : ne dépend pas de l'ordinal de ressource.
            IntPtr[] small = new IntPtr[1];
            if (Environment.ProcessPath is string exe && ExtractIconEx(exe, 0, null, small, 1) > 0 && small[0] != IntPtr.Zero)
            {
                return small[0];
            }
        }
        catch
        {
            // On retombe sur l'icône système ci-dessous.
        }

        // Dernier repli : mieux vaut l'icône générique de Windows que pas d'icône cliquable du tout.
        _iconIsShared = true;
        return LoadImage(IntPtr.Zero, new IntPtr(ICON_ORDINAL), IMAGE_ICON, 0, 0, LR_SHARED | LR_DEFAULTSIZE);
    }

    private NOTIFYICONDATAW Build(uint flags)
    {
        return new NOTIFYICONDATAW
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = Handle,
            // Identifiée par un uID et non par un GUID : le shell lie une icône à GUID au chemin exact
            // de l'exe, et elle cesse d'apparaître dès que le binaire est déplacé ou recompilé ailleurs.
            uID = 1,
            uFlags = flags,
            uCallbackMessage = (uint)WM_TRAYCALLBACK,
            hIcon = _icon,
            szTip = _tooltip,
            szInfo = string.Empty,
            szInfoTitle = string.Empty,
        };
    }

    private void Add()
    {
        try
        {
            NOTIFYICONDATAW data = Build(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
            if (!Shell_NotifyIcon(NIM_ADD, ref data))
            {
                _added = false;
                return;
            }

            _added = true;

            // Protocole récent : clics rapportés par NIN_SELECT/WM_CONTEXTMENU, avec le point d'ancrage
            // de l'icône dans wParam (plus fiable que la position du curseur, écran secondaire compris).
            data.uVersion = NOTIFYICON_VERSION_4;
            _useVersion4 = Shell_NotifyIcon(NIM_SETVERSION, ref data);
        }
        catch
        {
            // Sans icône, l'app reste utilisable : la fenêtre se ferme alors comme avant.
        }
    }

    /// <summary>Message ponctuel affiché par Windows au-dessus de l'icône (bulle / notification).</summary>
    public void ShowHint(string title, string text)
    {
        if (_disposed) return;

        try
        {
            NOTIFYICONDATAW data = Build(NIF_INFO);
            data.szInfoTitle = title;
            data.szInfo = text;
            Shell_NotifyIcon(NIM_MODIFY, ref data);
        }
        catch
        {
            // Simple message d'accueil : son absence ne change rien au fonctionnement.
        }
    }

    /// <summary>Convertit des pixels écran en unités WPF, pour placer un menu au bon endroit quelle que
    /// soit la mise à l'échelle de l'affichage (l'app est PerMonitorV2, voir app.manifest).</summary>
    public Point DeviceToDip(Point devicePoint)
    {
        if (_source?.CompositionTarget is not { } target) return devicePoint;
        return target.TransformFromDevice.Transform(devicePoint);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == _taskbarCreated && _taskbarCreated != 0)
        {
            // L'Explorateur a redémarré : toutes les icônes ont disparu, il faut se réinscrire.
            Add();
            return IntPtr.Zero;
        }

        if (msg != WM_TRAYCALLBACK) return IntPtr.Zero;

        int notification = (short)(lParam.ToInt64() & 0xFFFF);
        switch (notification)
        {
            case NIN_SELECT:
            case NIN_KEYSELECT:
            case WM_LBUTTONUP:
            case WM_LBUTTONDBLCLK:
                handled = true;
                Activated?.Invoke();
                break;

            case WM_CONTEXTMENU:
            case WM_RBUTTONUP:
                handled = true;
                MenuRequested?.Invoke(AnchorPoint(wParam));
                break;
        }

        return IntPtr.Zero;
    }

    /// <summary>Les coordonnées sont signées : un écran secondaire placé à gauche du principal donne
    /// des valeurs négatives, qu'un cast non signé transformerait en position hors écran.</summary>
    private Point AnchorPoint(IntPtr wParam)
    {
        if (_useVersion4)
        {
            long packed = wParam.ToInt64();
            return new Point((short)(packed & 0xFFFF), (short)((packed >> 16) & 0xFFFF));
        }

        return GetCursorPos(out POINT cursor) ? new Point(cursor.X, cursor.Y) : new Point(0, 0);
    }

    /// <summary>Retire l'icône. Sans NIM_DELETE, une icône fantôme reste affichée jusqu'à ce que la
    /// souris la survole. Appelable plusieurs fois : la sortie de l'app passe par deux chemins.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            NOTIFYICONDATAW data = Build(NIF_MESSAGE);
            Shell_NotifyIcon(NIM_DELETE, ref data);
        }
        catch
        {
            // Au pire l'icône s'efface au prochain survol de la souris.
        }

        _added = false;

        if (_icon != IntPtr.Zero && !_iconIsShared) DestroyIcon(_icon);
        _icon = IntPtr.Zero;

        _source?.RemoveHook(_hook);
        _source?.Dispose();
        _source = null;
    }
}
