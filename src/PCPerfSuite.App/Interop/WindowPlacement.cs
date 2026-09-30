using System.Runtime.InteropServices;
using PCPerfSuite.Core.Hardware.Displays;

namespace PCPerfSuite.App.Interop;

/// <summary>
/// Place une fenêtre en pixels physiques, sans passer par Left/Top : en PerMonitorV2, WPF convertit ces DIP avec le DPI
/// de l'écran où la fenêtre se trouve encore, et une fenêtre envoyée sur un écran d'une autre échelle tombe à côté
/// (dotnet/wpf #4127). Garde la fenêtre toujours au-dessus et ne l'active jamais.
/// </summary>
internal static class WindowPlacement
{
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    public static void MoveTo(IntPtr hwnd, PixelPoint position)
    {
        if (hwnd == IntPtr.Zero) return;

        try
        {
            SetWindowPos(hwnd, HWND_TOPMOST, position.X, position.Y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
        }
        catch
        {
            // Au pire, la fenêtre reste où elle est jusqu'au prochain placement.
        }
    }

    /// <summary>Coin haut-gauche actuel en pixels physiques, null si la fenêtre est illisible.</summary>
    public static PixelPoint? GetPosition(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return null;

        try
        {
            return GetWindowRect(hwnd, out RECT rect) ? new PixelPoint(rect.Left, rect.Top) : null;
        }
        catch
        {
            return null;
        }
    }
}
