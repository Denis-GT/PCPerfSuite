using System.Diagnostics;
using Microsoft.Win32;
using PCPerfSuite.Core.Installations;

namespace PCPerfSuite.Core.Overlay;

/// <summary>Ce que l'on sait de RTSS sur ce PC. <see cref="Version"/> et <see cref="ExecutablePath"/> restent
/// null quand l'installeur de RTSS ne les a pas laissés lisibles : ce n'est pas une absence de RTSS.</summary>
public sealed record RtssStatus(bool IsInstalled, string? Version, string? ExecutablePath, bool IsRunning);

/// <summary>
/// Détection de RivaTuner Statistics Server (RTSS), qui fournit les FPS et dessine l'overlay en jeu.
///
/// RTSS n'a pas de lien de téléchargement stable vers sa dernière version : guru3d.com sert le fichier par des
/// pages à jeton, et son nom change à chaque version. PCPerfSuite ne le télécharge donc pas lui-même, il
/// ouvre la page officielle (<see cref="DownloadPageUrl"/>).
///
/// « Installé » et « lancé » sont deux états distincts : les FPS ne sont lus que si RTSS tourne, mais l'app doit
/// pouvoir dire « installé, à lancer » plutôt que de redemander de l'installer.
/// </summary>
public static class RtssInstallation
{
    /// <summary>Page de téléchargement officielle de RTSS (Guru3D, qui héberge RivaTuner).</summary>
    public const string DownloadPageUrl = "https://www.guru3d.com/download/rtss-rivatuner-statistics-server-download/";

    private const string DisplayNamePrefix = "RivaTuner Statistics Server";
    private const string ProcessName = "RTSS";
    private const string FolderName = "RivaTuner Statistics Server";

    private static readonly string[] UninstallRoots =
    {
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
    };

    /// <summary>Relit l'état à chaque appel (registre, fichiers, processus), sans cache : c'est ce qui permet de
    /// voir RTSS apparaître dès que l'utilisateur revient de son installation. Ne lève jamais.</summary>
    public static RtssStatus Detect()
    {
        bool registered = false;
        string? version = null;
        var folders = new List<string>();

        try
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

                        // L'inscription de RTSS se reconnaît à son nom affiché (« RivaTuner Statistics Server 7.3.7 »)
                        // ou à sa clé, « RTSS » : MSI Afterburner l'installe sous la même clé que l'installeur autonome.
                        string? displayName = entry.GetValue("DisplayName") as string;
                        bool isRtss = displayName?.StartsWith(DisplayNamePrefix, StringComparison.OrdinalIgnoreCase) == true
                                      || name.Equals(ProcessName, StringComparison.OrdinalIgnoreCase);
                        if (!isRtss) continue;

                        registered = true;
                        if (version is null && entry.GetValue("DisplayVersion") as string is { Length: > 0 } displayVersion)
                        {
                            version = displayVersion;
                        }

                        if (entry.GetValue("InstallLocation") as string is { Length: > 0 } location) folders.Add(location);
                        if (FolderOf(entry.GetValue("UninstallString") as string) is { } fromUninstaller) folders.Add(fromUninstaller);
                    }
                }
            }
        }
        catch
        {
            // Registre illisible : on continue avec ce que les fichiers et les processus disent.
        }

        // Emplacements par défaut : ce sont ceux où RTSS s'installe, avec ou sans MSI Afterburner. Le registre ne
        // donne pas toujours de dossier (InstallLocation est vide sur bien des machines).
        folders.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), FolderName));
        folders.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), FolderName));

        bool running = false;
        string? runningPath = null;
        try
        {
            Process[] processes = Process.GetProcessesByName(ProcessName);
            running = processes.Length > 0;
            foreach (Process process in processes)
            {
                try { runningPath ??= process.MainModule?.FileName; }
                catch { /* chemin inaccessible : le processus existe, c'est l'essentiel */ }
                finally { process.Dispose(); }
            }
        }
        catch
        {
            // Liste des processus indisponible : on ne sait pas si RTSS tourne, ce qui vaut « non lancé ».
        }

        string? executable = runningPath ?? FindExecutable(folders);
        version ??= ReadFileVersion(executable);

        return new RtssStatus(registered || executable is not null || running, version, executable, running);
    }

    /// <summary>Lance RTSS. Ne lève jamais : sans chemin connu, l'utilisateur est renvoyé vers le menu Démarrer.</summary>
    public static bool TryLaunch(string? executablePath, out string? error)
    {
        if (executablePath is null || !File.Exists(executablePath))
        {
            error = "L'emplacement de RTSS est introuvable : lance-le depuis le menu Démarrer.";
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo(executablePath)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(executablePath)!,
            })?.Dispose();
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = $"RTSS n'a pas pu être lancé ({ex.Message}).";
            return false;
        }
    }

    private static string? FindExecutable(IEnumerable<string> folders)
    {
        foreach (string folder in folders)
        {
            try
            {
                string candidate = Path.Combine(folder, "RTSS.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch
            {
                // Chemin mal formé dans le registre : on passe au suivant.
            }
        }

        return null;
    }

    /// <summary>Dossier du programme de désinstallation, dont la commande est parfois entre guillemets et suivie
    /// d'arguments (« "C:\...\uninstall.exe" /S »).</summary>
    private static string? FolderOf(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;

        try
        {
            string trimmed = command.Trim();
            string executable = trimmed.StartsWith('"')
                ? trimmed[1..].Split('"')[0]
                : trimmed.Split(' ')[0];
            return Path.GetDirectoryName(executable);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Repli quand le registre n'indique pas de version. À n'utiliser qu'en dernier : la version du
    /// fichier peut retarder sur celle de l'installeur (7.3.5 dans RTSS.exe pour une inscription 7.3.7 Beta 6).</summary>
    private static string? ReadFileVersion(string? executable)
    {
        if (executable is null) return null;

        try
        {
            return FileVersionInfo.GetVersionInfo(executable).ProductVersion is { Length: > 0 } product ? product : null;
        }
        catch
        {
            return null;
        }
    }
}
