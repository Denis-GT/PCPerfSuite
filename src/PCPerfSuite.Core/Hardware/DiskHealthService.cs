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

/// <summary>
/// Vérifie l'état de santé d'un disque via l'API Windows Storage Management (root\Microsoft\Windows\Storage) —
/// le même mécanisme que "Optimiser les lecteurs"/Gestion des disques dans Windows, indépendant du
/// fabricant (SATA/NVMe/USB). Nécessite les droits administrateur (voir app.manifest) : les compteurs de
/// fiabilité (MSFT_StorageReliabilityCounter) sont refusés sans élévation.
/// </summary>
public sealed class DiskHealthService
{
    public Task<DiskHealthReport> CheckAsync(string driveName, CancellationToken ct = default)
        => Task.Run(() => Check(driveName), ct);

    private static DiskHealthReport Check(string driveName)
    {
        ManagementObject? disk;
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\Storage", "SELECT * FROM MSFT_PhysicalDisk");
            disk = FindBestMatch(searcher.Get(), driveName, "FriendlyName");
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
    /// Corrèle un nom de disque LibreHardwareMonitor (ex: "WD_BLACK SN770 2TB") avec l'entrée
    /// Windows Storage Management correspondante (ex: "NVMe WD_BLACK SN770 2TB") : les deux sources
    /// ne formatent pas le nom à l'identique (préfixe de bus en plus/en moins), donc on compare les
    /// noms normalisés (lettres/chiffres uniquement) par inclusion plutôt que par égalité stricte.
    /// </summary>
    private static ManagementObject? FindBestMatch(ManagementObjectCollection collection, string targetName, string nameProperty)
    {
        string normalizedTarget = Normalize(targetName);
        if (normalizedTarget.Length == 0) return null;

        ManagementObject? best = null;
        int bestScore = -1;

        foreach (ManagementBaseObject item in collection)
        {
            var candidate = (ManagementObject)item;
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

    private static string Normalize(string value)
        => new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}
