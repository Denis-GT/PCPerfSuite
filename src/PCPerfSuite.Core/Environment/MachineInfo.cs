using System.Management;
using System.Runtime.InteropServices;

namespace PCPerfSuite.Core.SystemInfo;

/// <summary>
/// Nature du châssis. L'état <see cref="Unknown"/> existe parce que l'ignorance doit se dire : confondre
/// « ce n'est pas un portable » avec « je n'ai pas réussi à savoir » revenait à traiter un portable dont
/// le dépôt WMI est cassé comme un PC de bureau, et donc à s'autoriser à écrire dans ses ventilateurs.
/// </summary>
public enum ChassisKind
{
    Unknown,
    Desktop,
    Laptop,
}

/// <summary>
/// Identité de la machine (fabricant, modèle, portable ou non), lue une seule fois via WMI. Sert à choisir
/// les sources de capteurs propres à une marque et à expliquer à l'utilisateur pourquoi une fonction n'est
/// pas disponible sur son PC.
/// </summary>
public sealed class MachineInfo
{
    public string Manufacturer { get; }
    public string Model { get; }
    public ChassisKind Chassis { get; }

    /// <summary>Portable avéré. Un châssis indéterminé répond non : cette propriété sert à décrire la
    /// machine et à tenter les sources propres aux portables, jamais à autoriser une écriture — pour cela,
    /// voir <see cref="SoftwareFanControlRefused"/>.</summary>
    public bool IsLaptop => Chassis == ChassisKind.Laptop;

    /// <summary>
    /// Règle de compatibilité 5 : le refroidissement d'un portable appartient au contrôleur embarqué du
    /// constructeur, et une écriture malheureuse peut laisser la machine sans ventilation. La question
    /// n'est donc pas « est-ce un portable ? » mais « ai-je la certitude que ce n'en est pas un ? » :
    /// seul un PC de bureau avéré autorise le pilotage logiciel. Un doute coûte une fonctionnalité sur
    /// une machine mal identifiée ; l'inverse coûterait un processeur.
    /// </summary>
    public bool SoftwareFanControlRefused => Chassis != ChassisKind.Desktop;

    /// <summary>Cartes graphiques vues par Windows (nom complet), y compris celles qu'aucune API de contrôle ne pilote.</summary>
    public IReadOnlyList<string> VideoControllers { get; }

    public bool HasNvidiaGpu => VideoControllers.Any(name => name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase));

    private MachineInfo(string manufacturer, string model, ChassisKind chassis, IReadOnlyList<string> videoControllers)
    {
        Manufacturer = manufacturer;
        Model = model;
        Chassis = chassis;
        VideoControllers = videoControllers;
    }

    private static readonly Lazy<MachineInfo> CurrentLazy = new(Read);

    public static MachineInfo Current => CurrentLazy.Value;

    private static MachineInfo Read()
    {
        string manufacturer = "";
        string model = "";
        bool? wmiSaysMobile = null;

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Manufacturer, Model, PCSystemType FROM Win32_ComputerSystem");
            foreach (ManagementBaseObject system in searcher.Get())
            {
                using (system)
                {
                    manufacturer = system["Manufacturer"]?.ToString()?.Trim() ?? "";
                    model = system["Model"]?.ToString()?.Trim() ?? "";
                    // PCSystemType 2 : "Mobile" (portable). Complété par le type de châssis et la batterie.
                    if (system["PCSystemType"] is { } type) wmiSaysMobile = Convert.ToInt32(type) == 2;
                }
            }
        }
        catch
        {
            // WMI indisponible : identité inconnue par cette voie, on retente les deux autres signaux.
        }

        return new MachineInfo(manufacturer, model, ReadChassis(wmiSaysMobile), ReadVideoControllers());
    }

    /// <summary>Croise les trois sources disponibles. Elles ne pèsent pas le même poids : un « oui » suffit
    /// à conclure au portable — aucune ne se trompe dans ce sens — alors que « PC de bureau » demande un
    /// accord. L'absence de batterie seule ne suffit pas, un portable dont la batterie a été retirée la
    /// déclare absente elle aussi ; et WMI seul ne suffit pas, certaines images OEM déclarant un type de
    /// machine fantaisiste. Sans accord, l'état reste indéterminé.</summary>
    private static ChassisKind ReadChassis(bool? wmiSaysMobile)
    {
        bool? chassisSaysLaptop = HasLaptopChassis();
        bool? hasBattery = HasBattery();

        if (wmiSaysMobile == true || chassisSaysLaptop == true || hasBattery == true) return ChassisKind.Laptop;

        bool somethingAnswered = wmiSaysMobile is not null || chassisSaysLaptop is not null;
        if (hasBattery == false && somethingAnswered) return ChassisKind.Desktop;

        return ChassisKind.Unknown;
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

    /// <summary>Null quand aucun boîtier n'a répondu — ce qui n'est pas la même chose qu'un boîtier de
    /// bureau.</summary>
    private static bool? HasLaptopChassis()
    {
        try
        {
            bool answered = false;

            using var searcher = new ManagementObjectSearcher("SELECT ChassisTypes FROM Win32_SystemEnclosure");
            foreach (ManagementBaseObject enclosure in searcher.Get())
            {
                using (enclosure)
                {
                    if (enclosure["ChassisTypes"] is not ushort[] types) continue;

                    answered = true;
                    if (types.Any(t => LaptopChassisTypes.Contains(t))) return true;
                }
            }

            return answered ? false : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Présence d'une batterie, via l'API d'alimentation de Windows : aucun privilège, aucun
    /// pilote, et surtout aucune dépendance à WMI — c'est précisément ce qu'il faut quand le dépôt WMI est
    /// cassé, le cas où toutes les autres pistes se taisent. Null quand Windows ne sait pas répondre.</summary>
    private static bool? HasBattery()
    {
        try
        {
            if (!GetSystemPowerStatus(out SYSTEM_POWER_STATUS status)) return null;

            if (status.BatteryFlag == NoSystemBattery) return false;
            if (status.BatteryFlag == UnknownBatteryStatus) return null;
            return true;
        }
        catch
        {
            // DllNotFoundException, EntryPointNotFoundException : l'app ne tourne pas sous Windows.
            return null;
        }
    }

    private const byte NoSystemBattery = 128;
    private const byte UnknownBatteryStatus = 255;

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);
}
