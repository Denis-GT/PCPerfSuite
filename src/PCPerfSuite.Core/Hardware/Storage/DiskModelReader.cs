using System.Management;

namespace PCPerfSuite.Core.Hardware.Storage;

/// <summary>
/// Repli pour le nom de modèle d'un disque quand LibreHardwareMonitor ne le fournit pas (chaîne vide :
/// arrive avec certains contrôleurs NVMe/ponts dont l'IDENTIFY échoue alors que la lecture SMART, elle,
/// réussit). Lu une seule fois via WMI (Win32_DiskDrive), indexé par le même numéro que
/// \\.\PhysicalDriveN — c'est ce numéro que LibreHardwareMonitor expose dans IHardware.Identifier
/// (ex. "/nvme/0"), donc pas besoin d'un matching flou par nom ici.
/// </summary>
internal static class DiskModelReader
{
    private static readonly Lazy<IReadOnlyDictionary<int, string>> ModelsByIndexLazy =
        new(Read, LazyThreadSafetyMode.ExecutionAndPublication);

    public static IReadOnlyDictionary<int, string> ModelsByIndex => ModelsByIndexLazy.Value;

    private static IReadOnlyDictionary<int, string> Read()
    {
        var models = new Dictionary<int, string>();

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Index, Model FROM Win32_DiskDrive");
            foreach (ManagementBaseObject item in searcher.Get())
            {
                using var disk = (ManagementObject)item;
                if (disk["Index"] is not { } indexValue) continue;

                string? model = disk["Model"]?.ToString()?.Trim();
                if (string.IsNullOrEmpty(model)) continue;

                models[Convert.ToInt32(indexValue)] = model;
            }
        }
        catch (Exception)
        {
            // Best-effort : si WMI échoue, HardwareMonitorService garde son propre nom de repli.
        }

        return models;
    }
}
