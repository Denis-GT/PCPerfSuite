using PCPerfSuite.Core.Hardware.Displays;

namespace PCPerfSuite.Core.Tests;

/// <summary>Rapprochement DisplayConfig ↔ WmiMonitorID, et empreinte du numéro de série.</summary>
public class MonitorSerialsTests
{
    private const string DevicePath = @"\\?\DISPLAY#DEL4123#5&1a2b3c&0&UID4353#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";

    [Fact]
    public void SameInstance_Matches()
        => Assert.True(MonitorSerials.MatchesInstance(DevicePath, @"DISPLAY\DEL4123\5&1a2b3c&0&UID4353_0"));

    [Fact]
    public void Case_IsIgnored()
        => Assert.True(MonitorSerials.MatchesInstance(DevicePath.ToLowerInvariant(), @"DISPLAY\DEL4123\5&1A2B3C&0&UID4353_0"));

    [Fact]
    public void OtherConnector_DoesNotMatch()
        => Assert.False(MonitorSerials.MatchesInstance(DevicePath, @"DISPLAY\DEL4123\5&1a2b3c&0&UID4354_0"));

    [Fact]
    public void Prefix_DoesNotMatch()
        => Assert.False(MonitorSerials.MatchesInstance(DevicePath, @"DISPLAY\DEL4123\5&1a2b3c&0&UID435_0"));

    [Fact]
    public void Hash_IsShortStableAndHidesTheSerial()
    {
        byte[] key = "clé du PC"u8.ToArray();
        string hash = MonitorSerials.HashSerial("CN0ABC123", key);
        Assert.Equal(16, hash.Length);
        Assert.Equal(hash, MonitorSerials.HashSerial(" CN0ABC123 ", key));
        Assert.NotEqual(hash, MonitorSerials.HashSerial("CN0ABC124", key));
        Assert.DoesNotContain("ABC123", hash);
    }

    /// <summary>Le même écran sur deux PC ne donne pas la même empreinte : deux rapports ne se relient pas.</summary>
    [Fact]
    public void Hash_DependsOnThePc()
        => Assert.NotEqual(MonitorSerials.HashSerial("CN0ABC123", "PC 1"u8.ToArray()), MonitorSerials.HashSerial("CN0ABC123", "PC 2"u8.ToArray()));

    [Fact]
    public void WmiString_StopsAtTheFirstZero()
        => Assert.Equal("CN0", MonitorSerials.DecodeWmiString(new ushort[] { 'C', 'N', '0', 0, 0, 'X' }));

    [Theory]
    [InlineData(new ushort[] { '0', 0, 0 })]
    [InlineData(new ushort[] { 0, 0 })]
    public void WmiString_EmptyOrZero_IsNull(ushort[] value)
        => Assert.Null(MonitorSerials.DecodeWmiString(value));

    [Fact]
    public void WmiString_NotAnArray_IsNull()
        => Assert.Null(MonitorSerials.DecodeWmiString("CN0"));
}
