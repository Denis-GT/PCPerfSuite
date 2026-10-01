using PCPerfSuite.Core.Processes;

namespace PCPerfSuite.Core.Tests;

/// <summary>Éditeur validé des exécutables : vérifié hors du fil d'interface, gardé tant que le fichier ne change pas.</summary>
public sealed class ApplicationPublisherCacheTests
{
    private const string Game = @"C:\Jeux\game.exe";
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 20, 0, 0, TimeSpan.Zero);

    private readonly ManualClock _clock = new(T0);
    private readonly List<Action> _queued = new();
    private ExecutableStamp? _stamp = new(1000, new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc));
    private string? _publisher = "Studio SAS";
    private int _verifications;

    private ApplicationPublisherCache Create() => new(
        path =>
        {
            _verifications++;
            return _publisher;
        },
        _ => _stamp,
        work => _queued.Add(work),
        _clock);

    private void RunQueued()
    {
        foreach (Action work in _queued.ToList())
        {
            _queued.Remove(work);
            work();
        }
    }

    [Fact]
    public void L_editeur_est_inconnu_tant_que_la_verification_n_a_pas_abouti()
    {
        ApplicationPublisherCache cache = Create();
        int resolved = 0;
        cache.Resolved += () => resolved++;

        Assert.Null(cache.PublisherOf(Game));
        Assert.Null(cache.PublisherOf(Game));
        Assert.Single(_queued);

        RunQueued();

        Assert.Equal(1, resolved);
        Assert.Equal("Studio SAS", cache.PublisherOf(Game));
        Assert.Equal(1, _verifications);
    }

    [Fact]
    public void Un_fichier_inchange_n_est_pas_reverifie()
    {
        ApplicationPublisherCache cache = Create();
        cache.PublisherOf(Game);
        RunQueued();

        _clock.Now = T0 + ApplicationPublisherCache.StampRecheck + TimeSpan.FromSeconds(1);

        Assert.Equal("Studio SAS", cache.PublisherOf(Game));
        Assert.Empty(_queued);
        Assert.Equal(1, _verifications);
    }

    [Fact]
    public void Un_fichier_remplace_au_meme_chemin_est_reverifie()
    {
        ApplicationPublisherCache cache = Create();
        cache.PublisherOf(Game);
        RunQueued();

        _stamp = new ExecutableStamp(2000, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));
        _publisher = null;
        _clock.Now = T0 + ApplicationPublisherCache.StampRecheck;

        Assert.Null(cache.PublisherOf(Game));
        RunQueued();
        Assert.Null(cache.PublisherOf(Game));
        Assert.Equal(2, _verifications);
    }

    [Fact]
    public void Un_fichier_absent_n_a_pas_d_editeur_et_ne_leve_pas()
    {
        _stamp = null;
        ApplicationPublisherCache cache = Create();

        cache.PublisherOf(Game);
        RunQueued();

        Assert.Null(cache.PublisherOf(Game));
        Assert.Equal(0, _verifications);
    }

    [Fact]
    public void Une_verification_qui_leve_vaut_un_editeur_inconnu()
    {
        var cache = new ApplicationPublisherCache(_ => throw new IOException(), _ => _stamp, work => work(), _clock);

        Assert.Null(cache.PublisherOf(Game));
        Assert.Null(cache.PublisherOf(Game));
    }
}
