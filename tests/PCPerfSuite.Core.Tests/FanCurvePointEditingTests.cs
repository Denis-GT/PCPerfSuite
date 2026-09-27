using PCPerfSuite.Core.Hardware;

namespace PCPerfSuite.Core.Tests;

public class FanCurvePointEditingTests
{
    private static FanCurvePoint Point(float temp, float percent) => new() { TempC = temp, Percent = percent };

    [Fact]
    public void Le_nouveau_point_coupe_le_plus_grand_ecart_a_la_hauteur_de_la_courbe()
    {
        // Écarts : 20→30 (10), 30→70 (40), 70→85 (15). Le plus grand est au milieu : 50 °C, à mi-hauteur.
        var points = new List<FanCurvePoint> { Point(30, 20), Point(70, 80) };

        FanCurvePoint? created = FanCurveMath.TryCreatePoint(points);

        Assert.NotNull(created);
        Assert.Equal(50, created!.TempC);
        Assert.Equal(50, created.Percent);
    }

    [Fact]
    public void Au_bout_de_la_plage_le_nouveau_point_prolonge_la_partie_plate()
    {
        // Écart 70→85 (15) plus large que tous les autres : la courbe y est plate à 100 %.
        List<FanCurvePoint> points = FanCurveMath.EquilibrePoints();

        FanCurvePoint created = FanCurveMath.TryCreatePoint(points)!;

        Assert.InRange(created.TempC, 71, 84);
        Assert.Equal(100, created.Percent);
    }

    [Fact]
    public void Ajouter_un_point_ne_change_pas_la_forme_de_la_courbe()
    {
        var points = new List<FanCurvePoint> { Point(25, 15), Point(45, 35), Point(60, 90), Point(80, 40) };
        var before = new List<FanCurvePoint>(points);

        FanCurvePoint created = FanCurveMath.TryCreatePoint(points)!;
        FanCurveMath.InsertSorted(points, created);

        // Le pourcentage du point est arrondi à l'entier : au plus un demi-point d'écart, nulle part ailleurs.
        for (float temp = FanCurveMath.MinTempC; temp <= FanCurveMath.MaxTempC; temp += 0.5f)
        {
            float expected = FanCurveMath.Evaluate(before, temp);
            float actual = FanCurveMath.Evaluate(points, temp);
            Assert.InRange(actual, expected - 0.51f, expected + 0.51f);
        }
    }

    [Fact]
    public void Le_nouveau_point_reste_a_distance_de_ses_voisins()
    {
        List<FanCurvePoint> points = FanCurveMath.EquilibrePoints();

        FanCurvePoint created = FanCurveMath.TryCreatePoint(points)!;

        Assert.All(points, p => Assert.True(Math.Abs(p.TempC - created.TempC) >= FanCurveMath.MinTempGap));
    }

    [Fact]
    public void Pas_de_nouveau_point_au_maximum()
    {
        List<FanCurvePoint> points = Enumerable.Range(0, FanCurveMath.MaxPoints)
            .Select(i => Point(20 + i * 4, 50)).ToList();

        Assert.Null(FanCurveMath.TryCreatePoint(points));
    }

    [Fact]
    public void Pas_de_nouveau_point_sur_une_courbe_vide()
    {
        Assert.Null(FanCurveMath.TryCreatePoint(new List<FanCurvePoint>()));
    }

    [Fact]
    public void Ajouter_en_serie_garde_la_courbe_triee_et_espacee_jusqu_au_maximum()
    {
        List<FanCurvePoint> points = FanCurveMath.EquilibrePoints();

        while (FanCurveMath.TryCreatePoint(points) is { } created)
        {
            FanCurveMath.InsertSorted(points, created);
            Assert.True(points.Count <= FanCurveMath.MaxPoints);
        }

        Assert.True(points.Count > 10, "on doit pouvoir ajouter beaucoup de points");

        for (int i = 1; i < points.Count; i++)
        {
            Assert.True(points[i].TempC - points[i - 1].TempC >= FanCurveMath.MinTempGap,
                $"points {i - 1} et {i} trop proches ({points[i - 1].TempC} et {points[i].TempC})");
        }

        Assert.All(points, p => Assert.InRange(p.TempC, FanCurveMath.MinTempC, FanCurveMath.MaxTempC));
    }

    // La courbe de départ est 30, 40, 50, 60, 70. À température égale, le nouveau point passe après l'existant.
    [Theory]
    [InlineData(20, 0)]
    [InlineData(35, 1)]
    [InlineData(47, 2)]
    [InlineData(85, 5)]
    [InlineData(30, 1)]
    [InlineData(70, 5)]
    public void Un_point_s_insere_a_sa_place_dans_l_ordre(float temp, int expectedIndex)
    {
        List<FanCurvePoint> points = FanCurveMath.EquilibrePoints();

        int index = FanCurveMath.InsertSorted(points, Point(temp, 10));

        Assert.Equal(expectedIndex, index);
        Assert.Equal(temp, points[index].TempC);
        Assert.Equal(points.OrderBy(p => p.TempC).Select(p => p.TempC), points.Select(p => p.TempC));
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    [InlineData(16, true)]
    public void On_ne_retire_pas_en_dessous_du_minimum(int count, bool expected)
    {
        Assert.Equal(expected, FanCurveMath.CanRemovePoint(count));
    }

    [Fact]
    public void Les_bornes_de_la_courbe_sont_coherentes()
    {
        Assert.True(FanCurveMath.MinPoints >= 2);
        Assert.True(FanCurveMath.MaxPoints > FanCurveMath.MinPoints);

        // Assez de place, dans la plage, pour le maximum de points à l'écart minimal.
        Assert.True((FanCurveMath.MaxPoints - 1) * FanCurveMath.MinTempGap <= FanCurveMath.MaxTempC - FanCurveMath.MinTempC);
    }
}
