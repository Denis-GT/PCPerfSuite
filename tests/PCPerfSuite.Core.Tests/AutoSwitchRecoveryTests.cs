using PCPerfSuite.Core.Compatibility;
using PCPerfSuite.Core.Profiles;
using PCPerfSuite.Core.Safety;
using PCPerfSuite.Core.Safety.Events;

namespace PCPerfSuite.Core.Tests;

/// <summary>Prudence après incident : une bascule suivie d'un arrêt anormal, d'un écran bleu ou d'un TDR est notée au
/// journal des bascules ; la ligne du diagnostic ne nomme aucune application.</summary>
public sealed class AutoSwitchRecoveryTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 20, 0, 0, TimeSpan.Zero);
    private readonly TempDirectory _dir = new();
    private readonly ManualClock _clock = new(T0.AddHours(1));

    public void Dispose() => _dir.Dispose();

    private string UsagePath => _dir.File("usage.json");

    private AutoSwitchRecoveryHandler Handler() => new(() => UsagePath, id => id == "g1" ? "Jeu exigeant (auto)" : null, _clock);

    private static RecoveredEntry Recovered(IncidentQualificationKind kind, string? requester = AutoSwitchRequester.Id, IReadOnlyList<Incident>? incidents = null)
    {
        var values = new Dictionary<string, string> { ["groupe"] = "g1", ["gpu-oc"] = "oui", ["watts"] = "non", ["etat-demarrage"] = "non" };
        if (requester is not null) values[ProfileGroupProbation.RequesterKey] = requester;
        var entry = new SessionJournalEntry(Guid.NewGuid(), ProfileGroupProbation.Component, ProfileGroupProbation.ApplyAction,
            values, T0, T0, 4242, SessionEntryState.InProgress, null, T0);
        return new RecoveredEntry(entry, new IncidentQualification(kind, incidents ?? [], IncidentClassifier.Label(kind)));
    }

    private UsageHistory Reload() => UsageHistory.FromFile(UsageHistoryStore.Read(UsagePath).File, _clock.GetUtcNow());

    [Fact]
    public void Un_ecran_bleu_apres_une_bascule_est_note_au_journal_des_bascules()
    {
        string? note = Handler().Handle([Recovered(IncidentQualificationKind.BlueScreen)]);

        Assert.NotNull(note);
        AutoSwitchJournalEntry incident = Assert.Single(Reload().UnacknowledgedIncidents());
        Assert.Equal("g1", incident.GroupId);
        Assert.Equal("Jeu exigeant (auto)", incident.GroupName);
        Assert.Contains("suspendu", incident.Reason);
    }

    [Fact]
    public void Un_TDR_peu_apres_la_bascule_compte()
    {
        Incident tdr = new(IncidentKind.DisplayDriverReset, T0.AddMinutes(8), "Display 4101");

        Assert.NotNull(Handler().Handle([Recovered(IncidentQualificationKind.Interrupted, incidents: [tdr])]));
        Assert.Contains("TDR", Assert.Single(Reload().Journal).Reason);
    }

    [Fact]
    public void Une_application_par_un_autre_demandeur_n_est_pas_une_bascule()
    {
        Assert.Null(Handler().Handle([Recovered(IncidentQualificationKind.BlueScreen, requester: "manuel")]));
        Assert.Null(Handler().Handle([Recovered(IncidentQualificationKind.BlueScreen, requester: null)]));
        Assert.False(File.Exists(UsagePath));
    }

    [Fact]
    public void Un_arret_normal_ne_compte_pas()
    {
        Assert.Null(Handler().Handle([Recovered(IncidentQualificationKind.CleanShutdown)]));
        Assert.False(File.Exists(UsagePath));
    }

    [Fact]
    public void L_historique_existant_est_garde()
    {
        var history = new UsageHistory(TimeZoneInfo.Utc);
        history.AddJournal(new AutoSwitchJournalEntry { TimeUtc = T0, Kind = AutoSwitchJournalKinds.Switch });
        UsageHistoryStore.Write(UsagePath, UsageHistoryStore.Serialize(history.ToFile()));

        Handler().Handle([Recovered(IncidentQualificationKind.PowerLoss)]);

        Assert.Equal(2, Reload().Journal.Count);
    }

    [Fact]
    public void Le_gestionnaire_passe_apres_celui_des_groupes_sur_le_meme_composant()
    {
        AutoSwitchRecoveryHandler handler = Handler();

        Assert.Equal(RecoveryStage.AutoSwitch, handler.Stage);
        Assert.Equal([ProfileGroupProbation.Component], handler.Components);
        Assert.True(RecoveryStage.AutoSwitch > RecoveryStage.ProfileGroups);
    }

    // ---- Ligne du diagnostic ----

    [Fact]
    public void Desactivee_la_ligne_le_dit()
    {
        var provider = new AutoSwitchRowProvider(() => new AutoSwitchStatus(false, AutoSwitchState.Off, "", null, null, 0, 0));

        Assert.Equal("désactivée", Assert.Single(provider.GetRows()).Status);
    }

    [Fact]
    public void La_ligne_dit_le_dernier_changement_sans_nom_d_application()
    {
        var last = new AutoSwitchJournalEntry
        {
            TimeUtc = T0,
            Kind = AutoSwitchJournalKinds.Switch,
            Usage = ProfileGroupUsage.HeavyGaming,
            GroupName = "Jeu exigeant (auto)",
            ReasonKind = nameof(UsageReasonKind.Rule),
            Reason = @"règle « game » (C:\Jeux\game.exe)",
            NotApplied = ["Carte graphique : overclock non posé, renonciation Intel non acceptée"],
        };
        var provider = new AutoSwitchRowProvider(() => new AutoSwitchStatus(true, AutoSwitchState.Idle, "Jeu exigeant : groupe en place.", last, null, 2, 3));

        CompatibilityRow row = Assert.Single(provider.GetRows());

        Assert.Equal("activée", row.Status);
        Assert.Contains("Jeu exigeant", row.Detail);
        Assert.Contains("règle d'application", row.Detail);
        Assert.Contains("renonciation Intel", row.Detail);
        Assert.Contains("2 règles", row.Detail);
        Assert.DoesNotContain("game.exe", row.Detail);
        Assert.DoesNotContain(@"C:\", row.Detail);
        Assert.True(row.IsSupported);
    }

    [Fact]
    public void Verrouillee_ou_apres_un_incident_la_ligne_est_en_alerte()
    {
        var incident = new AutoSwitchJournalEntry { TimeUtc = T0, Kind = AutoSwitchJournalKinds.Incident, GroupName = "Jeu exigeant (auto)" };

        CompatibilityRow locked = Assert.Single(new AutoSwitchRowProvider(() =>
            new AutoSwitchStatus(true, AutoSwitchState.Locked, "Verrouillée", null, null, 0, 0)).GetRows());
        CompatibilityRow afterIncident = Assert.Single(new AutoSwitchRowProvider(() =>
            new AutoSwitchStatus(true, AutoSwitchState.Idle, "", null, incident, 0, 0)).GetRows());

        Assert.Equal("verrouillée", locked.Status);
        Assert.False(locked.IsSupported);
        Assert.False(afterIncident.IsSupported);
        Assert.Contains("groupe suspendu", afterIncident.Detail);
    }
}
