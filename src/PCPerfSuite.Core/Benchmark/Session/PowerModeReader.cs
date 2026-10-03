using System.Runtime.InteropServices;
using PCPerfSuite.Core.PowerSettings;

namespace PCPerfSuite.Core.Benchmark.Session;

/// <summary>Mode d'alimentation effectif (curseur de Windows 10/11) et plan actif, pour le contexte du résultat.</summary>
public sealed record PowerModeReading(Guid? OverlayScheme, string OverlayLabel, Guid? ActiveScheme, string? ActiveSchemeName, string? Problem)
{
    public string Describe()
    {
        string plan = ActiveSchemeName ?? (ActiveScheme is { } g ? g.ToString() : "plan non lu");
        return $"{OverlayLabel} · plan « {plan} »";
    }
}

/// <summary>
/// Lit le mode d'alimentation effectif par <c>PowerGetEffectiveOverlayScheme</c> (le curseur « Meilleure efficacité
/// énergétique / Équilibré / Meilleures performances »), qui change les plafonds de fréquence sans toucher au plan, et
/// le plan actif par <see cref="IPowerPlanValues"/>. Best-effort : rien ne lève.
/// </summary>
public static class PowerModeReader
{
    public static readonly Guid BestPowerEfficiency = new("961cc777-2547-4f9d-8174-7d86181b8a7a");
    public static readonly Guid BestPerformance = new("ded574b5-45a0-4f42-8737-46345c09c238");
    public static readonly Guid HighPerformanceOverlay = new("3af9b8d9-7c97-431d-ad78-34a8bfea439f");

    /// <summary>Libellé de Windows pour un GUID de superposition ; le GUID nul est « Équilibré ».</summary>
    public static string OverlayLabel(Guid? overlay)
    {
        if (overlay is null) return "mode d'alimentation non lu";
        if (overlay == Guid.Empty) return "Équilibré";
        if (overlay == BestPowerEfficiency) return "Meilleure efficacité énergétique";
        if (overlay == BestPerformance) return "Meilleures performances";
        if (overlay == HighPerformanceOverlay) return "Performances élevées";
        return $"mode inconnu ({overlay})";
    }

    public static PowerModeReading Read(IPowerPlanValues? plans = null)
    {
        Guid? overlay = null;
        string? problem = null;
        try
        {
            uint status = PowerGetEffectiveOverlayScheme(out Guid effective);
            if (status == 0) overlay = effective;
            else problem = $"PowerGetEffectiveOverlayScheme a rendu {status}";
        }
        catch (Exception ex)
        {
            problem = $"mode d'alimentation non lu ({ex.GetType().Name})";
        }

        Guid? active = null;
        string? name = null;
        try
        {
            plans ??= PowerPlanValues.Instance;
            active = plans.ActiveScheme();
            if (active is { } scheme) name = plans.FriendlyName(scheme);
        }
        catch (Exception ex)
        {
            problem = problem is null ? $"plan actif non lu ({ex.GetType().Name})" : $"{problem} ; plan actif non lu";
        }

        return new PowerModeReading(overlay, OverlayLabel(overlay), active, name, problem);
    }

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetEffectiveOverlayScheme(out Guid effectiveOverlayGuid);
}
