using System.Globalization;

namespace PCPerfSuite.Core.Hardware.Cpu.Throttle;

/// <summary>Textes du bridage CPU, communs à la carte « Bridage » de Monitoring et au diagnostic.</summary>
public static class CpuThrottleText
{
    /// <summary>Raisons actives (« thermique, puissance »), « aucun » si toutes sont lues et inactives, null si aucune
    /// raison n'est lisible sur ce PC.</summary>
    public static string? Reasons(CpuThrottleReading reading)
    {
        var active = new List<string>();
        if (reading.Thermal == true) active.Add("thermique");
        if (reading.Prochot == true) active.Add("PROCHOT");
        if (reading.PowerLimit == true) active.Add("puissance");
        if (reading.CurrentLimit == true) active.Add("courant");
        if (reading.CrossDomain == true) active.Add("autre domaine");
        if (active.Count > 0) return string.Join(", ", active);

        bool anyRead = reading.Thermal is not null || reading.Prochot is not null || reading.PowerLimit is not null
            || reading.CurrentLimit is not null || reading.CrossDomain is not null;
        return anyRead ? "aucun" : null;
    }

    public static string Source(CpuThrottleReading reading) => reading.Source switch
    {
        CpuThrottleSource.IntelMsr => "registres MSR (Intel, PawnIO)",
        CpuThrottleSource.AmdPmTable => reading.Amd is { } amd
            ? $"PM table AMD (version 0x{amd.TableVersion.ToString("X6", CultureInfo.InvariantCulture)}, expérimental)"
            : "PM table AMD (expérimental)",
        _ => "compteurs Windows",
    };

    /// <summary>Indice des compteurs Windows : la performance garantie est sous la nominale.</summary>
    public static string? CounterHint(CpuThrottleReading reading)
    {
        if (reading.PerformanceLimitPercent is not { } limit) return null;
        return limit < 99.5
            ? $"Windows garantit {Format(limit)} % de la fréquence nominale (indice de limitation)"
            : "aucune limite signalée par Windows";
    }

    /// <summary>Fréquence et TjMax, pour le détail.</summary>
    public static string Figures(CpuThrottleReading reading)
    {
        var parts = new List<string>();
        if (reading.EffectiveMhz is { } mhz)
        {
            string percent = reading.PerformancePercent is { } p ? $" ({Format(p)} % de la nominale)" : "";
            parts.Add($"fréquence effective {Format(mhz)} MHz{percent}");
        }
        if (reading.BaseMhz is { } baseMhz) parts.Add($"base {Format(baseMhz)} MHz");
        if (reading.MaxTurboMhz is { } turbo) parts.Add($"turbo {Format(turbo)} MHz");
        if (reading.TjMaxC is { } tjMax)
        {
            parts.Add(reading.TccOffsetC is { } offset and > 0
                ? $"TjMax {tjMax} °C, bridage dès {reading.ThrottleTemperatureC} °C (décalage TCC {offset} °C)"
                : $"TjMax {tjMax} °C");
        }
        if (reading.Amd is { } amd)
        {
            parts.Add($"PPT {Pair(amd.PptWatts, amd.PptLimitWatts, "W")}, TDC {Pair(amd.TdcAmps, amd.TdcLimitAmps, "A")}, "
                + $"EDC {Pair(amd.EdcAmps, amd.EdcLimitAmps, "A")}, THM {Pair(amd.ThmC, amd.ThmLimitC, "°C")}");
        }
        if (reading.PowerThrottledPercent is { } throttled) parts.Add($"temps bridé par la limite de puissance {Format(throttled)} % (expérimental)");
        return string.Join(" ; ", parts);
    }

    private static string Pair(float? value, float? limit, string unit)
        => $"{(value is { } v ? Format(v) : "--")}/{(limit is { } l ? Format(l) : "--")} {unit}";

    private static string Format(double value) => value.ToString("0", CultureInfo.InvariantCulture);
}
