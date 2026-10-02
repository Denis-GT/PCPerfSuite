using PCPerfSuite.Core.Profiles;

namespace PCPerfSuite.Core.Tests;

/// <summary>Politique de bascule : verrouillage, pause manuelle, pause pendant le bail d'un autre demandeur, prudence
/// après incident, délai entre deux bascules.</summary>
public sealed class AutoSwitchPolicyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 20, 0, 0, TimeSpan.Zero);

    private static readonly ProfileGroup Heavy = new()
    {
        Id = "heavy", Name = "Jeu exigeant (auto)", Usage = ProfileGroupUsage.HeavyGaming, Origin = ProfileGroupOrigin.Generated,
        Revision = 3, Gpu = ProfileGroupEditor.GpuOrigin(),
    };

    private static readonly UsageVerdict HeavyVerdict = new(new UsageTarget(ProfileGroupUsage.HeavyGaming, null),
        UsageReasonKind.BorderlessFullscreen, "plein écran, GPU à 95 %", T0);

    private static AutoSwitchContext Context(
        bool enabled = true, UsageVerdict? verdict = null, UsageGroupChoice? choice = null, AutoSwitchHandled? handled = null,
        DateTimeOffset? lastSwitch = null, string? locked = null, string? lease = null, DateTimeOffset? manualUntil = null,
        bool tuning = false, bool applying = false, DateTimeOffset? warmup = null, DateTimeOffset? now = null)
        => new(enabled, now ?? T0, warmup ?? T0.AddMinutes(-5), verdict ?? HeavyVerdict, choice ?? new UsageGroupChoice(Heavy, false, 1),
            handled, lastSwitch, locked, lease, manualUntil, "réglage manuel dans l'onglet GPU", tuning, applying);

    [Fact]
    public void Un_nouvel_usage_avec_son_groupe_fait_basculer()
    {
        AutoSwitchDecision decision = AutoSwitchPolicy.Decide(Context());

        Assert.True(decision.ShouldSwitch);
        Assert.Same(Heavy, decision.Group);
    }

    [Fact]
    public void Desactivee_rien_ne_bascule()
        => Assert.Equal(AutoSwitchState.Off, AutoSwitchPolicy.Decide(Context(enabled: false)).State);

    [Fact]
    public void Apres_une_securite_thermique_la_bascule_est_verrouillee()
    {
        AutoSwitchDecision decision = AutoSwitchPolicy.Decide(Context(locked: "processeur à 98 °C pendant 15 s"));

        Assert.Equal(AutoSwitchState.Locked, decision.State);
        Assert.False(decision.ShouldSwitch);
        Assert.Contains("Déverrouiller", decision.Text);
    }

    [Fact]
    public void Le_verrou_l_emporte_sur_tout_le_reste()
        => Assert.Equal(AutoSwitchState.Locked, AutoSwitchPolicy.Decide(Context(locked: "GPU", lease: "Réglages pilotés par le bench", applying: true)).State);

    [Fact]
    public void Tant_qu_un_autre_demandeur_tient_le_bail_la_bascule_est_en_pause()
    {
        AutoSwitchDecision decision = AutoSwitchPolicy.Decide(Context(lease: "Réglages pilotés par le bench depuis 14:05 (mesure en cours)."));

        Assert.Equal(AutoSwitchState.Paused, decision.State);
        Assert.Contains("réglages pilotés par le bench", decision.Text);
    }

    [Fact]
    public void Un_reglage_manuel_met_en_pause_jusqu_a_la_fin_du_delai()
    {
        Assert.Equal(AutoSwitchState.Paused, AutoSwitchPolicy.Decide(Context(manualUntil: T0.AddMinutes(4))).State);
        Assert.True(AutoSwitchPolicy.Decide(Context(manualUntil: T0.AddMinutes(-1))).ShouldSwitch);
    }

    [Fact]
    public void Apres_la_pause_l_usage_adopte_n_est_pas_reimpose()
    {
        // Pendant la pause, la bascule a « adopté » l'usage en cours : son groupe n'est pas reposé à la fin de la pause.
        var adopted = new AutoSwitchHandled(HeavyVerdict.Target, Heavy.Id, Heavy.Revision);

        Assert.Equal(AutoSwitchState.Idle, AutoSwitchPolicy.Decide(Context(handled: adopted, manualUntil: T0.AddMinutes(-1))).State);
    }

    [Fact]
    public void Le_reglage_d_un_groupe_dans_un_onglet_met_en_pause()
        => Assert.Equal(AutoSwitchState.Paused, AutoSwitchPolicy.Decide(Context(tuning: true)).State);

    [Fact]
    public void Une_application_en_cours_fait_attendre()
        => Assert.Equal(AutoSwitchState.Busy, AutoSwitchPolicy.Decide(Context(applying: true)).State);

    [Fact]
    public void Rien_avant_la_fin_du_demarrage()
        => Assert.Equal(AutoSwitchState.Waiting, AutoSwitchPolicy.Decide(Context(warmup: T0.AddSeconds(30))).State);

    [Fact]
    public void Sans_verdict_ni_groupe_rien_ne_bascule()
    {
        Assert.Equal(AutoSwitchState.Waiting, AutoSwitchPolicy.Decide(Context() with { Verdict = null }).State);

        AutoSwitchDecision none = AutoSwitchPolicy.Decide(Context(choice: UsageGroupChoice.None));
        Assert.Equal(AutoSwitchState.Waiting, none.State);
        Assert.Contains("aucun groupe", none.Text);
    }

    [Fact]
    public void Un_groupe_suspendu_apres_un_incident_n_est_jamais_repose_d_emblee()
    {
        AutoSwitchDecision decision = AutoSwitchPolicy.Decide(Context(choice: new UsageGroupChoice(Heavy, Suspended: true, 1)));

        Assert.Equal(AutoSwitchState.Waiting, decision.State);
        Assert.False(decision.ShouldSwitch);
        Assert.Contains("suspendu", decision.Text);
    }

    [Fact]
    public void Le_groupe_deja_en_place_ne_rebascule_pas()
    {
        var handled = new AutoSwitchHandled(HeavyVerdict.Target, Heavy.Id, Heavy.Revision);

        Assert.Equal(AutoSwitchState.Idle, AutoSwitchPolicy.Decide(Context(handled: handled)).State);
    }

    [Fact]
    public void Un_groupe_modifie_ou_regenere_depuis_est_repose()
    {
        var handled = new AutoSwitchHandled(HeavyVerdict.Target, Heavy.Id, Heavy.Revision - 1);

        Assert.True(AutoSwitchPolicy.Decide(Context(handled: handled)).ShouldSwitch);
    }

    [Fact]
    public void Au_plus_une_bascule_toutes_les_deux_minutes()
    {
        var handled = new AutoSwitchHandled(UsageTarget.Office, "office", 1);

        AutoSwitchDecision soon = AutoSwitchPolicy.Decide(Context(handled: handled, lastSwitch: T0.AddSeconds(-90)));
        Assert.Equal(AutoSwitchState.Waiting, soon.State);
        Assert.Contains("possible à", soon.Text);

        Assert.True(AutoSwitchPolicy.Decide(Context(handled: handled, lastSwitch: T0 - AutoSwitchPolicy.MinInterval)).ShouldSwitch);
    }

    [Fact]
    public void Une_horloge_reculee_ne_bloque_pas_la_bascule()
        => Assert.True(AutoSwitchPolicy.Decide(Context(lastSwitch: T0.AddHours(1))).ShouldSwitch);

    [Fact]
    public void Les_raisons_ont_un_libelle_sans_nom_d_application()
    {
        foreach (UsageReasonKind reason in Enum.GetValues<UsageReasonKind>())
        {
            Assert.NotEqual("raison inconnue", UsageReasons.Label(reason));
        }

        Assert.Equal("règle d'application", UsageReasons.Label(nameof(UsageReasonKind.Rule)));
        Assert.Equal("raison inconnue", UsageReasons.Label("Futur"));
        Assert.Equal("raison inconnue", UsageReasons.Label((string?)null));
    }
}
