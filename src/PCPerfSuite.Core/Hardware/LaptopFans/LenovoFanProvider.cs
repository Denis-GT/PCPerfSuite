using System.Management;

namespace PCPerfSuite.Core.Hardware.LaptopFans;

/// <summary>
/// Portables Lenovo Legion / LOQ / IdeaPad Gaming : méthode Fan_GetCurrentFanSpeed de la classe WMI
/// LENOVO_FAN_METHOD (celle de Legion Zone), qui renvoie directement des RPM. La vitesse maximale vient de
/// LENOVO_FAN_TABLE_DATA. Identifiants repris de Lenovo Legion Toolkit (SensorsControllerV2).
/// </summary>
internal sealed class LenovoFanProvider : ILaptopFanProvider, IDisposable
{
    private static readonly (int FanId, int SensorId, string Key, string Name, LaptopFanRole Role)[] Fans =
    {
        (0, 3, "cpu", "Ventilateur CPU", LaptopFanRole.Cpu),
        (1, 4, "gpu", "Ventilateur GPU", LaptopFanRole.Gpu),
    };

    private ManagementObject? _fanMethod;
    private readonly Dictionary<int, float> _maxRpm = new();

    public string Vendor => "Lenovo";
    public bool IsVerified => false;

    public bool TryDetect()
    {
        _fanMethod = WmiMethods.FirstInstance("LENOVO_FAN_METHOD");
        if (_fanMethod is null) return false;

        // Une première lecture valide la méthode (absente des IdeaPad/ThinkPad qui n'ont que la classe).
        GetRpm(Fans[0].FanId);

        foreach (var fan in Fans)
        {
            using ManagementObject? table = WmiMethods.FirstInstance("LENOVO_FAN_TABLE_DATA", $"Sensor_ID = {fan.SensorId} AND Fan_Id = {fan.FanId}");
            if (table?["CurrentFanMaxSpeed"] is { } max && Convert.ToSingle(max) > 0) _maxRpm[fan.FanId] = Convert.ToSingle(max);
        }

        return true;
    }

    public IReadOnlyList<LaptopFanReading> Read()
        => Fans.Select(fan =>
        {
            float rpm = GetRpm(fan.FanId);
            return new LaptopFanReading
            {
                Key = fan.Key,
                Name = fan.Name,
                Role = fan.Role,
                Rpm = rpm,
                Percent = _maxRpm.TryGetValue(fan.FanId, out float max) ? Math.Min(100f, MathF.Round(rpm * 100f / max)) : null,
            };
        }).ToList();

    private float GetRpm(int fanId)
    {
        // Particularité du BIOS Lenovo : la valeur revient dans "CurrentFanSpeed" et non dans "Data".
        using ManagementBaseObject result = WmiMethods.Invoke(_fanMethod!, "Fan_GetCurrentFanSpeed", ("FanID", fanId));
        return Convert.ToSingle(result["CurrentFanSpeed"]);
    }

    public void Dispose() => _fanMethod?.Dispose();
}
