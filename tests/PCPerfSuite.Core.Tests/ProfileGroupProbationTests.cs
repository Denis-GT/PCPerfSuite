using PCPerfSuite.Core.PowerSettings;
using PCPerfSuite.Core.Profiles;
using PCPerfSuite.Core.Safety;
using PCPerfSuite.Core.Safety.Events;

namespace PCPerfSuite.Core.Tests;

/// <summary>Prudence au démarrage : la période probatoire d'un groupe risqué, et la reprise après un incident.</summary>
public sealed class ProfileGroupProbationTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 20, 0, 0, TimeSpan.Zero);
    private readonly TempDirectory _dir = new();
    private readonly ManualClock _clock = new(T0);
    private readonly SessionJournal _journal;
    private readonly ProfileGroupProbation _probation;

    public ProfileGroupProbationTests()
    {
        _journal = new SessionJournal(_dir.File("journal-session.jsonl"), _clock);
        _probation = new ProfileGroupProbation(_journal, _clock);
    }

    public void Dispose() => _dir.Dispose();

    private SessionJournalEntry Only() => Assert.Single(_journal.Read().Entries);

    // ---- Période probatoire ----

    [Fact]
    public void Une_application_risquee_s_inscrit_en_cours_avec_ce_qu_elle_releve()
    {
        Assert.True(_probation.Begin("g1", ProfileGroupProbation.ApplyAction, gpuRaised: true, wattsRaised: false, madeStartupState: true));

        SessionJournalEntry entry = Only();
        Assert.Equal(SessionEntryState.InProgress, entry.State);
        Assert.Equal("groupe-profils", entry.Component);
        Assert.Equal("g1", entry.Values["groupe"]);
        Assert.Equal("oui", entry.Values["gpu-oc"]);
        Assert.Equal("non", entry.Values["watts"]);
        Assert.Equal("oui", entry.Values["etat-demarrage"]);
        Assert.Equal(T0 + ProfileGroupProbation.Window, _probation.Current!.DeadlineUtc);
    }

    [Fact]
    public void Rien_de_releve_apres_l_application_clot_la_periode()
    {
        _probation.Begin("g1", ProfileGroupProbation.ApplyAction, true, false, true);

        _probation.AfterApplication(raisedNow: false);

        Assert.Equal(SessionEntryState.Completed, Only().State);
        Assert.Null(_probation.Current);
    }

    [Fact]
    public void La_periode_arrive_a_son_terme_apres_30_minutes()
    {
        _probation.Begin("g1", ProfileGroupProbation.ApplyAction, true, false, true);

        Assert.False(_probation.IsDue(T0.AddMinutes(29)));
        Assert.True(_probation.IsDue(T0.AddMinutes(30)));

        _probation.Close();
        Assert.Equal(SessionEntryState.Completed, Only().State);
    }

    [Fact]
    public void Un_nouveau_groupe_risque_clot_le_precedent_et_ouvre_sa_periode()
    {
        _probation.Begin("g1", ProfileGroupProbation.ApplyAction, true, false, true);
        _probation.Begin("g2", ProfileGroupProbation.ApplyAction, false, true, true);

        List<SessionJournalEntry> entries = _journal.Read().Entries.ToList();
        Assert.Equal(SessionEntryState.Completed, entries[0].State);
        Assert.Equal(SessionEntryState.InProgress, entries[1].State);
        Assert.Equal("g2", _probation.Current!.GroupId);
    }

    [Fact]
    public void Un_groupe_sans_risque_ne_clot_que_si_plus_rien_n_est_releve()
    {
        _probation.Begin("g1", ProfileGroupProbation.ApplyAction, true, false, true);

        _probation.NoteReplaced(stillRaised: true);
        Assert.Equal(SessionEntryState.InProgress, Only().State);

        _probation.NoteReplaced(stillRaised: false);
        Assert.Equal(SessionEntryState.Completed, Only().State);
    }

    [Fact]
    public void La_fermeture_propre_clot_terminee_jamais_echouee()
    {
        _probation.Begin("g1", ProfileGroupProbation.ApplyAction, true, false, true);

        _probation.Close();
        _probation.Close();

        Assert.Equal(SessionEntryState.Completed, Only().State);
    }

    [Fact]
    public void Une_securite_thermique_clot_echouee()
    {
        _probation.Begin("g1", ProfileGroupProbation.ApplyAction, true, false, true);

        _probation.Fail("sécurité thermique GPU");

        SessionJournalEntry entry = Only();
        Assert.Equal(SessionEntryState.Failed, entry.State);
        Assert.Equal("sécurité thermique GPU", entry.Cause);
    }

    [Fact]
    public void Sans_journal_sur_le_disque_la_periode_ne_commence_pas()
    {
        Directory.CreateDirectory(_dir.File("dossier"));
        var probation = new ProfileGroupProbation(new SessionJournal(_dir.File("dossier"), _clock), _clock);

        Assert.False(probation.Begin("g1", ProfileGroupProbation.ApplyAction, true, false, true));
        Assert.Null(probation.Current);
        Assert.NotNull(probation.LastProblem);
    }

    // ---- Politique d'incident ----

    private static RecoveredEntry Recovered(IncidentQualificationKind kind, string action = "application",
        string startup = "oui", IReadOnlyList<Incident>? incidents = null, string component = "groupe-profils")
    {
        var entry = new SessionJournalEntry(Guid.NewGuid(), component, action,
            new Dictionary<string, string> { ["groupe"] = "g1", ["gpu-oc"] = "oui", ["watts"] = "non", ["etat-demarrage"] = startup },
            T0, T0, 4242, SessionEntryState.InProgress, null, T0);
        return new RecoveredEntry(entry, new IncidentQualification(kind, incidents ?? [], IncidentClassifier.Label(kind)));
    }

    [Theory]
    [InlineData(IncidentQualificationKind.PowerLoss)]
    [InlineData(IncidentQualificationKind.ForcedShutdown)]
    [InlineData(IncidentQualificationKind.UnexpectedShutdown)]
    [InlineData(IncidentQualificationKind.BlueScreen)]
    public void Un_arret_anormal_est_impute_au_groupe(IncidentQualificationKind kind)
    {
        ProfileGroupIncidentDecision decision = ProfileGroupIncidentPolicy.Evaluate(Recovered(kind))!;

        Assert.Equal("g1", decision.GroupId);
        Assert.True(decision.GpuRaised);
        Assert.False(decision.WattsRaised);
        Assert.True(decision.MadeStartupState);
    }

    [Fact]
    public void Un_redemarrage_de_cause_inconnue_compte_aussi()
        => Assert.StartsWith("cause non établie", ProfileGroupIncidentPolicy.Evaluate(Recovered(IncidentQualificationKind.Unknown))!.Cause);

    [Theory]
    [InlineData(IncidentQualificationKind.CleanShutdown)]
    [InlineData(IncidentQualificationKind.Interrupted)]
    public void Un_arret_normal_ou_l_app_seule_ne_comptent_pas(IncidentQualificationKind kind)
        => Assert.Null(ProfileGroupIncidentPolicy.Evaluate(Recovered(kind)));

    [Fact]
    public void Un_tdr_dans_les_30_minutes_compte()
    {
        Incident tdr = new(IncidentKind.DisplayDriverReset, T0.AddMinutes(12), "Display 4101");

        ProfileGroupIncidentDecision decision = ProfileGroupIncidentPolicy.Evaluate(Recovered(IncidentQualificationKind.Interrupted, incidents: [tdr]))!;

        Assert.Equal("pilote graphique relancé (TDR) 12 min après l'application", decision.Cause);
    }

    [Fact]
    public void Un_tdr_apres_30_minutes_ne_compte_pas()
    {
        Incident tdr = new(IncidentKind.DisplayDriverReset, T0.AddMinutes(31), "Display 4101");

        Assert.Null(ProfileGroupIncidentPolicy.Evaluate(Recovered(IncidentQualificationKind.CleanShutdown, incidents: [tdr])));
    }

    [Fact]
    public void Une_autre_fonction_n_est_pas_concernee()
        => Assert.Null(ProfileGroupIncidentPolicy.Evaluate(Recovered(IncidentQualificationKind.BlueScreen, component: "test-combine")));

    // ---- Reprise au démarrage ----

    private static AppSettings SettingsWithGroup(out ProfileGroup group)
    {
        var settings = new AppSettings();
        settings.Cpu.ApplyAtStartup = true;
        settings.Gpu.ApplyOverclockAtStartup = true;
        group = new ProfileGroup { Id = "g1", Name = "Jeu" };
        settings.ProfileGroups.Groups.Add(group);
        return settings;
    }

    [Fact]
    public void Apres_un_ecran_bleu_le_gpu_n_est_plus_reapplique_et_le_groupe_est_suspendu()
    {
        AppSettings settings = SettingsWithGroup(out _);
        var handler = new ProfileGroupRecoveryHandler(mutate => mutate(settings), _clock);

        string? note = handler.Handle([Recovered(IncidentQualificationKind.BlueScreen)]);

        Assert.False(settings.Gpu.ApplyOverclockAtStartup);
        Assert.True(settings.Cpu.ApplyAtStartup);
        ProfileGroupSuspension suspension = settings.ProfileGroups.Suspensions["g1"];
        Assert.True(suspension.GpuStartupUnchecked);
        Assert.False(suspension.CpuStartupUnchecked);
        Assert.Equal(T0, suspension.SinceUtc);
        Assert.Contains("« Appliquer au démarrage » décoché : GPU", note);
    }

    [Fact]
    public void Un_groupe_qui_n_etait_pas_l_etat_de_demarrage_est_suspendu_sans_toucher_aux_cases()
    {
        AppSettings settings = SettingsWithGroup(out _);
        var handler = new ProfileGroupRecoveryHandler(mutate => mutate(settings), _clock);

        handler.Handle([Recovered(IncidentQualificationKind.PowerLoss, startup: "non")]);

        Assert.True(settings.Gpu.ApplyOverclockAtStartup);
        Assert.Contains("g1", settings.ProfileGroups.Suspensions.Keys);
    }

    [Fact]
    public void Un_groupe_supprime_depuis_garde_la_case_decochee()
    {
        var settings = new AppSettings();
        settings.Gpu.ApplyOverclockAtStartup = true;
        var handler = new ProfileGroupRecoveryHandler(mutate => mutate(settings), _clock);

        handler.Handle([Recovered(IncidentQualificationKind.BlueScreen)]);

        Assert.False(settings.Gpu.ApplyOverclockAtStartup);
        Assert.Empty(settings.ProfileGroups.Suspensions);
    }

    [Fact]
    public void Sans_incident_rien_n_est_ecrit()
    {
        int updates = 0;
        var handler = new ProfileGroupRecoveryHandler(_ => updates++, _clock);

        Assert.Null(handler.Handle([Recovered(IncidentQualificationKind.CleanShutdown)]));
        Assert.Equal(0, updates);
    }

    [Fact]
    public void Le_gestionnaire_est_inscrit_a_l_etape_des_groupes()
    {
        var handler = new ProfileGroupRecoveryHandler(_ => { }, _clock);

        Assert.Equal(RecoveryStage.ProfileGroups, handler.Stage);
        Assert.Equal(["groupe-profils"], handler.Components);
    }

    // ---- Réapplication au lancement ----

    private static AppSettings StartupSettings(bool madeStartupState = true, int storedCore = 150)
    {
        var settings = new AppSettings();
        settings.Gpu.ApplyOverclockAtStartup = true;
        settings.Gpu.CoreClockOffsetMhz = storedCore;
        settings.ProfileGroups.Groups.Add(new ProfileGroup { Id = "g1" });
        settings.ProfileGroups.Active = new ProfileGroupActiveState
        {
            GroupId = "g1",
            MadeStartupState = madeStartupState,
            GpuRaised = true,
            Gpu = new GpuRetainedValues { CoreOffsetMhz = 150 },
        };
        return settings;
    }

    [Fact]
    public void Reposer_au_lancement_l_etat_risque_du_groupe_est_une_application()
    {
        ProfileGroupStartupRisk risk = ProfileGroupStartupCheck.Evaluate(StartupSettings())!;

        Assert.Equal("g1", risk.GroupId);
        Assert.True(risk.GpuRaised);
        Assert.False(risk.WattsRaised);
    }

    [Fact]
    public void Un_onglet_modifie_depuis_n_est_plus_le_groupe()
        => Assert.Null(ProfileGroupStartupCheck.Evaluate(StartupSettings(storedCore: 100)));

    [Fact]
    public void Un_groupe_transitoire_n_est_pas_reapplique_au_lancement()
        => Assert.Null(ProfileGroupStartupCheck.Evaluate(StartupSettings(madeStartupState: false)));

    [Fact]
    public void Une_case_decochee_ne_repose_rien()
    {
        AppSettings settings = StartupSettings();
        settings.Gpu.ApplyOverclockAtStartup = false;

        Assert.Null(ProfileGroupStartupCheck.Evaluate(settings));
    }

    [Fact]
    public void Un_groupe_suspendu_n_ouvre_pas_de_periode()
    {
        AppSettings settings = StartupSettings();
        settings.ProfileGroups.Suspensions["g1"] = new ProfileGroupSuspension { Cause = "écran bleu" };

        Assert.Null(ProfileGroupStartupCheck.Evaluate(settings));
    }

    [Fact]
    public void Une_bascule_transitoire_ne_masque_pas_l_etat_de_demarrage_risque()
    {
        // Groupe risqué appliqué à la main (état de démarrage), puis bascule automatique vers un autre groupe.
        AppSettings settings = StartupSettings();
        settings.ProfileGroups.Groups.Add(new ProfileGroup { Id = "auto" });
        settings.ProfileGroups.Remember(settings.ProfileGroups.Active!);
        settings.ProfileGroups.Remember(new ProfileGroupActiveState { GroupId = "auto", MadeStartupState = false, RequesterId = "bascule-auto" });

        Assert.Equal("auto", settings.ProfileGroups.Active!.GroupId);
        ProfileGroupStartupRisk risk = ProfileGroupStartupCheck.Evaluate(settings)!;
        Assert.Equal("g1", risk.GroupId);
        Assert.True(risk.GpuRaised);
    }

    [Fact]
    public void Un_fichier_d_avant_l_etat_de_demarrage_retombe_sur_le_groupe_actif()
    {
        AppSettings settings = StartupSettings();

        Assert.Null(settings.ProfileGroups.StartupState);
        Assert.Equal("g1", settings.ProfileGroups.EffectiveStartupState!.GroupId);
    }

    [Fact]
    public void Supprimer_le_groupe_efface_aussi_son_etat_de_demarrage()
    {
        AppSettings settings = StartupSettings();
        settings.ProfileGroups.Remember(settings.ProfileGroups.Active!);

        ProfileGroupEditor.Delete(settings.ProfileGroups, "g1");

        Assert.Null(settings.ProfileGroups.StartupState);
        Assert.Null(ProfileGroupStartupCheck.Evaluate(settings));
    }

    [Fact]
    public void Normaliser_retire_un_etat_de_demarrage_orphelin_ou_transitoire()
    {
        var settings = new ProfileGroupsSettings
        {
            Groups = { new ProfileGroup { Id = "g1" } },
            StartupState = new ProfileGroupActiveState { GroupId = "disparu", MadeStartupState = true },
        };
        Assert.True(settings.Normalize());
        Assert.Null(settings.StartupState);

        settings.StartupState = new ProfileGroupActiveState { GroupId = "g1", MadeStartupState = false };
        Assert.True(settings.Normalize());
        Assert.Null(settings.StartupState);
    }

    [Fact]
    public void Le_demandeur_est_note_dans_la_ligne_du_journal()
    {
        _probation.Begin("g1", ProfileGroupProbation.ApplyAction, true, false, false, requesterId: "bascule-auto");

        Assert.Equal("bascule-auto", Only().Values[ProfileGroupProbation.RequesterKey]);
    }

    [Fact]
    public void Sans_demandeur_la_cle_est_absente()
    {
        _probation.Begin("g1", ProfileGroupProbation.ApplyAction, true, false, true);

        Assert.False(Only().Values.ContainsKey(ProfileGroupProbation.RequesterKey));
    }
}
