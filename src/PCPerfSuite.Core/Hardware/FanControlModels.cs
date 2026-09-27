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

    /// <summary>De combien de points de % par seconde la consigne peut monter (voir <see cref="FanSpeedRamp"/>).
    /// <see cref="FanSpeedRamp.MaxPercentPerSecond"/> = sans limite, le comportement d'avant ce réglage.</summary>
    public float RampUpPercentPerSecond { get; set; } = FanSpeedRamp.MaxPercentPerSecond;

    /// <summary>De combien de points de % par seconde la consigne peut descendre.</summary>
    public float RampDownPercentPerSecond { get; set; } = FanSpeedRamp.MaxPercentPerSecond;

    /// <summary>Copie indépendante, points compris : modifier un ventilateur ne doit pas modifier le profil
    /// où sa configuration a été enregistrée, ni l'inverse.</summary>
    public FanCurveConfig Clone() => new()
    {
        ControlSensorId = ControlSensorId,
        Mode = Mode,
        ManualPercent = ManualPercent,
        Source = Source,
        Points = Points.Select(p => new FanCurvePoint { TempC = p.TempC, Percent = p.Percent }).ToList(),
        HysteresisC = HysteresisC,
        MinPercent = MinPercent,
        MaxPercent = MaxPercent,
        StopBelowTempC = StopBelowTempC,
        RampUpPercentPerSecond = RampUpPercentPerSecond,
        RampDownPercentPerSecond = RampDownPercentPerSecond,
    };
}

/// <summary>Interpolation linéaire d'une courbe temp→% : plate avant le premier point et après le
/// dernier, interpolée entre les deux points encadrants sinon.</summary>
public static class FanCurveMath
{
    /// <summary>Plage de températures d'une courbe : celle de l'éditeur, et donc la seule où un point peut être posé.</summary>
    public const float MinTempC = 20;

    public const float MaxTempC = 85;

    /// <summary>Écart minimal entre deux points, pour qu'un point ne puisse pas en croiser un autre
    /// (la courbe resterait dessinable, mais deviendrait impossible à rattraper à la souris).</summary>
    public const float MinTempGap = 2;

    public const int MinPoints = 2;

    /// <summary>Au-delà, les étiquettes de température se chevauchent dans une carte en demi-largeur.</summary>
    public const int MaxPoints = 16;

    public static bool CanRemovePoint(int count) => count > MinPoints;

    /// <summary>
    /// Un nouveau point qui ne change pas la forme de la courbe : au milieu du plus grand écart de température entre
    /// deux points voisins (ou entre un bout de la plage et le point le plus proche, où la courbe est plate), à la
    /// hauteur que la courbe y a déjà. Null quand la courbe a atteint <see cref="MaxPoints"/> ou qu'aucun écart ne
    /// laisse la place d'un point à au moins <see cref="MinTempGap"/> de ses voisins.
    /// </summary>
    public static FanCurvePoint? TryCreatePoint(IReadOnlyList<FanCurvePoint> points)
    {
        if (points.Count >= MaxPoints || points.Count == 0) return null;

        List<FanCurvePoint> sorted = points.OrderBy(p => p.TempC).ToList();

        // Les intervalles entre voisins, bornes de la plage comprises, du plus large au plus étroit.
        var gaps = new List<(float From, float To)> { (MinTempC, sorted[0].TempC) };
        for (int i = 0; i < sorted.Count - 1; i++) gaps.Add((sorted[i].TempC, sorted[i + 1].TempC));
        gaps.Add((sorted[^1].TempC, MaxTempC));

        foreach ((float from, float to) in gaps.OrderByDescending(g => g.To - g.From))
        {
            // Température entière, comme celles que pose l'éditeur : l'arrondi ne doit pas rapprocher le point d'un voisin.
            float temp = MathF.Round((from + to) / 2);
            if (temp - from < MinTempGap || to - temp < MinTempGap) continue;

            return new FanCurvePoint { TempC = temp, Percent = MathF.Round(Evaluate(sorted, temp)) };
        }

        return null;
    }

    /// <summary>Insère un point à sa place dans l'ordre des températures et renvoie son rang.</summary>
    public static int InsertSorted(IList<FanCurvePoint> points, FanCurvePoint point)
    {
        int index = 0;
        while (index < points.Count && points[index].TempC <= point.TempC) index++;

        points.Insert(index, point);
        return index;
    }

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

/// <summary>
/// Vitesse de changement de régime : la consigne envoyée au ventilateur rejoint celle de la courbe à un rythme borné,
/// un pour la montée (accélération) et un pour la descente (décélération), en points de % par seconde. Un ventilateur
/// qui change de régime d'un coup s'entend bien plus qu'un qui glisse ; beaucoup préfèrent une montée rapide, pour
/// suivre la chauffe, et une descente lente, qui passe inaperçue.
///
/// Complète l'hystérésis sans la remplacer : l'hystérésis décide QUAND la consigne descend, la rampe À QUELLE
/// VITESSE le ventilateur y va. Le temps est celui qui s'est réellement écoulé entre deux relevés : la rampe ne
/// dépend pas de la cadence de rafraîchissement choisie dans le Monitoring.
///
/// Un état par ventilateur (la dernière consigne posée et son heure), d'où une petite classe.
/// </summary>
public sealed class FanSpeedRamp
{
    public const float MinPercentPerSecond = 1;

    /// <summary>À partir de cette valeur, pas de limite : la consigne suit la courbe d'un coup.</summary>
    public const float MaxPercentPerSecond = 100;

    private float? _output;
    private TimeSpan _lastStep;

    /// <summary>
    /// La consigne à poser maintenant pour aller vers <paramref name="target"/>.
    /// </summary>
    /// <param name="currentPercent">Consigne lue sur le matériel : point de départ de la rampe quand elle n'en a pas
    /// encore (lancement de l'app, passage en mode Courbe), pour qu'un ventilateur laissé à 40 % par le BIOS monte ou
    /// descende depuis 40 % au lieu de sauter. Null si elle n'est pas lue : la rampe part alors de la cible.</param>
    /// <param name="now">Horloge monotone (pas l'heure murale, qui peut reculer).</param>
    public float Step(float target, float upPercentPerSecond, float downPercentPerSecond, float? currentPercent, TimeSpan now)
    {
        if (_output is not { } previous)
        {
            previous = currentPercent is { } read && float.IsFinite(read) ? Math.Clamp(read, 0, 100) : target;
            _lastStep = now;
        }

        double seconds = Math.Max(0, (now - _lastStep).TotalSeconds);
        _lastStep = now;

        // Un ventilateur à l'arrêt repart directement à sa consigne : aux petits pourcentages par lesquels la rampe le
        // ferait passer, beaucoup de ventilateurs n'ont pas assez de couple pour démarrer.
        float next = previous <= 0 && target > 0
            ? target
            : Advance(previous, target, upPercentPerSecond, downPercentPerSecond, seconds);

        _output = next;
        return next;
    }

    /// <summary>Pose une consigne sans rampe — la protection thermique ne doit pas attendre. La suite repart de là, pour
    /// que la redescente reste progressive.</summary>
    public void Jump(float percent, TimeSpan now)
    {
        _output = percent;
        _lastStep = now;
    }

    /// <summary>Oublie la dernière consigne : la prochaine partira de ce que le matériel indique. À appeler quand
    /// quelqu'un d'autre que la rampe a changé la vitesse (mode Auto, repérage, autre mode).</summary>
    public void Reset() => _output = null;

    /// <summary>Le pas autorisé en <paramref name="seconds"/> secondes, de <paramref name="from"/> vers <paramref name="to"/>,
    /// sans jamais dépasser la cible. Un rythme illisible (fichier édité à la main) vaut « sans limite », jamais « figé ».</summary>
    public static float Advance(float from, float to, float upPercentPerSecond, float downPercentPerSecond, double seconds)
    {
        float rate = to >= from ? upPercentPerSecond : downPercentPerSecond;
        if (!float.IsFinite(rate) || rate >= MaxPercentPerSecond) return to;

        // Horloge immobile, qui recule ou illisible : on ne bouge pas, et surtout pas à l'envers.
        if (!(seconds > 0)) return from;

        float step = (float)(Math.Max(rate, MinPercentPerSecond) * seconds);
        return to > from ? Math.Min(to, from + step) : Math.Max(to, from - step);
    }
}
