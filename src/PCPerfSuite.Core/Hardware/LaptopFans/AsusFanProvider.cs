using System.Management;

namespace PCPerfSuite.Core.Hardware.LaptopFans;

/// <summary>
/// Portables ASUS (ROG, TUF, Zephyrus, Vivobook...) : méthode DSTS de l'interface ACPI "ATK" (classe WMI
/// AsusAtkWmi_WMNB), celle qu'interroge Armoury Crate. Identifiants et décodage repris du pilote Linux
/// asus-wmi et de G-Helper.
/// </summary>
internal sealed class AsusFanProvider : ILaptopFanProvider, IDisposable
{
    private const uint CpuFan = 0x00110013;
    private const uint GpuFan = 0x00110014;
    private const uint MidFan = 0x00110031;

    /// <summary>Au-delà, la valeur n'est pas une vitesse (G-Helper : "INADEQUATE_MAX").</summary>
    private const int MaxPlausibleLevel = 104;

    private static readonly (uint Id, string Key, string Name, LaptopFanRole Role)[] Fans =
    {
        (CpuFan, "cpu", "Ventilateur CPU", LaptopFanRole.Cpu),
        (GpuFan, "gpu", "Ventilateur GPU", LaptopFanRole.Gpu),
        (MidFan, "mid", "Ventilateur central", LaptopFanRole.Other),
    };

    private readonly int[] _maxLevels;
    private ManagementObject? _atk;
    private (uint Id, string Key, string Name, LaptopFanRole Role)[] _present = Array.Empty<(uint, string, string, LaptopFanRole)>();

    public AsusFanProvider(string model)
    {
        _maxLevels = DefaultMaxLevels(model);
    }

    public string Vendor => "ASUS";
    public bool IsVerified => true;

    public bool TryDetect()
    {
        _atk = WmiMethods.FirstInstance("AsusAtkWmi_WMNB");
        if (_atk is null) return false;

        // Seuls les ventilateurs que le firmware déclare présents sont proposés (le central n'existe que sur certains modèles).
        _present = Fans.Where(fan => DecodeLevel(GetStatus(fan.Id)) is not null).ToArray();
        return _present.Length > 0;
    }

    public IReadOnlyList<LaptopFanReading> Read()
    {
        var readings = new List<LaptopFanReading>(_present.Length);
        for (int i = 0; i < _present.Length; i++)
        {
            var fan = _present[i];
            int? level = DecodeLevel(GetStatus(fan.Id));
            readings.Add(new LaptopFanReading
            {
                Key = fan.Key,
                Name = fan.Name,
                Role = fan.Role,
                Rpm = level * 100,
                Percent = level is { } l ? Percent(Array.IndexOf(Fans, fan), l) : null,
            });
        }
        return readings;
    }

    private uint GetStatus(uint deviceId)
    {
        using ManagementBaseObject result = WmiMethods.Invoke(_atk!, "DSTS", ("Device_ID", deviceId));
        return Convert.ToUInt32(result["device_status"]);
    }

    /// <summary>Vitesse en centaines de RPM, ou null si le ventilateur est absent. Le bit 0x10000 signale un
    /// périphérique présent, les 16 bits de poids faible portent la valeur (décodage de G-Helper).</summary>
    internal static int? DecodeLevel(uint status)
    {
        int raw = unchecked((int)status) - 0x10000;
        int level = raw & 0xFFFF;
        if (level > 120 || (level == 0 && raw < 0)) return null;
        return level;
    }

    /// <summary>% de la vitesse maximale : maximum connu du modèle, relevé si on l'observe dépassé (comme
    /// G-Helper). Armoury Crate calcule le sien de la même façon, d'où un écart possible de quelques %.</summary>
    private float Percent(int fanIndex, int level)
    {
        if (level > _maxLevels[fanIndex] && level <= MaxPlausibleLevel) _maxLevels[fanIndex] = level;
        return Math.Min(100f, MathF.Round(level * 100f / _maxLevels[fanIndex]));
    }

    /// <summary>Vitesse maximale (centaines de RPM) des ventilateurs CPU, GPU et central, par modèle. Relevés de
    /// G-Helper ; 58 par défaut.</summary>
    private static int[] DefaultMaxLevels(string model)
    {
        (string Model, int Cpu, int Gpu, int Mid)[] table =
        {
            ("GA401I", 78, 76, 58), ("GA401", 71, 73, 58), ("GA402", 55, 56, 58),
            ("G513R", 58, 60, 58), ("G513Q", 69, 69, 58), ("GA503", 64, 64, 58),
            ("GU603", 62, 64, 58), ("FA507R", 63, 57, 58), ("FA507X", 63, 68, 58),
            ("FX607J", 74, 72, 58), ("GX650", 62, 62, 58), ("G732", 61, 60, 58),
            ("G713", 56, 60, 58), ("Z301", 72, 64, 58), ("GV601", 78, 59, 85),
            ("GA403", 68, 68, 80), ("GU605", 62, 62, 92),
        };

        foreach (var entry in table)
        {
            if (model.Contains(entry.Model, StringComparison.OrdinalIgnoreCase)) return new[] { entry.Cpu, entry.Gpu, entry.Mid };
        }

        return new[] { 58, 58, 58 };
    }

    public void Dispose() => _atk?.Dispose();
}
