using PCPerfSuite.Core.Compatibility;
using PCPerfSuite.Core.Safety.Events;

namespace PCPerfSuite.Core.Tests;

/// <summary>Lecture du journal Système : requête, types d'événements, champs gardés (jamais de texte libre).</summary>
public class SystemEventReaderTests
{
    [Theory]
    [InlineData("Microsoft-Windows-Kernel-Power", 41, SystemEventKind.KernelPower41)]
    [InlineData("Microsoft-Windows-WER-SystemErrorReporting", 1001, SystemEventKind.BugCheck)]
    [InlineData("EventLog", 6008, SystemEventKind.UnexpectedShutdown)]
    [InlineData("EventLog", 6005, SystemEventKind.BootStarted)]
    [InlineData("Microsoft-Windows-Kernel-General", 12, SystemEventKind.BootStarted)]
    [InlineData("Microsoft-Windows-Kernel-General", 13, SystemEventKind.CleanShutdown)]
    [InlineData("Microsoft-Windows-Kernel-Power", 109, SystemEventKind.CleanShutdown)]
    [InlineData("Microsoft-Windows-WHEA-Logger", 18, SystemEventKind.HardwareError)]
    [InlineData("Display", 4101, SystemEventKind.DisplayDriverReset)]
    [InlineData("disk", 153, SystemEventKind.DiskError)]
    [InlineData("Microsoft-Windows-Kernel-Processor-Power", 37, SystemEventKind.FirmwareLimited)]
    public void KindOf_KnownProviderAndId(string provider, int id, SystemEventKind expected)
    {
        Assert.Equal(expected, SystemEventReader.KindOf(provider, id));
    }

    [Theory]
    [InlineData("Service Control Manager", 7)]
    [InlineData("Microsoft-Windows-Kernel-Power", 42)]
    [InlineData("Application Error", 1001)]
    [InlineData(null, 41)]
    public void KindOf_OtherProvidersWithTheSameId_AreIgnored(string? provider, int id)
    {
        Assert.Null(SystemEventReader.KindOf(provider, id));
    }

    [Fact]
    public void BuildQuery_ListsEachIdOnce_AndKeepsTheTimeFilter()
    {
        string query = SystemEventReader.BuildQuery(SystemEventReader.AllKinds, "TimeCreated[timediff(@SystemTime) <= 1000]");

        Assert.StartsWith("*[System[(", query);
        Assert.Contains("EventID=41 or", query);
        Assert.Contains("EventID=4101", query);
        Assert.EndsWith("and TimeCreated[timediff(@SystemTime) <= 1000]]]", query);
        Assert.Equal(17, query.Split("EventID=").Length - 1);
    }

    [Fact]
    public void BuildQuery_OnlyTheRequestedKinds()
    {
        string query = SystemEventReader.BuildQuery([SystemEventKind.FirmwareLimited], "x");

        Assert.Equal("*[System[(EventID=37) and x]]", query);
    }

    [Theory]
    [InlineData("0x0000009f (0xffffe001, 0x0, 0x0, 0x0)", "0x0000009F")]
    [InlineData("0x00000124", "0x00000124")]
    [InlineData(@"C:\Windows\MEMORY.DMP", null)]
    [InlineData(null, null)]
    public void BugcheckCodeOf_KeepsOnlyTheCode(string? param1, string? expected)
    {
        Assert.Equal(expected, SystemEventReader.BugcheckCodeOf(param1));
    }

    [Theory]
    [InlineData("nvlddmkm", "nvlddmkm")]
    [InlineData("amdkmdag", "amdkmdag")]
    [InlineData(@"C:\Program Files\jeu.exe", null)]
    [InlineData("un texte libre avec des espaces", null)]
    public void DriverOf_OnlyADriverName(string value, string? expected)
    {
        Assert.Equal(expected, SystemEventReader.DriverOf(value));
    }

    [Theory]
    [InlineData(@"\Device\Harddisk1\DR1", "disque 1")]
    [InlineData(@"\Device\Harddisk12\DR12", "disque 12")]
    [InlineData("autre chose", null)]
    public void DiskOf_OnlyTheDiskNumber(string value, string? expected)
    {
        Assert.Equal(expected, SystemEventReader.DiskOf(value));
    }

    [Fact]
    public void ToUInt64_AcceptsTheEventPropertyTypes()
    {
        Assert.Equal(0x9FUL, SystemEventReader.ToUInt64(0x9Fu));
        Assert.Equal(5UL, SystemEventReader.ToUInt64(5L));
        Assert.Equal(7UL, SystemEventReader.ToUInt64("7"));
        Assert.Null(SystemEventReader.ToUInt64(-1));
        Assert.Null(SystemEventReader.ToUInt64(null));
    }

    [Fact]
    public void ReadLastDays_OnThisPc_NeverThrows()
    {
        SystemEventReadResult result = SystemEventReader.ReadLastDays(1);

        Assert.True(result.Events is not null || result.Problem is not null);
        if (result.Events is { } events) Assert.Equal(events.OrderBy(e => e.TimeUtc), events);
    }

    [Fact]
    public void Row_BeforeTheFirstRead_SaysNotYetRead()
    {
        Assert.Equal("Pas encore lu", WindowsEventsRowProvider.BuildRow(null).Status);
    }

    [Fact]
    public void Row_WhenTheLogIsUnreadable_GivesTheCause()
    {
        CompatibilityRow row = WindowsEventsRowProvider.BuildRow(
            SystemEventReadResult.Failed(UnavailableCause.MissingRights, "journal Système illisible sans les droits d'administrateur"));

        Assert.False(row.IsSupported);
        Assert.Contains("administrateur", row.Detail);
    }

    [Fact]
    public void Row_CountsIncidentsByKind_WithoutAnyMessageText()
    {
        DateTimeOffset t = new(2026, 9, 20, 8, 0, 0, TimeSpan.Zero);
        CompatibilityRow row = WindowsEventsRowProvider.BuildRow(new SystemEventReadResult(
        [
            new SystemEventRecord(SystemEventKind.KernelPower41, 41, t) { BugcheckCode = 0, PowerButtonTimestamp = 0 },
            new SystemEventRecord(SystemEventKind.DisplayDriverReset, 4101, t.AddDays(2)) { Detail = "nvlddmkm" },
            new SystemEventRecord(SystemEventKind.DisplayDriverReset, 4101, t.AddDays(3)) { Detail = "nvlddmkm" },
        ], null));

        Assert.Equal("3 incidents sur 30 jours", row.Status);
        Assert.Contains("arrêt brutal : 1", row.Detail);
        Assert.Contains("pilote graphique relancé (TDR) : 2", row.Detail);
        Assert.False(row.IsSupported);
    }

    [Fact]
    public void Row_WithoutIncident_IsSupported()
    {
        CompatibilityRow row = WindowsEventsRowProvider.BuildRow(new SystemEventReadResult(
            [new SystemEventRecord(SystemEventKind.BootStarted, 12, DateTimeOffset.UtcNow)], null));

        Assert.True(row.IsSupported);
        Assert.Equal("Aucun incident sur 30 jours", row.Status);
    }
}
