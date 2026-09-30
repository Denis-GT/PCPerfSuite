using System.Globalization;

namespace PCPerfSuite.Core.Hardware.Gpu;

/// <summary>Réglages à poser en une fois ; un champ null n'est pas touché. La tension est dans l'unité de la carte
/// (<see cref="GpuOverclockSnapshot.VoltageUnit"/>).</summary>
public sealed record GpuOverclockRequest
{
    public int? CoreOffsetMhz { get; init; }
    public int? MemoryOffsetMhz { get; init; }
    public float? PowerLimitPercent { get; init; }
    public int? TemperatureLimitC { get; init; }
    public int? Voltage { get; init; }

    /// <summary>Unité de <see cref="Voltage"/> au moment de la demande : sert au bilan quand la carte n'a pas pu être
    /// relue (1100 mV ne doit pas s'afficher « 1100 % »).</summary>
    public GpuVoltageUnit VoltageUnit { get; init; } = GpuVoltageUnit.Percent;
}

public enum GpuSetting
{
    CoreOffset,
    MemoryOffset,
    PowerLimit,
    TemperatureLimit,
    Voltage,
}

public enum GpuApplyStatus
{
    /// <summary>Relu identique à la demande.</summary>
    Retained,

    /// <summary>Accepté par le pilote, mais relu différent : il a rogné la demande.</summary>
    Trimmed,

    /// <summary>Refusé par le pilote.</summary>
    Refused,

    /// <summary>Accepté par le pilote, mais la carte n'a pas pu être relue sur ce réglage.</summary>
    NotReadBack,
}

/// <summary>Ce que le pilote a répondu à chaque essai ; null : réglage non demandé.</summary>
public sealed record GpuApplyAccepted(bool? Clocks, bool? PowerLimit, bool? TemperatureLimit, bool? Voltage);

/// <summary>Un réglage : ce qui était demandé, ce que la carte a relu, et ce qu'on en conclut. <paramref name="IsSigned"/>
/// affiche le signe (décalages).</summary>
public sealed record GpuApplyItem(
    GpuSetting Setting, double Requested, double? Retained, GpuApplyStatus Status, string Unit, bool IsSigned)
{
    public string Label => Setting switch
    {
        GpuSetting.CoreOffset => "cœur",
        GpuSetting.MemoryOffset => "mémoire",
        GpuSetting.PowerLimit => "puissance",
        GpuSetting.TemperatureLimit => "limite de température",
        _ => "tension",
    };

    public string Format(double value)
    {
        string number = value.ToString("0", CultureInfo.CurrentCulture);
        return IsSigned && value >= 0 ? $"+{number} {Unit}" : $"{number} {Unit}";
    }

    public string Describe() => Status switch
    {
        GpuApplyStatus.Retained => $"{Label} {Format(Retained!.Value)}",
        GpuApplyStatus.Trimmed => $"{Label} {Format(Retained!.Value)} au lieu de {Format(Requested)} demandés",
        GpuApplyStatus.Refused => $"{Label} {Format(Requested)} refusé par le pilote",
        _ => $"{Label} {Format(Requested)} envoyé, non relu",
    };
}

/// <summary>Bilan d'une application : un élément par réglage demandé, dans l'ordre de <see cref="GpuSetting"/>.</summary>
public sealed record GpuApplyReport(IReadOnlyList<GpuApplyItem> Items)
{
    public static GpuApplyReport Empty { get; } = new(Array.Empty<GpuApplyItem>());

    public bool AllRetained => Items.All(i => i.Status == GpuApplyStatus.Retained);

    public bool AnyRefused => Items.Any(i => i.Status == GpuApplyStatus.Refused);

    public GpuApplyItem? Find(GpuSetting setting) => Items.FirstOrDefault(i => i.Setting == setting);

    /// <summary>« Relu sur la carte : cœur +150 MHz ; mémoire +400 MHz au lieu de +500 MHz demandés. »</summary>
    public string Describe()
        => Items.Count == 0 ? "" : $"Relu sur la carte : {string.Join(" ; ", Items.Select(i => i.Describe()))}.";
}

/// <summary>
/// Comparaison demandé / retenu, isolée ici pour être testée sans pilote. Les Try* des backends renvoient true dès que
/// l'appel passe, sans vérifier (NVAPI SetPerformanceStates20, ADLX TrySetInt) : seule la relecture fait foi.
/// </summary>
public static class GpuApplyComparison
{
    private const double ClockToleranceMhz = 1;
    private const double PowerTolerancePercent = 0.5;
    private const double TemperatureTolerance = 0;
    private const double VoltageTolerance = 1;

    public static GpuApplyReport Compare(
        GpuOverclockRequest request, GpuApplyAccepted accepted, GpuOverclockSnapshot? overclock, GpuControlSnapshot? power)
    {
        var items = new List<GpuApplyItem>();

        if (request.CoreOffsetMhz is { } core)
        {
            items.Add(Item(GpuSetting.CoreOffset, core, accepted.Clocks,
                overclock is { CoreOffsetSupported: true } ? overclock.CoreOffsetMhz : null, ClockToleranceMhz, "MHz", true));
        }

        if (request.MemoryOffsetMhz is { } memory)
        {
            items.Add(Item(GpuSetting.MemoryOffset, memory, accepted.Clocks,
                overclock is { MemoryOffsetSupported: true } ? overclock.MemoryOffsetMhz : null, ClockToleranceMhz,
                overclock?.MemoryOffsetUnit ?? "MHz", true));
        }

        if (request.PowerLimitPercent is { } percent)
        {
            items.Add(Item(GpuSetting.PowerLimit, percent, accepted.PowerLimit,
                power is { PowerLimitSupported: true } ? power.PowerLimitPercent : null, PowerTolerancePercent, "%", false));
        }

        if (request.TemperatureLimitC is { } temperature)
        {
            items.Add(Item(GpuSetting.TemperatureLimit, temperature, accepted.TemperatureLimit,
                overclock is { TemperatureLimitSupported: true } ? overclock.TemperatureLimitC : null, TemperatureTolerance,
                "°C", false));
        }

        if (request.Voltage is { } voltage)
        {
            bool percentUnit = (overclock?.VoltageUnit ?? request.VoltageUnit) == GpuVoltageUnit.Percent;
            items.Add(Item(GpuSetting.Voltage, voltage, accepted.Voltage,
                overclock is { VoltageSupported: true } ? overclock.Voltage : null, VoltageTolerance,
                percentUnit ? "%" : "mV", !percentUnit && overclock?.VoltageIsOffset == true));
        }

        return new GpuApplyReport(items);
    }

    private static GpuApplyItem Item(
        GpuSetting setting, double requested, bool? accepted, double? retained, double tolerance, string unit, bool signed)
    {
        // La relecture prime sur la réponse du pilote : ADLX et IGCL répondent « refusé » dès que l'un des deux
        // décalages échoue, alors que l'autre a pu être posé.
        bool matches = retained is { } value && Math.Abs(value - requested) <= tolerance;
        GpuApplyStatus status = matches
            ? GpuApplyStatus.Retained
            : accepted != true
                ? GpuApplyStatus.Refused
                : retained is null
                    ? GpuApplyStatus.NotReadBack
                    : GpuApplyStatus.Trimmed;

        return new GpuApplyItem(setting, requested, retained, status, unit, signed);
    }
}
