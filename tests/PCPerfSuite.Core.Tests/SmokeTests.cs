using PCPerfSuite.Core.Hardware;

namespace PCPerfSuite.Core.Tests;

/// <summary>Vérifie que le projet de tests référence bien Core et s'exécute : les tests métier s'ajoutent
/// avec chaque fonctionnalité qu'ils protègent.</summary>
public class SmokeTests
{
    [Fact]
    public void Une_courbe_est_plate_avant_son_premier_point()
    {
        var points = new List<FanCurvePoint> { new() { TempC = 40, Percent = 30 }, new() { TempC = 60, Percent = 80 } };

        Assert.Equal(30, FanCurveMath.Evaluate(points, 20));
    }
}
