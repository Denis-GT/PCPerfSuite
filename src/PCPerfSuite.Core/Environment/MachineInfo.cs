using System.Management;

namespace PCPerfSuite.Core.SystemInfo;

/// <summary>
/// Identité de la machine (fabricant, modèle, portable ou non), lue une seule fois via WMI. Sert à choisir
/// les sources de capteurs propres à une marque et à expliquer à l'utilisateur pourquoi une fonction n'est
/// pas disponible sur son PC.
/// </summary>
public sealed class MachineInfo
{
    public string Manufacturer { get; }
    public string Model { get; }
    public bool IsLaptop { get; }

    /// <summary>Cartes graphiques vues par Windows (nom complet), y compris celles qu'aucune API de contrôle ne pilote.</summary>
    public IReadOnlyList<string> VideoControllers { get; }

    public bool HasNvidiaGpu => VideoControllers.Any(name => name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase));

    private MachineInfo(string manufacturer, string model, bool isLaptop, IReadOnlyList<string> videoControllers)
    {
        Manufacturer = manufacturer;
        Model = model;
        IsLaptop = isLaptop;
        VideoControllers = videoControllers;
    }

    private static readonly Lazy<MachineInfo> CurrentLazy = new(Read);

    public static MachineInfo Current => CurrentLazy.Value;

    private static MachineInfo Read()
    {
        string manufacturer = "";
        string model = "";
        bool isLaptop = false;

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Manufacturer, Model, PCSystemType FROM Win32_ComputerSystem");
            foreach (ManagementBaseObject system in searcher.Get())
            {
                using (system)
                {
                    manufacturer = system["Manufacturer"]?.ToString()?.Trim() ?? "";
                    model = system["Model"]?.ToString()?.Trim() ?? "";
                    // PCSystemType 2 : "Mobile" (portable). Complété par le type de châssis ci-dessous.
                    isLaptop = system["PCSystemType"] is { } type && Convert.ToInt32(type) == 2;
                }
            }
        }
        catch
        {
            // WMI indisponible : identité inconnue, les sources propres à une marque ne seront pas tentées.
        }

        if (!isLaptop) isLaptop = HasLaptopChassis();

        return new MachineInfo(manufacturer, model, isLaptop, ReadVideoControllers());
    }

    private static IReadOnlyList<string> ReadVideoControllers()
    {
        var names = new List<string>();
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController");
            foreach (ManagementBaseObject controller in searcher.Get())
            {
                using (controller)
                {
                    if (controller["Name"]?.ToString()?.Trim() is { Length: > 0 } name) names.Add(name);
                }
            }
        }
        catch
        {
            // Liste vide : les messages retombent sur une explication générique.
        }

        return names;
    }

    /// <summary>Types de châssis SMBIOS des portables : Portable, Laptop, Notebook, Sub Notebook, Tablet,
    /// Convertible, Detachable.</summary>
    private static readonly int[] LaptopChassisTypes = { 8, 9, 10, 14, 30, 31, 32 };

    private static bool HasLaptopChassis()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT ChassisTypes FROM Win32_SystemEnclosure");
            foreach (ManagementBaseObject enclosure in searcher.Get())
            {
                using (enclosure)
                {
                    if (enclosure["ChassisTypes"] is ushort[] types && types.Any(t => LaptopChassisTypes.Contains(t))) return true;
                }
            }
        }
        catch
        {
            // Pas de type de châssis : on s'en tient à PCSystemType.
        }

        return false;
    }
}
