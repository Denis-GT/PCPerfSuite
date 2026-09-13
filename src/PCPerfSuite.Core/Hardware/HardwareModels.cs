namespace PCPerfSuite.Core.Hardware;

public sealed class FanReading
{
    public required string HardwareName { get; init; }
    public required string SensorName { get; init; }
    public float? Rpm { get; init; }
    public float? PercentControl { get; init; }

    /// <summary>Identifiant stable du capteur (utilisé plus tard pour piloter la courbe de ce ventilateur précis).</summary>
    public required string SensorId { get; init; }
}

public sealed class CpuSnapshot
{
    public string Name { get; init; } = "CPU inconnu";
    public float? LoadPercent { get; init; }
    public float? PackageTempC { get; init; }
    public float? MaxCoreTempC { get; init; }
    public float? PowerWatts { get; init; }
    public float? MaxClockMhz { get; init; }
}

public sealed class GpuSnapshot
{
    public string Name { get; init; } = "GPU inconnu";
    public string Vendor { get; init; } = "Inconnu";
    public float? LoadPercent { get; init; }
    public float? CoreTempC { get; init; }
    public float? HotSpotTempC { get; init; }
    public float? CoreClockMhz { get; init; }
    public float? MemoryClockMhz { get; init; }
    public float? PowerWatts { get; init; }
    public float? VramUsedMb { get; init; }
    public float? VramTotalMb { get; init; }
    public float? FanRpm { get; init; }
    public float? FanPercent { get; init; }
}

public sealed class MemorySnapshot
{
    public float? UsedGb { get; init; }
    public float? TotalGb { get; init; }
    public float? LoadPercent { get; init; }
}

/// <summary>Capteur nommé générique (température, tension...) pour les valeurs dont la liste
/// varie selon la carte mère et qu'on ne peut donc pas modéliser avec des propriétés fixes.</summary>
public sealed class SensorReading
{
    public required string Name { get; init; }
    public float? Value { get; init; }
}

public sealed class MotherboardSnapshot
{
    public string Name { get; init; } = "Carte mère inconnue";
    public float? SystemTempC { get; init; }
    public float? VrmTempC { get; init; }

    /// <summary>Autres zones de température exposées par la puce Super I/O (chipset, PCH, sondes numérotées...).</summary>
    public IReadOnlyList<SensorReading> OtherTemperatures { get; init; } = Array.Empty<SensorReading>();

    /// <summary>Tensions (Vcore, +12V, +5V, +3.3V, DRAM...) — la liste dépend entièrement du modèle de carte mère.</summary>
    public IReadOnlyList<SensorReading> Voltages { get; init; } = Array.Empty<SensorReading>();
}

public sealed class DiskSnapshot
{
    public string Name { get; init; } = "Disque inconnu";

    /// <summary>Identifiant stable côté LibreHardwareMonitor (pas le numéro PhysicalDriveN de Windows).</summary>
    public string Identifier { get; init; } = "";

    public float? UsedPercent { get; init; }
    public float? ReadRateBytesPerSecond { get; init; }
    public float? WriteRateBytesPerSecond { get; init; }
    public float? TemperatureC { get; init; }

    /// <summary>Vie restante estimée (%), quand le firmware l'expose (SMART SSD/NVMe) — null pour la plupart des HDD.</summary>
    public float? RemainingLifePercent { get; init; }
}

public sealed class HardwareSnapshot
{
    public CpuSnapshot Cpu { get; init; } = new();
    public GpuSnapshot? Gpu { get; init; }
    public MemorySnapshot Memory { get; init; } = new();
    public MotherboardSnapshot Motherboard { get; init; } = new();
    public IReadOnlyList<FanReading> Fans { get; init; } = Array.Empty<FanReading>();
    public IReadOnlyList<DiskSnapshot> Disks { get; init; } = Array.Empty<DiskSnapshot>();
    public DateTime CapturedAtUtc { get; init; } = DateTime.UtcNow;
}
