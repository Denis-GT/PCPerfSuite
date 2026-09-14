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
        var disks = new List<DiskSnapshot>();

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

                case HardwareType.Storage:
                    disks.Add(ReadDisk(hardware));
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
            Disks = disks,
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
        // Les capteurs de la carte mère vivent souvent sur le sous-matériel (puce Super I/O).
        List<ISensor> subSensors = hardware.SubHardware.SelectMany(sub => sub.Sensors).ToList();

        List<ISensor> allTempSensors = hardware.Sensors.Concat(subSensors)
            .Where(s => s.SensorType == SensorType.Temperature)
            .ToList();

        ISensor? systemSensor = allTempSensors.FirstOrDefault(s =>
            s.Name.Contains("System", StringComparison.OrdinalIgnoreCase)
            || s.Name.Contains("Motherboard", StringComparison.OrdinalIgnoreCase));

        ISensor? vrmSensor = allTempSensors.FirstOrDefault(s =>
            s.Name.Contains("VRM", StringComparison.OrdinalIgnoreCase));

        List<SensorReading> otherTemps = allTempSensors
            .Where(s => s != systemSensor && s != vrmSensor)
            .Select(s => new SensorReading { Name = s.Name, Value = s.Value })
            .ToList();

        List<SensorReading> voltages = hardware.Sensors.Concat(subSensors)
            .Where(s => s.SensorType == SensorType.Voltage)
            .Select(s => new SensorReading { Name = s.Name, Value = s.Value })
            .ToList();

        return new MotherboardSnapshot
        {
            Name = hardware.Name,
            SystemTempC = systemSensor?.Value,
            VrmTempC = vrmSensor?.Value,
            OtherTemperatures = otherTemps,
            Voltages = voltages,
        };
    }

    private static DiskSnapshot ReadDisk(IHardware hardware)
    {
        float? usedPercent = FindSensor(hardware, SensorType.Load, "Used Space")?.Value;
        float? readRate = FindSensor(hardware, SensorType.Throughput, "Read Rate")?.Value;
        float? writeRate = FindSensor(hardware, SensorType.Throughput, "Write Rate")?.Value;
        float? temp = hardware.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature)?.Value;

        // Les SSD/NVMe exposent soit "Remaining Life" (déjà le % restant), soit "Percentage Used"
        // (convention NVMe standard : usure consommée, donc vie restante = 100 - valeur). La plupart
        // des HDD n'exposent ni l'un ni l'autre : RemainingLifePercent reste alors null (affiché "--").
        float? remainingLife = FindSensor(hardware, SensorType.Level, "Remaining Life")?.Value;
        if (remainingLife is null)
        {
            float? percentageUsed = FindSensor(hardware, SensorType.Level, "Percentage Used")?.Value;
            if (percentageUsed is { } used) remainingLife = Math.Clamp(100 - used, 0, 100);
        }

        return new DiskSnapshot
        {
            Name = hardware.Name,
            Identifier = hardware.Identifier.ToString(),
            UsedPercent = usedPercent,
            ReadRateBytesPerSecond = readRate,
            WriteRateBytesPerSecond = writeRate,
            TemperatureC = temp,
            RemainingLifePercent = remainingLife,
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
                PercentControlSensorId = matchingControl?.Control is not null ? matchingControl.Identifier.ToString() : null,
            });
        }
    }

    private static ISensor? FindSensor(IHardware hardware, SensorType type, string nameContains)
    {
        return hardware.Sensors.FirstOrDefault(s =>
            s.SensorType == type && s.Name.Contains(nameContains, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Bascule un ventilateur en pilotage logiciel et applique un % cible (0-100), borné aux
    /// limites que la puce Super I/O accepte réellement. Retourne false si le capteur est introuvable
    /// (carte mère débranchée du point de vue LibreHardwareMonitor — ne devrait pas arriver en usage normal).</summary>
    public bool TrySetFanPercent(string controlSensorId, float percent)
    {
        IControl? control = FindControl(controlSensorId);
        if (control is null) return false;

        float clamped = Math.Clamp(percent, control.MinSoftwareValue, control.MaxSoftwareValue);
        control.SetSoftware(clamped);
        return true;
    }

    /// <summary>Rend le pilotage du ventilateur au firmware de la carte mère (courbe BIOS par défaut).</summary>
    public bool TrySetFanAuto(string controlSensorId)
    {
        IControl? control = FindControl(controlSensorId);
        if (control is null) return false;

        control.SetDefault();
        return true;
    }

    private IControl? FindControl(string controlSensorId)
    {
        foreach (IHardware hardware in _computer.Hardware)
        {
            IControl? found = FindControlIn(hardware, controlSensorId) ?? hardware.SubHardware
                .Select(sub => FindControlIn(sub, controlSensorId))
                .FirstOrDefault(c => c is not null);

            if (found is not null) return found;
        }
        return null;
    }

    private static IControl? FindControlIn(IHardware hardware, string controlSensorId)
    {
        ISensor? sensor = hardware.Sensors.FirstOrDefault(s =>
            s.SensorType == SensorType.Control && s.Identifier.ToString() == controlSensorId);
        return sensor?.Control;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _computer.Close();
    }
}
