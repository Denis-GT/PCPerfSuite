using System.Globalization;
using PCPerfSuite.Core.Hardware;
using PCPerfSuite.Core.Hardware.Cpu;
using PCPerfSuite.Core.Hardware.Fans;

namespace PCPerfSuite.Core.Profiles;

public enum ConformityState
{
    /// <summary>L'état relu est celui que le groupe a laissé.</summary>
    Conform,

    /// <summary>Quelque chose a changé depuis (réglage à la main, firmware au réveil, sécurité thermique…).</summary>
    Modified,

    /// <summary>Une veille vient d'avoir lieu : la relecture n'est pas encore faite. Jamais « conforme » d'ici là.</summary>
    PendingCheck,

    /// <summary>La dimension ne peut pas être relue (carte devenue muette, ventilateurs pas encore relevés).</summary>
    Unverifiable,
}

/// <summary>Ce qu'une dimension est devenue depuis l'application du groupe.</summary>
public sealed record DimensionConformity(ProfileDimension Dimension, ConformityState State, IReadOnlyList<string> Differences)
{
    public static DimensionConformity Conform(ProfileDimension dimension) => new(dimension, ConformityState.Conform, []);

    public string Describe() => State switch
    {
        ConformityState.Conform => $"{DimensionReport.Title(Dimension)} : conforme",
        ConformityState.Modified => $"{DimensionReport.Title(Dimension)} : modifié ({string.Join(", ", Differences)})",
        ConformityState.PendingCheck => $"{DimensionReport.Title(Dimension)} : à revérifier après la veille",
        _ => $"{DimensionReport.Title(Dimension)} : non vérifiable{(Differences.Count > 0 ? $" ({string.Join(", ", Differences)})" : "")}",
    };
}

/// <summary>
/// « Groupe actif : conforme » ou « modifié depuis », en logique pure. On compare l'état relu maintenant à l'état
/// <b>retenu</b> juste après l'application (relu lui aussi), jamais aux valeurs demandées : le pilote les rogne, et la
/// page dirait « modifié » à tort. Seul ce que le groupe a touché est comparé.
/// </summary>
public static class ProfileGroupConformity
{
    private const float WattsTolerance = 1f;
    private const float PowerTolerance = 0.5f;

    public static DimensionConformity CompareCpu(CpuProfile retained, CpuTargetState current)
    {
        var differences = new List<string>();
        foreach ((string id, CpuProfilePowerValue expected) in retained.PowerSettings)
        {
            CpuPowerSettingReading? reading = current.Settings.FirstOrDefault(r => r.Setting.Id == id);
            if (reading is null)
            {
                differences.Add($"« {id} » illisible");
                continue;
            }

            if (expected.Ac is { } ac && ac != reading.Ac)
            {
                differences.Add($"« {reading.Setting.Label} » : {reading.Setting.Describe(reading.Ac)} au lieu de {reading.Setting.Describe(ac)}");
            }
            else if (current.HasBattery && expected.Battery is { } dc && dc != reading.Dc)
            {
                differences.Add($"« {reading.Setting.Label} » sur batterie : {reading.Setting.Describe(reading.Dc)} au lieu de {reading.Setting.Describe(dc)}");
            }
        }

        if (retained.SustainedWatts is { } sustained)
        {
            if (current.Watts is not { } watts) differences.Add("limites en watts illisibles");
            else if (Math.Abs(watts.Sustained - sustained) > WattsTolerance)
            {
                differences.Add($"limite soutenue {CpuGroupPlanner.Watts(watts.Sustained)} au lieu de {CpuGroupPlanner.Watts(sustained)}");
            }
            else if (retained.BurstWatts is { } burst && watts.Burst is { } currentBurst && Math.Abs(currentBurst - burst) > WattsTolerance)
            {
                differences.Add($"limite de pointe {CpuGroupPlanner.Watts(currentBurst)} au lieu de {CpuGroupPlanner.Watts(burst)}");
            }
        }

        return Result(ProfileDimension.Cpu, differences);
    }

    public static DimensionConformity CompareGpu(GpuRetainedValues retained, GpuTargetState current)
    {
        if (!current.IsAvailable || current.Overclock is null)
        {
            return new DimensionConformity(ProfileDimension.Gpu, ConformityState.Unverifiable, ["carte non relue"]);
        }

        GpuRetainedValues now = current.ToRetained();
        var differences = new List<string>();
        Compare("cœur", retained.CoreOffsetMhz, now.CoreOffsetMhz, 0.5, "MHz", true, differences);
        Compare("mémoire", retained.MemoryOffsetMhz, now.MemoryOffsetMhz, 0.5, current.Overclock.MemoryOffsetUnit, true, differences);
        Compare("puissance", retained.PowerLimitPercent, now.PowerLimitPercent, PowerTolerance, "%", false, differences);
        Compare("limite de température", retained.TemperatureLimitC, now.TemperatureLimitC, 0, "°C", false, differences);
        Compare("tension", retained.Voltage, now.Voltage, 0.5, "", false, differences);
        return Result(ProfileDimension.Gpu, differences);
    }

    public static DimensionConformity CompareFans(FanProfile retained, FanTargetState current)
    {
        if (!current.IsReady)
        {
            return new DimensionConformity(ProfileDimension.Fans, ConformityState.Unverifiable, ["ventilateurs pas encore relevés"]);
        }

        var differences = new List<string>();
        foreach (FanCurveConfig expected in retained.Fans)
        {
            FanTargetFan? fan = current.Fans.FirstOrDefault(f => f.FanId == expected.ControlSensorId);
            FanCurveConfig? actual = current.Current.Fans.FirstOrDefault(c => c.ControlSensorId == expected.ControlSensorId);
            string name = fan?.Name ?? expected.ControlSensorId;
            if (fan is null || actual is null)
            {
                differences.Add($"« {name} » absent");
                continue;
            }

            IReadOnlyList<string> changes = FanConfigs.Differences(expected, actual);
            if (changes.Count > 0) differences.Add($"« {name} » : {string.Join(", ", changes)}");
        }

        return Result(ProfileDimension.Fans, differences);
    }

    private static void Compare(string label, double? expected, double? actual, double tolerance, string unit, bool signed, List<string> into)
    {
        if (expected is not { } wanted) return;
        if (actual is not { } now)
        {
            into.Add($"{label} illisible");
            return;
        }

        if (Math.Abs(now - wanted) > tolerance) into.Add($"{label} {Format(now, unit, signed)} au lieu de {Format(wanted, unit, signed)}");
    }

    private static string Format(double value, string unit, bool signed)
    {
        string number = value.ToString("0", CultureInfo.CurrentCulture);
        string text = signed && value >= 0 ? $"+{number}" : number;
        return unit.Length == 0 ? text : $"{text} {unit}";
    }

    private static DimensionConformity Result(ProfileDimension dimension, List<string> differences)
        => differences.Count == 0 ? DimensionConformity.Conform(dimension) : new DimensionConformity(dimension, ConformityState.Modified, differences);
}
