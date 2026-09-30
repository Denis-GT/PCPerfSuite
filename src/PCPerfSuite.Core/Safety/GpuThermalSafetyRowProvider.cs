using System.Globalization;
using PCPerfSuite.Core.Compatibility;
using PCPerfSuite.Core.Hardware;

namespace PCPerfSuite.Core.Safety;

/// <summary>
/// Diagnostic « Sécurité thermique GPU » : armée ou non, ses seuils, les températures qu'elle suit et son dernier
/// déclenchement. Dit aussi quand elle ne peut rien surveiller (ce PC ne publie pas la température du GPU).
/// </summary>
public sealed class GpuThermalSafetyRowProvider : ICompatibilityRowProvider
{
    public const string RowTitle = "Sécurité thermique GPU";

    private readonly GpuControlService _gpu;

    public GpuThermalSafetyRowProvider(GpuControlService gpu) => _gpu = gpu;

    public string Title => RowTitle;

    public IReadOnlyList<CompatibilityRow> GetRows() => [BuildRow(_gpu.Vendor is not null, _gpu.ThermalSafety)];

    /// <summary>La ligne, isolée ici pour être testée sans carte.</summary>
    public static CompatibilityRow BuildRow(bool gpuControllable, GpuThermalSafety safety)
    {
        if (!gpuControllable)
        {
            return new CompatibilityRow(RowTitle, "Sans objet",
                "Aucun GPU pilotable : l'app ne pose aucun overclock, il n'y a rien à retirer.", true);
        }

        string limits = string.Join(", ", safety.Limits.Select(l =>
            $"{l.Sensor} ≥ {l.ThresholdC.ToString("0", CultureInfo.CurrentCulture)} °C pendant {l.Delay.TotalSeconds:0} s"));
        string followed = $"Suivi : cœur {Temperature(safety.LastCoreTempC, safety.CoreEverRead)}, " +
                          $"point chaud {Temperature(safety.LastHotSpotTempC, safety.HotSpotEverRead)}.";
        string lastTrip = safety.LastTripMessage is { } trip && safety.LastTripAt is { } at
            ? $" Dernier déclenchement à {at:HH:mm:ss} : {trip}"
            : "";

        bool monitorable = safety.CoreEverRead || safety.HotSpotEverRead;
        (string status, bool ok) = (safety.IsArmed, monitorable) switch
        {
            (true, true) => ("Armée", true),
            (true, false) => ("Armée, sans température", false),
            (false, true) => ("En attente", true),
            _ => ("En attente, sans température", true),
        };

        string state = safety.IsArmed
            ? $"Un réglage relevé par l'app tient : la carte revient d'origine si {limits}. Le point chaud ne compte que s'il est lu."
            : $"Aucun réglage relevé par l'app : rien à surveiller. Seuils quand elle s'arme : {limits}.";
        if (!monitorable)
        {
            state += " Aucune température du GPU n'a encore été lue sur ce PC : sans elle, la sécurité ne peut rien surveiller.";
        }

        return new CompatibilityRow(RowTitle, status, $"{state} {followed}{lastTrip}", ok);
    }

    private static string Temperature(float? value, bool everRead)
        => value is { } t
            ? $"{t.ToString("0", CultureInfo.CurrentCulture)} °C"
            : everRead ? "-- (pas lue à ce relevé)" : "N/D (ce GPU ne la publie pas, ou pas encore lue)";
}
