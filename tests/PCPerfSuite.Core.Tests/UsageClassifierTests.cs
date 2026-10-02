using PCPerfSuite.Core.Profiles;

namespace PCPerfSuite.Core.Tests;

/// <summary>Classifieur d'usage sur des séquences construites : entrée, maintien, sortie, oscillation, menu de jeu,
/// batterie, règles.</summary>
public sealed class UsageClassifierTests
{
    private const string Game = @"C:\Jeux\game.exe";
    private const string Browser = @"C:\Programmes\navigateur.exe";
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 20, 0, 0, TimeSpan.Zero);

    private readonly UsageClassifier _classifier = new();
    private DateTimeOffset _now = T0;

    private static UsageSample Desktop(DateTimeOffset t) => new(t, Browser, false, false, 8, 5, null, false);

    private static UsageSample Fullscreen(DateTimeOffset t, float gpu, double? fps = null, bool battery = false, string app = Game)
        => new(t, app, true, false, 30, gpu, fps, battery);

    /// <summary>Un relevé par seconde pendant <paramref name="seconds"/> secondes.</summary>
    private void Feed(int seconds, Func<DateTimeOffset, UsageSample> sample, int stepSeconds = 1)
    {
        for (int i = 0; i < seconds; i += stepSeconds)
        {
            _classifier.Add(sample(_now));
            _now = _now.AddSeconds(stepSeconds);
        }
    }

    private string? Usage => _classifier.Current?.Usage;

    [Fact]
    public void Le_premier_releve_donne_la_bureautique()
    {
        Assert.True(_classifier.Add(Desktop(T0)));
        Assert.Equal(ProfileGroupUsage.Office, Usage);
        Assert.Equal(UsageReasonKind.NoGame, _classifier.Current!.Reason);
    }

    [Fact]
    public void Un_jeu_plein_ecran_fait_entrer_en_jeu_apres_30_secondes_tenues()
    {
        Feed(5, Desktop);
        Feed(28, t => Fullscreen(t, gpu: 70));
        Assert.Equal(ProfileGroupUsage.Office, Usage);
        Assert.NotNull(_classifier.Pending);

        Feed(3, t => Fullscreen(t, gpu: 70));
        Assert.Equal(ProfileGroupUsage.LightGaming, Usage);
        Assert.Equal(UsageReasonKind.BorderlessFullscreen, _classifier.Current!.Reason);
        Assert.DoesNotContain("game", _classifier.Current.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Un_jeu_tres_charge_entre_directement_en_jeu_exigeant()
    {
        Feed(5, Desktop);
        Feed(32, t => Fullscreen(t, gpu: 99));

        Assert.Equal(ProfileGroupUsage.HeavyGaming, Usage);
    }

    [Fact]
    public void Le_jeu_se_maintient_puis_sort_apres_2_minutes_sans_signe()
    {
        Feed(40, t => Fullscreen(t, gpu: 70));
        Assert.Equal(ProfileGroupUsage.LightGaming, Usage);

        Feed(110, Desktop);
        Assert.Equal(ProfileGroupUsage.LightGaming, Usage);

        Feed(30, Desktop);
        Assert.Equal(ProfileGroupUsage.Office, Usage);
    }

    [Fact]
    public void Une_alternance_rapide_ne_fait_jamais_entrer_en_jeu()
    {
        for (int cycle = 0; cycle < 12; cycle++)
        {
            Feed(10, t => Fullscreen(t, gpu: 80));
            Feed(10, Desktop);
        }

        Assert.Equal(ProfileGroupUsage.Office, Usage);
    }

    [Fact]
    public void Un_creux_de_deux_releves_ne_relance_pas_l_attente()
    {
        Feed(20, t => Fullscreen(t, gpu: 70));
        Feed(2, Desktop);
        Feed(10, t => Fullscreen(t, gpu: 70));

        Assert.Equal(ProfileGroupUsage.LightGaming, Usage);
    }

    [Fact]
    public void Un_menu_de_jeu_plein_ecran_sans_RTSS_reste_en_jeu()
    {
        Feed(40, t => Fullscreen(t, gpu: 75));
        Assert.Equal(ProfileGroupUsage.LightGaming, Usage);

        // Menu : GPU presque au repos, mais le jeu reste au premier plan en plein écran, longtemps.
        Feed(600, t => Fullscreen(t, gpu: 15));

        Assert.Equal(ProfileGroupUsage.LightGaming, Usage);
        Assert.Equal(UsageReasonKind.StickyFullscreen, _classifier.LastCandidate!.Reason);
    }

    [Fact]
    public void Un_menu_avec_RTSS_reste_en_jeu()
    {
        Feed(40, t => Fullscreen(t, gpu: 75, fps: 144));
        Feed(300, t => Fullscreen(t, gpu: 20, fps: 60));

        Assert.Equal(ProfileGroupUsage.LightGaming, Usage);
        Assert.Equal(UsageReasonKind.Rtss, _classifier.LastCandidate!.Reason);
    }

    [Fact]
    public void Quitter_le_jeu_pour_une_autre_application_plein_ecran_sort_du_jeu()
    {
        Feed(40, t => Fullscreen(t, gpu: 75));

        // Une vidéo en plein écran dans le navigateur : GPU peu chargé, autre application.
        Feed(150, t => Fullscreen(t, gpu: 20, app: Browser));

        Assert.Equal(ProfileGroupUsage.Office, Usage);
    }

    [Fact]
    public void Sans_RTSS_un_jeu_fenetre_est_reconnu_a_la_charge_GPU()
    {
        Feed(70, t => new UsageSample(t, Game, false, false, 25, 75, null, false));

        Assert.Equal(ProfileGroupUsage.LightGaming, Usage);
        Assert.Equal(UsageReasonKind.GpuLoad, _classifier.Current!.Reason);
    }

    [Fact]
    public void Un_navigateur_accroche_par_RTSS_mais_GPU_au_repos_reste_en_bureautique()
    {
        Feed(120, t => new UsageSample(t, Browser, false, false, 10, 8, 60, false));

        Assert.Equal(ProfileGroupUsage.Office, Usage);
    }

    [Fact]
    public void Un_GPU_muet_ne_fait_pas_entrer_en_jeu_mais_le_plein_ecran_exclusif_oui()
    {
        Feed(120, t => new UsageSample(t, Game, true, false, 20, null, null, false));
        Assert.Equal(ProfileGroupUsage.Office, Usage);

        Feed(35, t => new UsageSample(t, Game, true, true, 20, null, null, false));
        Assert.Equal(ProfileGroupUsage.LightGaming, Usage);
        Assert.Equal(UsageReasonKind.ExclusiveFullscreen, _classifier.Current!.Reason);
    }

    [Fact]
    public void Une_charge_processeur_soutenue_hors_jeu_va_au_jeu_exigeant()
    {
        Feed(150, t => new UsageSample(t, @"C:\Outils\compilateur.exe", false, false, 92, 3, null, false));

        Assert.Equal(ProfileGroupUsage.HeavyGaming, Usage);
        Assert.Equal(UsageReasonKind.CpuLoad, _classifier.Current!.Reason);
        Assert.Contains("processeur", _classifier.Current.Detail);
    }

    [Fact]
    public void Sur_batterie_le_jeu_exigeant_est_ramene_au_jeu_leger()
    {
        Feed(40, t => Fullscreen(t, gpu: 97, battery: true));

        Assert.Equal(ProfileGroupUsage.LightGaming, Usage);
        Assert.Equal(UsageReasonKind.BatteryCap, _classifier.Current!.Reason);
    }

    [Fact]
    public void Brancher_le_secteur_remonte_au_jeu_exigeant()
    {
        Feed(40, t => Fullscreen(t, gpu: 97, battery: true));
        Feed(65, t => Fullscreen(t, gpu: 97));

        Assert.Equal(ProfileGroupUsage.HeavyGaming, Usage);
    }

    [Fact]
    public void Le_passage_du_leger_a_l_exigeant_attend_une_minute_et_le_retour_deux()
    {
        Feed(40, t => Fullscreen(t, gpu: 70));
        Assert.Equal(ProfileGroupUsage.LightGaming, Usage);

        Feed(50, t => Fullscreen(t, gpu: 98));
        Assert.Equal(ProfileGroupUsage.LightGaming, Usage);
        Feed(60, t => Fullscreen(t, gpu: 98));
        Assert.Equal(ProfileGroupUsage.HeavyGaming, Usage);

        // Entre les deux seuils (70-85 %) : l'exigeant se garde.
        Feed(200, t => Fullscreen(t, gpu: 78));
        Assert.Equal(ProfileGroupUsage.HeavyGaming, Usage);

        Feed(200, t => Fullscreen(t, gpu: 50));
        Assert.Equal(ProfileGroupUsage.LightGaming, Usage);
    }

    [Fact]
    public void Un_trou_de_veille_remet_l_attente_a_zero()
    {
        Feed(20, t => Fullscreen(t, gpu: 70));
        _now = _now.AddMinutes(5);
        Feed(20, t => Fullscreen(t, gpu: 70));
        Assert.Equal(ProfileGroupUsage.Office, Usage);

        Feed(15, t => Fullscreen(t, gpu: 70));
        Assert.Equal(ProfileGroupUsage.LightGaming, Usage);
    }

    [Fact]
    public void Une_horloge_qui_recule_ne_fige_pas_le_verdict()
    {
        // Attente d'entrée en jeu, puis l'horloge recule d'une heure (synchro NTP) : l'attente repart de la nouvelle heure
        // au lieu de rester datée dans le futur pendant une heure.
        Feed(20, t => Fullscreen(t, gpu: 70));
        _now = _now.AddHours(-1);
        Feed(32, t => Fullscreen(t, gpu: 70));

        Assert.Equal(ProfileGroupUsage.LightGaming, Usage);
    }

    [Fact]
    public void Tout_oublier_repart_de_la_bureautique()
    {
        Feed(40, t => Fullscreen(t, gpu: 97));
        Assert.Equal(ProfileGroupUsage.HeavyGaming, Usage);

        _classifier.Reset();
        Assert.Null(_classifier.Current);
        Assert.Null(_classifier.Pending);

        Feed(1, Desktop);
        Assert.Equal(ProfileGroupUsage.Office, Usage);
    }

    [Fact]
    public void Des_releves_espaces_en_mode_eco_comptent_pour_leur_duree()
    {
        // Un relevé toutes les 5 s : la moyenne GPU est couverte au bout de 30 s comme avec un relevé par seconde.
        Feed(70, t => new UsageSample(t, Game, false, false, 25, 75, null, false), stepSeconds: 5);

        Assert.Equal(ProfileGroupUsage.LightGaming, Usage);
    }

    /// <summary>Mode éco : un relevé « vide » (premier plan seulement), puis un relevé avec les charges 1 s plus tard, toutes
    /// les 6 s.</summary>
    private void FeedEco(int cycles, float gpu, double? fps = null, bool fullscreen = true)
    {
        for (int i = 0; i < cycles; i++)
        {
            _classifier.Add(new UsageSample(_now, Game, fullscreen, false, null, null, null, false));
            _now = _now.AddSeconds(1);
            _classifier.Add(new UsageSample(_now, Game, fullscreen, false, 30, gpu, fps, false));
            _now = _now.AddSeconds(5);
        }
    }

    [Fact]
    public void En_mode_eco_les_charges_espacees_suffisent_a_reconnaitre_un_jeu_exigeant()
    {
        FeedEco(25, gpu: 97);

        Assert.Equal(ProfileGroupUsage.HeavyGaming, Usage);
        Assert.NotNull(_classifier.GpuMean);
    }

    [Fact]
    public void En_mode_eco_un_releve_vide_n_interrompt_pas_l_entree_en_jeu()
    {
        FeedEco(7, gpu: 70);

        Assert.Equal(ProfileGroupUsage.LightGaming, Usage);
    }

    [Fact]
    public void Une_charge_trop_ancienne_ne_vaut_plus_pour_un_signe_instantane()
    {
        _classifier.Add(Fullscreen(_now, gpu: 90));
        _now = _now.AddSeconds(15);

        _classifier.Add(new UsageSample(_now, Game, true, false, null, null, null, false));

        Assert.Equal(UsageReasonKind.NoGame, _classifier.LastCandidate!.Reason);
    }

    [Fact]
    public void Une_regle_l_emporte_apres_10_secondes_et_sort_apres_2_minutes()
    {
        var rule = new UsageRuleTarget("r1", ProfileGroupUsage.HeavyGaming, null, "game");
        Feed(12, t => Desktop(t) with { AppKey = Game, Rule = rule });

        Assert.Equal(ProfileGroupUsage.HeavyGaming, Usage);
        Assert.Equal(UsageReasonKind.Rule, _classifier.Current!.Reason);
        Assert.Same(rule, _classifier.Current.Rule);

        Feed(100, Desktop);
        Assert.Equal(ProfileGroupUsage.HeavyGaming, Usage);
        Feed(25, Desktop);
        Assert.Equal(ProfileGroupUsage.Office, Usage);
    }

    [Fact]
    public void Une_regle_bureautique_l_emporte_sur_les_signes_de_jeu()
    {
        var rule = new UsageRuleTarget("r1", ProfileGroupUsage.Office, null, "game");
        Feed(120, t => Fullscreen(t, gpu: 95, fps: 120) with { Rule = rule });

        Assert.Equal(ProfileGroupUsage.Office, Usage);
    }

    [Fact]
    public void Une_regle_vers_un_groupe_vise_ce_groupe()
    {
        var rule = new UsageRuleTarget("r1", null, "groupe-42", "game");
        Feed(12, t => Desktop(t) with { Rule = rule });

        Assert.Equal(new UsageTarget(null, "groupe-42"), _classifier.Current!.Target);
    }

    [Fact]
    public void Une_regle_sans_cible_est_ignoree()
    {
        var rule = new UsageRuleTarget("r1", null, null, "game");
        Feed(60, t => Desktop(t) with { Rule = rule });

        Assert.Equal(UsageReasonKind.NoGame, _classifier.Current!.Reason);
    }

    [Fact]
    public void Une_charge_hors_bornes_est_ramenee_et_ne_leve_pas()
    {
        Feed(40, t => new UsageSample(t, Game, true, false, float.NaN, 250, double.NaN, false));

        Assert.True(_classifier.Current!.Target.IsGaming);
    }
}
