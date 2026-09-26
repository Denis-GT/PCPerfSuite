using PCPerfSuite.Core.Overlay;

namespace PCPerfSuite.Core.Hardware.Fans;

/// <summary>Un cooler de la carte graphique piloté par l'API du constructeur, avec la lecture de la
/// bibliothèque de capteurs pour le même canal (null si on ne l'a pas retrouvée).</summary>
public sealed record GpuFanPair(int CoolerId, FanReading? Reading);

/// <summary>
/// Une carte graphique arrive par deux chemins : l'API du constructeur (NVAPI, ADLX, IGCL), qui pilote ses
/// coolers, et LibreHardwareMonitor, qui les lit — et qui expose aussi des « commandes » pour le même matériel.
/// Sans rapprochement, l'onglet Ventilateurs listait chaque ventilateur deux fois (3 coolers, 6 lignes).
///
/// Quand l'API du constructeur expose des coolers, c'est elle qui pilote : les lectures LibreHardwareMonitor de
/// cette carte ne servent plus qu'à afficher la vitesse et le pourcentage de chaque cooler, et n'apparaissent pas
/// une deuxième fois comme ventilateurs pilotables. Piloter le même ventilateur par deux API à la fois serait pire
/// qu'un doublon : chacune écraserait la consigne de l'autre.
/// </summary>
public sealed class GpuFanPairing
{
    public static readonly GpuFanPairing None = new(Array.Empty<GpuFanPair>(), Array.Empty<FanReading>());

    private GpuFanPairing(IReadOnlyList<GpuFanPair> pairs, IReadOnlyList<FanReading> replaced)
    {
        Pairs = pairs;
        Replaced = replaced;
    }

    /// <summary>Un élément par cooler, par numéro croissant.</summary>
    public IReadOnlyList<GpuFanPair> Pairs { get; }

    /// <summary>Lectures de la bibliothèque de capteurs qui décrivent ces mêmes ventilateurs : à ne pas lister une
    /// deuxième fois. Toutes celles de la carte, rapprochées d'un cooler ou non — la carte est pilotée par l'API du
    /// constructeur, pas par deux chemins.</summary>
    public IReadOnlyList<FanReading> Replaced { get; }

    /// <param name="coolerIds">Coolers exposés par l'API du constructeur.</param>
    /// <param name="vendor">Marque de la carte pilotée par cette API.</param>
    /// <param name="readings">Tous les ventilateurs lus dans le relevé.</param>
    public static GpuFanPairing Pair(IReadOnlyList<int> coolerIds, GpuVendor? vendor, IReadOnlyList<FanReading> readings)
    {
        if (coolerIds.Count == 0 || vendor is null) return None;

        List<int> ids = coolerIds.Distinct().OrderBy(id => id).ToList();

        // La première carte de la même marque : l'API du constructeur pilote celle-là (dans un PC à deux cartes
        // NVIDIA, la seconde garde ses commandes de la bibliothèque de capteurs).
        string prefix = HardwarePrefix(vendor.Value);
        string? hardwareId = readings
            .Where(r => r.Group == SensorGroup.Gpu && r.HardwareId is { } id && id.StartsWith(prefix, StringComparison.Ordinal))
            .Select(r => r.HardwareId)
            .FirstOrDefault();

        if (hardwareId is null) return new GpuFanPairing(ids.Select(id => new GpuFanPair(id, null)).ToList(), Array.Empty<FanReading>());

        List<FanReading> gpuReadings = readings
            .Where(r => r.HardwareId == hardwareId)
            .OrderBy(r => r.Channel ?? int.MaxValue)
            .ToList();

        return new GpuFanPairing(MatchByChannel(ids, gpuReadings), gpuReadings);
    }

    /// <summary>Même numéro de canal des deux côtés quand c'est possible (chez NVIDIA, /gpu-nvidia/0/fan/1 est le
    /// cooler 1), sinon dans l'ordre, si les deux listes ont la même longueur. Un rapprochement incertain est
    /// abandonné : afficher la vitesse d'un autre ventilateur est pire que « -- ».</summary>
    private static List<GpuFanPair> MatchByChannel(List<int> ids, List<FanReading> readings)
    {
        bool everyIdHasChannel = ids.All(id => readings.Any(r => r.Channel == id));

        if (!everyIdHasChannel && ids.Count == readings.Count)
        {
            return ids.Select((id, i) => new GpuFanPair(id, readings[i])).ToList();
        }

        return ids.Select(id => new GpuFanPair(id, readings.FirstOrDefault(r => r.Channel == id))).ToList();
    }

    private static string HardwarePrefix(GpuVendor vendor) => vendor switch
    {
        GpuVendor.Nvidia => "/gpu-nvidia",
        GpuVendor.Amd => "/gpu-amd",
        _ => "/gpu-intel",
    };
}
