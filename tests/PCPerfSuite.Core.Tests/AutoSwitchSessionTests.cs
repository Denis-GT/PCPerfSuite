using PCPerfSuite.Core.Profiles;

namespace PCPerfSuite.Core.Tests;

/// <summary>Enchaînements du moteur de bascule, sans matériel : démarrage, bascule réussie ou refusée, adoption après un
/// réglage manuel, réveil, réactivation, verrou.</summary>
public sealed class AutoSwitchSessionTests
{
    private const string Game = @"C:\Jeux\game.exe";
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 20, 0, 0, TimeSpan.Zero);

    private readonly UsageClassifier _classifier = new();
    private readonly AutoSwitchSession _session;
    private readonly ProfileGroupsSettings _store = new();
    private readonly ProfileGroup _office = Group("office", ProfileGroupUsage.Office);
    private readonly ProfileGroup _heavy = Group("heavy", ProfileGroupUsage.HeavyGaming);
    private readonly ProfileGroup _light = Group("light", ProfileGroupUsage.LightGaming);
    private DateTimeOffset _now = T0;

    public AutoSwitchSessionTests()
    {
        _session = new AutoSwitchSession(_classifier, T0);
        _store.Groups.AddRange([_office, _heavy, _light]);
    }

    private static ProfileGroup Group(string id, string usage) => new()
    {
        Id = id, Name = id, Usage = usage, Origin = ProfileGroupOrigin.Generated, Gpu = ProfileGroupEditor.GpuOrigin(),
    };

    private static ProfileGroupReport Report(ProfileGroup group, string? refusal = null)
        => new(group.Id, group.Name, T0, null, [new DimensionReport(ProfileDimension.Gpu, [ReportItem.Applied("gpu", "origine")], [])], refusal, []);

    private void Desktop(int seconds) => Feed(seconds, t => new UsageSample(t, @"C:\Programmes\outil.exe", false, false, 8, 5, null, false));

    private void Gaming(int seconds) => Feed(seconds, t => new UsageSample(t, Game, true, false, 40, 97, 120, false));

    private void Feed(int seconds, Func<DateTimeOffset, UsageSample> sample)
    {
        for (int i = 0; i < seconds; i++)
        {
            _classifier.Add(sample(_now));
            _now = _now.AddSeconds(1);
        }
    }

    private AutoSwitchDecision Decide() => _session.Decide(true, _now, _store, null, false, false, out _);

    /// <summary>Fait la bascule demandée, avec le rapport donné.</summary>
    private AutoSwitchDecision SwitchWith(Func<ProfileGroup, ProfileGroupReport?> report)
    {
        AutoSwitchDecision decision = Decide();
        Assert.True(decision.ShouldSwitch, decision.Text);
        _session.BeginSwitch(_now);
        _session.EndSwitch(_classifier.Current!.Target, decision.Group!, report(decision.Group!));
        return decision;
    }

    [Fact]
    public void Rien_ne_bascule_pendant_le_demarrage()
    {
        Desktop(30);
        Assert.Equal(AutoSwitchState.Waiting, Decide().State);

        Desktop(31);
        Assert.Same(_office, Decide().Group);
    }

    [Fact]
    public void Une_bascule_reussie_tient_la_cible_pour_traitee()
    {
        Desktop(61);
        SwitchWith(g => Report(g));

        Assert.Equal(AutoSwitchState.Idle, Decide().State);
        Assert.False(_session.LastHandledAdopted);
    }

    [Fact]
    public void Une_bascule_refusee_en_bloc_ou_en_erreur_est_retentee_apres_le_delai()
    {
        Desktop(61);
        SwitchWith(g => Report(g, refusal: "réglages pilotés par le bench"));

        Assert.Equal(AutoSwitchState.Waiting, Decide().State);
        Desktop(121);
        SwitchWith(_ => null);

        Desktop(121);
        Assert.True(Decide().ShouldSwitch);
    }

    [Fact]
    public void Apres_un_reglage_manuel_l_usage_en_cours_est_adopte_et_pas_reimpose()
    {
        Desktop(61);
        Gaming(200);
        Assert.Same(_heavy, Decide().Group);

        Assert.True(_session.OnManualWrite("réglage manuel dans l'onglet GPU", _now, _store));
        Assert.False(_session.OnManualWrite("réglage manuel dans l'onglet GPU", _now, _store));
        Assert.Equal(AutoSwitchState.Paused, Decide().State);

        Gaming((int)AutoSwitchPolicy.ManualPause.TotalSeconds + 1);
        Assert.Equal(AutoSwitchState.Idle, Decide().State);
        Assert.True(_session.LastHandledAdopted);
    }

    [Fact]
    public void Un_changement_d_usage_apres_la_pause_rebascule()
    {
        Desktop(61);
        Gaming(200);
        _session.OnManualWrite("réglage manuel", _now, _store);
        Gaming((int)AutoSwitchPolicy.ManualPause.TotalSeconds + 1);

        Desktop(200);

        Assert.Same(_office, Decide().Group);
    }

    [Fact]
    public void Au_reveil_le_verdict_d_avant_la_veille_est_oublie()
    {
        Desktop(61);
        Gaming(40);
        SwitchWith(g => Report(g));
        Assert.Equal(ProfileGroupUsage.LightGaming, _classifier.Current!.Usage);

        // Veille de plusieurs heures, réveil sur le bureau.
        _now = _now.AddHours(3);
        _session.OnResume(_now);
        Desktop(61);

        AutoSwitchDecision decision = Decide();
        Assert.Same(_office, decision.Group);
        Assert.Equal(ProfileGroupUsage.Office, _classifier.Current!.Usage);
    }

    [Fact]
    public void Au_reveil_le_groupe_pose_est_repose_mais_une_adoption_est_gardee()
    {
        Desktop(61);
        Gaming(40);
        SwitchWith(g => Report(g));
        _session.OnResume(_now);
        Assert.Null(_session.LastHandled);

        Gaming(61);
        _session.OnManualWrite("réglage manuel", _now, _store);
        _session.EndManualPause();
        _session.OnResume(_now);
        Gaming(61);

        Assert.NotNull(_session.LastHandled);
        Assert.Equal(AutoSwitchState.Idle, Decide().State);
    }

    [Fact]
    public void A_la_reactivation_le_verdict_d_avant_est_oublie_et_un_jeu_a_le_temps_d_etre_reconnu()
    {
        Desktop(61);
        Gaming(40);
        SwitchWith(g => Report(g));

        // Bascule désactivée pendant des heures (le classifieur ne tourne plus), réactivée sur le bureau.
        _now = _now.AddHours(2);
        _session.OnEnabled(_now);
        Assert.Null(_classifier.Current);
        Assert.Null(_session.LastHandled);

        Desktop(30);
        Assert.Equal(AutoSwitchState.Waiting, Decide().State);
        Desktop(6);
        Assert.Same(_office, Decide().Group);
    }

    [Fact]
    public void Reactiver_en_plein_jeu_pose_le_jeu_et_pas_d_abord_la_bureautique()
    {
        _now = T0.AddMinutes(5);
        _session.OnEnabled(_now);

        Gaming((int)AutoSwitchPolicy.EnableWarmup.TotalSeconds + 1);

        Assert.Same(_heavy, Decide().Group);
        Assert.True(AutoSwitchPolicy.EnableWarmup > UsageThresholds.Default.EnterGaming);
    }

    [Fact]
    public void Le_verrou_bloque_jusqu_a_deverrouiller()
    {
        Desktop(61);
        _session.Lock("processeur : 98 °C pendant 15 s");
        Assert.Equal(AutoSwitchState.Locked, Decide().State);

        _session.Unlock();
        Assert.True(Decide().ShouldSwitch);
    }

    [Fact]
    public void Une_bascule_en_cours_fait_attendre()
    {
        Desktop(61);
        _session.BeginSwitch(_now);

        Assert.Equal(AutoSwitchState.Busy, Decide().State);
    }
}
