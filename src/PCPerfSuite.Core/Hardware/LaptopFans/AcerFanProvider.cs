using System.Management;

namespace PCPerfSuite.Core.Hardware.LaptopFans;

/// <summary>
/// Portables Acer Predator / Nitro : méthode GetGamingSysInfo de la classe WMI AcerGamingFunction (celle de
/// PredatorSense / NitroSense), commande 0x0001 "lecture de capteur" avec l'index du capteur dans l'octet 1
/// (0x02 : ventilateur CPU, 0x06 : ventilateur GPU). Réponse : octet 0 = statut (0 si succès), bits 8-23 = RPM.
/// Protocole repris du pilote Linux acer-wmi.
/// </summary>
internal sealed class AcerFanProvider : ILaptopFanProvider, IDisposable
{
    private const uint SensorReadingCommand = 0x0001;

    private static readonly (uint SensorId, string Key, string Name, LaptopFanRole Role)[] Fans =
    {
        (0x02, "cpu", "Ventilateur CPU", LaptopFanRole.Cpu),
        (0x06, "gpu", "Ventilateur GPU", LaptopFanRole.Gpu),
    };

    private ManagementObject? _gaming;

    public string Vendor => "Acer";
    public bool IsVerified => false;

    public bool TryDetect()
    {
        _gaming = WmiMethods.FirstInstance("AcerGamingFunction");
        return _gaming is not null && GetRpm(Fans[0].SensorId) is not null;
    }

    public IReadOnlyList<LaptopFanReading> Read()
        => Fans.Select(fan => new LaptopFanReading
        {
            Key = fan.Key,
            Name = fan.Name,
            Role = fan.Role,
            Rpm = GetRpm(fan.SensorId),
        }).ToList();

    private float? GetRpm(uint sensorId)
    {
        using ManagementBaseObject result = WmiMethods.Invoke(_gaming!, "GetGamingSysInfo",
            ("gmInput", SensorReadingCommand | (sensorId << 8)));
        return DecodeRpm(Convert.ToUInt64(result["gmOutput"]));
    }

    internal static float? DecodeRpm(ulong output)
        => (output & 0xFF) == 0 ? (float)((output >> 8) & 0xFFFF) : null;

    public void Dispose() => _gaming?.Dispose();
}
