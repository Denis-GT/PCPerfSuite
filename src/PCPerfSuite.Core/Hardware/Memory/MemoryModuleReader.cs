using System.Globalization;
using System.Management;

namespace PCPerfSuite.Core.Hardware.Memory;

/// <summary>
/// Fiche matérielle de la mémoire — type, fréquence, barrettes, emplacements — lue une seule fois via WMI,
/// comme le fait la page « Mémoire » du Gestionnaire des tâches. Ces données viennent de la table SMBIOS du
/// BIOS : elles ne changent pas tant que la machine tourne, il serait absurde de les relire à chaque relevé.
///
/// Best-effort de bout en bout : tout ce que ce BIOS ne publie pas reste null, et le motif de l'échec est
/// conservé pour être affiché plutôt que de laisser une carte vide sans explication. WMI est volontairement
/// préféré à une lecture directe du SPD sur le SMBus : celle-ci dépend du chipset et échoue sur beaucoup de
/// mini-PC et de portables, alors que Win32_PhysicalMemory répond partout où le BIOS remplit sa table.
/// https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-physicalmemory
/// </summary>
internal static class MemoryModuleReader
{
    public sealed class Report
    {
        public IReadOnlyList<MemoryModuleInfo> Modules { get; init; } = Array.Empty<MemoryModuleInfo>();
        public int? SlotCount { get; init; }

        /// <summary>Null quand la fiche a été lue ; sinon, ce qu'il faut afficher à sa place.</summary>
        public string? UnavailableReason { get; init; }

        public bool HasModules => Modules.Count > 0;
    }

    private static readonly Lazy<Report> CurrentLazy = new(Read, LazyThreadSafetyMode.ExecutionAndPublication);

    public static Report Current => CurrentLazy.Value;

    private static Report Read()
    {
        List<MemoryModuleInfo> modules;

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Capacity, Speed, ConfiguredClockSpeed, Manufacturer, PartNumber, DeviceLocator, "
                + "BankLabel, SMBIOSMemoryType, MemoryType, FormFactor FROM Win32_PhysicalMemory");

            modules = searcher.Get().Cast<ManagementBaseObject>().Select(ReadModule).ToList();
        }
        catch (Exception ex)
        {
            return new Report
            {
                UnavailableReason = $"Windows n'a pas pu décrire les barrettes de ce PC (WMI) : {ex.Message}",
            };
        }

        if (modules.Count == 0)
        {
            // Cas réel et non exceptionnel : beaucoup de portables et de mini-PC à mémoire soudée laissent
            // la table SMBIOS des barrettes vide. Les valeurs d'utilisation, elles, restent disponibles.
            return new Report
            {
                SlotCount = ReadSlotCount(),
                UnavailableReason = "Le BIOS de ce PC ne décrit aucune barrette : mémoire soudée sur la carte, "
                                    + "ou table SMBIOS incomplète. L'utilisation de la mémoire reste mesurée normalement.",
            };
        }

        return new Report { Modules = modules, SlotCount = ReadSlotCount() };
    }

    private static MemoryModuleInfo ReadModule(ManagementBaseObject memory)
    {
        using (memory)
        {
            // ConfiguredClockSpeed est la fréquence réelle après application du profil XMP/EXPO, celle
            // qu'affiche le Gestionnaire des tâches ; Speed est la fréquence nominale de la barrette. Les
            // deux manquent sur certains BIOS, d'où la chaîne de replis.
            int? speed = ToInt(memory, "ConfiguredClockSpeed") ?? ToInt(memory, "Speed");

            return new MemoryModuleInfo
            {
                Slot = Text(memory, "DeviceLocator"),
                BankLabel = Text(memory, "BankLabel"),
                CapacityGb = ToUlong(memory, "Capacity") is { } bytes && bytes > 0
                    ? bytes / (1024d * 1024d * 1024d)
                    : null,
                SpeedMhz = speed > 0 ? speed : null,
                // SMBIOSMemoryType est le code de la table SMBIOS, le seul fiable ; MemoryType est
                // l'ancienne énumération de WMI, figée depuis des années et qui rend 0 sur tout ce qui est
                // postérieur à la DDR3. On la garde en repli, elle vaut mieux que rien sur un vieux PC.
                TypeLabel = MemoryTypeLabel(ToInt(memory, "SMBIOSMemoryType"), ToInt(memory, "MemoryType")),
                FormFactorLabel = FormFactorLabel(ToInt(memory, "FormFactor")),
                Manufacturer = Text(memory, "Manufacturer"),
                PartNumber = Text(memory, "PartNumber"),
            };
        }
    }

    /// <summary>Nombre d'emplacements de la carte, barrettes absentes comprises : c'est ce qui permet
    /// d'afficher « 1 barrette sur 2 emplacements » et donc de dire qu'il reste de la place.</summary>
    private static int? ReadSlotCount()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT MemoryDevices FROM Win32_PhysicalMemoryArray");
            int total = 0;
            foreach (ManagementBaseObject array in searcher.Get())
            {
                using (array)
                {
                    total += ToInt(array, "MemoryDevices") ?? 0;
                }
            }
            return total > 0 ? total : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Codes de la table SMBIOS, champ « Memory Type » du type 17. Un code absent de cette table
    /// est rendu tel quel : un « Type 42 » informe, un vide n'informe pas.</summary>
    private static string? MemoryTypeLabel(int? smbiosType, int? legacyType)
    {
        string? label = smbiosType switch
        {
            null or 0 or 1 or 2 => null,
            20 => "DDR",
            21 => "DDR2",
            24 => "DDR3",
            26 => "DDR4",
            34 => "DDR5",
            30 => "LPDDR4",
            35 => "LPDDR5",
            27 => "LPDDR",
            28 => "LPDDR2",
            29 => "LPDDR3",
            _ => $"Type {smbiosType}",
        };

        if (label is not null) return label;

        return legacyType switch
        {
            20 => "DDR",
            21 => "DDR2",
            24 => "DDR3",
            _ => null,
        };
    }

    /// <summary>Codes « Form Factor » du type 17 de la table SMBIOS.</summary>
    private static string? FormFactorLabel(int? formFactor) => formFactor switch
    {
        8 => "DIMM",
        12 => "SODIMM",
        13 => "SRIMM",
        // « Row of chips » : les puces soudées directement sur la carte, sans emplacement.
        15 => "Soudée sur la carte",
        _ => null,
    };

    private static string? Text(ManagementBaseObject source, string property)
    {
        try
        {
            string? value = source[property]?.ToString()?.Trim();
            // Beaucoup de BIOS remplissent ces champs de zéros, d'espaces ou d'un « Unknown » littéral :
            // autant de valeurs qui ne veulent rien dire et qu'il vaut mieux traiter comme absentes.
            if (value is null or "" || value.All(c => c == '0')) return null;
            return value.Equals("Unknown", StringComparison.OrdinalIgnoreCase) ? null : value;
        }
        catch (ManagementException)
        {
            return null;
        }
    }

    private static int? ToInt(ManagementBaseObject source, string property)
    {
        try
        {
            return source[property] is { } value
                ? Convert.ToInt32(value, CultureInfo.InvariantCulture)
                : null;
        }
        catch (Exception)
        {
            // ManagementException (propriété absente de ce fournisseur), FormatException, OverflowException.
            return null;
        }
    }

    private static ulong? ToUlong(ManagementBaseObject source, string property)
    {
        try
        {
            return source[property] is { } value
                ? Convert.ToUInt64(value, CultureInfo.InvariantCulture)
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
