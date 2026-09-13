using LibreHardwareMonitor.Hardware;

namespace PCPerfSuite.Core.Hardware;

/// <summary>
/// Encapsule LibreHardwareMonitorLib pour exposer un instantané typé et
/// indépendant du fabricant (Intel/AMD, NVIDIA/AMD/Intel) plutôt que l'arbre
/// brut de capteurs. Nécessite les droits administrateur pour la plupart des
/// capteurs (le pilote WinRing0 embarqué par la lib s'installe/se charge au
/// premier accès).
/// </summary>
public sealed class HardwareMonitorService : IDisposable
{
    private readonly Computer _computer;
    private readonly UpdateVisitor _visitor = new();
    private bool _disposed;

    public HardwareMonitorService()
    {
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsMotherboardEnabled = true,
            IsStorageEnabled = true,
            IsNetworkEnabled = false,
        };

        _computer.Open();
    }

    public HardwareSnapshot GetSnapshot()
    {
        _computer.Accept(_visitor);

        var cpu = new CpuSnapshot();
        GpuSnapshot? gpu = null;
        var memory = new MemorySnapshot();
        var motherboard = new MotherboardSnapshot();
        var fans = new List<FanReading>();

        foreach (IHardware hardware in _computer.Hardware)
        {
            switch (hardware.HardwareType)
            {
                case HardwareType.Cpu:
                    cpu = ReadCpu(hardware);
                    break;

                case HardwareType.GpuNvidia:
                case HardwareType.GpuAmd:
                case HardwareType.GpuIntel:
                    gpu = ReadGpu(hardware);
                    CollectFans(hardware, fans);
                    break;

                case HardwareType.Memory:
                    memory = ReadMemory(hardware);
                    break;

                case HardwareType.Motherboard:
                    motherboard = ReadMotherboard(hardware);
                    CollectFans(hardware, fans);
                    foreach (IHardware sub in hardware.SubHardware)
                    {
                        CollectFans(sub, fans);
                    }
                    break;
            }
        }

        return new HardwareSnapshot
        {
            Cpu = cpu,
            Gpu = gpu,
            Memory = memory,
            Motherboard = motherboard,
            Fans = fans,
        };
    }

    private static CpuSnapshot ReadCpu(IHardware hardware)
    {
        float? load = FindSensor(hardware, SensorType.Load, "CPU Total")?.Value
                       ?? hardware.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Load)?.Value;

        float? package = FindSensor(hardware, SensorType.Temperature, "CPU Package")?.Value;
        float? maxCore = hardware.Sensors
            .Where(s => s.SensorType == SensorType.Temperature && s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase))
            .Select(s => s.Value)
            .Where(v => v.HasValue)
            .DefaultIfEmpty()
            .Max();

        float? power = FindSensor(hardware, SensorType.Power, "CPU Package")?.Value
                        ?? hardware.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Power)?.Value;

        float? maxClock = hardware.Sensors
            .Where(s => s.SensorType == SensorType.Clock)
            .Select(s => s.Value)
            .Where(v => v.HasValue)
            .DefaultIfEmpty()
            .Max();

        return new CpuSnapshot
        {
            Name = hardware.Name,
            LoadPercent = load,
            PackageTempC = package ?? maxCore,
            MaxCoreTempC = maxCore,
            PowerWatts = power,
            MaxClockMhz = maxClock,
        };
    }

    private static GpuSnapshot ReadGpu(IHardware hardware)
    {
        string vendor = hardware.HardwareType switch
        {
            HardwareType.GpuNvidia => "NVIDIA",
            HardwareType.GpuAmd => "AMD",
            HardwareType.GpuIntel => "Intel",
            _ => "Inconnu",
        };

        var fanSensor = hardware.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Fan);
        var fanControl = hardware.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Control);

        return new GpuSnapshot
        {
            Name = hardware.Name,
            Vendor = vendor,
            LoadPercent = FindSensor(hardware, SensorType.Load, "GPU Core")?.Value
                          ?? hardware.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Load)?.Value,
            CoreTempC = FindSensor(hardware, SensorType.Temperature, "GPU Core")?.Value,
            HotSpotTempC = FindSensor(hardware, SensorType.Temperature, "GPU Hot Spot")?.Value,
            CoreClockMhz = FindSensor(hardware, SensorType.Clock, "GPU Core")?.Value,
            MemoryClockMhz = FindSensor(hardware, SensorType.Clock, "GPU Memory")?.Value,
            PowerWatts = FindSensor(hardware, SensorType.Power, "GPU Package")?.Value
                         ?? hardware.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Power)?.Value,
            VramUsedMb = FindSensor(hardware, SensorType.SmallData, "GPU Memory Used")?.Value,
            VramTotalMb = FindSensor(hardware, SensorType.SmallData, "GPU Memory Total")?.Value,
            FanRpm = fanSensor?.Value,
            FanPercent = fanControl?.Value,
        };
    }

    private static MemorySnapshot ReadMemory(IHardware hardware)
    {
        float? used = FindSensor(hardware, SensorType.Data, "Memory Used")?.Value;
        float? available = FindSensor(hardware, SensorType.Data, "Memory Available")?.Value;
        float? load = hardware.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Load)?.Value;

        float? total = used.HasValue && available.HasValue ? used + available : null;

        return new MemorySnapshot
        {
            UsedGb = used,
            TotalGb = total,
            LoadPercent = load,
        };
    }

    private static MotherboardSnapshot ReadMotherboard(IHardware hardware)
    {
        // Les capteurs de température de la carte mère vivent souvent sur le sous-matériel (puce Super I/O).
        var allTempSensors = hardware.Sensors
            .Concat(hardware.SubHardware.SelectMany(sub => sub.Sensors))
            .Where(s => s.SensorType == SensorType.Temperature)
            .ToList();

        float? systemTemp = allTempSensors
            .FirstOrDefault(s => s.Name.Contains("System", StringComparison.OrdinalIgnoreCase)
                                  || s.Name.Contains("Motherboard", StringComparison.OrdinalIgnoreCase))?.Value;

        float? vrmTemp = allTempSensors
            .FirstOrDefault(s => s.Name.Contains("VRM", StringComparison.OrdinalIgnoreCase))?.Value;

        return new MotherboardSnapshot
        {
            Name = hardware.Name,
            SystemTempC = systemTemp,
            VrmTempC = vrmTemp,
        };
    }

    private static void CollectFans(IHardware hardware, List<FanReading> into)
    {
        var fanSensors = hardware.Sensors.Where(s => s.SensorType == SensorType.Fan);
        var controlSensors = hardware.Sensors.Where(s => s.SensorType == SensorType.Control).ToList();

        foreach (ISensor fan in fanSensors)
        {
            // Tente d'associer le capteur de contrôle (%) qui porte souvent le même index/nom que le capteur RPM.
            var matchingControl = controlSensors.FirstOrDefault(c => c.Index == fan.Index)
                                   ?? controlSensors.ElementAtOrDefault(fan.Index);

            into.Add(new FanReading
            {
                HardwareName = hardware.Name,
                SensorName = fan.Name,
                Rpm = fan.Value,
                PercentControl = matchingControl?.Value,
                SensorId = fan.Identifier.ToString(),
            });
        }
    }

    private static ISensor? FindSensor(IHardware hardware, SensorType type, string nameContains)
    {
        return hardware.Sensors.FirstOrDefault(s =>
            s.SensorType == type && s.Name.Contains(nameContains, StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _computer.Close();
    }
}
