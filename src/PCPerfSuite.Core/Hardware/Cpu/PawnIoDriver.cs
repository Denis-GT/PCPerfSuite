using System.Runtime.InteropServices;
using Microsoft.Win32;
using PCPerfSuite.Core.Installations;

namespace PCPerfSuite.Core.Hardware.Cpu;

/// <summary>Ce que l'installeur de PawnIO a laissé sur ce disque, relu à chaque demande (voir
/// <see cref="PawnIoDriver.ReadInstallation"/>). <paramref name="Version"/> est celle du produit ("2.2.0").</summary>
public readonly record struct PawnIoInstallation(string? Version, string? LibraryPath)
{
    /// <summary>Vrai si PawnIOLib.dll est présent : c'est ce fichier, et non la seule clé du registre, qui rend
    /// le pilote utilisable.</summary>
    public bool IsOnDisk => LibraryPath is not null;
}

/// <summary>
/// Accès au pilote PawnIO, le pilote signé qui remplace WinRing0 depuis LibreHardwareMonitor 0.9.5.
///
/// PawnIO n'expose pas un accès brut au matériel : il exécute des "modules" signés (des petits
/// programmes compilés) qui décident eux-mêmes de ce qu'ils autorisent. Chaque version d'un module a
/// sa propre liste blanche : l'IntelMSR de LibreHardwareMonitorLib 0.9.6 ne fait que lire les MSR,
/// celui de PawnIO.Modules 0.2.4 et suivantes écrit aussi 0x610 (PL1/PL2). Une écriture refusée revient
/// en STATUS_ACCESS_DENIED et n'est pas un bug de l'app : c'est le module qui dit non, et l'interface
/// doit l'expliquer plutôt que d'afficher une erreur brute.
///
/// Les modules viennent des ressources de LibreHardwareMonitorLib, sauf ceux que PCPerfSuite livre à
/// côté de l'exe (<see cref="ShippedPawnIoModules"/> : IntelMSR seulement), essayés en premier. PawnIO ne
/// charge que des modules signés par leur auteur : l'app ne peut pas écrire les siens, toute capacité
/// matérielle dépend des modules officiels. Les fonctions de chaque module chargé sont relevées
/// (<see cref="LoadedModules"/>) pour le diagnostic.
/// </summary>
public static class PawnIoDriver
{
    /// <summary>Page officielle du pilote, proposée à l'utilisateur quand il n'est pas installé.</summary>
    public const string DownloadUrl = "https://pawnio.eu/";

    /// <summary>Lien stable vers la dernière version de l'installeur : c'est celui du bouton « Download » de
    /// pawnio.eu, publié par l'auteur du pilote sur son dépôt GitHub officiel.</summary>
    public const string SetupUrl = "https://github.com/namazso/PawnIO.Setup/releases/latest/download/PawnIO_setup.exe";

    /// <summary>Comment télécharger et lancer l'installeur officiel. « -install -silent » est la ligne de commande
    /// qu'emploient les logiciels qui embarquent PawnIO (LibreHardwareMonitor lance « -install ») ; l'installeur
    /// est signé par « namazso », l'auteur du pilote, et PCPerfSuite refuse tout fichier qui ne l'est pas.</summary>
    public static OfficialInstallerSource SetupSource { get; } = new(
        new Uri(SetupUrl), OfficialInstaller.GitHubHosts, "PawnIO_setup.exe", "-install -silent", "namazso");

    /// <summary>Page de la dernière version publiée : sa redirection donne le numéro de version sans rien télécharger
    /// (voir <see cref="OfficialInstaller.TryGetLatestReleaseVersionAsync"/>).</summary>
    public const string LatestReleaseUrl = "https://github.com/namazso/PawnIO.Setup/releases/latest";

    /// <summary>ERROR_ALREADY_EXISTS. Depuis la 2.2.0, l'installeur rend des codes d'erreur Windows en ligne de
    /// commande, et celui-ci quand la version qu'il porte est déjà installée : il ne réinstalle pas par-dessus.</summary>
    public const int SetupAlreadyInstalledExitCode = 183;

    /// <summary>Dernière version publiée de PawnIO, null si elle n'a pas pu être lue. Ne lève jamais.</summary>
    public static Task<Version?> TryGetLatestVersionAsync(CancellationToken cancellationToken)
        => OfficialInstaller.TryGetLatestReleaseVersionAsync(new Uri(LatestReleaseUrl), OfficialInstaller.GitHubHosts, cancellationToken);

    /// <summary>Vrai si la version installée (« 2.2.0 », telle que l'inscrit l'installeur) est au moins la dernière
    /// publiée. Faux quand l'une des deux est inconnue ou illisible : dans le doute, la mise à jour est proposée.</summary>
    public static bool IsUpToDate(string? installedVersion, Version? latest)
    {
        if (latest is null || !System.Version.TryParse(installedVersion, out System.Version? installed)) return false;

        // « 2.2 » et « 2.2.0.0 » sont la même version, mais pas pour Version.CompareTo : un composant absent y vaut -1.
        return Normalize(installed) >= Normalize(latest);

        static System.Version Normalize(System.Version v)
            => new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
    }

    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO";

    private static readonly object LoadLock = new();
    private static bool _probed;
    private static bool _foundOnDiskAtProbe;
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

    /// <summary>Vrai si ce PC peut faire tourner PawnIO : il n'existe que pour les processeurs x64.</summary>
    public static bool IsSupportedPlatform => RuntimeInformation.ProcessArchitecture == Architecture.X64;

    /// <summary>Vrai si la bibliothèque était déjà sur le disque quand le pilote a été sondé, au lancement de
    /// l'app. Avec un fichier présent aujourd'hui mais <see cref="IsInstalled"/> faux, cela distingue un pilote
    /// installé depuis (il suffit de relancer l'app) d'un pilote installé mais inutilisable (trop ancien, endommagé).</summary>
    public static bool WasOnDiskAtProbe
    {
        get
        {
            EnsureProbed();
            return _foundOnDiskAtProbe;
        }
    }

    /// <summary>Relit le registre et le disque, sans passer par le sondage fait une fois au lancement. C'est ce qui
    /// permet de voir un pilote installé pendant que l'app tourne. Ne lève jamais.</summary>
    public static PawnIoInstallation ReadInstallation() => LocateInstallation();

    /// <summary>Ouvre la page officielle dans le navigateur par défaut, pour l'utilisateur qui préfère installer
    /// le pilote lui-même.</summary>
    public static bool TryOpenDownloadPage(out string? error) => ExternalLink.TryOpen(DownloadUrl, out error);

    private static void EnsureProbed()
    {
        lock (LoadLock)
        {
            if (_probed) return;
            _probed = true;

            if (!IsSupportedPlatform)
            {
                _loadError = "PawnIO n'existe que pour les processeurs x64.";
                return;
            }

            PawnIoInstallation installation = LocateInstallation();
            Version = installation.Version;

            string? path = installation.LibraryPath;
            _foundOnDiskAtProbe = path is not null;
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

    /// <summary>Sans effet de bord : ne touche à aucun état du sondage, pour pouvoir être rappelée à volonté.</summary>
    private static PawnIoInstallation LocateInstallation()
    {
        string? version = null;

        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(UninstallKey);

            // La version du produit ne se lit que là : la bibliothèque, elle, ne connaît que sa version d'API.
            // "2.2.0.0" est inscrit en quatre composants, on n'en montre que les trois qui parlent.
            if (key?.GetValue("DisplayVersion") as string is { Length: > 0 } displayVersion)
            {
                version = string.Join('.', displayVersion.Split('.').Take(3));
            }

            if (key?.GetValue("InstallLocation") as string is { Length: > 0 } location)
            {
                string fromRegistry = Path.Combine(location, "PawnIOLib.dll");
                if (File.Exists(fromRegistry)) return new PawnIoInstallation(version, fromRegistry);
            }
        }
        catch
        {
            // Clé illisible : on retombe sur l'emplacement d'installation par défaut.
        }

        try
        {
            string fallback = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PawnIO", "PawnIOLib.dll");
            return new PawnIoInstallation(version, File.Exists(fallback) ? fallback : null);
        }
        catch
        {
            return new PawnIoInstallation(version, null);
        }
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

    private static IReadOnlyList<PawnIoModuleInfo> _loadedModules = Array.Empty<PawnIoModuleInfo>();

    /// <summary>Modules chargés par l'app pendant cette session, un par nom (le dernier chargé). Instantané immuable,
    /// remplacé d'un bloc : lisible depuis n'importe quel thread, par le diagnostic notamment.</summary>
    public static IReadOnlyList<PawnIoModuleInfo> LoadedModules => Volatile.Read(ref _loadedModules);

    /// <summary>
    /// Charge un module PawnIO : celui que livre PCPerfSuite s'il existe (voir <see cref="ShippedPawnIoModules"/>),
    /// puis celui de LibreHardwareMonitorLib. Avec <paramref name="preferredFunction"/>, un module qui expose cette
    /// fonction passe avant les autres. Un module refusé par PawnIO (signature, machine) laisse sa place au suivant, et
    /// la raison est gardée dans <see cref="PawnIoModuleInfo.Note"/>. Retourne null — avec une raison affichable — si
    /// le pilote est absent, si aucun module de ce nom n'existe, ou si tous refusent la machine (un module Intel sur un
    /// AMD, par exemple).
    /// </summary>
    public static PawnIoModule? TryLoadModule(string moduleName, out string? error, string? preferredFunction = null)
    {
        PawnIoModule? module = TryLoadModuleCore(moduleName, out error, preferredFunction);
        RememberOutcome(moduleName, module is null ? error ?? "chargement refusé" : null);
        return module;
    }

    private static IReadOnlyDictionary<string, string> _loadFailures = new Dictionary<string, string>();

    /// <summary>Dernier échec de chargement par nom de module pendant cette session (effacé par un chargement réussi),
    /// pour que le diagnostic dise pourquoi un module attendu n'est pas chargé. Instantané immuable.</summary>
    public static IReadOnlyDictionary<string, string> LoadFailures => Volatile.Read(ref _loadFailures);

    private static void RememberOutcome(string moduleName, string? failure)
    {
        lock (LoadLock)
        {
            var failures = new Dictionary<string, string>(_loadFailures);
            if (failure is null) failures.Remove(moduleName);
            else failures[moduleName] = failure;
            _loadFailures = failures;
        }
    }

    private static PawnIoModule? TryLoadModuleCore(string moduleName, out string? error, string? preferredFunction)
    {
        EnsureProbed();

        if (_library == IntPtr.Zero)
        {
            error = _loadError;
            return null;
        }

        IReadOnlyList<PawnIoModuleCandidate> candidates = PawnIoModuleChoice.Order(ReadCandidates(moduleName), preferredFunction);
        if (candidates.Count == 0)
        {
            error = $"Le module {moduleName} est absent de cette version de LibreHardwareMonitorLib.";
            return null;
        }

        string? note = null;
        error = null;
        foreach (PawnIoModuleCandidate candidate in candidates)
        {
            int hr = _open!(out IntPtr handle);
            if (hr != 0 || handle == IntPtr.Zero)
            {
                error = "Ouverture du pilote PawnIO refusée (app lancée sans les droits administrateur ?).";
                return null;
            }

            hr = _load!(handle, candidate.Blob, (nuint)candidate.Blob.Length);
            if (hr == 0)
            {
                PawnIoModuleInfo info = note is null ? candidate.Info : candidate.Info with { Note = note };
                Remember(info);
                error = null;
                return new PawnIoModule(handle, info);
            }

            _close!(handle);
            string refusal = PawnIoModule.DescribeError(hr);
            error = $"Le module {moduleName} a refusé cette machine ({refusal}).";
            if (note is null && candidate.Info.Source == PawnIoModuleSource.PCPerfSuite)
            {
                note = $"Le module {moduleName} livré avec PCPerfSuite ({ShippedPawnIoModules.RelativePath(moduleName)}) " +
                       $"a été refusé par PawnIO ({refusal}).";
            }
        }

        return null;
    }

    private static void Remember(PawnIoModuleInfo info)
    {
        lock (LoadLock)
        {
            _loadedModules = _loadedModules.Where(m => m.Name != info.Name).Append(info).ToArray();
        }
    }

    /// <summary>Les modules de ce nom disponibles sur ce PC, le livré puis celui de LHM. Ne lève jamais.</summary>
    internal static IReadOnlyList<PawnIoModuleCandidate> ReadCandidates(string moduleName)
    {
        var candidates = new List<PawnIoModuleCandidate>(2);
        if (ShippedPawnIoModules.TryRead(moduleName) is { } shipped) candidates.Add(shipped);
        if (ReadLibreHardwareMonitorModule(moduleName) is { } lhm) candidates.Add(lhm);
        return candidates;
    }

    /// <summary>Version de LibreHardwareMonitorLib, en trois composants (« 0.9.6 »). Null si illisible.</summary>
    public static string? LibreHardwareMonitorVersion
    {
        get
        {
            try
            {
                System.Version? version = typeof(LibreHardwareMonitor.Hardware.Computer).Assembly.GetName().Version;
                return version is null ? null : $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}";
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>Les modules sont embarqués dans LibreHardwareMonitorLib sous "Resources.PawnIo.&lt;nom&gt;.bin".</summary>
    internal static PawnIoModuleCandidate? ReadLibreHardwareMonitorModule(string moduleName)
    {
        try
        {
            System.Reflection.Assembly assembly = typeof(LibreHardwareMonitor.Hardware.Computer).Assembly;
            using Stream? stream = assembly.GetManifestResourceStream(
                $"LibreHardwareMonitor.Resources.PawnIo.{moduleName}.bin");
            if (stream is null) return null;

            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            byte[] blob = buffer.ToArray();

            var info = new PawnIoModuleInfo(moduleName, PawnIoModuleSource.LibreHardwareMonitor, LibreHardwareMonitorVersion,
                PawnIoModuleFunctions.Parse(blob), PawnIoModuleFunctions.Sha256(blob), IsOfficialCopy: null);
            return new PawnIoModuleCandidate(info, blob);
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

    public string Name => Info.Name;

    /// <summary>Provenance, version et fonctions du module chargé.</summary>
    public PawnIoModuleInfo Info { get; }

    /// <summary>Vrai si le module expose cette fonction (« ioctl_write_msr »…).</summary>
    public bool Supports(string function) => Info.Supports(function);

    /// <summary>Code de retour du dernier appel, pour expliquer un refus à l'utilisateur.</summary>
    public int LastError { get; private set; }

    internal PawnIoModule(IntPtr handle, PawnIoModuleInfo info)
    {
        _handle = handle;
        Info = info;
    }

    private static readonly ulong[] NoValues = [];

    public bool TryExecute(string function, ulong[] input, int outputCount, out ulong[] output)
        => TryExecute(function, input, outputCount, out output, out _);

    /// <param name="returned">Nombre de valeurs réellement écrites par le module (au plus <paramref name="outputCount"/>) :
    /// <paramref name="output"/> est toujours de la taille demandée, complété de zéros. Un appel réussi qui annonce 0
    /// valeur alors qu'on en attendait compte comme complet : rien ne garantit que chaque version de PawnIO renseigne
    /// cette taille (à vérifier), et le compter vide ferait régresser les lectures qui marchaient avant.</param>
    public bool TryExecute(string function, ulong[] input, int outputCount, out ulong[] output, out int returned)
    {
        output = outputCount > 0 ? new ulong[outputCount] : NoValues;
        returned = 0;

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
                    out nuint returnSize);
                returned = returnSize == 0 && LastError == 0 ? output.Length : (int)Math.Min(returnSize, (nuint)output.Length);
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
        // Lecture comme écriture : le registre n'est pas sur la liste blanche du module.
        0xD0000022 => "accès refusé par le module PawnIO",
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
