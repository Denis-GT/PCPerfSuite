namespace PCPerfSuite.Core.Hardware;

public sealed class GpuFanInfo
{
    public required int CoolerId { get; init; }
    public int CurrentLevelPercent { get; init; }
    public int CurrentRpm { get; init; }
    public int MinLevelPercent { get; init; }
    public int MaxLevelPercent { get; init; }
}

public sealed class GpuControlSnapshot
{
    public required string Name { get; init; }

    public float PowerLimitPercent { get; init; }
    public float PowerLimitMinPercent { get; init; } = 50;
    public float PowerLimitMaxPercent { get; init; } = 100;
    public float PowerLimitDefaultPercent { get; init; } = 100;

    public IReadOnlyList<GpuFanInfo> Fans { get; init; } = Array.Empty<GpuFanInfo>();
}
