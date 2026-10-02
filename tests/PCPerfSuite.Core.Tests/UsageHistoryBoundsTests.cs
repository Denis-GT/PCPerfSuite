using PCPerfSuite.Core.Profiles;

namespace PCPerfSuite.Core.Tests;

/// <summary>
/// Les plafonds et la rétention d'usage.json valent aussi pour ce que l'app n'a pas écrit elle-même (fichier gonflé,
/// horloge qui avait sauté en avant), pour une bascule désactivée, et ne cèdent pas à un saut d'horloge en session.
/// </summary>
public sealed class UsageHistoryBoundsTests : IDisposable
{
    private const string Game = @"C:\Jeux\game.exe";
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private string UsagePath => _dir.File("usage.json");

    private static UsageObservation Obs(DateTimeOffset t, string? app = Game)
        => new(t, ProfileGroupUsage.Office, 20, 10, 50, 40, false, app, false);

    private static UsageDayRecord Day(string date) => new() { Date = date, Usage = ProfileGroupUsage.Office, Seconds = 60 };

    [Fact]
    public void Une_lecture_ratee_ne_donne_aucun_historique()
    {
        var failed = new UsageHistoryRead(new UsageHistoryFile(), "lecture impossible (IOException)", Failed: true);

        Assert.Null(UsageHistory.FromRead(failed, T0));
        Assert.NotNull(UsageHistory.FromRead(new UsageHistoryRead(new UsageHistoryFile(), null), T0));
    }

    [Fact]
    public void Ce_qui_est_date_dans_le_futur_est_ecarte_ou_ramene_a_maintenant()
    {
        var file = new UsageHistoryFile
        {
            Days = [Day("2026-10-01"), Day("2031-06-01")],
            Apps = [new UsageAppRecord { Path = Game, LastSeenUtc = T0.AddYears(5), Seconds = new() { [ProfileGroupUsage.Office] = 60 } }],
            Journal = [new AutoSwitchJournalEntry { TimeUtc = T0.AddYears(5), Kind = AutoSwitchJournalKinds.Switch }],
        };

        UsageHistory history = UsageHistory.FromFile(file, T0, TimeZoneInfo.Utc);

        Assert.Equal(1, history.DaysWithData);
        Assert.Equal(T0, Assert.Single(history.Apps).LastSeenUtc);
        Assert.Equal(T0, Assert.Single(history.Journal).TimeUtc);
        Assert.True(history.ChangedOnLoad);
    }

    [Fact]
    public void Un_fichier_gonfle_garde_les_200_applications_les_plus_recentes()
    {
        var file = new UsageHistoryFile
        {
            Apps = Enumerable.Range(0, UsageHistory.MaxApps + 50)
                .Select(i => new UsageAppRecord { Path = $@"C:\Apps\app{i}.exe", LastSeenUtc = T0.AddMinutes(-i), Seconds = new() })
                .ToList(),
        };

        UsageHistory history = UsageHistory.FromFile(file, T0, TimeZoneInfo.Utc);

        Assert.Equal(UsageHistory.MaxApps, history.Apps.Count);
        Assert.Contains(history.Apps, a => a.Path.EndsWith(@"\app0.exe", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(history.Apps, a => a.Path.EndsWith($@"\app{UsageHistory.MaxApps}.exe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Un_fichier_anormalement_gros_est_mis_de_cote_sans_etre_charge()
    {
        using (FileStream stream = File.Create(UsagePath)) stream.SetLength(UsageHistoryStore.MaxFileBytes + 1);

        UsageHistoryRead read = UsageHistoryStore.Read(UsagePath);

        Assert.False(read.Failed);
        Assert.Contains("anormalement gros", read.Problem);
        Assert.False(File.Exists(UsagePath));
        Assert.True(File.Exists(UsagePath + ".corrupt"));
    }

    [Fact]
    public void Bascule_desactivee_le_fichier_est_tout_de_meme_elague()
    {
        var file = new UsageHistoryFile
        {
            Days = [Day("2026-08-01"), Day("2026-09-30")],
            Apps = [new UsageAppRecord { Path = Game, LastSeenUtc = T0.AddDays(-45), Seconds = new() }],
        };
        Assert.Null(UsageHistoryStore.Write(UsagePath, UsageHistoryStore.Serialize(file)));

        Assert.Null(UsageHistoryStore.Prune(UsagePath, T0, TimeZoneInfo.Utc));

        UsageHistory reread = UsageHistory.FromFile(UsageHistoryStore.Read(UsagePath).File, T0, TimeZoneInfo.Utc);
        Assert.Equal(1, reread.DaysWithData);
        Assert.Empty(reread.Apps);
    }

    [Fact]
    public void Un_fichier_sans_rien_a_ecarter_n_est_pas_reecrit()
    {
        var file = new UsageHistoryFile { Days = [Day("2026-09-30")] };
        Assert.Null(UsageHistoryStore.Write(UsagePath, UsageHistoryStore.Serialize(file)));
        DateTime written = File.GetLastWriteTimeUtc(UsagePath).AddMinutes(-5);
        File.SetLastWriteTimeUtc(UsagePath, written);

        UsageHistoryStore.Prune(UsagePath, T0, TimeZoneInfo.Utc);

        Assert.Equal(written, File.GetLastWriteTimeUtc(UsagePath));
    }

    [Fact]
    public void Un_saut_d_horloge_en_avant_pendant_la_session_n_efface_pas_l_historique()
    {
        long elapsedMs = 0;
        var clock = new RetentionClock(T0, () => elapsedMs);
        UsageHistory history = new(TimeZoneInfo.Utc);
        history.Record(Obs(T0.AddDays(-10)));
        history.Record(Obs(T0));

        // Dix minutes plus tard, l'horloge de Windows affiche deux mois de plus.
        elapsedMs = (long)TimeSpan.FromMinutes(10).TotalMilliseconds;
        DateTimeOffset jumped = T0.AddDays(60);
        history.Prune(clock.PruneTime(jumped));

        Assert.Equal(2, history.DaysWithData);
        Assert.Equal(T0.AddMinutes(10), clock.PruneTime(jumped));
    }

    [Fact]
    public void L_horloge_de_windows_fait_foi_tant_qu_elle_suit_le_temps_ecoule()
    {
        long elapsedMs = 0;
        var clock = new RetentionClock(T0, () => elapsedMs);

        // Une resynchronisation ordinaire, une horloge qui recule : l'heure de Windows est gardée.
        Assert.Equal(T0.AddHours(2), clock.PruneTime(T0.AddHours(2)));
        Assert.Equal(T0.AddDays(-1), clock.PruneTime(T0.AddDays(-1)));

        // L'app ouverte 40 jours (veilles comprises) : la rétention avance avec le temps réellement écoulé.
        elapsedMs = (long)TimeSpan.FromDays(40).TotalMilliseconds;
        Assert.Equal(T0.AddDays(40), clock.PruneTime(T0.AddDays(40)));
    }
}
