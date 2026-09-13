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

public sealed class MotherboardSnapshot
{
    public string Name { get; init; } = "Carte mère inconnue";
    public float? SystemTempC { get; init; }
    public float? VrmTempC { get; init; }
}

public sealed class HardwareSnapshot
{
    public CpuSnapshot Cpu { get; init; } = new();
    public GpuSnapshot? Gpu { get; init; }
    public MemorySnapshot Memory { get; init; } = new();
    public MotherboardSnapshot Motherboard { get; init; } = new();
    public IReadOnlyList<FanReading> Fans { get; init; } = Array.Empty<FanReading>();
    public DateTime CapturedAtUtc { get; init; } = DateTime.UtcNow;
}
