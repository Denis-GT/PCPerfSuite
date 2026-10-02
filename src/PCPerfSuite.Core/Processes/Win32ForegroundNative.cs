using System.Runtime.InteropServices;
using System.Text;
using PCPerfSuite.Core.Hardware.Displays;

namespace PCPerfSuite.Core.Processes;

/// <summary>
/// Les appels Windows de <see cref="ForegroundAppReader"/>. Chacun est « best-effort » : une fenêtre fermée entre deux
/// appels, un processus protégé ou une API absente donnent null ou faux, jamais une exception. Les bornes sont en pixels
/// physiques : l'app est PerMonitorV2 (app.manifest), donc GetWindowRect, DwmGetWindowAttribute et GetMonitorInfo
/// parlent la même unité que <see cref="DisplayTopology"/>.
/// </summary>
internal sealed class Win32ForegroundNative : IForegroundNative
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    public IntPtr ForegroundWindow() => DisplayTopology.ForegroundWindow();

    public bool IsIgnored(IntPtr hwnd) => DisplayTopology.IsIgnoredForegroundWindow(hwnd);

    public bool Exists(IntPtr hwnd)
    {
        try { return hwnd != IntPtr.Zero && DisplayConfigNative.IsWindow(hwnd); }
        catch { return false; }
    }

    public uint ProcessIdOf(IntPtr hwnd)
    {
        try
        {
            DisplayConfigNative.GetWindowThreadProcessId(hwnd, out uint pid);
            return pid;
        }
        catch
        {
            return 0;
        }
    }

    public string? ClassNameOf(IntPtr hwnd)
    {
        try
        {
            var name = new StringBuilder(256);
            return DisplayConfigNative.GetClassName(hwnd, name, name.Capacity) > 0 ? name.ToString() : null;
        }
        catch
        {
            return null;
        }
    }

    public string? ImagePathOf(uint processId)
    {
        if (processId == 0) return null;

        IntPtr handle = IntPtr.Zero;
        try
        {
            // Le droit le plus faible qui donne le chemin : il passe même sur la plupart des processus protégés.
            handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
            if (handle == IntPtr.Zero) return null;

            var buffer = new char[1024];
            uint size = (uint)buffer.Length;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) && size > 0 ? new string(buffer, 0, (int)size) : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (handle != IntPtr.Zero) CloseHandle(handle);
        }
    }

    public IReadOnlyList<ChildWindowInfo> ChildWindows(IntPtr hwnd)
    {
        var children = new List<ChildWindowInfo>();
        try
        {
            EnumChildProc callback = (child, _) =>
            {
                children.Add(new ChildWindowInfo(child, ClassNameOf(child), ProcessIdOf(child)));
                return children.Count < 64;
            };
            EnumChildWindows(hwnd, callback, IntPtr.Zero);
            GC.KeepAlive(callback);
        }
        catch
        {
            // Liste partielle : le cadre seul sera retenu.
        }

        return children;
    }

    public WindowGeometry? GeometryOf(IntPtr hwnd)
    {
        try
        {
            // Le cadre visible, sans les bordures invisibles de Windows 10/11 ; GetWindowRect si DWM ne répond pas.
            if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out DisplayConfigNative.RECT frame,
                    Marshal.SizeOf<DisplayConfigNative.RECT>()) != 0
                && !GetWindowRect(hwnd, out frame))
            {
                return null;
            }

            IntPtr monitor = DisplayConfigNative.MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (monitor == IntPtr.Zero) return null;

            var info = new DisplayConfigNative.MONITORINFOEXW { Size = (uint)Marshal.SizeOf<DisplayConfigNative.MONITORINFOEXW>() };
            if (!DisplayConfigNative.GetMonitorInfo(monitor, ref info)) return null;

            return new WindowGeometry(ToPixels(frame), ToPixels(info.Monitor), IsZoomed(hwnd), IsIconic(hwnd));
        }
        catch
        {
            return null;
        }
    }

    public UserNotificationState NotificationState()
    {
        try
        {
            if (SHQueryUserNotificationState(out int state) != 0) return UserNotificationState.Unknown;
            return state switch
            {
                5 => UserNotificationState.Normal, // QUNS_ACCEPTS_NOTIFICATIONS
                2 => UserNotificationState.Busy, // QUNS_BUSY
                3 => UserNotificationState.RunningD3DFullScreen, // QUNS_RUNNING_D3D_FULL_SCREEN
                _ => UserNotificationState.Other,
            };
        }
        catch
        {
            return UserNotificationState.Unknown;
        }
    }

    private static PixelRect ToPixels(DisplayConfigNative.RECT rect) => new(rect.Left, rect.Top, rect.Right, rect.Bottom);

    private delegate bool EnumChildProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, [Out] char[] exeName, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(IntPtr parent, EnumChildProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out DisplayConfigNative.RECT rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out DisplayConfigNative.RECT value, int size);

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);
}
