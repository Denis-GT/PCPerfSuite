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
}

public sealed class FanCurvePoint
{
    public float TempC { get; set; }
    public float Percent { get; set; }
}

/// <summary>Configuration persistée d'un ventilateur pilotable, identifiée par le capteur de contrôle
/// LibreHardwareMonitor (stable tant que la carte mère ne change pas).</summary>
public sealed class FanCurveConfig
{
    public required string ControlSensorId { get; init; }
    public FanControlMode Mode { get; set; } = FanControlMode.Auto;
    public float ManualPercent { get; set; } = 50;
    public FanTempSource Source { get; set; } = FanTempSource.CpuPackage;
    public List<FanCurvePoint> Points { get; set; } = FanCurveMath.EquilibrePoints();
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
