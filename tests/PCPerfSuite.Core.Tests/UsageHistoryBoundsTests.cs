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
    public void Une_horloge_en_retard_au_lancement_n_efface_rien()
    {
        // Pile du BIOS morte : Windows démarre au 01/01/2020, avant la synchronisation de l'heure.
        DateTimeOffset behind = new(2020, 1, 1, 8, 0, 0, TimeSpan.Zero);
        var file = new UsageHistoryFile
        {
            Days = [Day("2026-09-15"), Day("2026-09-30")],
            Apps = [new UsageAppRecord { Path = Game, LastSeenUtc = T0, Seconds = new() { [ProfileGroupUsage.Office] = 60 } }],
            Journal = [new AutoSwitchJournalEntry { TimeUtc = T0, Kind = AutoSwitchJournalKinds.Switch }],
        };

        UsageHistory history = UsageHistory.FromFile(file, behind, TimeZoneInfo.Utc);

        Assert.True(history.ClockBehind);
        Assert.False(history.ChangedOnLoad);
        Assert.Equal(2, history.DaysWithData);
        Assert.Equal(T0, Assert.Single(history.Apps).LastSeenUtc);
        Assert.Single(history.Journal);

        // Tant que l'heure retarde, l'élagage de la session ne touche à rien ; remise à l'heure, il reprend.
        history.Prune(behind.AddMinutes(5));
        Assert.Equal(2, history.DaysWithData);
        history.Prune(T0.AddDays(20));
        Assert.False(history.ClockBehind);
        Assert.Equal(1, history.DaysWithData);
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
    public void Ce_qui_a_plus_de_30_jours_dans_le_fichier_est_a_reecrire_meme_bascule_desactivee()
    {
        var file = new UsageHistoryFile
        {
            Days = [Day("2026-08-01"), Day("2026-09-30")],
            Apps = [new UsageAppRecord { Path = Game, LastSeenUtc = T0.AddDays(-45), Seconds = new() }],
        };
        Assert.Null(UsageHistoryStore.Write(UsagePath, UsageHistoryStore.Serialize(file)));

        UsageHistory pruned = UsageHistory.FromRead(UsageHistoryStore.Read(UsagePath), T0, TimeZoneInfo.Utc)!;

        Assert.True(pruned.ChangedOnLoad);
        Assert.Equal(1, pruned.DaysWithData);
        Assert.Empty(pruned.Apps);
    }

    [Fact]
    public void Un_fichier_sans_rien_a_ecarter_n_est_pas_a_reecrire()
    {
        var file = new UsageHistoryFile { Days = [Day("2026-09-30")] };

        Assert.False(UsageHistory.FromFile(file, T0, TimeZoneInfo.Utc).ChangedOnLoad);
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
