using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace PCPerfSuite.App.Interop;

/// <summary>
/// Garde une fenêtre "toujours au-dessus" réellement devant. Topmost ne fait que la ranger parmi les fenêtres
/// topmost, et la barre des tâches en est une : quand on clique dessus, Windows la remonte devant l'overlay, qui y
/// reste. À chaque changement de fenêtre au premier plan, la fenêtre est donc remise en tête de ce groupe.
/// </summary>
internal sealed class TopmostKeeper : IDisposable
{
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOOWNERZORDER = 0x0200;

    /// <summary>Délai du second passage : le shell peut encore réordonner ses fenêtres juste après l'activation.</summary>
    private static readonly TimeSpan SecondPassDelay = TimeSpan.FromMilliseconds(250);

    private delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventProc proc,
        uint idProcess, uint idThread, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    private readonly IntPtr _hwnd;
    private readonly DispatcherTimer _secondPass;

    // Gardé en champ : sans référence, le ramasse-miettes libérerait le délégué appelé par Windows.
    private readonly WinEventProc _proc;
    private IntPtr _hook;

    /// <summary>À créer sur le thread de l'interface : Windows y rappelle le hook, par sa boucle de messages.</summary>
    public TopmostKeeper(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _proc = OnForegroundChanged;
        _secondPass = new DispatcherTimer { Interval = SecondPassDelay };
        _secondPass.Tick += (_, _) =>
        {
            _secondPass.Stop();
            BringToTop();
        };

        try
        {
            _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT);
        }
        catch
        {
            // Sans hook, il reste la remise au premier plan à chaque rendu de l'overlay.
        }
    }

    public void BringToTop()
    {
        if (_hwnd == IntPtr.Zero) return;

        try
        {
            SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
        }
        catch
        {
            // Au pire, l'overlay reste derrière jusqu'au prochain essai.
        }
    }

    private void OnForegroundChanged(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        BringToTop();
        _secondPass.Stop();
        _secondPass.Start();
    }

    public void Dispose()
    {
        _secondPass.Stop();
        if (_hook == IntPtr.Zero) return;

        UnhookWinEvent(_hook);
        _hook = IntPtr.Zero;
    }
}
