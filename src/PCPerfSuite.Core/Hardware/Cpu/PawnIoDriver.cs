using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PCPerfSuite.Core.Hardware.Cpu;

/// <summary>
/// Accès au pilote PawnIO, le pilote signé qui remplace WinRing0 depuis LibreHardwareMonitor 0.9.5.
///
/// PawnIO n'expose pas un accès brut au matériel : il exécute des "modules" signés (des petits
/// programmes compilés) qui décident eux-mêmes de ce qu'ils autorisent. Le module IntelMSR, par
/// exemple, n'accepte l'écriture que d'une liste fermée de registres. Une écriture refusée revient
/// donc en STATUS_ACCESS_DENIED et n'est pas un bug de l'app : c'est le module qui dit non, et
/// l'interface doit l'expliquer plutôt que d'afficher une erreur brute.
///
/// Les modules eux-mêmes sont pris dans les ressources de LibreHardwareMonitorLib, qui les embarque
/// déjà : rien de plus à livrer avec PCPerfSuite, et ils restent signés par leur auteur.
/// </summary>
public static class PawnIoDriver
{
    /// <summary>Page officielle du pilote, proposée à l'utilisateur quand il n'est pas installé.</summary>
    public const string DownloadUrl = "https://pawnio.eu/";

    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO";

    private static readonly object LoadLock = new();
    private static bool _probed;
    private static IntPtr _library;
    private static string? _loadError;

    /// <summary>Version du produit installé ("2.2.0"), telle que l'inscrit son installeur.</summary>
    public static string? Version { get; private set; }

    /// <summary>Version de l'interface de programmation exposée par PawnIOLib ("2.0"), à ne pas confondre
    /// avec celle du produit : pawnio_version renvoie l'API, qui bouge bien plus rarement.</summary>
    public static string? ApiVersion { get; private set; }

    /// <summary>Vrai si PawnIOLib est présent et chargeable dans ce processus.</summary>
    public static bool IsInstalled
    {
        get
        {
            EnsureProbed();
            return _library != IntPtr.Zero;
        }
    }

    /// <summary>Raison lisible de l'indisponibilité du pilote, ou null s'il est disponible.</summary>
    public static string? UnavailableReason
    {
        get
        {
            EnsureProbed();
            return _library != IntPtr.Zero ? null : _loadError;
        }
    }

    /// <summary>Ouvre la page de téléchargement dans le navigateur par défaut. Partagé entre l'onglet
    /// réglages CPU et le diagnostic de compatibilité, qui proposent tous les deux d'installer le pilote.</summary>
    public static bool TryOpenDownloadPage(out string? error)
    {
        try
        {
            Process.Start(new ProcessStartInfo(DownloadUrl) { UseShellExecute = true });
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = $"Impossible d'ouvrir le navigateur. L'adresse est : {DownloadUrl} ({ex.Message})";
            return false;
        }
    }

    private static void EnsureProbed()
    {
        lock (LoadLock)
        {
            if (_probed) return;
            _probed = true;

            if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            {
                _loadError = "PawnIO n'existe que pour les processeurs x64.";
                return;
            }

            string? path = FindLibraryPath();
            if (path is null)
            {
                _loadError = "Le pilote PawnIO n'est pas installé.";
                return;
            }

            if (!NativeLibrary.TryLoad(path, out IntPtr library))
            {
                _loadError = "Le pilote PawnIO est installé mais sa bibliothèque n'a pas pu être chargée.";
                return;
            }

            _library = library;

            if (!TryResolveExports())
            {
                NativeLibrary.Free(library);
                _library = IntPtr.Zero;
                _loadError = "La bibliothèque PawnIO installée n'expose pas les points d'entrée attendus " +
                             "(version trop ancienne ?). Réinstaller PawnIO depuis pawnio.eu.";
                return;
            }

            ApiVersion = ReadApiVersion();
            Version ??= ApiVersion;
        }
    }

    // Points d'entrée résolus une seule fois, à l'ouverture de la bibliothèque. Sans ce cache, chaque
    // appel au pilote — donc chaque lecture de MSR, plusieurs par seconde sur le chemin de lecture des
    // limites de puissance — refaisait un NativeLibrary.GetExport suivi d'un
    // Marshal.GetDelegateForFunctionPointer.
    private static PawnIoOpen? _open;
    private static PawnIoLoad? _load;
    private static PawnIoExecute? _execute;
    private static PawnIoClose? _close;

    /// <summary>Point d'entrée d'exécution, null tant que la bibliothèque n'est pas chargée.</summary>
    internal static PawnIoExecute? Execute => _execute;

    /// <summary>Point d'entrée de fermeture, null tant que la bibliothèque n'est pas chargée.</summary>
    internal static PawnIoClose? Close => _close;

    /// <summary>Résout tous les points d'entrée d'un coup. Un PawnIO plus ancien pourrait ne pas les
    /// exporter tous : l'absence doit rester une indisponibilité annoncée, pas une exception au premier
    /// appel matériel.</summary>
    private static bool TryResolveExports()
    {
        try
        {
            _open = GetExport<PawnIoOpen>("pawnio_open");
            _load = GetExport<PawnIoLoad>("pawnio_load");
            _execute = GetExport<PawnIoExecute>("pawnio_execute");
            _close = GetExport<PawnIoClose>("pawnio_close");
            return true;
        }
        catch
        {
            _open = null;
            _load = null;
            _execute = null;
            _close = null;
            return false;
        }
    }

    private static string? FindLibraryPath()
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(UninstallKey);

            // La version du produit ne se lit que là : la bibliothèque, elle, ne connaît que sa version d'API.
            // "2.2.0.0" est inscrit en quatre composants, on n'en montre que les trois qui parlent.
            if (key?.GetValue("DisplayVersion") as string is { Length: > 0 } displayVersion)
            {
                Version = string.Join('.', displayVersion.Split('.').Take(3));
            }

            if (key?.GetValue("InstallLocation") as string is { Length: > 0 } location)
            {
                string fromRegistry = Path.Combine(location, "PawnIOLib.dll");
                if (File.Exists(fromRegistry)) return fromRegistry;
            }
        }
        catch
        {
            // Clé illisible : on retombe sur l'emplacement d'installation par défaut.
        }

        string fallback = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PawnIO", "PawnIOLib.dll");
        return File.Exists(fallback) ? fallback : null;
    }

    /// <summary>Version d'API, encodée (majeure &lt;&lt; 16) | (mineure &lt;&lt; 8) | correctif.</summary>
    private static string? ReadApiVersion()
    {
        try
        {
            var version = GetExport<PawnIoVersion>("pawnio_version");
            if (version(out uint raw) != 0) return null;
            return $"{(raw >> 16) & 0xFF}.{(raw >> 8) & 0xFF}.{raw & 0xFF}";
        }
        catch
        {
            return null;
        }
    }

    internal static TDelegate GetExport<TDelegate>(string name) where TDelegate : Delegate
    {
        IntPtr address = NativeLibrary.GetExport(_library, name);
        return Marshal.GetDelegateForFunctionPointer<TDelegate>(address);
    }

    /// <summary>
    /// Charge un module PawnIO depuis les ressources de LibreHardwareMonitorLib. Retourne null — avec une
    /// raison affichable — si le pilote est absent, si la ressource n'existe pas dans cette version de la
    /// librairie, ou si le module refuse la machine (un module Intel sur un AMD, par exemple).
    /// </summary>
    public static PawnIoModule? TryLoadModule(string moduleName, out string? error)
    {
        EnsureProbed();

        if (_library == IntPtr.Zero)
        {
            error = _loadError;
            return null;
        }

        byte[]? blob = ReadModuleBlob(moduleName);
        if (blob is null)
        {
            error = $"Le module {moduleName} est absent de cette version de LibreHardwareMonitorLib.";
            return null;
        }

        int hr = _open!(out IntPtr handle);
        if (hr != 0 || handle == IntPtr.Zero)
        {
            error = "Ouverture du pilote PawnIO refusée (app lancée sans les droits administrateur ?).";
            return null;
        }

        hr = _load!(handle, blob, (nuint)blob.Length);
        if (hr != 0)
        {
            _close!(handle);
            error = $"Le module {moduleName} a refusé cette machine ({PawnIoModule.DescribeError(hr)}).";
            return null;
        }

        error = null;
        return new PawnIoModule(handle, moduleName);
    }

    /// <summary>Les modules sont embarqués dans LibreHardwareMonitorLib sous "Resources.PawnIo.&lt;nom&gt;.bin".</summary>
    private static byte[]? ReadModuleBlob(string moduleName)
    {
        try
        {
            System.Reflection.Assembly assembly = typeof(LibreHardwareMonitor.Hardware.Computer).Assembly;
            using Stream? stream = assembly.GetManifestResourceStream(
                $"LibreHardwareMonitor.Resources.PawnIo.{moduleName}.bin");
            if (stream is null) return null;

            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }
        catch
        {
            return null;
        }
    }

    internal delegate int PawnIoVersion(out uint version);
    internal delegate int PawnIoOpen(out IntPtr handle);
    internal delegate int PawnIoLoad(IntPtr handle, byte[] blob, nuint size);
    internal delegate int PawnIoExecute(
        IntPtr handle,
        [MarshalAs(UnmanagedType.LPStr)] string name,
        ulong[] input, nuint inputSize,
        ulong[] output, nuint outputSize,
        out nuint returnSize);
    internal delegate int PawnIoClose(IntPtr handle);
}

/// <summary>
/// Un module PawnIO chargé. Toutes les fonctions du module passent par <see cref="TryExecute"/>, qui ne
/// lève jamais : un appel refusé remonte en false avec son code, parce qu'un refus est une information à
/// afficher ("ce registre n'est pas modifiable sur ton PC"), pas une erreur à faire remonter.
/// </summary>
public sealed class PawnIoModule : IDisposable
{
    private readonly object _lock = new();
    private IntPtr _handle;

    public string Name { get; }

    /// <summary>Code de retour du dernier appel, pour expliquer un refus à l'utilisateur.</summary>
    public int LastError { get; private set; }

    internal PawnIoModule(IntPtr handle, string name)
    {
        _handle = handle;
        Name = name;
    }

    private static readonly ulong[] NoValues = [];

    public bool TryExecute(string function, ulong[] input, int outputCount, out ulong[] output)
    {
        output = outputCount > 0 ? new ulong[outputCount] : NoValues;

        lock (_lock)
        {
            if (_handle == IntPtr.Zero)
            {
                LastError = unchecked((int)0x80004005); // E_FAIL : module déjà libéré.
                return false;
            }

            if (PawnIoDriver.Execute is not { } execute)
            {
                LastError = unchecked((int)0x80004005); // E_FAIL : bibliothèque non chargée.
                return false;
            }

            try
            {
                LastError = execute(
                    _handle, function,
                    input, (nuint)input.Length,
                    output, (nuint)output.Length,
                    out _);
                return LastError == 0;
            }
            catch (Exception ex)
            {
                LastError = ex.HResult;
                return false;
            }
        }
    }

    /// <summary>Traduit un code d'erreur PawnIO. Les modules renvoient des NTSTATUS, que PawnIO convertit en
    /// HRESULT en forçant le bit 0x10000000 : 0xC0000022 (accès refusé) devient donc 0xD0000022.</summary>
    public static string DescribeError(int hresult) => (uint)hresult switch
    {
        0xD0000022 => "écriture refusée par le module PawnIO",
        0xD00000BB => "non supporté sur ce processeur",
        0xD000000D => "paramètre invalide",
        0xD0000001 => "le module a échoué",
        0xD00000C1 => "appel inconnu du module",
        _ => $"code 0x{(uint)hresult:X8}",
    };

    public string DescribeLastError() => DescribeError(LastError);

    public void Dispose()
    {
        lock (_lock)
        {
            if (_handle == IntPtr.Zero) return;

            try { PawnIoDriver.Close?.Invoke(_handle); }
            catch { /* best-effort : le pilote se libère de toute façon à la fin du processus */ }

            _handle = IntPtr.Zero;
        }
    }
}
