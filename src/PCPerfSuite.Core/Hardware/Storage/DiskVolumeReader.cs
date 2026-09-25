using System.Management;
using System.Text.RegularExpressions;

namespace PCPerfSuite.Core.Hardware.Storage;

/// <summary>
/// Lettres de lecteur et noms de volume (« C: », « SSD Jeux ») de chaque disque physique, lus via WMI :
/// Win32_LogicalDiskToPartition relie une lettre à une partition (« Disk #1, Partition #2 »), et le numéro
/// de disque est le même que LibreHardwareMonitor place dans son identifiant (voir <see cref="DiskModelReader"/>).
///
/// Contrairement au modèle du disque, cette information CHANGE pendant la session — clé USB branchée,
/// volume renommé — donc elle n'est pas figée dans un Lazy. Mais la requête WMI coûte plusieurs dizaines de
/// millisecondes et le monitoring lit les disques chaque seconde : le résultat est mis en cache, et rafraîchi
/// en arrière-plan quand il a plus de <see cref="RefreshAfter"/>. Le tout premier appel, lui, attend le
/// résultat pour que la première image affichée soit déjà complète.
///
/// Best-effort : sur échec de WMI, la liste est vide et rien ne lève.
/// </summary>
internal static class DiskVolumeReader
{
    private static readonly TimeSpan RefreshAfter = TimeSpan.FromSeconds(30);

    private static readonly Regex PartitionDisk = new(@"Disk #(\d+),", RegexOptions.Compiled);
    private static readonly Regex DriveLetter = new(@"DeviceID=""([A-Za-z]:)""", RegexOptions.Compiled);

    private static readonly object Gate = new();
    private static IReadOnlyDictionary<int, IReadOnlyList<DiskVolume>> _cache =
        new Dictionary<int, IReadOnlyList<DiskVolume>>();
    private static DateTime _readAtUtc = DateTime.MinValue;
    private static bool _refreshing;

    /// <summary>Message de la dernière erreur WMI, null si la dernière lecture a réussi. Pour le diagnostic.</summary>
    public static string? LastError { get; private set; }

    public static IReadOnlyList<DiskVolume> ForDisk(int diskIndex)
    {
        EnsureFresh();
        return _cache.TryGetValue(diskIndex, out IReadOnlyList<DiskVolume>? volumes)
            ? volumes
            : Array.Empty<DiskVolume>();
    }

    private static void EnsureFresh()
    {
        bool first;
        lock (Gate)
        {
            if (_refreshing) return;
            first = _readAtUtc == DateTime.MinValue;
            if (!first && DateTime.UtcNow - _readAtUtc < RefreshAfter) return;
            _refreshing = true;
        }

        if (first)
        {
            RefreshNow();
        }
        else
        {
            _ = Task.Run(RefreshNow);
        }
    }

    private static void RefreshNow()
    {
        try
        {
            IReadOnlyDictionary<int, IReadOnlyList<DiskVolume>> read = Read();
            lock (Gate) _cache = read;
        }
        finally
        {
            lock (Gate)
            {
                _readAtUtc = DateTime.UtcNow;
                _refreshing = false;
            }
        }
    }

    private static IReadOnlyDictionary<int, IReadOnlyList<DiskVolume>> Read()
    {
        var byDisk = new Dictionary<int, List<DiskVolume>>();

        try
        {
            var labels = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            using (var searcher = new ManagementObjectSearcher("SELECT DeviceID, VolumeName FROM Win32_LogicalDisk"))
            {
                foreach (ManagementBaseObject item in searcher.Get())
                {
                    using var logical = (ManagementObject)item;
                    if (logical["DeviceID"]?.ToString() is not { Length: > 0 } letter) continue;

                    string? label = logical["VolumeName"]?.ToString()?.Trim();
                    labels[letter] = string.IsNullOrEmpty(label) ? null : label;
                }
            }

            using (var searcher = new ManagementObjectSearcher("SELECT Antecedent, Dependent FROM Win32_LogicalDiskToPartition"))
            {
                foreach (ManagementBaseObject item in searcher.Get())
                {
                    using var link = (ManagementObject)item;
                    string partition = link["Antecedent"]?.ToString() ?? "";
                    string logical = link["Dependent"]?.ToString() ?? "";

                    Match disk = PartitionDisk.Match(partition);
                    Match letter = DriveLetter.Match(logical);
                    if (!disk.Success || !letter.Success) continue;

                    int index = int.Parse(disk.Groups[1].Value);
                    string driveLetter = letter.Groups[1].Value.ToUpperInvariant();

                    if (!byDisk.TryGetValue(index, out List<DiskVolume>? list))
                    {
                        list = new List<DiskVolume>();
                        byDisk[index] = list;
                    }
                    labels.TryGetValue(driveLetter, out string? label);
                    list.Add(new DiskVolume(driveLetter, label));
                }
            }

            LastError = null;
        }
        catch (Exception ex)
        {
            // Best-effort : sans WMI, le disque s'affiche simplement sans ses lettres.
            LastError = ex.Message;
        }

        return byDisk.ToDictionary(
            e => e.Key,
            e => (IReadOnlyList<DiskVolume>)e.Value.OrderBy(v => v.DriveLetter, StringComparer.Ordinal).ToList());
    }
}
