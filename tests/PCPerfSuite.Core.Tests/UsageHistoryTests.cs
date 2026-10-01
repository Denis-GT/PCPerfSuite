using System.Text;
using PCPerfSuite.Core.Profiles;

namespace PCPerfSuite.Core.Tests;

/// <summary>Historique d'usage : agrégats pondérés par le temps, percentiles exacts, rétention, lecture tolérante
/// d'usage.json et écriture atomique.</summary>
public sealed class UsageHistoryTests : IDisposable
{
    private const string Game = @"C:\Jeux\game.exe";
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private static UsageObservation Obs(DateTimeOffset t, string usage = ProfileGroupUsage.Office, float? cpuTemp = 50, float? gpuTemp = 40,
        float? cpu = 20, float? gpu = 10, bool battery = false, string? app = Game, bool fullscreen = false)
        => new(t, usage, cpu, gpu, cpuTemp, gpuTemp, battery, app, fullscreen);

    private static UsageHistory NewHistory() => new(TimeZoneInfo.Utc);

    [Fact]
    public void Chaque_releve_compte_pour_le_temps_qu_il_represente()
    {
        UsageHistory history = NewHistory();
        history.Record(Obs(T0));
        history.Record(Obs(T0.AddSeconds(1)));
        history.Record(Obs(T0.AddSeconds(6)));
        history.Record(Obs(T0.AddSeconds(26)));
        history.Record(Obs(T0.AddMinutes(10)));

        // 1 (premier) + 1 + 5 + 5 (plafond) + 1 (après un trou)
        Assert.Equal(13, history.StatsFor(ProfileGroupUsage.Office).Seconds, 3);
    }

    [Fact]
    public void Un_releve_repete_n_est_compte_qu_une_fois()
    {
        UsageHistory history = NewHistory();
        history.Record(Obs(T0));
        history.Record(Obs(T0));

        Assert.Equal(1, history.StatsFor(ProfileGroupUsage.Office).Seconds, 3);
    }

    [Fact]
    public void Les_percentiles_de_temperature_sont_exacts()
    {
        UsageHistory history = NewHistory();
        for (int i = 0; i < 100; i++)
        {
            float temp = i < 50 ? 40 : i < 95 ? 60 : 85;
            history.Record(Obs(T0.AddSeconds(i), ProfileGroupUsage.HeavyGaming, cpuTemp: temp, gpuTemp: temp + 5));
        }

        UsageStats stats = history.StatsFor(ProfileGroupUsage.HeavyGaming);

        Assert.Equal(40, stats.CpuTempP50);
        Assert.Equal(60, stats.CpuTempP95);
        Assert.Equal(65, stats.GpuTempP95);
        Assert.Equal(100, stats.CpuTempSeconds, 3);
        Assert.Equal(20, stats.CpuLoadAverage!.Value, 3);
    }

    [Fact]
    public void Un_capteur_absent_ne_compte_pas()
    {
        UsageHistory history = NewHistory();
        history.Record(Obs(T0, cpuTemp: null, gpuTemp: float.NaN, cpu: null, gpu: null));

        UsageStats stats = history.StatsFor(ProfileGroupUsage.Office);

        Assert.Equal(1, stats.Seconds, 3);
        Assert.Null(stats.CpuTempP95);
        Assert.Null(stats.GpuTempP50);
        Assert.Null(stats.CpuLoadAverage);
    }

    [Fact]
    public void Un_usage_sans_releve_donne_des_statistiques_vides()
        => Assert.Equal(0, NewHistory().StatsFor(ProfileGroupUsage.LightGaming).Seconds);

    [Fact]
    public void Les_applications_vues_gardent_leur_temps_par_usage()
    {
        UsageHistory history = NewHistory();
        history.Record(Obs(T0, ProfileGroupUsage.Office));
        history.Record(Obs(T0.AddSeconds(1), ProfileGroupUsage.HeavyGaming, fullscreen: true));
        history.Record(Obs(T0.AddSeconds(2), ProfileGroupUsage.HeavyGaming, app: @"c:\JEUX\game.exe", fullscreen: true));

        UsageAppRecord app = Assert.Single(history.Apps);
        Assert.Equal(1, app.Seconds![ProfileGroupUsage.Office], 3);
        Assert.Equal(2, app.Seconds[ProfileGroupUsage.HeavyGaming], 3);
        Assert.Equal(2, app.FullscreenSeconds, 3);
        Assert.Equal(T0.AddSeconds(2), app.LastSeenUtc);
    }

    [Fact]
    public void Au_dela_de_200_applications_la_plus_ancienne_est_oubliee()
    {
        UsageHistory history = NewHistory();
        for (int i = 0; i <= UsageHistory.MaxApps; i++)
        {
            history.Record(Obs(T0.AddSeconds(i), app: $@"C:\Apps\app{i}.exe"));
        }

        Assert.Equal(UsageHistory.MaxApps, history.Apps.Count);
        Assert.DoesNotContain(history.Apps, a => a.Path.EndsWith(@"\app0.exe", StringComparison.Ordinal));
    }

    [Fact]
    public void Ce_qui_a_plus_de_30_jours_est_ecarte()
    {
        UsageHistory history = NewHistory();
        history.Record(Obs(T0.AddDays(-40), app: @"C:\Vieux\vieux.exe"));
        history.AddJournal(new AutoSwitchJournalEntry { TimeUtc = T0.AddDays(-35), Kind = AutoSwitchJournalKinds.Switch });
        history.Record(Obs(T0));
        history.AddJournal(new AutoSwitchJournalEntry { TimeUtc = T0, Kind = AutoSwitchJournalKinds.Switch });

        history.Prune(T0);

        Assert.Equal(1, history.DaysWithData);
        Assert.Single(history.Apps);
        Assert.Single(history.Journal);
    }

    [Fact]
    public void Le_journal_garde_les_200_dernieres_lignes()
    {
        UsageHistory history = NewHistory();
        for (int i = 0; i < UsageHistory.MaxJournal + 10; i++)
        {
            history.AddJournal(new AutoSwitchJournalEntry { TimeUtc = T0.AddSeconds(i), Kind = AutoSwitchJournalKinds.Switch, Reason = i.ToString() });
        }

        Assert.Equal(UsageHistory.MaxJournal, history.Journal.Count);
        Assert.Equal("10", history.Journal[0].Reason);
    }

    [Fact]
    public void Les_incidents_non_signales_se_retrouvent_puis_s_acquittent()
    {
        UsageHistory history = NewHistory();
        history.AddJournal(new AutoSwitchJournalEntry { TimeUtc = T0, Kind = AutoSwitchJournalKinds.Incident });
        history.AddJournal(new AutoSwitchJournalEntry { TimeUtc = T0, Kind = AutoSwitchJournalKinds.Switch });

        IReadOnlyList<AutoSwitchJournalEntry> incidents = history.UnacknowledgedIncidents();
        Assert.Single(incidents);

        history.Acknowledge(incidents);
        Assert.Empty(history.UnacknowledgedIncidents());
    }

    [Fact]
    public void Un_aller_retour_par_le_fichier_garde_tout()
    {
        UsageHistory history = NewHistory();
        history.Record(Obs(T0, ProfileGroupUsage.HeavyGaming, cpuTemp: 70));
        history.Record(Obs(T0.AddSeconds(1), ProfileGroupUsage.HeavyGaming, cpuTemp: 72));
        history.AddJournal(new AutoSwitchJournalEntry { TimeUtc = T0, Kind = AutoSwitchJournalKinds.Switch, NotApplied = ["GPU : refusé"] });
        string path = _dir.File("usage.json");

        Assert.Null(UsageHistoryStore.Write(path, UsageHistoryStore.Serialize(history.ToFile())));
        UsageHistoryRead read = UsageHistoryStore.Read(path);
        UsageHistory reloaded = UsageHistory.FromFile(read.File, T0, TimeZoneInfo.Utc);

        Assert.Null(read.Problem);
        Assert.Equal(72, reloaded.StatsFor(ProfileGroupUsage.HeavyGaming).CpuTempP95);
        Assert.Equal("GPU : refusé", Assert.Single(Assert.Single(reloaded.Journal).NotApplied!));
        Assert.Single(reloaded.Apps);
        Assert.False(reloaded.IsDirty);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Un_fichier_absent_donne_un_historique_vide()
    {
        UsageHistoryRead read = UsageHistoryStore.Read(_dir.File("absent.json"));

        Assert.Null(read.Problem);
        Assert.Equal(0, UsageHistory.FromFile(read.File, T0).DaysWithData);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ pas du json")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"Days\":\"oups\"}")]
    public void Un_fichier_vide_ou_illisible_repart_de_zero(string content)
    {
        string path = _dir.File("usage.json");
        File.WriteAllText(path, content, Encoding.UTF8);

        UsageHistoryRead read = UsageHistoryStore.Read(path);

        Assert.NotNull(read.Problem);
        Assert.Equal(0, UsageHistory.FromFile(read.File, T0).DaysWithData);
        if (content.Length > 0) Assert.True(File.Exists(path + ".corrupt"));
    }

    [Fact]
    public void Des_valeurs_hors_bornes_ou_nulles_sont_ecartees_et_les_doublons_fusionnes()
    {
        const string json = """
        {
          "Version": 1,
          "Days": [
            null,
            { "Date": "2026-10-01", "Usage": "bureautique", "Seconds": 60, "CpuLoadSum": 1200, "CpuLoadSeconds": 60, "CpuTemps": { "45": 60, "abc": 5, "300": 5, "-3": 5 } },
            { "Date": "2026-10-01", "Usage": "bureautique", "Seconds": 40, "CpuTemps": { "55": 40 } },
            { "Date": "pas une date", "Usage": "bureautique", "Seconds": 10 },
            { "Date": "2026-10-01", "Usage": "", "Seconds": 10 },
            { "Date": "2026-10-01", "Usage": "gaming-leger", "Seconds": -5 }
          ],
          "Apps": [ null, { "Path": "relatif.exe" }, { "Path": "C:\\Jeux\\game.exe", "LastSeenUtc": "2026-10-01T10:00:00+00:00", "Seconds": { "bureautique": 10, "gaming-leger": -1 } } ],
          "Journal": [ null, { "TimeUtc": "2026-10-01T12:00:00+00:00", "Kind": "bascule" } ]
        }
        """;
        string path = _dir.File("usage.json");
        File.WriteAllText(path, json, Encoding.UTF8);

        UsageHistoryRead read = UsageHistoryStore.Read(path);
        UsageHistory history = UsageHistory.FromFile(read.File, T0, TimeZoneInfo.Utc);
        UsageStats office = history.StatsFor(ProfileGroupUsage.Office);

        Assert.Equal(100, office.Seconds, 3);
        Assert.Equal(1, office.Days);
        Assert.Equal(100, office.CpuTempSeconds, 3);
        Assert.Equal(55, office.CpuTempP95);
        Assert.Equal(0, history.StatsFor(ProfileGroupUsage.LightGaming).Seconds);
        UsageAppRecord app = Assert.Single(history.Apps);
        Assert.False(app.Seconds!.ContainsKey(ProfileGroupUsage.LightGaming));
        Assert.Single(history.Journal);
    }

    [Fact]
    public void Un_fichier_d_une_version_plus_recente_est_relu_en_partie_et_ses_ajouts_gardes()
    {
        const string json = """{"Version":7,"Futur":{"x":1},"Journal":[{"TimeUtc":"2026-10-01T12:00:00+00:00","Kind":"nouveau-genre"}]}""";
        string path = _dir.File("usage.json");
        File.WriteAllText(path, json, Encoding.UTF8);

        UsageHistoryRead read = UsageHistoryStore.Read(path);
        UsageHistory history = UsageHistory.FromFile(read.File, T0);
        string written = Encoding.UTF8.GetString(UsageHistoryStore.Serialize(history.ToFile()));

        Assert.Contains("version plus récente", read.Problem);
        Assert.Single(history.Journal);
        Assert.Contains("\"Futur\"", written);
        Assert.Contains("\"Version\":1", written);
    }

    [Fact]
    public void Effacer_vide_tout_l_historique()
    {
        UsageHistory history = NewHistory();
        history.Record(Obs(T0));
        history.AddJournal(new AutoSwitchJournalEntry { TimeUtc = T0 });
        history.MarkSaved();

        history.Clear();

        Assert.Equal(0, history.DaysWithData);
        Assert.Empty(history.Apps);
        Assert.Empty(history.Journal);
        Assert.True(history.IsDirty);
    }

    [Fact]
    public void L_ecrivain_garde_le_dernier_enregistrement_demande()
    {
        var written = new List<string>();
        var gate = new ManualResetEventSlim(false);
        var writer = new UsageHistoryWriter(() => "usage.json", (_, bytes) =>
        {
            gate.Wait(TimeSpan.FromSeconds(5));
            lock (written) written.Add(Encoding.UTF8.GetString(bytes));
            return null;
        });

        writer.Save(Encoding.UTF8.GetBytes("1"));
        writer.Save(Encoding.UTF8.GetBytes("2"));
        writer.Save(Encoding.UTF8.GetBytes("3"));
        gate.Set();

        Assert.True(writer.Flush(TimeSpan.FromSeconds(5)));
        Assert.Equal("3", written[^1]);
        Assert.True(written.Count <= 2);
        Assert.Null(writer.LastError);
    }

    [Fact]
    public void Une_ecriture_impossible_est_signalee_sans_lever()
    {
        // Un dossier à la place du fichier : le remplacement échoue.
        string path = _dir.File("usage.json");
        Directory.CreateDirectory(path);

        Assert.NotNull(UsageHistoryStore.Write(path, Encoding.UTF8.GetBytes("{}")));
        Assert.False(File.Exists(path + ".tmp"));
    }
}
