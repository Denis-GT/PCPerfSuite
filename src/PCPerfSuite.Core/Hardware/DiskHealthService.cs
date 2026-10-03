using System.Management;

namespace PCPerfSuite.Core.Hardware;

public enum DiskHealthStatus
{
    Healthy,
    Warning,
    Unhealthy,
    Unknown,
}

public sealed class DiskHealthReport
{
    public required DiskHealthStatus Status { get; init; }
    public double? TemperatureC { get; init; }
    public double? WearPercent { get; init; }
    public ulong? PowerOnHours { get; init; }
    public ulong? ReadErrorsTotal { get; init; }
    public ulong? WriteErrorsTotal { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>Disque physique vu par Windows Storage Management (MSFT_PhysicalDisk) : bus et média en codes bruts, avec
/// leurs libellés (<see cref="PhysicalDiskLabels"/>). Null = non fourni.</summary>
public sealed record PhysicalDiskInfo(string DeviceId, string? FriendlyName, int? BusType, int? MediaType, int? PhysicalSectorSize, int? LogicalSectorSize)
{
    public string BusTypeLabel => PhysicalDiskLabels.BusType(BusType);

    public string MediaTypeLabel => PhysicalDiskLabels.MediaType(MediaType);

    /// <summary>Vrai pour un disque à plateaux, faux pour un SSD ou une mémoire persistante, null si non dit.</summary>
    public bool? IsRotational => MediaType switch { 3 => true, 4 or 5 => false, _ => null };

    /// <summary>« NVMe SSD », « SATA HDD », « USB (média non dit) ».</summary>
    public string Describe()
    {
        string bus = BusTypeLabel;
        string media = MediaTypeLabel;
        return media == PhysicalDiskLabels.Unknown ? $"{bus} (média non dit)" : $"{bus} {media}";
    }
}

/// <summary>Libellés des codes MSFT_PhysicalDisk, en logique pure.</summary>
public static class PhysicalDiskLabels
{
    public const string Unknown = "inconnu";

    public static string BusType(int? code) => code switch
    {
        1 => "SCSI",
        2 => "ATAPI",
        3 => "ATA",
        4 => "IEEE 1394",
        5 => "SSA",
        6 => "Fibre Channel",
        7 => "USB",
        8 => "RAID",
        9 => "iSCSI",
        10 => "SAS",
        11 => "SATA",
        12 => "SD",
        13 => "MMC",
        14 => "virtuel",
        15 => "virtuel (fichier)",
        16 => "Storage Spaces",
        17 => "NVMe",
        18 => "SCM",
        19 => "UFS",
        _ => Unknown,
    };

    public static string MediaType(int? code) => code switch
    {
        3 => "HDD",
        4 => "SSD",
        5 => "SCM",
        _ => Unknown,
    };
}

/// <summary>
/// Vérifie l'état de santé d'un disque via l'API Windows Storage Management (root\Microsoft\Windows\Storage) —
/// le même mécanisme que "Optimiser les lecteurs"/Gestion des disques dans Windows, indépendant du
/// fabricant (SATA/NVMe/USB). Nécessite les droits administrateur (voir app.manifest) : les compteurs de
/// fiabilité (MSFT_StorageReliabilityCounter) sont refusés sans élévation.
/// </summary>
public sealed class DiskHealthService
{
    /// <param name="hardwareIdentifier">Identifiant LibreHardwareMonitor du disque (ex. "/nvme/0"), utilisé
    /// pour un appariement par numéro de disque physique quand le nom seul ne suffit pas ou est vide.</param>
    public Task<DiskHealthReport> CheckAsync(string driveName, string? hardwareIdentifier = null, CancellationToken ct = default)
        => Task.Run(() => Check(driveName, hardwareIdentifier), ct);

    /// <summary>Bus, type de média et secteurs du disque physique n° <paramref name="deviceNumber"/>
    /// (\\.\PhysicalDriveN = MSFT_PhysicalDisk.DeviceId hors Storage Spaces). Pour le bench (#10) et le gestionnaire de
    /// disques (#19). Lent (WMI) : hors du fil d'interface.</summary>
    public Task<PhysicalDiskInfo?> ReadPhysicalDiskInfoAsync(int deviceNumber, CancellationToken ct = default)
        => Task.Run(() => ReadPhysicalDiskInfo(deviceNumber), ct);

    /// <summary>Lecture synchrone de <see cref="ReadPhysicalDiskInfoAsync"/>. Best-effort : null si WMI ne répond pas ou ne
    /// connaît pas ce disque.</summary>
    public static PhysicalDiskInfo? ReadPhysicalDiskInfo(int deviceNumber)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage",
                $"SELECT DeviceId, FriendlyName, BusType, MediaType, PhysicalSectorSize, LogicalSectorSize FROM MSFT_PhysicalDisk WHERE DeviceId = '{deviceNumber}'");
            foreach (ManagementBaseObject item in searcher.Get())
            {
                using var disk = (ManagementObject)item;
                return new PhysicalDiskInfo(
                    System.Convert.ToString(disk["DeviceId"]) ?? deviceNumber.ToString(),
                    disk["FriendlyName"] as string,
                    TryConvertInt32(disk["BusType"]),
                    TryConvertInt32(disk["MediaType"]),
                    TryConvertInt32(disk["PhysicalSectorSize"]),
                    TryConvertInt32(disk["LogicalSectorSize"]));
            }
        }
        catch
        {
            // Best-effort : sans Windows Storage Management, le bench dit « disque physique non lu ».
        }
        return null;
    }

    private static int? TryConvertInt32(object? value)
    {
        if (value is null) return null;
        try { return System.Convert.ToInt32(value); } catch { return null; }
    }

    private static DiskHealthReport Check(string driveName, string? hardwareIdentifier)
    {
        ManagementObject? disk;
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\Storage", "SELECT * FROM MSFT_PhysicalDisk");
            disk = FindBestMatch(searcher.Get(), driveName, hardwareIdentifier, "FriendlyName");
        }
        catch (Exception ex)
        {
            return new DiskHealthReport
            {
                Status = DiskHealthStatus.Unknown,
                ErrorMessage = $"Windows Storage Management indisponible : {ex.Message}",
            };
        }

        if (disk is null)
        {
            return new DiskHealthReport
            {
                Status = DiskHealthStatus.Unknown,
                ErrorMessage = "Disque introuvable dans Windows Storage Management.",
            };
        }

        using (disk)
        {
            DiskHealthStatus status = System.Convert.ToInt32(disk["HealthStatus"]) switch
            {
                0 => DiskHealthStatus.Healthy,
                1 => DiskHealthStatus.Warning,
                2 => DiskHealthStatus.Unhealthy,
                _ => DiskHealthStatus.Unknown,
            };

            (double? temp, double? wear, ulong? hours, ulong? reads, ulong? writes) = TryReadReliabilityCounters(disk);

            return new DiskHealthReport
            {
                Status = status,
                TemperatureC = temp,
                WearPercent = wear,
                PowerOnHours = hours,
                ReadErrorsTotal = reads,
                WriteErrorsTotal = writes,
            };
        }
    }

    /// <summary>Compteurs additionnels (température, usure, heures, erreurs) : pas critiques, et refusés
    /// sur certains bus (ex: pont USB) ou si l'app n'est pas élevée — on dégrade en douceur si absent.</summary>
    private static (double?, double?, ulong?, ulong?, ulong?) TryReadReliabilityCounters(ManagementObject disk)
    {
        try
        {
            string deviceId = System.Convert.ToString(disk["DeviceId"]) ?? "";
            using var searcher = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\Storage",
                $"SELECT * FROM MSFT_StorageReliabilityCounter WHERE DeviceId = '{EscapeWmiString(deviceId)}'");

            using ManagementObjectCollection results = searcher.Get();
            foreach (ManagementBaseObject item in results)
            {
                using var counter = (ManagementObject)item;
                double? temp = TryConvertDouble(counter["Temperature"]);
                double? wear = TryConvertDouble(counter["Wear"]);
                ulong? hours = TryConvertUInt64(counter["PowerOnHours"]);
                ulong? reads = TryConvertUInt64(counter["ReadErrorsTotal"]);
                ulong? writes = TryConvertUInt64(counter["WriteErrorsTotal"]);
                return (temp, wear, hours, reads, writes);
            }
        }
        catch
        {
            // Best-effort : voir commentaire ci-dessus.
        }

        return (null, null, null, null, null);
    }

    private static double? TryConvertDouble(object? value)
    {
        if (value is null) return null;
        try { return System.Convert.ToDouble(value); } catch { return null; }
    }

    private static ulong? TryConvertUInt64(object? value)
    {
        if (value is null) return null;
        try { return System.Convert.ToUInt64(value); } catch { return null; }
    }

    private static string EscapeWmiString(string value) => value.Replace("'", "''");

    /// <summary>
    /// Corrèle un disque LibreHardwareMonitor avec l'entrée Windows Storage Management correspondante.
    /// Essaie d'abord un appariement par numéro de disque physique (fiable, et le seul qui marche quand
    /// LibreHardwareMonitor n'a pas pu lire de nom) : le dernier segment de l'identifiant LibreHardwareMonitor
    /// ("/nvme/0" -&gt; 0) est le même numéro que Windows utilise pour \\.\PhysicalDriveN, généralement identique
    /// à MSFT_PhysicalDisk.DeviceId pour un disque en accès direct (hors grappe Storage Spaces). À défaut,
    /// retombe sur une comparaison de noms normalisés (lettres/chiffres uniquement, par inclusion) : les deux
    /// sources ne formatent pas le nom à l'identique (ex: "WD_BLACK SN770 2TB" vs "NVMe WD_BLACK SN770 2TB").
    /// </summary>
    private static ManagementObject? FindBestMatch(
        ManagementObjectCollection collection, string targetName, string? hardwareIdentifier, string nameProperty)
    {
        var candidates = collection.Cast<ManagementBaseObject>().Cast<ManagementObject>().ToList();

        if (TryParsePhysicalDriveIndex(hardwareIdentifier) is { } index)
        {
            ManagementObject? byIndex = candidates.FirstOrDefault(
                c => string.Equals(c["DeviceId"]?.ToString(), index.ToString(), StringComparison.Ordinal));
            if (byIndex is not null)
            {
                foreach (ManagementObject other in candidates.Where(c => c != byIndex)) other.Dispose();
                return byIndex;
            }
        }

        string normalizedTarget = Normalize(targetName);
        if (normalizedTarget.Length == 0)
        {
            foreach (ManagementObject c in candidates) c.Dispose();
            return null;
        }

        ManagementObject? best = null;
        int bestScore = -1;

        foreach (ManagementObject candidate in candidates)
        {
            string? name = candidate[nameProperty] as string;
            if (string.IsNullOrWhiteSpace(name))
            {
                candidate.Dispose();
                continue;
            }

            string normalizedCandidate = Normalize(name);
            bool matches = normalizedCandidate == normalizedTarget
                           || normalizedCandidate.Contains(normalizedTarget)
                           || normalizedTarget.Contains(normalizedCandidate);

            if (!matches)
            {
                candidate.Dispose();
                continue;
            }

            int score = System.Math.Min(normalizedCandidate.Length, normalizedTarget.Length);
            if (score > bestScore)
            {
                best?.Dispose();
                best = candidate;
                bestScore = score;
            }
            else
            {
                candidate.Dispose();
            }
        }

        return best;
    }

    private static int? TryParsePhysicalDriveIndex(string? hardwareIdentifier)
    {
        if (string.IsNullOrEmpty(hardwareIdentifier)) return null;

        int lastSlash = hardwareIdentifier.LastIndexOf('/');
        string tail = lastSlash >= 0 ? hardwareIdentifier[(lastSlash + 1)..] : hardwareIdentifier;
        return int.TryParse(tail, out int index) ? index : null;
    }

    private static string Normalize(string value)
        => new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}
