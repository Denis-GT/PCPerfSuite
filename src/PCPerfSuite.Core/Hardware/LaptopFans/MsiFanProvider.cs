using System.Management;

namespace PCPerfSuite.Core.Hardware.LaptopFans;

/// <summary>
/// Portables MSI : méthode Get_Fan de la classe WMI MSI_ACPI (celle de MSI Center), sous-fonction 0x00. La
/// réponse porte jusqu'à quatre compteurs 16 bits big-endian après un octet de succès ; RPM = 480000 / compteur
/// (0 : ventilateur à l'arrêt). Protocole repris du pilote Linux msi-wmi-platform. L'ordre des ventilateurs
/// n'étant pas documenté, ils sont simplement numérotés.
/// </summary>
internal sealed class MsiFanProvider : ILaptopFanProvider, IDisposable
{
    private const int FanCount = 2;

    private ManagementObject? _acpi;

    public string Vendor => "MSI";
    public bool IsVerified => false;

    public bool TryDetect()
    {
        _acpi = WmiMethods.FirstInstance("MSI_ACPI");
        return _acpi is not null && QueryFans() is not null;
    }

    public IReadOnlyList<LaptopFanReading> Read()
    {
        byte[]? bytes = QueryFans();
        return Enumerable.Range(0, FanCount).Select(i => new LaptopFanReading
        {
            Key = $"fan{i + 1}",
            Name = $"Ventilateur {i + 1}",
            Role = LaptopFanRole.Other,
            Rpm = bytes is null ? null : DecodeRpm(bytes, i),
        }).ToList();
    }

    internal static float DecodeRpm(byte[] bytes, int fanIndex)
    {
        int offset = 1 + fanIndex * 2;
        int count = (bytes[offset] << 8) | bytes[offset + 1];
        return count == 0 ? 0 : 480000f / count;
    }

    /// <summary>Octets de réponse, ou null si le firmware signale un échec (premier octet à 0).</summary>
    private byte[]? QueryFans()
    {
        using ManagementObject input = WmiMethods.CreateInstance("Package_32");
        input["Bytes"] = new byte[32];

        using ManagementBaseObject result = WmiMethods.Invoke(_acpi!, "Get_Fan", ("Data", input));
        if (result["Data"] is not ManagementBaseObject output) return null;

        using (output)
        {
            return output["Bytes"] is byte[] { Length: >= 1 + FanCount * 2 } bytes && bytes[0] != 0 ? bytes : null;
        }
    }

    public void Dispose() => _acpi?.Dispose();
}
