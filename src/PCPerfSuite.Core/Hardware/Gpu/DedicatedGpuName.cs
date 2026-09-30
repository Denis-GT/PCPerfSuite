using System.Text.RegularExpressions;

namespace PCPerfSuite.Core.Hardware.Gpu;

/// <summary>
/// Reconnaît une carte graphique dédiée d'après le nom que Windows lui donne (Win32_VideoController). Sert quand
/// aucune API constructeur n'a répondu et qu'il ne reste que ce nom pour savoir quel pilote soupçonner. Un nom inconnu
/// n'est jamais pris pour une carte dédiée : un GPU intégré qu'on croirait dédié ferait réinstaller un pilote pour rien.
///
/// Les marques recyclent le nom de leurs gammes pour leurs GPU intégrés, d'où les exceptions :
/// - Intel : « Arc(TM) Graphics » et « Arc(TM) Pro Graphics » (Meteor Lake, Arrow Lake), « Arc(TM) 140V GPU » (Lunar
///   Lake), « Arc(TM) 140T GPU » (Arrow Lake-H), « Arc(TM) B390 GPU » et « Arc(TM) Pro B390 GPU » (Panther Lake) sont
///   intégrés. Seul un modèle A ou B à chiffres placé juste après « Arc » ou « Arc Pro », et pas suivi de « GPU », est
///   une carte dédiée : « Arc(TM) A770 Graphics », « Arc(TM) A370M Graphics », « Arc(TM) B580 Graphics »,
///   « Arc(TM) Pro A40 Graphics ».
/// - AMD : « Radeon(TM) Graphics », « Radeon 780M », « Radeon 890M », « Radeon 8060S » sont intégrés, comme
///   « Radeon(TM) RX Vega 11 Graphics » (Ryzen 5 2400G) malgré son « RX ». Seules les gammes RX et Pro sont dédiées.
/// - NVIDIA : pas de GPU intégré sur les PC x64 visés (D9).
/// </summary>
public static partial class DedicatedGpuName
{
    /// <summary>Marque de la carte dédiée que désigne ce nom, ou null pour un GPU intégré, virtuel ou inconnu.</summary>
    public static GpuVendor? VendorOf(string name)
    {
        if (name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || name.Contains("GeForce", StringComparison.OrdinalIgnoreCase))
            return GpuVendor.Nvidia;

        if (RadeonRxOrProRegex().IsMatch(name) && !RadeonIntegratedVegaRegex().IsMatch(name))
            return GpuVendor.Amd;

        if (ArcDiscreteModelRegex().IsMatch(name))
            return GpuVendor.Intel;

        return null;
    }

    /// <summary>Marques des cartes dédiées parmi ces noms, dans l'ordre de Windows, chacune une seule fois.</summary>
    public static IReadOnlyList<GpuVendor> VendorsOf(IEnumerable<string> names)
    {
        var vendors = new List<GpuVendor>();
        foreach (string name in names)
        {
            if (VendorOf(name) is { } vendor && !vendors.Contains(vendor)) vendors.Add(vendor);
        }

        return vendors;
    }

    /// <summary>« Radeon RX 7900 XTX », « Radeon(TM) RX 6800 », « Radeon PRO W7900 », « Radeon Pro 5500M ».</summary>
    [GeneratedRegex(@"\bRadeon(?:\s*(?:\(TM\)|™))?\s+(?:RX|Pro)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RadeonRxOrProRegex();

    /// <summary>« Radeon(TM) RX Vega 10 Graphics », « RX Vega 11 Graphics » : iGPU des Ryzen 2000 et 3000 à graphismes
    /// intégrés. Les Vega dédiées s'appellent « Radeon RX Vega » ou « RX Vega 56 », sans « Graphics ».</summary>
    [GeneratedRegex(@"\bRX\s+Vega\s+\d+\s+Graphics\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RadeonIntegratedVegaRegex();

    /// <summary>« Arc(TM) A770 Graphics », « Arc™ B580 », « Arc(TM) Pro B60 Graphics », mais pas « Arc(TM) B390 GPU ».</summary>
    [GeneratedRegex(@"\bArc(?:\s*(?:\(TM\)|™))?\s+(?:Pro\s+)?[AB]\d{2,3}M?\b(?!\s*GPU\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ArcDiscreteModelRegex();
}
