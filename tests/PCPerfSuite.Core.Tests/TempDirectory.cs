namespace PCPerfSuite.Core.Tests;

/// <summary>Dossier temporaire propre à un test, supprimé à la fin : pour les classes qui écrivent vraiment sur le disque
/// (journal de session), sans jamais toucher au dossier de données de l'app.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Root = Path.Combine(Path.GetTempPath(), "PCPerfSuite-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string File(string name) => Path.Combine(Root, name);

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>Horloge réglée à la main. <see cref="Now"/> est le temps qui passe : l'heure murale et l'horodatage monotone
/// (en ticks de <see cref="Now"/>) le suivent ; <see cref="WallShift"/> ne décale que l'heure murale, comme un changement
/// d'heure de Windows.</summary>
internal sealed class ManualClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public TimeSpan WallShift { get; set; }

    public override DateTimeOffset GetUtcNow() => Now + WallShift;

    public override long GetTimestamp() => Now.UtcTicks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
}
