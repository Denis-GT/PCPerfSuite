namespace PCPerfSuite.Core.Hardware.Cpu.CoreParking;

/// <summary>Les deux valeurs d'un réglage : sur secteur et sur batterie.</summary>
public readonly record struct CoreParkingValue(uint Ac, uint Dc);

/// <summary>Une valeur à poser sur un réglage.</summary>
public sealed record CoreParkingTarget(CoreParkingSetting Setting, CoreParkingValue Value);

public enum CoreParkingPreset
{
    /// <summary>Les valeurs d'avant PCPerfSuite : c'est <see cref="CoreParkingService.RestoreAll"/>, le même chemin que
    /// « Tout rétablir ».</summary>
    WindowsOrigin,

    /// <summary>Plancher à 100 % : d'après Microsoft, plus aucun cœur n'est parqué.</summary>
    AllCoresActive,

    /// <summary>Plafond réduit : une partie des cœurs reste parquée, même en charge.</summary>
    Economy,
}

/// <summary>
/// Ce que pose chaque préréglage, en logique pure. Seuls les nombres de cœurs bougent : l'ordonnancement hybride reste
/// tel qu'il est.
/// </summary>
public static class CoreParkingPresets
{
    /// <summary>Plafond du préréglage « Économie » : la moitié des cœurs de chaque classe.</summary>
    public const uint EconomyMaxPercent = 50;

    /// <summary>
    /// Valeurs à écrire pour un préréglage, en sautant celles déjà en place et les réglages illisibles sur ce PC.
    /// <list type="bullet">
    /// <item>« Tous les cœurs actifs » : plancher et plafond à 100 %. Sur un PC à batterie, sur secteur seulement : la
    /// batterie garde sa valeur, c'est là que le parking économise le plus.</item>
    /// <item>« Économie » : plafond à <see cref="EconomyMaxPercent"/>, et plancher ramené sous ce plafond (à l'origine,
    /// ou plus bas). Sur secteur comme sur batterie.</item>
    /// <item>« Windows (origine) » : rien ici, c'est une restauration.</item>
    /// </list>
    /// </summary>
    public static IReadOnlyList<CoreParkingTarget> Targets(
        CoreParkingPreset preset,
        IReadOnlyList<CoreParkingSetting> available,
        Func<CoreParkingSetting, CoreParkingValue?> current,
        Func<CoreParkingSetting, CoreParkingValue?> origin,
        bool hasBattery)
    {
        var targets = new List<CoreParkingTarget>();
        if (preset == CoreParkingPreset.WindowsOrigin) return targets;

        foreach (CoreParkingSetting setting in available.Where(s => s.IsCoreCount))
        {
            if (current(setting) is not { } now) continue;

            CoreParkingValue wanted = preset switch
            {
                CoreParkingPreset.AllCoresActive => new CoreParkingValue(100, hasBattery ? now.Dc : 100),
                _ when setting.IsMinimum => Floor(origin(setting) ?? now),
                _ => new CoreParkingValue(EconomyMaxPercent, EconomyMaxPercent),
            };

            if (wanted != now) targets.Add(new CoreParkingTarget(setting, wanted));
        }

        return targets;
    }

    private static CoreParkingValue Floor(CoreParkingValue value)
        => new(Math.Min(value.Ac, EconomyMaxPercent), Math.Min(value.Dc, EconomyMaxPercent));
}
