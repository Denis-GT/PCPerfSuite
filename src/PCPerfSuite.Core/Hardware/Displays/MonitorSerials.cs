using System.Management;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace PCPerfSuite.Core.Hardware.Displays;

/// <summary>
/// Numéros de série EDID des écrans, par WMI (root\wmi, WmiMonitorID), pour départager deux écrans identiques. Lecture
/// lente (WMI) : jamais sur le thread d'interface. Le numéro ne sort jamais d'ici en clair : seule son empreinte
/// (<see cref="HashSerial"/>) est rendue, enregistrée et comparée. Il n'apparaît pas non plus dans le diagnostic.
///
/// L'empreinte est un HMAC dont la clé est propre au PC (MachineGuid) : un settings.json joint à un rapport ne permet
/// ni de retrouver le numéro par force brute, ni de relier deux rapports venus de PC différents avec le même écran.
/// </summary>
public static class MonitorSerials
{
    /// <summary>Empreinte du numéro de série par chemin de périphérique moniteur. Vide si WMI ne répond pas, ou si
    /// l'écran ne donne pas de numéro (fréquent : beaucoup d'écrans mettent 0 ou rien), ou si la clé du PC est
    /// illisible (mieux vaut ne pas départager que garder une empreinte sans clé).</summary>
    public static IReadOnlyDictionary<string, string> ReadHashes(IEnumerable<string> devicePaths, CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        List<string> paths = devicePaths.Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (paths.Count == 0 || MachineKey.Value is not { } key) return result;

        try
        {
            var options = new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(5), ReturnImmediately = true };
            using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT InstanceName, SerialNumberID FROM WmiMonitorID", options);
            foreach (ManagementBaseObject monitor in searcher.Get())
            {
                using (monitor)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string? instance = monitor["InstanceName"] as string;
                    string? serial = DecodeWmiString(monitor["SerialNumberID"]);
                    if (instance is null || serial is null) continue;

                    string? path = paths.FirstOrDefault(p => MatchesInstance(p, instance));
                    if (path is not null) result[path] = HashSerial(serial, key);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // WMI absent, service arrêté ou classe inconnue : on ne départagera pas deux écrans identiques.
        }

        return result;
    }

    /// <summary>
    /// Vrai si le chemin de DisplayConfig (« \\?\DISPLAY#DEL4123#5&amp;1a2b&amp;0&amp;UID4353#{e6f07b5f-…} ») et
    /// l'instance WMI (« DISPLAY\DEL4123\5&amp;1a2b&amp;0&amp;UID4353_0 ») désignent le même écran : même identifiant
    /// d'instance PnP, aux séparateurs et au suffixe près.
    /// </summary>
    public static bool MatchesInstance(string devicePath, string instanceName)
    {
        string? fromPath = InstanceFromDevicePath(devicePath);
        if (fromPath is null) return false;

        string instance = instanceName.Trim();
        int underscore = instance.LastIndexOf('_');
        if (underscore > 0 && instance[(underscore + 1)..].All(char.IsDigit)) instance = instance[..underscore];

        return string.Equals(fromPath, instance, StringComparison.OrdinalIgnoreCase);
    }

    private static string? InstanceFromDevicePath(string devicePath)
    {
        string path = devicePath.Trim();
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path[4..];

        int guid = path.IndexOf("#{", StringComparison.Ordinal);
        if (guid >= 0) path = path[..guid];

        return path.Length == 0 ? null : path.Replace('#', '\\');
    }

    /// <summary>Empreinte courte (HMAC-SHA256 à la clé du PC, 16 caractères hexadécimaux) : assez pour départager deux
    /// écrans, sans rien laisser du numéro.</summary>
    public static string HashSerial(string serial, byte[] machineKey)
    {
        byte[] hash = HMACSHA256.HashData(machineKey, Encoding.UTF8.GetBytes(serial.Trim()));
        return Convert.ToHexString(hash, 0, 8);
    }

    /// <summary>Faux si la clé du PC est illisible : aucune empreinte n'est alors calculée (le diagnostic le dit).</summary>
    public static bool HasMachineKey => MachineKey.Value is not null;

    /// <summary>
    /// Faut-il relire les numéros de série ? Oui après tout changement de configuration : un échange de câbles entre
    /// deux écrans identiques, ou un autre exemplaire du même modèle sur le même port, redonnent exactement les mêmes
    /// chemins pour d'autres écrans. Sinon, seulement si les chemins branchés ont changé depuis la dernière lecture
    /// réussie (<paramref name="lastRead"/> null : jamais lue, ou lecture vide à refaire).
    /// </summary>
    public static bool ShouldRead(IReadOnlySet<string>? lastRead, IEnumerable<string> paths, bool afterDisplayChange)
        => afterDisplayChange || lastRead is null || !lastRead.SetEquals(paths);

    /// <summary>Clé propre au PC : MachineGuid de Windows (HKLM\SOFTWARE\Microsoft\Cryptography, lisible sans
    /// administrateur), lue une fois. Null si illisible.</summary>
    private static readonly Lazy<byte[]?> MachineKey = new(() =>
    {
        try
        {
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using RegistryKey? key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            return key?.GetValue("MachineGuid") is string guid && !string.IsNullOrWhiteSpace(guid)
                ? Encoding.UTF8.GetBytes("PCPerfSuite.MonitorSerial:" + guid.Trim())
                : null;
        }
        catch
        {
            return null;
        }
    });

    /// <summary>WmiMonitorID rend ses chaînes en tableau d'entiers (un caractère par case, zéros de remplissage).
    /// Null si vide ou « 0 », valeur de remplissage de bien des écrans.</summary>
    public static string? DecodeWmiString(object? value)
    {
        if (value is not Array array) return null;

        var text = new StringBuilder();
        foreach (object? item in array)
        {
            int code = item is null ? 0 : Convert.ToInt32(item);
            if (code == 0) break;
            text.Append((char)code);
        }

        string result = text.ToString().Trim();
        return result.Length == 0 || result.All(c => c == '0') ? null : result;
    }
}
