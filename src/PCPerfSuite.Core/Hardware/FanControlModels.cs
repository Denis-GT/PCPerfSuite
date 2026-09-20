namespace PCPerfSuite.Core.Hardware;

public enum FanControlMode
{
    Auto,
    Manual,
    Curve,
}

/// <summary>Capteur de température utilisé comme entrée de la courbe.</summary>
public enum FanTempSource
{
    CpuPackage,
    GpuCore,
    MotherboardSystem,

    /// <summary>La plus chaude entre CPU et GPU — le bon choix pour un ventilateur de boîtier, qui doit
    /// suivre celui des deux qui chauffe.</summary>
    HottestOfCpuGpu,
}

/// <summary>Ce qui sait piloter un ventilateur : la puce Super I/O de la carte mère
/// (HardwareMonitorService) ou le GPU via NVAPI (GpuControlService). Même contrat des deux côtés pour
/// que l'onglet Ventilateurs les traite exactement pareil.</summary>
public interface IFanController
{
    bool TrySetPercent(string fanId, float percent);

    /// <summary>Rend le ventilateur à sa régulation d'origine (BIOS ou VBIOS).</summary>
    bool TrySetAuto(string fanId);
}

public sealed class FanCurvePoint
{
    public float TempC { get; set; }
    public float Percent { get; set; }
}

/// <summary>Configuration persistée d'un ventilateur pilotable, identifiée par le capteur de contrôle
/// LibreHardwareMonitor (stable tant que la carte mère ne change pas) ou par l'identifiant du cooler
/// GPU ("gpu:0").</summary>
public sealed class FanCurveConfig
{
    public required string ControlSensorId { get; init; }
    public FanControlMode Mode { get; set; } = FanControlMode.Auto;
    public float ManualPercent { get; set; } = 50;
    public FanTempSource Source { get; set; } = FanTempSource.CpuPackage;
    public List<FanCurvePoint> Points { get; set; } = FanCurveMath.EquilibrePoints();

    /// <summary>Le ventilateur ne ralentit qu'après cette baisse de température, pour éviter qu'il
    /// monte et descende en boucle autour d'un point de la courbe.</summary>
    public float HysteresisC { get; set; } = 3;

    /// <summary>Bornes appliquées après la courbe : un ventilateur qui cale sous 25% ou qu'on ne veut
    /// jamais entendre à fond se règle ici.</summary>
    public float MinPercent { get; set; }

    public float MaxPercent { get; set; } = 100;

    /// <summary>Arrêt complet du ventilateur (0 RPM) sous cette température — null = jamais à l'arrêt.
    /// Tous les ventilateurs ne redémarrent pas proprement : à utiliser en connaissance de cause.</summary>
    public float? StopBelowTempC { get; set; }
}

/// <summary>Interpolation linéaire d'une courbe temp→% : plate avant le premier point et après le
/// dernier, interpolée entre les deux points encadrants sinon.</summary>
public static class FanCurveMath
{
    public static float Evaluate(IReadOnlyList<FanCurvePoint> points, float tempC)
    {
        if (points.Count == 0) return 50;
        List<FanCurvePoint> sorted = points.OrderBy(p => p.TempC).ToList();

        if (tempC <= sorted[0].TempC) return sorted[0].Percent;
        if (tempC >= sorted[^1].TempC) return sorted[^1].Percent;

        for (int i = 0; i < sorted.Count - 1; i++)
        {
            FanCurvePoint a = sorted[i];
            FanCurvePoint b = sorted[i + 1];
            if (tempC < a.TempC || tempC > b.TempC) continue;

            float span = b.TempC - a.TempC;
            if (span <= 0) return a.Percent;

            float t = (tempC - a.TempC) / span;
            return a.Percent + t * (b.Percent - a.Percent);
        }

        return sorted[^1].Percent;
    }

    public static List<FanCurvePoint> SilencieuxPoints() => new()
    {
        new FanCurvePoint { TempC = 30, Percent = 20 },
        new FanCurvePoint { TempC = 45, Percent = 30 },
        new FanCurvePoint { TempC = 55, Percent = 45 },
        new FanCurvePoint { TempC = 65, Percent = 65 },
        new FanCurvePoint { TempC = 75, Percent = 90 },
    };

    public static List<FanCurvePoint> EquilibrePoints() => new()
    {
        new FanCurvePoint { TempC = 30, Percent = 30 },
        new FanCurvePoint { TempC = 40, Percent = 40 },
        new FanCurvePoint { TempC = 50, Percent = 55 },
        new FanCurvePoint { TempC = 60, Percent = 75 },
        new FanCurvePoint { TempC = 70, Percent = 100 },
    };

    public static List<FanCurvePoint> PerfPoints() => new()
    {
        new FanCurvePoint { TempC = 30, Percent = 45 },
        new FanCurvePoint { TempC = 40, Percent = 60 },
        new FanCurvePoint { TempC = 50, Percent = 75 },
        new FanCurvePoint { TempC = 60, Percent = 90 },
        new FanCurvePoint { TempC = 70, Percent = 100 },
    };
}

/// <summary>
/// Applique une courbe en tenant compte de l'hystérésis : la consigne ne redescend qu'une fois la
/// température retombée d'au moins HysteresisC sous celle qui avait servi à la calculer. Sans ça, une
/// température qui oscille d'un degré autour d'un point de la courbe fait "pomper" le ventilateur.
/// Un état par ventilateur, d'où une petite classe plutôt qu'une fonction pure.
/// </summary>
public sealed class FanCurveRegulator
{
    private float? _referenceTempC;
    private float _target;

    /// <summary>Ventilateur actuellement arrêté par le seuil « arrêt à froid ». Voir Evaluate : la sortie
    /// de l'arrêt a sa propre hystérésis.</summary>
    private bool _stopped;

    public float Evaluate(IReadOnlyList<FanCurvePoint> points, float tempC, FanCurveConfig config)
    {
        bool recompute = _referenceTempC is not { } reference
                         || tempC >= reference
                         || reference - tempC >= Math.Max(0, config.HysteresisC);

        if (recompute)
        {
            _referenceTempC = tempC;
            _target = FanCurveMath.Evaluate(points, tempC);
        }

        float referenceTemp = _referenceTempC ?? tempC;

        // Arrêt à froid, avec son hystérésis à lui : on s'arrête sous le seuil, mais on ne repart qu'une
        // fois remonté d'un cran au-dessus. Sans ça, une température qui oscille autour du seuil fait
        // démarrer et stopper le ventilateur à chaque relevé — le bruit le plus pénible qui soit.
        if (config.StopBelowTempC is { } stop)
        {
            float restart = stop + Math.Max(0, config.HysteresisC);
            _stopped = _stopped ? referenceTemp < restart : referenceTemp < stop;
            if (_stopped) return 0;
        }
        else
        {
            _stopped = false;
        }

        float min = Math.Clamp(config.MinPercent, 0, 100);
        float max = Math.Clamp(config.MaxPercent, min, 100);
        return Math.Clamp(_target, min, max);
    }

    /// <summary>À appeler quand la courbe ou les bornes changent : la prochaine consigne repart de la
    /// température courante au lieu de rester sur l'ancienne référence.</summary>
    public void Reset()
    {
        _referenceTempC = null;
        _stopped = false;
    }
}
