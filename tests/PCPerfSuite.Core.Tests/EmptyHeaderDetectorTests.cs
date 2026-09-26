using PCPerfSuite.Core.Hardware.Fans;

namespace PCPerfSuite.Core.Tests;

public class EmptyHeaderDetectorTests
{
    private const string Id = "/lpc/nct6798d/0/control/0";

    private static bool Observe(EmptyHeaderDetector detector, float? rpm, float? percent, string id = Id, FanCategory category = FanCategory.Unidentified)
        => detector.Observe(id, category, rpm, percent);

    [Fact]
    public void Un_ventilateur_qui_tourne_n_est_pas_vide()
        => Assert.False(Observe(new EmptyHeaderDetector(), rpm: 900, percent: 40));

    [Fact]
    public void Zero_tr_min_avec_une_commande_active_est_un_connecteur_vide()
        => Assert.True(Observe(new EmptyHeaderDetector(), rpm: 0, percent: 50));

    [Fact]
    public void Avec_une_commande_a_zero_on_ne_conclut_rien()
    {
        // Ventilateur arrêté exprès (mode 0 dB du BIOS, arrêt à froid de l'app) : il reste visible.
        Assert.False(Observe(new EmptyHeaderDetector(), rpm: 0, percent: 0));
    }

    [Fact]
    public void La_commande_doit_depasser_le_seuil()
    {
        var detector = new EmptyHeaderDetector();

        Assert.False(Observe(detector, rpm: 0, percent: EmptyHeaderDetector.DrivenPercent));
        Assert.True(Observe(detector, rpm: 0, percent: EmptyHeaderDetector.DrivenPercent + 1));
    }

    [Fact]
    public void Une_vitesse_non_lue_ne_permet_pas_de_conclure()
    {
        var detector = new EmptyHeaderDetector();

        Assert.False(Observe(detector, rpm: null, percent: 60));
        Assert.False(Observe(detector, rpm: 0, percent: null));
    }

    [Fact]
    public void Une_lecture_parasite_sous_le_seuil_ne_compte_pas_comme_une_rotation()
    {
        var detector = new EmptyHeaderDetector();

        Assert.True(Observe(detector, rpm: EmptyHeaderDetector.SpinningRpm, percent: 50));
        Assert.False(Observe(detector, rpm: EmptyHeaderDetector.SpinningRpm + 1, percent: 50));
    }

    [Fact]
    public void Un_ventilateur_qui_se_met_a_tourner_n_est_plus_jamais_classe_vide()
    {
        var detector = new EmptyHeaderDetector();

        Assert.True(Observe(detector, rpm: 0, percent: 50));   // branché à chaud plus tard...
        Assert.False(Observe(detector, rpm: 800, percent: 50)); // ...il tourne : il remonte dans sa catégorie
        Assert.False(Observe(detector, rpm: 0, percent: 50));   // et y reste, même s'il s'arrête ensuite
    }

    [Fact]
    public void Un_ventilateur_gpu_n_est_jamais_classe_vide()
    {
        // Les ventilateurs d'une carte graphique s'arrêtent au repos : c'est normal.
        Assert.False(Observe(new EmptyHeaderDetector(), rpm: 0, percent: 60, id: "gpu:1", category: FanCategory.Gpu));
    }

    [Fact]
    public void Chaque_ventilateur_a_son_propre_historique()
    {
        var detector = new EmptyHeaderDetector();

        Observe(detector, rpm: 900, percent: 50, id: "a");

        Assert.True(Observe(detector, rpm: 0, percent: 50, id: "b"));
        Assert.False(Observe(detector, rpm: 0, percent: 50, id: "a"));
    }
}
