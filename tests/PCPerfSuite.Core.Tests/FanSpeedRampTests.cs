using PCPerfSuite.Core.Hardware;

namespace PCPerfSuite.Core.Tests;

/// <summary>
/// La vitesse de changement de régime : la consigne envoyée rejoint celle de la courbe à un rythme borné, en montée
/// comme en descente, sans jamais dépasser la cible ni retenir la protection thermique.
/// </summary>
public class FanSpeedRampTests
{
    private const float Unlimited = FanSpeedRamp.MaxPercentPerSecond;

    private static TimeSpan At(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void Sans_limite_la_consigne_suit_la_courbe_d_un_coup()
    {
        var ramp = new FanSpeedRamp();

        Assert.Equal(30, ramp.Step(30, Unlimited, Unlimited, currentPercent: 30, At(0)));
        Assert.Equal(90, ramp.Step(90, Unlimited, Unlimited, currentPercent: 30, At(0.1)));
        Assert.Equal(20, ramp.Step(20, Unlimited, Unlimited, currentPercent: 90, At(0.2)));
    }

    [Fact]
    public void La_montee_avance_au_rythme_de_l_acceleration()
    {
        var ramp = new FanSpeedRamp();
        ramp.Step(30, 10, 2, currentPercent: 30, At(0));

        Assert.Equal(40, ramp.Step(80, 10, 2, currentPercent: 30, At(1)), 3);
        Assert.Equal(55, ramp.Step(80, 10, 2, currentPercent: 40, At(2.5)), 3);
    }

    [Fact]
    public void La_descente_avance_au_rythme_de_la_deceleration()
    {
        var ramp = new FanSpeedRamp();
        ramp.Step(80, 10, 2, currentPercent: 80, At(0));

        Assert.Equal(78, ramp.Step(30, 10, 2, currentPercent: 80, At(1)), 3);
        Assert.Equal(70, ramp.Step(30, 10, 2, currentPercent: 78, At(5)), 3);
    }

    [Fact]
    public void La_rampe_ne_depasse_jamais_la_cible()
    {
        var ramp = new FanSpeedRamp();
        ramp.Step(50, 10, 10, currentPercent: 50, At(0));

        Assert.Equal(52, ramp.Step(52, 10, 10, currentPercent: 50, At(10)));
        Assert.Equal(49, ramp.Step(49, 10, 10, currentPercent: 52, At(20)));
    }

    [Fact]
    public void Le_rythme_depend_du_temps_ecoule_pas_du_nombre_de_releves()
    {
        // Dix relevés d'un dixième de seconde ou un relevé d'une seconde : même chemin parcouru.
        var fast = new FanSpeedRamp();
        var slow = new FanSpeedRamp();
        fast.Step(20, 10, 10, currentPercent: 20, At(0));
        slow.Step(20, 10, 10, currentPercent: 20, At(0));

        float afterTenTicks = 0;
        for (int i = 1; i <= 10; i++) afterTenTicks = fast.Step(100, 10, 10, currentPercent: null, At(i / 10.0));
        float afterOneTick = slow.Step(100, 10, 10, currentPercent: null, At(1));

        Assert.Equal(30, afterTenTicks, 3);
        Assert.Equal(30, afterOneTick, 3);
    }

    [Fact]
    public void Au_premier_releve_la_rampe_part_de_la_vitesse_lue()
    {
        // Le BIOS laissait le ventilateur à 40 % : la courbe demande 80 %, il y monte depuis 40 %, sans sauter.
        var ramp = new FanSpeedRamp();

        Assert.Equal(40, ramp.Step(80, 10, 10, currentPercent: 40, At(0)));
        Assert.Equal(50, ramp.Step(80, 10, 10, currentPercent: 40, At(1)), 3);
    }

    [Fact]
    public void Sans_vitesse_lue_la_rampe_part_de_la_cible()
    {
        var ramp = new FanSpeedRamp();

        Assert.Equal(65, ramp.Step(65, 10, 10, currentPercent: null, At(0)));
    }

    [Fact]
    public void Un_ventilateur_a_l_arret_repart_directement_a_sa_consigne()
    {
        // À 5 %, 10 %… la plupart des ventilateurs ne démarrent pas : la rampe ne s'applique pas au redémarrage.
        var ramp = new FanSpeedRamp();
        ramp.Step(0, 5, 5, currentPercent: 0, At(0));

        Assert.Equal(35, ramp.Step(35, 5, 5, currentPercent: 0, At(1)));
    }

    [Fact]
    public void Une_consigne_posee_sans_rampe_sert_de_depart_a_la_redescente()
    {
        // Protection thermique : 100 % d'un coup, puis la redescente reste progressive.
        var ramp = new FanSpeedRamp();
        ramp.Step(40, 10, 5, currentPercent: 40, At(0));
        ramp.Jump(100, At(1));

        Assert.Equal(95, ramp.Step(40, 10, 5, currentPercent: 100, At(2)), 3);
    }

    [Fact]
    public void Apres_une_remise_a_zero_la_rampe_repart_de_la_vitesse_lue()
    {
        var ramp = new FanSpeedRamp();
        ramp.Step(90, 10, 10, currentPercent: 90, At(0));
        ramp.Reset();

        Assert.Equal(45, ramp.Step(70, 10, 10, currentPercent: 45, At(1)));
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(250f)]
    public void Un_rythme_illisible_ou_trop_grand_ne_limite_rien(float rate)
    {
        Assert.Equal(90, FanSpeedRamp.Advance(30, 90, rate, rate, seconds: 0.1));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-5f)]
    public void Un_rythme_nul_ou_negatif_ne_fige_jamais_le_ventilateur(float rate)
    {
        Assert.True(FanSpeedRamp.Advance(30, 90, rate, rate, seconds: 1) > 30);
    }

    [Fact]
    public void Une_horloge_qui_ne_bouge_pas_ne_fait_pas_avancer_la_rampe()
    {
        Assert.Equal(30, FanSpeedRamp.Advance(30, 90, 10, 10, seconds: 0));
        Assert.Equal(30, FanSpeedRamp.Advance(30, 90, 10, 10, seconds: -1));
    }
}
