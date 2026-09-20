using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PCPerfSuite.App.Interop;

/// <summary>
/// Icône d'un exécutable, telle que l'Explorateur l'affiche. Best-effort de bout en bout : un processus
/// protégé, un fichier effacé depuis son lancement ou une extension shell capricieuse rendent null, jamais
/// une exception — la liste des processus doit s'afficher dans tous les cas.
///
/// Les demandes sont servies une par une par un seul travailleur d'arrière-plan : au premier relevé, trois
/// cents processus se présentent d'un coup, et autant d'appels shell simultanés satureraient le pool de
/// threads pour un gain nul (le disque, lui, reste séquentiel). Le résultat est mis en cache par chemin,
/// les échecs compris, pour ne pas retenter à chaque relevé.
/// </summary>
internal static class ShellIcons
{
    /// <summary>Au-delà, le cache est vidé d'un bloc. Le nombre de chemins distincts d'une session est très
    /// en dessous ; ce plafond n'existe que pour qu'une machine qui lance des milliers d'exécutables
    /// différents ne fasse pas enfler la mémoire indéfiniment.</summary>
    private const int MaxCacheEntries = 1024;

    private const uint ShgfiIcon = 0x000000100;
    private const uint ShgfiSmallIcon = 0x000000001;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Queue<(string Path, TaskCompletionSource<ImageSource?> Completion)> Pending = new();
    private static bool _draining;

    /// <summary>Icône du fichier, prise au cache si elle y est déjà (aucun aller-retour, donc pas de
    /// clignotement au défilement) et calculée en arrière-plan sinon.</summary>
    public static Task<ImageSource?> GetAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return Task.FromResult<ImageSource?>(null);

        var completion = new TaskCompletionSource<ImageSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (Gate)
        {
            if (Cache.TryGetValue(path, out ImageSource? cached)) return Task.FromResult(cached);

            Pending.Enqueue((path, completion));
            if (_draining) return completion.Task;

            _draining = true;
        }

        Task.Run(Drain);
        return completion.Task;
    }

    private static void Drain()
    {
        while (true)
        {
            (string Path, TaskCompletionSource<ImageSource?> Completion) request;
            lock (Gate)
            {
                if (Pending.Count == 0)
                {
                    _draining = false;
                    return;
                }

                request = Pending.Dequeue();
            }

            ImageSource? icon;
            bool known;
            lock (Gate) known = Cache.TryGetValue(request.Path, out icon);

            if (!known)
            {
                icon = Extract(request.Path);
                lock (Gate)
                {
                    if (Cache.Count >= MaxCacheEntries) Cache.Clear();
                    Cache[request.Path] = icon;
                }
            }

            request.Completion.TrySetResult(icon);
        }
    }

    private static ImageSource? Extract(string path)
    {
        var info = new ShellFileInfo();
        try
        {
            IntPtr result = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<ShellFileInfo>(), ShgfiIcon | ShgfiSmallIcon);
            if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;

            BitmapSource source = Imaging.CreateBitmapSourceFromHIcon(
                info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

            // Gelée : l'image est construite ici, sur un thread d'arrière-plan, et affichée sur celui de
            // l'interface. Sans ça, WPF refuserait de la peindre.
            source.Freeze();
            return source;
        }
        catch (Exception)
        {
            // Volontairement large : l'appel traverse le shell, donc des extensions tierces dont on ne
            // maîtrise ni le code ni les exceptions. Une icône absente n'est pas une raison de tomber.
            return null;
        }
        finally
        {
            if (info.hIcon != IntPtr.Zero) DestroyIcon(info.hIcon);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", EntryPoint = "SHGetFileInfoW", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref ShellFileInfo psfi,
        uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
