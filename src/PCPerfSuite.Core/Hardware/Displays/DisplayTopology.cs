using System.Diagnostics;
using System.Text;
using static PCPerfSuite.Core.Hardware.Displays.DisplayConfigNative;

namespace PCPerfSuite.Core.Hardware.Displays;

/// <summary>
/// Inventaire des écrans, commun à l'overlay (#3), à l'OC d'écran (#17), au GPU dédié des portables (#22) et au
/// classifieur du premier plan (#9). API de Windows seulement (aucune marque supposée, aucun droit administrateur).
///
/// Deux sources, rapprochées par le nom GDI (« \\.\DISPLAYn ») : EnumDisplayMonitors donne les bornes en pixels
/// physiques, l'écran principal et, par shcore, l'échelle ; QueryDisplayConfig donne les noms conviviaux, l'EDID, la
/// sortie et l'adaptateur. La première suffit à placer une fenêtre : si la seconde échoue, les écrans restent, sans
/// nom. Rien ici ne lève (règle 2 de CLAUDE.md) : un échec se lit dans <see cref="DisplayTopologySnapshot.Problem"/>.
///
/// Lecture rapide (quelques millisecondes), faisable sur le thread d'interface. À refaire après chaque changement de
/// configuration : les HMONITOR et les numéros GDI ne survivent pas à un branchement.
/// </summary>
public static class DisplayTopology
{
    /// <summary>Nombre d'essais de QueryDisplayConfig : un écran branché entre la taille et la lecture la fait
    /// échouer en ERROR_INSUFFICIENT_BUFFER.</summary>
    private const int QueryAttempts = 3;

    public static DisplayTopologySnapshot Read()
    {
        var problems = new List<string>();

        List<RawMonitor> monitors = ReadMonitors(problems);
        Dictionary<string, List<DisplayTarget>> targets = ReadTargets(problems);
        Dictionary<string, string> adapters = ReadAdapterNames();

        List<DisplayMonitor> result = monitors
            .Select(m => new DisplayMonitor(
                m.Handle,
                m.GdiDeviceName,
                m.Bounds,
                m.WorkArea,
                m.Dpi ?? 96,
                m.Dpi is not null,
                m.IsPrimary,
                adapters.GetValueOrDefault(m.GdiDeviceName),
                targets.GetValueOrDefault(m.GdiDeviceName) ?? (IReadOnlyList<DisplayTarget>)Array.Empty<DisplayTarget>()))
            .ToList();

        if (result.Count > 0 && targets.Count > 0 && result.Any(m => m.Targets.Count == 0))
        {
            problems.Add("Certains écrans n'ont pas pu être rapprochés de leur description (nom, EDID, sortie).");
        }

        return new DisplayTopologySnapshot(Order(result), problems.Count > 0 ? string.Join(" ", problems) : null);
    }

    /// <summary>Principal d'abord, puis de gauche à droite et de haut en bas : l'ordre des numéros d'« Identifier ».</summary>
    public static IReadOnlyList<DisplayMonitor> Order(IEnumerable<DisplayMonitor> monitors)
        => monitors
            .OrderByDescending(m => m.IsPrimary)
            .ThenBy(m => m.Bounds.Left)
            .ThenBy(m => m.Bounds.Top)
            .ToList();

    /// <summary>L'écran de ce HMONITOR dans l'instantané, null s'il n'y est pas (configuration changée depuis).</summary>
    public static DisplayMonitor? FindMonitor(DisplayTopologySnapshot snapshot, IntPtr monitorHandle)
        => monitorHandle == IntPtr.Zero ? null : snapshot.Monitors.FirstOrDefault(m => m.Handle == monitorHandle);

    /// <summary>HMONITOR de l'écran qui contient la plus grande partie de la fenêtre (le plus proche si elle est hors
    /// de tout écran). IntPtr.Zero si la fenêtre n'existe plus.</summary>
    public static IntPtr MonitorFromWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return IntPtr.Zero;
        try { return DisplayConfigNative.MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST); }
        catch { return IntPtr.Zero; }
    }

    /// <summary>Échelle actuelle d'un écran (1,5 = 150 %), relue à la demande : l'utilisateur peut la changer sans que
    /// Windows signale un changement de configuration. Null si l'écran n'existe plus ou ne la donne pas.</summary>
    public static double? ReadScale(IntPtr monitorHandle)
    {
        if (monitorHandle == IntPtr.Zero) return null;

        try
        {
            return GetDpiForMonitor(monitorHandle, MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0 && dpiX > 0 ? dpiX / 96.0 : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Fenêtre au premier plan, IntPtr.Zero si aucune (bureau sécurisé, verrouillage).</summary>
    public static IntPtr ForegroundWindow()
    {
        try { return GetForegroundWindow(); }
        catch { return IntPtr.Zero; }
    }

    // ------------------------------------------------------------------ Filtre du premier plan

    /// <summary>Classes des fenêtres du shell qui passent au premier plan sans être « l'application de
    /// l'utilisateur » : barre des tâches (principale et secondaires), bureau, zone de notification étendue, Alt+Tab,
    /// aperçus des boutons de la barre.</summary>
    private static readonly HashSet<string> ShellClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "Progman",
        "WorkerW",
        "NotifyIconOverflowWindow",
        "TopLevelWindowForOverflowXamlIsland",
        "MultitaskingViewFrame",
        "XamlExplorerHostIslandWindow",
        "ForegroundStaging",
        "TaskListThumbnailWnd",
    };

    /// <summary>Classe des fenêtres UWP : ignorée seulement pour les processus du shell (menu Démarrer, recherche,
    /// centre de notifications), pas pour une vraie application du Store.</summary>
    private const string CoreWindowClass = "Windows.UI.Core.CoreWindow";

    private static readonly HashSet<string> ShellProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "StartMenuExperienceHost",
        "SearchHost",
        "SearchApp",
        "ShellExperienceHost",
    };

    /// <summary>
    /// Vrai pour une fenêtre au premier plan qui ne doit pas déplacer l'overlay « écran du jeu » (ni, demain, compter
    /// comme l'usage en cours pour #9) : une fenêtre de PCPerfSuite, ou du shell. Sans ce filtre, l'overlay sauterait
    /// d'écran à chaque clic sur la barre des tâches d'un autre écran.
    /// </summary>
    /// <param name="className">Classe de la fenêtre (GetClassName).</param>
    /// <param name="processName">Nom du processus sans « .exe », utile seulement pour une CoreWindow.</param>
    public static bool IsIgnoredForegroundWindow(string? className, string? processName, uint processId, uint ownProcessId)
    {
        if (processId != 0 && processId == ownProcessId) return true;
        if (string.IsNullOrEmpty(className)) return true;
        if (ShellClasses.Contains(className)) return true;

        return string.Equals(className, CoreWindowClass, StringComparison.OrdinalIgnoreCase)
               && processName is not null
               && ShellProcesses.Contains(processName);
    }

    /// <summary><see cref="IsIgnoredForegroundWindow(string?, string?, uint, uint)"/> pour une vraie fenêtre. Une
    /// fenêtre illisible (fermée entre-temps) est ignorée.</summary>
    public static bool IsIgnoredForegroundWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return true;

        try
        {
            var className = new StringBuilder(256);
            if (GetClassName(hwnd, className, className.Capacity) == 0) return true;
            GetWindowThreadProcessId(hwnd, out uint processId);

            string name = className.ToString();
            string? processName = string.Equals(name, CoreWindowClass, StringComparison.OrdinalIgnoreCase)
                ? ProcessName(processId)
                : null;
            return IsIgnoredForegroundWindow(name, processName, processId, (uint)System.Environment.ProcessId);
        }
        catch
        {
            return true;
        }
    }

    private static string? ProcessName(uint processId)
    {
        try
        {
            using Process process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ Lectures

    private sealed record RawMonitor(IntPtr Handle, string GdiDeviceName, PixelRect Bounds, PixelRect WorkArea, int? Dpi, bool IsPrimary);

    private static List<RawMonitor> ReadMonitors(List<string> problems)
    {
        var handles = new List<IntPtr>();
        try
        {
            MonitorEnumProc callback = (IntPtr monitor, IntPtr _, ref RECT _, IntPtr _) =>
            {
                handles.Add(monitor);
                return true;
            };
            if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero))
            {
                problems.Add("Windows n'a pas énuméré les écrans (EnumDisplayMonitors).");
            }
            GC.KeepAlive(callback);
        }
        catch (Exception ex)
        {
            problems.Add($"Écrans illisibles ({ex.GetType().Name}).");
            return new List<RawMonitor>();
        }

        var monitors = new List<RawMonitor>();
        foreach (IntPtr handle in handles)
        {
            try
            {
                var info = new MONITORINFOEXW { Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEXW>() };
                if (!GetMonitorInfo(handle, ref info)) continue;

                int? dpi = null;
                try
                {
                    if (GetDpiForMonitor(handle, MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0 && dpiX > 0) dpi = (int)dpiX;
                }
                catch
                {
                    // shcore absent ou refus : l'échelle reste inconnue, 100 % par défaut.
                }

                monitors.Add(new RawMonitor(
                    handle,
                    info.DeviceName ?? "",
                    ToPixelRect(info.Monitor),
                    ToPixelRect(info.WorkArea),
                    dpi,
                    (info.Flags & MONITORINFOF_PRIMARY) != 0));
            }
            catch
            {
                // Un écran débranché pendant la lecture : on passe au suivant.
            }
        }

        if (monitors.Any(m => m.Dpi is null)) problems.Add("L'échelle d'un écran n'a pas pu être lue : 100 % supposé.");
        return monitors;
    }

    private static PixelRect ToPixelRect(RECT rect) => new(rect.Left, rect.Top, rect.Right, rect.Bottom);

    /// <summary>Écrans physiques des chemins actifs, rangés par nom GDI de leur source.</summary>
    private static Dictionary<string, List<DisplayTarget>> ReadTargets(List<string> problems)
    {
        var byGdiName = new Dictionary<string, List<DisplayTarget>>(StringComparer.OrdinalIgnoreCase);

        if (!TryQueryPaths(out DISPLAYCONFIG_PATH_INFO[] paths, out DISPLAYCONFIG_MODE_INFO[] _, out int error))
        {
            problems.Add($"Les noms des écrans n'ont pas pu être lus (QueryDisplayConfig, erreur Windows {error}).");
            return byGdiName;
        }

        var adapterPaths = new Dictionary<ulong, string?>();
        foreach (DISPLAYCONFIG_PATH_INFO path in paths)
        {
            try
            {
                string? gdiName = ReadSourceName(path.SourceInfo);
                if (string.IsNullOrEmpty(gdiName)) continue;

                ulong luid = path.TargetInfo.AdapterId.ToUInt64();
                if (!adapterPaths.TryGetValue(luid, out string? adapterPath))
                {
                    adapterPath = ReadAdapterPath(path.TargetInfo.AdapterId);
                    adapterPaths[luid] = adapterPath;
                }

                DisplayTarget target = ReadTarget(path.TargetInfo, luid, adapterPath);
                if (!byGdiName.TryGetValue(gdiName, out List<DisplayTarget>? list))
                {
                    list = new List<DisplayTarget>();
                    byGdiName[gdiName] = list;
                }
                list.Add(target);
            }
            catch
            {
                // Un chemin illisible n'efface pas les autres.
            }
        }

        return byGdiName;
    }

    private static bool TryQueryPaths(out DISPLAYCONFIG_PATH_INFO[] paths, out DISPLAYCONFIG_MODE_INFO[] modes, out int error)
    {
        paths = Array.Empty<DISPLAYCONFIG_PATH_INFO>();
        modes = Array.Empty<DISPLAYCONFIG_MODE_INFO>();
        error = 0;

        try
        {
            for (int attempt = 0; attempt < QueryAttempts; attempt++)
            {
                error = GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint pathCount, out uint modeCount);
                if (error != ERROR_SUCCESS) return false;

                paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
                modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
                error = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);

                if (error == ERROR_SUCCESS)
                {
                    Array.Resize(ref paths, (int)pathCount);
                    Array.Resize(ref modes, (int)modeCount);
                    return true;
                }
                if (error != ERROR_INSUFFICIENT_BUFFER) return false;
            }
        }
        catch (Exception ex)
        {
            error = ex.HResult;
        }

        return false;
    }

    private static string? ReadSourceName(DISPLAYCONFIG_PATH_SOURCE_INFO source)
    {
        var request = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
        {
            Header = Header<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME, source.AdapterId, source.Id),
        };
        return DisplayConfigGetDeviceInfo(ref request) == ERROR_SUCCESS ? request.ViewGdiDeviceName : null;
    }

    private static string? ReadAdapterPath(LUID adapterId)
    {
        try
        {
            var request = new DISPLAYCONFIG_ADAPTER_NAME
            {
                Header = Header<DISPLAYCONFIG_ADAPTER_NAME>(DISPLAYCONFIG_DEVICE_INFO_GET_ADAPTER_NAME, adapterId, 0),
            };
            return DisplayConfigGetDeviceInfo(ref request) == ERROR_SUCCESS ? Blank(request.AdapterDevicePath) : null;
        }
        catch
        {
            return null;
        }
    }

    private static DisplayTarget ReadTarget(DISPLAYCONFIG_PATH_TARGET_INFO target, ulong luid, string? adapterPath)
    {
        double? refresh = target.RefreshRate.Denominator == 0 || target.RefreshRate.Numerator == 0
            ? null
            : (double)target.RefreshRate.Numerator / target.RefreshRate.Denominator;

        var request = new DISPLAYCONFIG_TARGET_DEVICE_NAME
        {
            Header = Header<DISPLAYCONFIG_TARGET_DEVICE_NAME>(DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME, target.AdapterId, target.Id),
        };

        if (DisplayConfigGetDeviceInfo(ref request) != ERROR_SUCCESS)
        {
            return new DisplayTarget(null, null, null, null, null, DisplayNames.FromOutputTechnology(target.OutputTechnology),
                0, luid, adapterPath, refresh);
        }

        bool edidValid = (request.Flags & TARGET_NAME_EDID_IDS_VALID) != 0;
        return new DisplayTarget(
            Blank(request.MonitorFriendlyDeviceName),
            Blank(request.MonitorDevicePath),
            edidValid ? request.EdidManufactureId : null,
            edidValid ? DisplayNames.DecodeManufacturer(request.EdidManufactureId) : null,
            edidValid ? request.EdidProductCodeId : null,
            DisplayNames.FromOutputTechnology(request.OutputTechnology),
            request.ConnectorInstance,
            luid,
            adapterPath,
            refresh);
    }

    private static DISPLAYCONFIG_DEVICE_INFO_HEADER Header<T>(int type, LUID adapterId, uint id) where T : struct => new()
    {
        Type = type,
        Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<T>(),
        AdapterId = adapterId,
        Id = id,
    };

    /// <summary>Nom du GPU de chaque source (« \\.\DISPLAY1 » → « NVIDIA GeForce RTX 4070 »), par EnumDisplayDevices,
    /// qui le donne sans passer par l'API d'un fabricant.</summary>
    private static Dictionary<string, string> ReadAdapterNames()
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            for (uint index = 0; index < 64; index++)
            {
                var device = new DISPLAY_DEVICEW { Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<DISPLAY_DEVICEW>() };
                if (!EnumDisplayDevices(null, index, ref device, 0)) break;
                if (Blank(device.DeviceName) is { } gdiName && Blank(device.DeviceString) is { } adapter) names[gdiName] = adapter;
            }
        }
        catch
        {
            // Le nom du GPU manque seulement dans le diagnostic.
        }
        return names;
    }

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
