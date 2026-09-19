using PCPerfSuite.Core.SystemInfo;

namespace PCPerfSuite.Core.Hardware.LaptopFans;

public enum LaptopFanSupport
{
    /// <summary>PC de bureau : ses ventilateurs passent par la carte mère (LibreHardwareMonitor).</summary>
    NotLaptop,

    /// <summary>Portable d'une marque dont on ne connaît pas l'interface.</summary>
    UnsupportedVendor,

    /// <summary>Marque prise en charge, mais ce modèle n'expose pas l'interface (ou la refuse).</summary>
    InterfaceMissing,

    Active,
}

/// <summary>
/// Ventilateurs des portables, que LibreHardwareMonitor ne voit pas : ils dépendent du contrôleur embarqué du
/// constructeur, lu ici via son interface WMI (voir <see cref="ILaptopFanProvider"/>). Un seul module est tenté,
/// celui de la marque de la machine ; s'il ne répond pas, le service reste inactif sans plus rien coûter.
/// </summary>
public sealed class LaptopFanService : IDisposable
{
    private readonly ILaptopFanProvider? _provider;

    public LaptopFanSupport Support { get; }

    /// <summary>Marque reconnue (ASUS, Lenovo...), null si aucune.</summary>
    public string? Vendor => _provider?.Vendor;

    public bool IsVerified => _provider?.IsVerified ?? false;

    /// <summary>Erreur de la détection, pour le rapport de compatibilité.</summary>
    public string? DetectionError { get; }

    public LaptopFanService(MachineInfo machine)
    {
        if (!machine.IsLaptop)
        {
            Support = LaptopFanSupport.NotLaptop;
            return;
        }

        ILaptopFanProvider? candidate = CreateProvider(machine);
        if (candidate is null)
        {
            Support = LaptopFanSupport.UnsupportedVendor;
            return;
        }

        _provider = candidate;
        try
        {
            Support = candidate.TryDetect() ? LaptopFanSupport.Active : LaptopFanSupport.InterfaceMissing;
        }
        catch (Exception ex)
        {
            Support = LaptopFanSupport.InterfaceMissing;
            DetectionError = ex.Message;
        }

        if (Support != LaptopFanSupport.Active) (candidate as IDisposable)?.Dispose();
    }

    /// <summary>Ventilateurs lus, vide si le service est inactif. Une lecture en échec renvoie des vitesses nulles.</summary>
    public IReadOnlyList<LaptopFanReading> Read()
    {
        if (Support != LaptopFanSupport.Active) return Array.Empty<LaptopFanReading>();

        try
        {
            return _provider!.Read();
        }
        catch
        {
            return Array.Empty<LaptopFanReading>();
        }
    }

    /// <summary>Le module de la marque du portable. Jamais tenté sur un PC de bureau : une carte mère ASUS y
    /// déclare la même interface, mais ses ventilateurs sont déjà lus via la carte mère.</summary>
    private static ILaptopFanProvider? CreateProvider(MachineInfo machine)
    {
        string manufacturer = machine.Manufacturer;

        if (Contains(manufacturer, "ASUS")) return new AsusFanProvider(machine.Model);
        if (Contains(manufacturer, "LENOVO")) return new LenovoFanProvider();
        if (manufacturer.Equals("HP", StringComparison.OrdinalIgnoreCase) || Contains(manufacturer, "Hewlett")) return new HpFanProvider();
        if (Contains(manufacturer, "Micro-Star") || Contains(manufacturer, "MSI")) return new MsiFanProvider();
        if (Contains(manufacturer, "Acer")) return new AcerFanProvider();
        return null;
    }

    private static bool Contains(string text, string value) => text.Contains(value, StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        if (Support == LaptopFanSupport.Active) (_provider as IDisposable)?.Dispose();
    }
}
