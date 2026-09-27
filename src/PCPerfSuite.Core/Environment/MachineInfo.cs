using System.Management;
using System.Runtime.InteropServices;

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

    /// <summary>Faux quand ni WMI (Win32_ComputerSystem/Win32_SystemEnclosure) ni la présence d'une
    /// batterie n'ont pu être lus : l'identité de la machine est alors inconnue. Voir
    /// <see cref="IsLaptop"/>, qui se règle par prudence sur vrai (portable) dans ce cas, pour que le
    /// contrôleur embarqué des portables (règle 5, lecture seule) ne soit jamais écrit à tort.</summary>
    public bool IsIdentityKnown { get; }

    /// <summary>Cartes graphiques vues par Windows (nom complet), y compris celles qu'aucune API de contrôle ne pilote.</summary>
    public IReadOnlyList<string> VideoControllers { get; }

    public bool HasNvidiaGpu => VideoControllers.Any(name => name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase));

    private MachineInfo(string manufacturer, string model, bool isLaptop, bool isIdentityKnown, IReadOnlyList<string> videoControllers)
    {
        Manufacturer = manufacturer;
        Model = model;
        IsLaptop = isLaptop;
        IsIdentityKnown = isIdentityKnown;
        VideoControllers = videoControllers;
    }

    private static readonly Lazy<MachineInfo> CurrentLazy = new(Read);

    public static MachineInfo Current => CurrentLazy.Value;

    private static MachineInfo Read()
    {
        string manufacturer = "";
        string model = "";
        bool isLaptop = false;
        bool identityKnown = false;

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
                    identityKnown = true;
                }
            }
        }
        catch
        {
            // WMI indisponible : identité inconnue par cette voie, on retente les deux autres signaux.
        }

        if (HasLaptopChassis(out bool chassisQuerySucceeded))
        {
            isLaptop = true;
            identityKnown = true;
        }
        else if (chassisQuerySucceeded)
        {
            identityKnown = true;
        }

        if (!isLaptop && HasSystemBattery())
        {
            // Deuxième signal indépendant de WMI : une batterie système est propre aux portables (et
            // à quelques tablettes), jamais à un PC de bureau.
            isLaptop = true;
            identityKnown = true;
        }

        // Identité inconnue : on refuse par prudence, en la traitant comme un portable, pour que
        // l'écriture sur un éventuel contrôleur embarqué (ventilateurs, alimentation) reste bloquée
        // (règle 5). Un PC de bureau mal identifié perd temporairement le contrôle GPU/CPU, ce qui est
        // sans danger ; l'inverse pourrait laisser une machine sans refroidissement.
        if (!identityKnown) isLaptop = true;

        return new MachineInfo(manufacturer, model, isLaptop, identityKnown, ReadVideoControllers());
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

    private static bool HasLaptopChassis(out bool querySucceeded)
    {
        querySucceeded = false;
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT ChassisTypes FROM Win32_SystemEnclosure");
            foreach (ManagementBaseObject enclosure in searcher.Get())
            {
                using (enclosure)
                {
                    querySucceeded = true;
                    if (enclosure["ChassisTypes"] is ushort[] types && types.Any(t => LaptopChassisTypes.Contains(t))) return true;
                }
            }
        }
        catch
        {
            // Pas de type de châssis disponible par cette voie.
        }

        return false;
    }

    /// <summary>Signal indépendant de WMI : Windows sait si une batterie système est présente même
    /// quand les requêtes WMI ci-dessus échouent (service WMI pas encore prêt à l'ouverture de
    /// session, par exemple).</summary>
    private static bool HasSystemBattery()
    {
        try
        {
            if (!GetSystemPowerStatus(out SYSTEM_POWER_STATUS status)) return false;
            // 128 = "pas de batterie système" (PC de bureau) ; 255 = statut inconnu.
            return status.BatteryFlag is not (128 or 255);
        }
        catch
        {
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);
}
