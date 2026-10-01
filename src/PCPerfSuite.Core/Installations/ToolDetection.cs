using Microsoft.Win32;
using PCPerfSuite.Core.Hardware.Cpu;
using PCPerfSuite.Core.Overlay;

namespace PCPerfSuite.Core.Installations;

/// <summary>Ce que l'on trouve d'un outil sur ce PC. <paramref name="ExecutablePath"/> : de quoi le lancer, null si rien
/// ne se lance (pilote) ou si l'emplacement est inconnu. <paramref name="Location"/> : dossier de l'outil portable.</summary>
public sealed record ToolInstallState(bool IsPresent, string? Version, string? ExecutablePath, bool IsPortable, string? Location = null)
{
    public static ToolInstallState Absent { get; } = new(false, null, null, false);
}

/// <summary>
/// Détection d'un outil, d'après sa définition : dossier portable de PCPerfSuite, inscription dans « Applications
/// installées », ou détection propre (PawnIO, RTSS). Registre et disque seulement, quelques millisecondes : à appeler
/// hors du thread d'interface. Ne lève jamais (règle 2) : un outil illisible est « absent ».
/// </summary>
public static class ToolDetection
{
    private static readonly string[] UninstallRoots =
    {
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
    };

    public static ToolInstallState Detect(ToolDefinition tool)
    {
        try
        {
            return tool.Detection switch
            {
                ToolDetectionKind.PortableFolder => DetectPortable(tool, ProgramDataFolder.TryGetExisting(ProgramDataFolder.ToolsFolderName, tool.Id)),
                ToolDetectionKind.UninstallEntry => DetectUninstallEntry(tool),
                ToolDetectionKind.PawnIo => DetectPawnIo(),
                ToolDetectionKind.Rtss => DetectRtss(),
                _ => tool.Delivery == ToolDelivery.BuiltIn ? DetectBuiltIn(tool) : ToolInstallState.Absent,
            };
        }
        catch
        {
            return ToolInstallState.Absent;
        }
    }

    /// <summary>Version la plus récente présente dans le dossier de l'outil (<c>Tools\&lt;id&gt;\&lt;version&gt;</c>) dont
    /// l'exe principal existe. Les dossiers de travail (« .partiel-… ») et les liens sont ignorés.</summary>
    internal static ToolInstallState DetectPortable(ToolDefinition tool, string? toolFolder)
    {
        if (toolFolder is null || tool.LaunchFile is null) return ToolInstallState.Absent;

        string? bestVersion = null;
        string? bestPath = null;
        string? bestFolder = null;
        foreach (string folder in Directory.EnumerateDirectories(toolFolder))
        {
            string version = Path.GetFileName(folder);
            if (!ToolCatalogParser.IsSafeVersion(version) || ProgramDataFolder.IsLink(folder)) continue;

            string executable = Path.Combine(folder, tool.LaunchFile);
            if (!File.Exists(executable)) continue;

            if (bestVersion is null || (ToolVersion.Compare(version, bestVersion) ?? 0) > 0)
            {
                bestVersion = version;
                bestPath = executable;
                bestFolder = folder;
            }
        }

        return bestVersion is null ? ToolInstallState.Absent : new ToolInstallState(true, bestVersion, bestPath, true, bestFolder);
    }

    private static ToolInstallState DetectUninstallEntry(ToolDefinition tool)
    {
        foreach (RegistryKey hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            foreach (string root in UninstallRoots)
            {
                using RegistryKey? uninstall = hive.OpenSubKey(root);
                if (uninstall is null) continue;

                foreach (string name in uninstall.GetSubKeyNames())
                {
                    using RegistryKey? entry = uninstall.OpenSubKey(name);
                    if (entry is null) continue;

                    string? displayName = entry.GetValue("DisplayName") as string;
                    bool matches = (tool.UninstallKeyName is { } key && name.Equals(key, StringComparison.OrdinalIgnoreCase))
                                   || (tool.UninstallDisplayName is { } prefix
                                       && displayName?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true);
                    if (!matches) continue;

                    string? version = entry.GetValue("DisplayVersion") as string;
                    string? executable = InstalledExecutable(tool, entry.GetValue("InstallLocation") as string, entry.GetValue("DisplayIcon") as string);
                    return new ToolInstallState(true, string.IsNullOrWhiteSpace(version) ? null : version.Trim(), executable, false);
                }
            }
        }

        return ToolInstallState.Absent;
    }

    /// <summary>L'exe de l'outil installé : d'après l'icône inscrite (« "C:\…\outil.exe",0 ») si c'est bien lui, sinon
    /// dans le dossier d'installation. Null quand ni l'un ni l'autre ne mène à un fichier existant.</summary>
    internal static string? InstalledExecutable(ToolDefinition tool, string? installLocation, string? displayIcon)
    {
        if (tool.InstalledExe is not { } exe) return null;

        if (PathOfIcon(displayIcon) is { } icon && string.Equals(Path.GetFileName(icon), exe, StringComparison.OrdinalIgnoreCase)
            && File.Exists(icon))
        {
            return icon;
        }

        if (!string.IsNullOrWhiteSpace(installLocation))
        {
            string candidate = Path.Combine(installLocation.Trim().Trim('"'), exe);
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    /// <summary>Chemin d'une valeur DisplayIcon, sans guillemets ni numéro d'icône.</summary>
    internal static string? PathOfIcon(string? displayIcon)
    {
        if (string.IsNullOrWhiteSpace(displayIcon)) return null;

        string value = displayIcon.Trim();
        if (value.StartsWith('"'))
        {
            int end = value.IndexOf('"', 1);
            return end > 1 ? value[1..end] : null;
        }

        int comma = value.LastIndexOf(',');
        if (comma > 0 && int.TryParse(value[(comma + 1)..].Trim(), out _)) value = value[..comma];
        return value.Trim();
    }

    private static ToolInstallState DetectPawnIo()
    {
        PawnIoInstallation installation = PawnIoDriver.ReadInstallation();
        return installation.IsOnDisk ? new ToolInstallState(true, installation.Version, null, false) : ToolInstallState.Absent;
    }

    private static ToolInstallState DetectRtss()
    {
        RtssStatus status = RtssInstallation.Detect();
        return status.IsInstalled ? new ToolInstallState(true, status.Version, status.ExecutablePath, false) : ToolInstallState.Absent;
    }

    private static ToolInstallState DetectBuiltIn(ToolDefinition tool)
    {
        if (tool.LaunchFile is null) return ToolInstallState.Absent;
        string path = Path.Combine(Environment.SystemDirectory, tool.LaunchFile);
        return File.Exists(path) ? new ToolInstallState(true, null, path, false) : ToolInstallState.Absent;
    }
}

/// <summary>Comparaison de numéros de version d'éditeurs différents (« 3.01 », « 17.1.5.0 », « 0.8.7.9547b »,
/// « 7.3.7 Beta 6 ») : par blocs de chiffres seulement, les zéros de fin ignorés (« 7.3.7.0 » = « 7.3.7 »).</summary>
public static class ToolVersion
{
    /// <summary>Négatif, nul ou positif comme <see cref="string.Compare(string, string)"/> ; null si l'un des deux n'a
    /// aucun chiffre (on ne sait pas comparer, on ne suppose rien).</summary>
    public static int? Compare(string? a, string? b)
    {
        List<long>? left = Numbers(a);
        List<long>? right = Numbers(b);
        if (left is null || right is null) return null;

        int length = Math.Max(left.Count, right.Count);
        for (int i = 0; i < length; i++)
        {
            long x = i < left.Count ? left[i] : 0;
            long y = i < right.Count ? right[i] : 0;
            if (x != y) return x < y ? -1 : 1;
        }

        return 0;
    }

    /// <summary>Vrai seulement si <paramref name="candidate"/> est sûrement plus récent que <paramref name="installed"/>.</summary>
    public static bool IsNewer(string? candidate, string? installed) => Compare(candidate, installed) > 0;

    private static List<long>? Numbers(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;

        var numbers = new List<long>();
        long current = 0;
        bool inNumber = false;
        foreach (char c in version)
        {
            if (char.IsAsciiDigit(c))
            {
                current = Math.Min(current * 10 + (c - '0'), long.MaxValue / 10);
                inNumber = true;
            }
            else if (inNumber)
            {
                numbers.Add(current);
                current = 0;
                inNumber = false;
            }
        }

        if (inNumber) numbers.Add(current);
        return numbers.Count == 0 ? null : numbers;
    }
}
