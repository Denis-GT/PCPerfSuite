using PCPerfSuite.Core.Benchmark.Protocol;
using Xunit;

namespace PCPerfSuite.Core.Tests;

public class LivenessWatchTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Pas_de_silence_avant_le_delai_puis_expiration()
    {
        var watch = new LivenessWatch(TimeSpan.FromSeconds(2), T0);

        Assert.False(watch.IsExpired(T0.AddSeconds(1.9)));
        Assert.True(watch.IsExpired(T0.AddSeconds(2)));
        Assert.Equal(TimeSpan.FromSeconds(3), watch.Silence(T0.AddSeconds(3)));
    }

    [Fact]
    public void Un_signe_de_vie_repousse_l_expiration()
    {
        var watch = new LivenessWatch(TimeSpan.FromSeconds(2), T0);

        watch.Note(T0.AddSeconds(1.5));

        Assert.False(watch.IsExpired(T0.AddSeconds(3)));
        Assert.True(watch.IsExpired(T0.AddSeconds(3.5)));
    }

    [Fact]
    public void Un_signe_date_du_passe_est_ignore()
    {
        var watch = new LivenessWatch(TimeSpan.FromSeconds(2), T0.AddSeconds(5));

        watch.Note(T0);

        Assert.Equal(T0.AddSeconds(5), watch.LastSignalAt);
        Assert.Equal(TimeSpan.Zero, watch.Silence(T0));
    }

    [Fact]
    public void Un_delai_nul_est_refuse()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LivenessWatch(TimeSpan.Zero, T0));
    }
}
