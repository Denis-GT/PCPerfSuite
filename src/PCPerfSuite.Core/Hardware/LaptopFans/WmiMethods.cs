using System.Management;

namespace PCPerfSuite.Core.Hardware.LaptopFans;

/// <summary>Appels de méthodes WMI de l'espace root\WMI, où les constructeurs publient l'interface ACPI de leur
/// contrôleur embarqué. Les types des paramètres varient d'un BIOS à l'autre (uint8, uint32...) : les valeurs
/// sont converties d'après le type CIM que déclare la méthode plutôt que supposées.</summary>
internal static class WmiMethods
{
    public const string Namespace = @"root\WMI";

    /// <summary>Première instance de la classe, ou null si la classe n'existe pas sur cette machine.</summary>
    public static ManagementObject? FirstInstance(string className, string? where = null)
    {
        try
        {
            string query = $"SELECT * FROM {className}" + (where is null ? "" : $" WHERE {where}");
            using var searcher = new ManagementObjectSearcher(Namespace, query);
            return searcher.Get().Cast<ManagementObject>().FirstOrDefault();
        }
        catch (ManagementException)
        {
            return null;
        }
    }

    /// <summary>Nouvelle instance d'une classe de données (argument d'entrée d'une méthode).</summary>
    public static ManagementObject CreateInstance(string className)
    {
        using var managementClass = new ManagementClass(Namespace, className, null);
        return managementClass.CreateInstance();
    }

    public static ManagementBaseObject Invoke(ManagementObject target, string method, params (string Name, object Value)[] arguments)
    {
        using ManagementBaseObject parameters = target.GetMethodParameters(method);
        foreach ((string name, object value) in arguments)
        {
            Set(parameters, name, value);
        }

        return target.InvokeMethod(method, parameters, null);
    }

    /// <summary>Affecte une propriété en convertissant la valeur vers le type CIM déclaré.</summary>
    public static void Set(ManagementBaseObject target, string name, object value)
        => target[name] = ConvertTo(target.Properties[name].Type, value);

    private static object ConvertTo(CimType type, object value) => type switch
    {
        CimType.UInt8 => Convert.ToByte(value),
        CimType.SInt8 => Convert.ToSByte(value),
        CimType.UInt16 => Convert.ToUInt16(value),
        CimType.SInt16 => Convert.ToInt16(value),
        CimType.UInt32 => Convert.ToUInt32(value),
        CimType.SInt32 => Convert.ToInt32(value),
        CimType.UInt64 => Convert.ToUInt64(value),
        CimType.SInt64 => Convert.ToInt64(value),
        _ => value,
    };
}
