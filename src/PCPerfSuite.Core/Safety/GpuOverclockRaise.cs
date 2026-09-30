using System.Globalization;
using PCPerfSuite.Core.Hardware;

namespace PCPerfSuite.Core.Safety;

/// <summary>
/// Ce qu'on demande à la carte pour la sécurité thermique : la rendre d'origine et la relire. <see cref="GpuControlService"/>
/// l'implémente ; l'interface permet de tester <see cref="GpuThermalSafety"/> sans pilote.
/// </summary>
public interface IGpuOverclockTarget
{
    void RestoreOverclockDefaults();

    GpuOverclockSnapshot? GetOverclock();

    GpuControlSnapshot? GetSnapshot();
}

/// <summary>Un réglage GPU relevé au-dessus de son origine, isolé ici pour être testé sans matériel.</summary>
public static class GpuOverclockRaise
{
    /// <summary>Marge sur la limite de puissance : le pilote rend parfois 100,2 % pour 100 %.</summary>
    private const float PowerTolerancePercent = 0.5f;

    /// <summary>
    /// Vrai si l'un des réglages relus dépasse sa valeur d'origine : décalage cœur ou mémoire positif, limite de
    /// puissance, de température ou tension au-dessus de celles d'origine. Baisser un réglage (décalage négatif,
    /// sous-tension, puissance réduite) ne fait pas chauffer davantage : cela ne compte pas. Un bloc que la carte
    /// n'expose pas est ignoré.
    /// </summary>
    public static bool IsRaised(GpuOverclockSnapshot? overclock, GpuControlSnapshot? power)
    {
        if (overclock is not null)
        {
            if (overclock.CoreOffsetSupported && overclock.CoreOffsetMhz > 0) return true;
            if (overclock.MemoryOffsetSupported && overclock.MemoryOffsetMhz > 0) return true;
            if (overclock.TemperatureLimitSupported && overclock.TemperatureLimitC > overclock.TemperatureLimitDefaultC) return true;
            if (overclock.VoltageSupported && overclock.Voltage > overclock.VoltageDefault) return true;
        }

        return power is { PowerLimitSupported: true }
               && power.PowerLimitPercent > power.PowerLimitDefaultPercent + PowerTolerancePercent;
    }

    /// <summary>Ce que la carte a relu, en clair : « cœur +0 MHz, mémoire +0 MHz, puissance 100 % ».</summary>
    public static string DescribeReadBack(GpuOverclockSnapshot? overclock, GpuControlSnapshot? power)
    {
        var parts = new List<string>();
        if (overclock is not null)
        {
            if (overclock.CoreOffsetSupported) parts.Add($"cœur {Signed(overclock.CoreOffsetMhz)} MHz");
            if (overclock.MemoryOffsetSupported) parts.Add($"mémoire {Signed(overclock.MemoryOffsetMhz)} {overclock.MemoryOffsetUnit}");
            if (overclock.TemperatureLimitSupported) parts.Add($"limite de température {overclock.TemperatureLimitC} °C");
            if (overclock.VoltageSupported)
            {
                parts.Add(overclock.VoltageUnit == GpuVoltageUnit.Percent
                    ? $"tension {overclock.Voltage} %"
                    : $"tension {(overclock.VoltageIsOffset ? Signed(overclock.Voltage) : overclock.Voltage.ToString(CultureInfo.CurrentCulture))} mV");
            }
        }

        if (power is { PowerLimitSupported: true })
            parts.Add($"puissance {power.PowerLimitPercent.ToString("0", CultureInfo.CurrentCulture)} %");

        return parts.Count == 0 ? "rien de relisible" : string.Join(", ", parts);
    }

    private static string Signed(int value)
        => value >= 0 ? $"+{value.ToString(CultureInfo.CurrentCulture)}" : value.ToString(CultureInfo.CurrentCulture);
}
