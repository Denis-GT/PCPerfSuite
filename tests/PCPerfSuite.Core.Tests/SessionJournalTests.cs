using System.Text;
using PCPerfSuite.Core.Safety;

namespace PCPerfSuite.Core.Tests;

/// <summary>Le journal de session : ajouts courts, dernier état gagnant, lecture tolérante après une coupure, compactage
/// au lancement seulement.</summary>
public class SessionJournalTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDirectory _dir = new();
    private readonly ManualClock _clock = new(T0);
    private readonly SessionJournal _journal;

    public SessionJournalTests() => _journal = new SessionJournal(_dir.File("journal-session.jsonl"), _clock);

    public void Dispose() => _dir.Dispose();

    private void AppendRaw(string text) => File.AppendAllText(_journal.FilePath, text, new UTF8Encoding(false));

    [Fact]
    public void MissingFile_ReadsEmpty()
    {
        SessionJournalContent content = _journal.Read();

        Assert.Empty(content.Entries);
        Assert.Equal(0, content.IgnoredLines);
        Assert.Null(content.Problem);
    }

    [Fact]
    public void Begin_WritesAnInProgressEntry_WithItsValuesBootAndProcess()
    {
        using SessionOperation operation = _journal.Begin("test-combine", "debut",
            new Dictionary<string, string> { ["duree-s"] = "300", ["cpu-w"] = "125" });

        Assert.True(operation.IsDurable);
        SessionJournalEntry entry = Assert.Single(_journal.Read().Entries);
        Assert.Equal(operation.Id, entry.Id);
        Assert.Equal("test-combine", entry.Component);
        Assert.Equal("debut", entry.Action);
        Assert.Equal("300", entry.Values["duree-s"]);
        Assert.Equal(SessionEntryState.InProgress, entry.State);
        Assert.Equal(T0, entry.StartedUtc);
        Assert.Equal(Environment.ProcessId, entry.ProcessId);
        Assert.NotNull(entry.BootUtc);
        Assert.True(entry.BootUtc <= T0);
    }

    [Fact]
    public void Complete_TheLastLineWins_AndKeepsTheOpeningValues()
    {
        SessionOperation operation = _journal.Begin("bench", "cpu", new Dictionary<string, string> { ["duree-s"] = "60" });
        _clock.Now = T0.AddMinutes(1);

        Assert.True(operation.Complete());

        SessionJournalEntry entry = Assert.Single(_journal.Read().Entries);
        Assert.Equal(SessionEntryState.Completed, entry.State);
        Assert.Equal("60", entry.Values["duree-s"]);
        Assert.Equal(T0, entry.StartedUtc);
        Assert.Equal(T0.AddMinutes(1), entry.UpdatedUtc);
        Assert.Equal(2, File.ReadAllLines(_journal.FilePath).Count(line => line.Length > 0));
    }

    [Fact]
    public void Dispose_WithoutClosing_WritesFailedWithTheAbandonedCause()
    {
        using (_journal.Begin("palier-oc", "essai"))
        {
        }

        SessionJournalEntry entry = Assert.Single(_journal.Read().Entries);
        Assert.Equal(SessionEntryState.Failed, entry.State);
        Assert.Equal(SessionJournal.AbandonedCause, entry.Cause);
    }

    [Fact]
    public void Dispose_AfterComplete_WritesNothingMore()
    {
        using (SessionOperation operation = _journal.Begin("bench", "cpu"))
        {
            operation.Complete();
        }

        Assert.Equal(SessionEntryState.Completed, Assert.Single(_journal.Read().Entries).State);
        Assert.Equal(2, File.ReadAllLines(_journal.FilePath).Count(line => line.Length > 0));
    }

    [Fact]
    public void TruncatedTail_IsIgnoredAndCounted()
    {
        _journal.Begin("bench", "cpu");
        AppendRaw("\n{\"v\":1,\"id\":\"4b0c");

        SessionJournalContent content = _journal.Read();

        Assert.Single(content.Entries);
        Assert.Equal(1, content.IgnoredLines);
    }

    [Fact]
    public void NulPaddingLeftByAPowerCut_IsSkipped()
    {
        _journal.Begin("bench", "cpu");
        AppendRaw("\n\0\0\0\0\0\0\0\0");

        SessionJournalContent content = _journal.Read();

        Assert.Single(content.Entries);
        Assert.Equal(0, content.IgnoredLines);
    }

    [Fact]
    public void WriteAfterATornLine_StartsOnItsOwnLine()
    {
        _journal.Begin("bench", "cpu");
        AppendRaw("\n{\"v\":1,\"id\":\"4b0c1d");  // coupure au milieu de la ligne, sans saut de ligne final

        SessionOperation second = _journal.Begin("test-combine", "debut");

        SessionJournalContent content = _journal.Read();
        Assert.Equal(2, content.Entries.Count);
        Assert.Contains(content.Entries, entry => entry.Id == second.Id);
        Assert.Equal(1, content.IgnoredLines);
    }

    [Fact]
    public void Values_PathsAndExecutablesAreMasked_KeysAreKebabCase()
    {
        _journal.Begin("Test Combiné", "Début", new Dictionary<string, string>
        {
            ["Jeu"] = @"C:\Jeux\jeu.exe",
            ["app"] = "steam.exe",
            ["url"] = "a/b",
            ["palier"] = "+150",
        });

        SessionJournalEntry entry = Assert.Single(_journal.Read().Entries);
        Assert.Equal("test-combin", entry.Component);
        Assert.Equal("d-but", entry.Action);
        Assert.Equal(SessionJournalText.Masked, entry.Values["jeu"]);
        Assert.Equal(SessionJournalText.Masked, entry.Values["app"]);
        Assert.Equal(SessionJournalText.Masked, entry.Values["url"]);
        Assert.Equal("+150", entry.Values["palier"]);
        Assert.DoesNotContain("jeu.exe", File.ReadAllText(_journal.FilePath));
    }

    [Fact]
    public void Cause_WithAPath_IsMasked()
    {
        SessionOperation operation = _journal.Begin("bench", "disque");
        operation.Fail(@"fichier D:\bench\test.bin introuvable");

        Assert.Equal(SessionJournalText.Masked, Assert.Single(_journal.Read().Entries).Cause);
    }

    [Fact]
    public void ManyLongValues_KeepEachLineShort()
    {
        var values = Enumerable.Range(0, 20).ToDictionary(i => $"valeur-numero-{i:00}", _ => new string('9', 200));

        _journal.Begin("test-combine", "debut", values);

        foreach (string line in File.ReadAllLines(_journal.FilePath).Where(line => line.Length > 0))
        {
            Assert.True(Encoding.UTF8.GetByteCount(line) < SessionJournal.MaxLineBytes);
        }
        Assert.NotEmpty(Assert.Single(_journal.Read().Entries).Values);
    }

    [Fact]
    public void Compact_DropsClosedEntriesOlderThanTheRetention_KeepsPendingAndRecent()
    {
        SessionOperation old = _journal.Begin("bench", "cpu");
        old.Complete();
        SessionOperation oldPending = _journal.Begin("test-combine", "debut");
        _clock.Now = T0.AddDays(25);
        SessionOperation recent = _journal.Begin("bench", "gpu");
        recent.Complete();

        _clock.Now = T0.AddDays(31);
        Assert.True(_journal.Compact(_clock.Now));

        SessionJournalContent content = _journal.Read();
        Assert.DoesNotContain(content.Entries, entry => entry.Id == old.Id);
        Assert.Contains(content.Entries, entry => entry.Id == oldPending.Id && entry.State == SessionEntryState.InProgress);
        Assert.Contains(content.Entries, entry => entry.Id == recent.Id && entry.State == SessionEntryState.Completed);
        Assert.False(File.Exists(_journal.FilePath + ".compactage"));
    }

    [Fact]
    public void Compact_RemovesIgnoredLines()
    {
        _journal.Begin("bench", "cpu");
        AppendRaw("\n{abime");

        Assert.True(_journal.Compact(T0));

        Assert.Equal(0, _journal.Read().IgnoredLines);
        Assert.Single(_journal.Read().Entries);
    }

    [Fact]
    public void Compact_OnALockedFile_LeavesItUnchanged()
    {
        _journal.Begin("bench", "cpu").Complete();
        _clock.Now = T0.AddDays(40);
        string before = File.ReadAllText(_journal.FilePath);

        using (new FileStream(_journal.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.False(_journal.Compact(_clock.Now));
            Assert.NotNull(_journal.LastError);
        }

        Assert.Equal(before, File.ReadAllText(_journal.FilePath));
    }

    [Fact]
    public void UnwritablePath_GivesANonDurableOperation_WithoutThrowing()
    {
        var journal = new SessionJournal(_dir.Root, _clock); // un dossier, pas un fichier

        SessionOperation operation = journal.Begin("bench", "cpu");

        Assert.False(operation.IsDurable);
        Assert.NotNull(journal.LastError);
    }

    [Fact]
    public void Close_RefusesInProgress()
    {
        SessionOperation operation = _journal.Begin("bench", "cpu");

        Assert.False(_journal.Close(operation.Id, "bench", SessionEntryState.InProgress, null));
    }
}
