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
    /// <param name="hardwareIdentifier">Identifiant LibreHardwareMonitor du disque (ex. "/nvme/0"), utilisé
    /// pour un appariement par numéro de disque physique quand le nom seul ne suffit pas ou est vide.</param>
    public Task<DiskHealthReport> CheckAsync(string driveName, string? hardwareIdentifier = null, CancellationToken ct = default)
        => Task.Run(() => Check(driveName, hardwareIdentifier), ct);

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
