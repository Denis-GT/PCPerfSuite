using System.Globalization;
using System.Text.Json.Serialization;

namespace PCPerfSuite.Core.Hardware.Gpu;

/// <summary>
/// Identité d'une carte graphique : sa marque, son nom et, quand le pilote les donne, ses identifiants PCI. Sert à ne
/// pas réappliquer un overclock pensé pour une autre carte, et demain à rattacher des profils (#8, #15), un GPU choisi
/// par le bench (#11, qui ajoute le LUID) ou la bascule de GPU des portables (#22).
///
/// Chaque champ sauf la marque est facultatif : un pilote ne les donne pas tous, et les réglages des versions
/// précédentes n'enregistraient que la marque. La marque est enregistrée en chaîne, lisible dans settings.json.
/// <paramref name="PciSubsystemId"/> suit le registre PCI : identifiant du sous-système dans les 16 bits hauts, son
/// fabricant (ASUS, MSI…) dans les 16 bits bas, comme le rend NVAPI.
/// </summary>
public sealed record GpuIdentity(
    [property: JsonConverter(typeof(JsonStringEnumConverter<GpuVendor>))] GpuVendor Vendor,
    string? Name = null,
    uint? PciVendorId = null,
    uint? PciDeviceId = null,
    uint? PciSubsystemId = null,
    ulong? Luid = null)
{
    /// <summary>
    /// Vrai quand <paramref name="current"/> est la carte sur laquelle <paramref name="saved"/> a été relevée : même
    /// marque, et chaque champ connu des deux côtés identique (nom sans tenir compte de la casse ni des espaces,
    /// identifiants PCI). Un champ absent d'un côté ne bloque pas. Le LUID change à chaque démarrage : il n'est pas
    /// comparé.
    /// </summary>
    public static bool Matches(GpuIdentity saved, GpuIdentity current)
    {
        if (saved.Vendor != current.Vendor) return false;

        if (saved.Name is { } savedName && current.Name is { } currentName
            && !string.Equals(NormalizeName(savedName), NormalizeName(currentName), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return SameWhenBothKnown(saved.PciVendorId, current.PciVendorId)
               && SameWhenBothKnown(saved.PciDeviceId, current.PciDeviceId)
               && SameWhenBothKnown(saved.PciSubsystemId, current.PciSubsystemId);
    }

    private static bool SameWhenBothKnown(uint? a, uint? b) => a is null || b is null || a == b;

    /// <summary>« NVIDIA  GeForce RTX 5070 Ti » et « nvidia geforce rtx 5070 ti » sont la même carte.</summary>
    public static string NormalizeName(string name)
        => string.Join(' ', name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Identifiant PCI lu en texte par un pilote (« 0x73BF », « 73BF », « 1002 ») : hexadécimal, avec ou
    /// sans préfixe. Null si illisible.</summary>
    public static uint? ParsePciId(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        string value = text.Trim();
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) value = value[2..];
        return uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint id) ? id : null;
    }

    /// <summary>Résumé pour le diagnostic : « NVIDIA GeForce RTX 5070 Ti (PCI 10DE:2C05, sous-système 1043:89F2) ».</summary>
    public string Describe()
    {
        string name = Name ?? Vendor.ToString();
        if (PciVendorId is not { } vendor || PciDeviceId is not { } device) return name;

        string ids = $"PCI {vendor:X4}:{device:X4}";
        if (PciSubsystemId is { } subsystem) ids += $", sous-système {subsystem & 0xFFFF:X4}:{subsystem >> 16:X4}";
        return $"{name} ({ids})";
    }
}
