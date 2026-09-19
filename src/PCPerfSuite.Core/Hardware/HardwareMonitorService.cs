using System.Diagnostics;
using LibreHardwareMonitor.Hardware;
using PCPerfSuite.Core.Overlay;

namespace PCPerfSuite.Core.Hardware;

/// <summary>
/// Encapsule LibreHardwareMonitorLib pour exposer un instantané typé et
/// indépendant du fabricant (Intel/AMD, NVIDIA/AMD/Intel) plutôt que l'arbre
/// brut de capteurs. Nécessite les droits administrateur pour la plupart des
/// capteurs (le pilote WinRing0 embarqué par la lib s'installe/se charge au
/// premier accès).
/// </summary>
public sealed class HardwareMonitorService : IFanController, IDisposable
{
    private readonly Computer _computer;
    private readonly UpdateVisitor _visitor = new();
    private bool _disposed;

    /// <summary>Part du temps qu'un groupe de capteurs peut passer à se lire en cadence automatique. Un CPU Intel,
    /// que LibreHardwareMonitor lit cœur par cœur (~30 ms mesurés sur un i5-13500T), est ainsi relu toutes les
    /// ~600 ms, et un groupe quasi gratuit à chaque relevé.</summary>
    public const double AutoReadBudget = 0.05;

    /// <summary>Plafond de la cadence automatique, pour qu'un groupe très coûteux reste quand même à jour.</summary>
    public static readonly TimeSpan MaxAutoInterval = TimeSpan.FromSeconds(5);

    private readonly SensorReadSchedule[] _schedules =
        Enum.GetValues<SensorGroup>().Select(group => new SensorReadSchedule(group)).ToArray();

    /// <summary>Charge CPU (groupe CpuLoad), lue indépendamment de la lecture LibreHardwareMonitor du CPU.</summary>
    private readonly CpuLoadSampler _cpuLoad = new();

    /// <summary>Batterie (groupe Battery), lue auprès du pilote Windows plutôt que par LibreHardwareMonitor : il faut
    /// l'état secteur et la capacité nominale, que la lib n'expose pas toutes.</summary>
    private readonly BatteryReader _battery = new();

    // Dernières valeurs lues hors LibreHardwareMonitor, reprises dans les relevés où leur groupe n'est pas relu.
    private float? _lastCpuLoad;
    private RtssFrameStats? _lastGame;
    private BatterySnapshot? _lastBattery;

    public HardwareMonitorService()
    {
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsMotherboardEnabled = true,
            IsStorageEnabled = true,
            IsNetworkEnabled = true,
            // Alimentations connectées (Corsair HXi/RMi...) : seule mesure de la conso totale d'un PC fixe.
            IsPsuEnabled = true,
        };

        _computer.Open();
    }

    /// <summary>Relevé du tick <paramref name="tick"/> : chaque groupe n'est relu que si c'est son tour.</summary>
    /// <param name="epoch">Change avec la durée du tick (voir <see cref="TickInterval"/>) ; les numéros de tick repartent alors de zéro.</param>
    public HardwareSnapshot GetSnapshot(long epoch, long tick)
    {
        long start = Stopwatch.GetTimestamp();

        var timings = new List<HardwareReadTiming>();
        var groupDurations = new TimeSpan[_schedules.Length];
        var groupRead = new bool[_schedules.Length];

        // Échéances évaluées une fois pour tout le relevé : le matériel d'un même groupe est relu ensemble.
        TimeSpan tickInterval = TickInterval;
        bool[] due = _schedules.Select(schedule => schedule.IsDue(epoch, tick, tickInterval)).ToArray();

        if (due[(int)SensorGroup.CpuLoad])
        {
            long loadStart = Stopwatch.GetTimestamp();
            _lastCpuLoad = _cpuLoad.Sample();
            RecordRead(SensorGroup.CpuLoad, "cpuload", "Charge CPU (compteur Windows)", loadStart, timings, groupDurations, groupRead);
        }

        if (due[(int)SensorGroup.Fps])
        {
            long fpsStart = Stopwatch.GetTimestamp();
            _lastGame = RtssFrameStatsReader.TryReadForeground();
            RecordRead(SensorGroup.Fps, "rtss", "FPS (RTSS)", fpsStart, timings, groupDurations, groupRead);
        }

        if (due[(int)SensorGroup.Battery])
        {
            long batteryStart = Stopwatch.GetTimestamp();
            _lastBattery = _battery.Read();
            RecordRead(SensorGroup.Battery, "battery", "Batterie (pilote Windows)", batteryStart, timings, groupDurations, groupRead);
        }

        foreach (IHardware hardware in _computer.Hardware)
        {
            int group = (int)GroupOf(hardware.HardwareType);
            if (!due[group]) continue;

            long hardwareStart = Stopwatch.GetTimestamp();
            hardware.Accept(_visitor);
            TimeSpan duration = Stopwatch.GetElapsedTime(hardwareStart);

            groupDurations[group] += duration;
            groupRead[group] = true;
            timings.Add(new HardwareReadTiming
            {
                Identifier = hardware.Identifier.ToString(),
                Name = hardware.Name,
                Duration = duration,
            });
        }

        for (int group = 0; group < _schedules.Length; group++)
        {
            if (groupRead[group]) _schedules[group].RecordRead(epoch, tick, groupDurations[group], tickInterval);
        }

        var cpu = new CpuSnapshot();
        GpuSnapshot? gpu = null;
        var memory = new MemorySnapshot();
        var motherboard = new MotherboardSnapshot();
        var fans = new List<FanReading>();
        var disks = new List<DiskSnapshot>();
        float? uploadRate = null;
        float? downloadRate = null;
        float? psuPower = null;

        foreach (IHardware hardware in _computer.Hardware)
        {
            switch (hardware.HardwareType)
            {
                case HardwareType.Cpu:
                    cpu = ReadCpu(hardware, _lastCpuLoad);
                    break;

                case HardwareType.GpuNvidia:
                case HardwareType.GpuAmd:
                case HardwareType.GpuIntel:
                    gpu = ReadGpu(hardware);
                    CollectFans(hardware, SensorGroup.Gpu, fans);
                    break;

                case HardwareType.Memory:
                    memory = ReadMemory(hardware);
                    break;

                case HardwareType.Motherboard:
                    motherboard = ReadMotherboard(hardware);
                    CollectFans(hardware, SensorGroup.Motherboard, fans);
                    foreach (IHardware sub in hardware.SubHardware)
                    {
                        CollectFans(sub, SensorGroup.Motherboard, fans);
                    }
                    break;

                case HardwareType.Storage:
                    disks.Add(ReadDisk(hardware));
                    break;

                case HardwareType.Network:
                    uploadRate = AddIfPresent(uploadRate, FindSensor(hardware, SensorType.Throughput, "Upload Speed")?.Value);
                    downloadRate = AddIfPresent(downloadRate, FindSensor(hardware, SensorType.Throughput, "Download Speed")?.Value);
                    break;

                case HardwareType.Psu:
                    // "Total watts" chez Corsair : la puissance fournie au PC, toutes sorties confondues.
                    psuPower = AddIfPresent(psuPower, (FindSensor(hardware, SensorType.Power, "Total")
                        ?? hardware.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Power))?.Value);
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
            Network = new NetworkSnapshot { UploadBytesPerSecond = uploadRate, DownloadBytesPerSecond = downloadRate },
            Battery = _lastBattery,
            PsuPowerWatts = psuPower,
            ReadTimings = timings,
            ReadDuration = Stopwatch.GetElapsedTime(start),
            GroupStatuses = _schedules.Select(schedule => schedule.GetStatus(tickInterval)).ToArray(),
            GroupsRead = Enum.GetValues<SensorGroup>().Where(group => groupRead[(int)group]).ToArray(),
            Game = _lastGame,
        };
    }

    /// <summary>Compte une lecture faite hors LibreHardwareMonitor (charge CPU, FPS) dans les durées du relevé.</summary>
    private static void RecordRead(SensorGroup group, string identifier, string name, long readStart,
        List<HardwareReadTiming> timings, TimeSpan[] groupDurations, bool[] groupRead)
    {
        TimeSpan duration = Stopwatch.GetElapsedTime(readStart);
        groupDurations[(int)group] += duration;
        groupRead[(int)group] = true;
        timings.Add(new HardwareReadTiming { Identifier = identifier, Name = name, Duration = duration });
    }

    /// <summary>Impose une cadence de relecture à un groupe, ou null pour la cadence automatique
    /// (déduite du coût mesuré). Peut être appelé pendant un relevé en cours.</summary>
    public void SetManualInterval(SensorGroup group, TimeSpan? interval)
        => _schedules[(int)group].ManualInterval = interval;

    /// <summary>Cadence actuelle d'un groupe, sans attendre le prochain relevé (après un changement de réglage).</summary>
    public SensorGroupReadStatus GetGroupStatus(SensorGroup group) => _schedules[(int)group].GetStatus(TickInterval);

    /// <summary>Cadence de base, l'actualisation globale : celle d'un groupe en automatique dont la lecture ne coûte pas cher.</summary>
    public void SetBaseInterval(TimeSpan interval)
    {
        foreach (SensorReadSchedule schedule in _schedules)
        {
            schedule.BaseInterval = interval;
        }
    }

    /// <summary>Rythme auquel appeler GetSnapshot : le plus court des intervalles voulus (actualisation ou cadence
    /// imposée). Les cadences de tous les groupes en sont des multiples entiers ; l'automatique ne fait que les allonger,
    /// ce tick ne dépend donc pas du coût mesuré et reste stable.</summary>
    public TimeSpan TickInterval => _schedules.Min(schedule => schedule.RequestedInterval);

    private static SensorGroup GroupOf(HardwareType type) => type switch
    {
        HardwareType.Cpu => SensorGroup.Cpu,
        HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel => SensorGroup.Gpu,
        HardwareType.Memory => SensorGroup.Memory,
        HardwareType.Storage => SensorGroup.Storage,
        HardwareType.Network => SensorGroup.Network,
        HardwareType.Psu => SensorGroup.Battery,
        // Carte mère et le reste de ce que LibreHardwareMonitor rattache à la carte (Super I/O, contrôleur embarqué).
        _ => SensorGroup.Motherboard,
    };

    private static CpuSnapshot ReadCpu(IHardware hardware, float? sampledLoad)
    {
        // Charge mesurée à chaque relevé quand Windows la fournit, sinon celle de LibreHardwareMonitor (relue moins souvent).
        float? load = sampledLoad
                       ?? FindSensor(hardware, SensorType.Load, "CPU Total")?.Value
                       ?? hardware.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Load)?.Value;

        float? package = FindSensor(hardware, SensorType.Temperature, "CPU Package")?.Value;

        // Intel publie "Core Max" ; ailleurs (AMD : "Core (Tctl/Tdie)"...) on prend le max des sondes "Core",
        // sans les "CPU Core #n Distance to TjMax" d'Intel qui sont des marges, pas des températures.
        float? maxCore = FindSensor(hardware, SensorType.Temperature, "Core Max")?.Value
                         ?? hardware.Sensors
                             .Where(s => s.SensorType == SensorType.Temperature
                                         && s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase)
                                         && !s.Name.Contains("Distance", StringComparison.OrdinalIgnoreCase))
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

        // Tension du package : "CPU Core" chez Intel, "Core (SVI2 TFN)" chez AMD Zen. Les tensions par cœur
        // ("CPU Core #n", "Core #n VID") sont écartées par le '#'.
        float? coreVoltage = hardware.Sensors.FirstOrDefault(s =>
            s.SensorType == SensorType.Voltage
            && s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase)
            && !s.Name.Contains('#'))?.Value;

        return new CpuSnapshot
        {
            Name = hardware.Name,
            LoadPercent = load,
            MaxCoreLoadPercent = FindSensor(hardware, SensorType.Load, "CPU Core Max")?.Value,
            PackageTempC = package ?? maxCore,
            MaxCoreTempC = maxCore,
            PowerWatts = power,
            MaxClockMhz = maxClock,
            CoreVoltage = coreVoltage,
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
            // "GPU Memory Junction" chez NVIDIA, "GPU Memory" chez AMD.
            MemoryJunctionTempC = FindSensor(hardware, SensorType.Temperature, "Memory")?.Value,
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
        // RAM et mémoire virtuelle vivent sur le même matériel, et "Memory Used" est contenu dans
        // "Virtual Memory Used" : on sépare les deux familles avant de chercher par nom.
        ISensor[] virtualSensors = hardware.Sensors.Where(s => s.Name.Contains("Virtual", StringComparison.OrdinalIgnoreCase)).ToArray();
        ISensor[] physicalSensors = hardware.Sensors.Except(virtualSensors).ToArray();

        float? used = FindSensor(physicalSensors, SensorType.Data, "Memory Used")?.Value;
        float? available = FindSensor(physicalSensors, SensorType.Data, "Memory Available")?.Value;
        float? load = physicalSensors.FirstOrDefault(s => s.SensorType == SensorType.Load)?.Value;

        float? total = used.HasValue && available.HasValue ? used + available : null;

        return new MemorySnapshot
        {
            UsedGb = used,
            AvailableGb = available,
            TotalGb = total,
            LoadPercent = load,
            VirtualUsedGb = FindSensor(virtualSensors, SensorType.Data, "Memory Used")?.Value,
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

        // LibreHardwareMonitor ne nomme "System"/"VRM"... que sur les cartes mères présentes dans sa
        // table de correspondance par modèle. Sur les autres (beaucoup de cartes récentes), la puce
        // Super I/O reste nommée génériquement ("Temperature #1"...) et les deux Contains ci-dessus ne
        // trouvent rien : on se rabat sur les deux premières sondes plutôt que d'afficher deux "--" qui
        // donneraient à tort l'impression que ces données sont perdues.
        systemSensor ??= allTempSensors.FirstOrDefault();
        vrmSensor ??= allTempSensors.FirstOrDefault(s => s != systemSensor);

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
            SystemTempLabel = FriendlyOrRawName(systemSensor, "Température système"),
            SystemTempC = systemSensor?.Value,
            VrmTempLabel = FriendlyOrRawName(vrmSensor, "VRM"),
            VrmTempC = vrmSensor?.Value,
            OtherTemperatures = otherTemps,
            Voltages = voltages,
        };
    }

    /// <summary>Le libellé français quand le nom du capteur confirme qu'il s'agit bien de cette zone,
    /// sinon son nom brut (repli générique "Temperature #N") pour ne jamais mal étiqueter une valeur
    /// dont on n'est pas sûr qu'elle corresponde vraiment au système/au VRM.</summary>
    private static string FriendlyOrRawName(ISensor? sensor, string friendlyName)
    {
        if (sensor is null) return friendlyName;

        bool confirmedByName = sensor.Name.Contains("System", StringComparison.OrdinalIgnoreCase)
            || sensor.Name.Contains("Motherboard", StringComparison.OrdinalIgnoreCase)
            || sensor.Name.Contains("VRM", StringComparison.OrdinalIgnoreCase);

        return confirmedByName ? friendlyName : sensor.Name;
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

    private static void CollectFans(IHardware hardware, SensorGroup group, List<FanReading> into)
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
                Group = group,
                SensorName = fan.Name,
                Rpm = fan.Value,
                PercentControl = matchingControl?.Value,
                SensorId = fan.Identifier.ToString(),
                PercentControlSensorId = matchingControl?.Control is not null ? matchingControl.Identifier.ToString() : null,
            });
        }
    }

    private static ISensor? FindSensor(IHardware hardware, SensorType type, string nameContains)
        => FindSensor(hardware.Sensors, type, nameContains);

    private static ISensor? FindSensor(IEnumerable<ISensor> sensors, SensorType type, string nameContains)
    {
        return sensors.FirstOrDefault(s =>
            s.SensorType == type && s.Name.Contains(nameContains, StringComparison.OrdinalIgnoreCase));
    }

    private static float? AddIfPresent(float? total, float? value)
        => value is { } v ? (total ?? 0) + v : total;

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

    bool IFanController.TrySetPercent(string fanId, float percent) => TrySetFanPercent(fanId, percent);

    bool IFanController.TrySetAuto(string fanId) => TrySetFanAuto(fanId);

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
        _cpuLoad.Dispose();
        _battery.Dispose();
        _computer.Close();
    }
}
