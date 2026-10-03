using System.Management;
using PCPerfSuite.Core.Compatibility;
using PCPerfSuite.Core.Hardware;

namespace PCPerfSuite.Core.Benchmark.Disk;

/// <summary>Un volume candidat au test disque, avec ce qu'on en sait et, s'il est écarté, pourquoi (règle 3).</summary>
public sealed record BenchVolume(
    string DriveLetter, string? Label, string Format, DriveType Type, long TotalBytes, long FreeBytes, bool IsSystem,
    VolumeDeviceInfo Device, PhysicalDiskInfo? Disk, bool? BitLockerOn, Unavailable? Unavailable)
{
    public bool IsEligible => Unavailable is null;

    public bool IsRemovable => Type == DriveType.Removable;

    public int SectorBytes => Device.SectorBytes;

    /// <summary>Disque à plateaux : d'après la pénalité de recherche (le plus sûr), sinon le type de média WMI.</summary>
    public bool IsRotational => Device.IncursSeekPenalty ?? Disk?.IsRotational ?? false;

    /// <summary>« C: Windows · NVMe SSD · 412 Go libres sur 931 » pour la liste déroulante.</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (IsSystem) parts.Add("système");
        if (IsRemovable) parts.Add("amovible");
        if (Disk is not null) parts.Add(Disk.Describe());
        else if (IsRotational) parts.Add("à plateaux");
        parts.Add($"{FreeBytes / Gigabyte:0} Go libres sur {TotalBytes / Gigabyte:0}");
        string name = string.IsNullOrWhiteSpace(Label) ? DriveLetter : $"{DriveLetter} {Label}";
        return $"{name} · {string.Join(" · ", parts)}";
    }

    private const double Gigabyte = 1e9;
}

/// <summary>Règles d'éligibilité d'un volume, en logique pure : local (fixe ou amovible), prêt, NTFS, ReFS ou exFAT.</summary>
public static class BenchVolumeRules
{
    public static Unavailable? Judge(DriveType type, bool isReady, string? format)
    {
        switch (type)
        {
            case DriveType.Network:
                return new Unavailable(UnavailableCause.HardwareOrDriver, "volume réseau : le test mesurerait le réseau, pas un disque");
            case DriveType.CDRom:
                return new Unavailable(UnavailableCause.HardwareOrDriver, "lecteur optique");
            case DriveType.Ram:
                return new Unavailable(UnavailableCause.HardwareOrDriver, "disque en mémoire vive");
            case DriveType.Fixed:
            case DriveType.Removable:
                break;
            default:
                return new Unavailable(UnavailableCause.HardwareOrDriver, "type de volume inconnu");
        }

        if (!isReady) return new Unavailable(UnavailableCause.HardwareOrDriver, "volume non prêt (aucun média ?)");

        string f = format?.Trim().ToUpperInvariant() ?? "";
        if (f is "NTFS" or "REFS" or "EXFAT") return null;
        if (f.StartsWith("FAT", StringComparison.Ordinal))
        {
            return new Unavailable(UnavailableCause.HardwareOrDriver, $"{format} : fichier limité à 4 Go et sans liste d'accès, non pris en charge");
        }
        return new Unavailable(UnavailableCause.UnsupportedModel, $"système de fichiers « {format} » non pris en charge");
    }

    /// <summary>Un système de fichiers qui porte des listes d'accès (le dossier de test peut y être protégé).</summary>
    public static bool SupportsAcl(string? format) => format?.Trim().ToUpperInvariant() is "NTFS" or "REFS";
}

/// <summary>
/// Inventaire des volumes pour le test disque : lettres de Windows (<see cref="DriveInfo"/>), périphérique par IOCTL,
/// disque physique par Windows Storage Management, BitLocker best-effort. Lent (WMI) : à appeler hors du fil
/// d'interface. Ne lève jamais ; un volume illisible est listé avec sa raison.
/// </summary>
public static class BenchVolumeReader
{
    public static Task<IReadOnlyList<BenchVolume>> ReadAsync(CancellationToken cancel = default) => Task.Run(Read, cancel);

    public static IReadOnlyList<BenchVolume> Read()
    {
        var volumes = new List<BenchVolume>();
        IReadOnlyDictionary<string, bool> bitLocker = ReadBitLocker();
        string systemLetter = VolumeDevice.NormalizeLetter(Path.GetPathRoot(Environment.SystemDirectory)) ?? "C:";
        var disks = new Dictionary<int, PhysicalDiskInfo?>();

        DriveInfo[] drives;
        try { drives = DriveInfo.GetDrives(); }
        catch (Exception) { return volumes; }

        foreach (DriveInfo drive in drives)
        {
            try
            {
                string? letter = VolumeDevice.NormalizeLetter(drive.Name);
                if (letter is null) continue;

                bool ready = false;
                string format = "";
                string? label = null;
                long total = 0, free = 0;
                // Un lecteur réseau, optique ou inconnu est écarté par son seul type : on n'interroge pas son média (un
                // partage injoignable bloque IsReady de longues secondes, un DVD se met à tourner).
                bool local = drive.DriveType is DriveType.Fixed or DriveType.Removable;
                try
                {
                    ready = local && drive.IsReady;
                    if (ready)
                    {
                        format = drive.DriveFormat;
                        label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? null : drive.VolumeLabel.Trim();
                        total = drive.TotalSize;
                        free = drive.AvailableFreeSpace;
                    }
                }
                catch (Exception)
                {
                    ready = false;
                }

                Unavailable? problem = BenchVolumeRules.Judge(drive.DriveType, ready, format);
                VolumeDeviceInfo device = problem is null ? VolumeDevice.Read(letter) : new VolumeDeviceInfo(null, null, null, null, "non lu : volume écarté");
                PhysicalDiskInfo? disk = null;
                if (device.DeviceNumber is { } number)
                {
                    if (!disks.TryGetValue(number, out disk))
                    {
                        disk = DiskHealthService.ReadPhysicalDiskInfo(number);
                        disks[number] = disk;
                    }
                }

                bitLocker.TryGetValue(letter, out bool encrypted);
                volumes.Add(new BenchVolume(letter, label, format, drive.DriveType, total, free, letter == systemLetter, device, disk,
                    bitLocker.ContainsKey(letter) ? encrypted : null, problem));
            }
            catch (Exception)
            {
                // Un volume qui disparaît en cours de lecture (clé retirée) : on passe au suivant.
            }
        }

        return volumes.OrderBy(v => v.IsSystem ? 0 : 1).ThenBy(v => v.DriveLetter, StringComparer.Ordinal).ToList();
    }

    /// <summary>État BitLocker par lettre (vrai = protection active). Demande l'administrateur ; vide sinon.</summary>
    private static IReadOnlyDictionary<string, bool> ReadBitLocker()
    {
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\CIMV2\Security\MicrosoftVolumeEncryption",
                "SELECT DriveLetter, ProtectionStatus FROM Win32_EncryptableVolume");
            foreach (ManagementBaseObject item in searcher.Get())
            {
                using var volume = (ManagementObject)item;
                string? letter = VolumeDevice.NormalizeLetter(volume["DriveLetter"]?.ToString());
                if (letter is null) continue;
                int status = Convert.ToInt32(volume["ProtectionStatus"]);
                if (status is 0 or 1) result[letter] = status == 1;
            }
        }
        catch (Exception)
        {
            // Sans administrateur ou sans BitLocker : rien à dire.
        }
        return result;
    }
}
