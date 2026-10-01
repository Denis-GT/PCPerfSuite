namespace PCPerfSuite.Core.Profiles;

/// <summary>Sens de la limite de puissance visée par une dimension, comparée à l'état relu.</summary>
public enum PowerTrend
{
    /// <summary>La dimension ne touche à aucune limite de puissance, ou les laisse telles quelles.</summary>
    Same,

    /// <summary>Une limite monte (watts, limite de puissance ou décalage GPU).</summary>
    Up,

    /// <summary>Les limites touchées baissent toutes.</summary>
    Down,

    /// <summary>On ne sait pas (état illisible) : traité comme une montée, par prudence.</summary>
    Unknown,
}

/// <summary>
/// L'ordre d'application d'un groupe (décision de Denis, 01/10/2026) : en montée en performance, les ventilateurs
/// d'abord, pour que le refroidissement suive avant la chaleur ; en descente, le processeur et la carte graphique
/// d'abord, pour ne pas ralentir les ventilateurs sous une charge encore haute. Le sens se lit sur la limite de
/// puissance visée, comparée à l'état relu : ce critère vaut aussi pour un groupe fait à la main, sans usage.
/// Indéterminé ou mixte : ventilateurs d'abord.
/// </summary>
public static class ApplyDirection
{
    public static ApplyOrder Decide(IEnumerable<PowerTrend> trends)
    {
        List<PowerTrend> all = trends.ToList();
        bool lowers = all.Contains(PowerTrend.Down);
        bool onlyLowersOrKeeps = all.All(t => t is PowerTrend.Down or PowerTrend.Same);
        return lowers && onlyLowersOrKeeps ? ApplyOrder.FansLast : ApplyOrder.FansFirst;
    }

    /// <summary>Combine les sens de plusieurs limites d'une même dimension : une hausse l'emporte, puis l'inconnu.</summary>
    public static PowerTrend Combine(IEnumerable<PowerTrend> trends)
    {
        List<PowerTrend> all = trends.ToList();
        if (all.Contains(PowerTrend.Up)) return PowerTrend.Up;
        if (all.Contains(PowerTrend.Unknown)) return PowerTrend.Unknown;
        return all.Contains(PowerTrend.Down) ? PowerTrend.Down : PowerTrend.Same;
    }

    /// <summary>Sens d'une limite : <paramref name="target"/> comparée à <paramref name="current"/>, à
    /// <paramref name="tolerance"/> près. Une limite non visée ne compte pas ; une limite visée mais illisible est
    /// inconnue.</summary>
    public static PowerTrend Of(double? target, double? current, double tolerance)
    {
        if (target is not { } wanted) return PowerTrend.Same;
        if (current is not { } now) return PowerTrend.Unknown;
        if (wanted > now + tolerance) return PowerTrend.Up;
        if (wanted < now - tolerance) return PowerTrend.Down;
        return PowerTrend.Same;
    }
}
