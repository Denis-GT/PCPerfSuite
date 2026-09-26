namespace PCPerfSuite.Core.Hardware.Fans;

/// <summary>
/// Toutes les courbes de ventilation enregistrées sous un nom : pour chaque ventilateur, son mode, sa courbe
/// et ses réglages de régulation (une <see cref="FanCurveConfig"/>).
///
/// Un profil est volontairement tolérant : il a pu être écrit sur une autre machine, avant qu'on débranche
/// un ventilateur, ou être modifié à la main dans settings.json. Une entrée qui ne correspond à aucun
/// ventilateur de ce PC est ignorée à l'application et signalée plutôt que subie ; une valeur hors limites
/// est ramenée dans ce que l'onglet accepte (voir <see cref="FanProfileMatcher.Sanitize"/>).
/// </summary>
public sealed class FanProfile
{
    public string Name { get; set; } = "Profil";

    /// <summary>Une entrée par ventilateur, identifiée par <see cref="FanCurveConfig.ControlSensorId"/>.</summary>
    public List<FanCurveConfig> Fans { get; set; } = new();

    /// <summary>Nom affiché de chaque ventilateur au moment de l'enregistrement, clé = identifiant du ventilateur.
    /// Sert à dire *quel* ventilateur manque quand le profil vient d'un autre PC, où l'identifiant seul
    /// (« /lpc/nct6798d/0/control/5 ») ne dit rien à personne.</summary>
    public Dictionary<string, string> FanNames { get; set; } = new();
}

/// <summary>Ce qui, dans un profil, correspond aux ventilateurs de ce PC et ce qui n'y correspond pas.</summary>
/// <param name="Applicable">Entrées dont le ventilateur est présent ici.</param>
/// <param name="Missing">Entrées dont le ventilateur n'existe pas (ou plus) ici.</param>
/// <param name="NotInProfile">Ventilateurs de ce PC que le profil ne mentionne pas : ils restent tels quels.</param>
public sealed record FanProfileMatch(
    IReadOnlyList<FanCurveConfig> Applicable,
    IReadOnlyList<FanCurveConfig> Missing,
    IReadOnlyList<string> NotInProfile);

/// <summary>Une entrée de profil ramenée dans les limites de l'onglet. <see cref="Config"/> est null quand rien
/// d'exploitable n'en reste : <see cref="Notes"/> dit alors pourquoi.</summary>
public sealed record SanitizedFanCurve(FanCurveConfig? Config, IReadOnlyList<string> Notes)
{
    public bool IsUsable => Config is not null;
}

public static class FanProfileMatcher
{
    public const float MaxHysteresisC = 10;
    public const float MinStopTempC = 20;
    public const float MaxStopTempC = 70;

    /// <summary>Rapproche un profil des ventilateurs présents. Une entrée en double (fichier édité à la main) ne
    /// compte qu'une fois, la première ; une entrée sans identifiant est ignorée.</summary>
    public static FanProfileMatch Match(FanProfile profile, IReadOnlyCollection<string> presentFanIds)
    {
        var present = new HashSet<string>(presentFanIds, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var applicable = new List<FanCurveConfig>();
        var missing = new List<FanCurveConfig>();

        foreach (FanCurveConfig? entry in profile.Fans)
        {
            if (entry is null || string.IsNullOrEmpty(entry.ControlSensorId)) continue;
            if (!seen.Add(entry.ControlSensorId)) continue;

            (present.Contains(entry.ControlSensorId) ? applicable : missing).Add(entry);
        }

        List<string> notInProfile = presentFanIds.Where(id => !seen.Contains(id)).ToList();
        return new FanProfileMatch(applicable, missing, notInProfile);
    }

    /// <summary>
    /// Ramène une entrée dans ce que l'onglet accepte, sans jamais lever : énumération inconnue (profil d'une version
    /// ultérieure), pourcentages ou températures hors plage, valeurs non numériques, points désordonnés, trop proches
    /// ou trop nombreux. Renvoie une copie ; l'entrée d'origine n'est pas modifiée.
    ///
    /// Une courbe qui garde moins de <see cref="FanCurveMath.MinPoints"/> points valides est inutilisable en mode
    /// Courbe (le ventilateur n'aurait plus de consigne) : l'entrée est alors refusée. Dans un autre mode, la courbe
    /// n'est pas en service et on lui pose la courbe Équilibré.
    /// </summary>
    public static SanitizedFanCurve Sanitize(FanCurveConfig entry)
    {
        var notes = new List<string>();

        FanControlMode mode = entry.Mode;
        if (!Enum.IsDefined(mode))
        {
            mode = FanControlMode.Auto;
            notes.Add("mode inconnu, remplacé par Auto");
        }

        FanTempSource source = entry.Source;
        if (!Enum.IsDefined(source))
        {
            source = FanTempSource.CpuPackage;
            notes.Add("température suivie inconnue, remplacée par le CPU");
        }

        bool percentsFixed = false;
        float manual = Bound(entry.ManualPercent, 0, 100, 50, ref percentsFixed);
        float min = Bound(entry.MinPercent, 0, 100, 0, ref percentsFixed);
        float max = Bound(entry.MaxPercent, 0, 100, 100, ref percentsFixed);
        if (max < min)
        {
            max = min;
            percentsFixed = true;
        }

        if (percentsFixed) notes.Add("vitesses ramenées dans 0-100 % (min ≤ max)");

        bool hysteresisFixed = false;
        float hysteresis = Bound(entry.HysteresisC, 0, MaxHysteresisC, 3, ref hysteresisFixed);
        if (hysteresisFixed) notes.Add($"hystérésis ramenée dans 0-{MaxHysteresisC:0} °C");

        float? stop = null;
        if (entry.StopBelowTempC is { } rawStop)
        {
            bool stopFixed = false;
            stop = Bound(rawStop, MinStopTempC, MaxStopTempC, 40, ref stopFixed);
            if (stopFixed) notes.Add($"arrêt à froid ramené dans {MinStopTempC:0}-{MaxStopTempC:0} °C");
        }

        List<FanCurvePoint> points = CleanPoints(entry.Points, out bool pointsFixed);
        if (pointsFixed) notes.Add("points de la courbe corrigés (hors plage, désordonnés, trop proches ou en trop)");

        if (points.Count < FanCurveMath.MinPoints)
        {
            if (mode == FanControlMode.Curve)
            {
                notes.Add($"courbe inutilisable (moins de {FanCurveMath.MinPoints} points valides)");
                return new SanitizedFanCurve(null, notes);
            }

            points = FanCurveMath.EquilibrePoints();
            notes.Add("courbe absente, courbe Équilibré posée");
        }

        var sanitized = new FanCurveConfig
        {
            ControlSensorId = entry.ControlSensorId,
            Mode = mode,
            ManualPercent = manual,
            Source = source,
            Points = points,
            HysteresisC = hysteresis,
            MinPercent = min,
            MaxPercent = max,
            StopBelowTempC = stop,
        };

        return new SanitizedFanCurve(sanitized, notes);
    }

    /// <summary>Points valides, triés par température, espacés d'au moins <see cref="FanCurveMath.MinTempGap"/> et
    /// au plus <see cref="FanCurveMath.MaxPoints"/>. Un point non numérique est écarté ; une température ou un
    /// pourcentage hors plage est ramené au bord.</summary>
    private static List<FanCurvePoint> CleanPoints(IEnumerable<FanCurvePoint?>? source, out bool changed)
    {
        List<FanCurvePoint?> original = source?.ToList() ?? new List<FanCurvePoint?>();

        List<FanCurvePoint> ordered = original
            .Where(p => p is not null && float.IsFinite(p.TempC) && float.IsFinite(p.Percent))
            .Select(p => new FanCurvePoint
            {
                TempC = Math.Clamp(p!.TempC, FanCurveMath.MinTempC, FanCurveMath.MaxTempC),
                Percent = Math.Clamp(p.Percent, 0, 100),
            })
            .OrderBy(p => p.TempC)
            .ToList();

        var kept = new List<FanCurvePoint>();
        foreach (FanCurvePoint point in ordered)
        {
            if (kept.Count >= FanCurveMath.MaxPoints) break;
            if (kept.Count > 0 && point.TempC - kept[^1].TempC < FanCurveMath.MinTempGap) continue;

            kept.Add(point);
        }

        changed = kept.Count != original.Count
                  || kept.Where((p, i) => original[i] is not { } o || o.TempC != p.TempC || o.Percent != p.Percent).Any();
        return kept;
    }

    /// <summary>Ramène <paramref name="value"/> dans [min, max]. Une valeur non numérique donne <paramref name="fallback"/>.</summary>
    private static float Bound(float value, float min, float max, float fallback, ref bool changed)
    {
        float bounded = float.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
        if (bounded != value) changed = true;
        return bounded;
    }
}
