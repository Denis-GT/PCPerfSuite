using PCPerfSuite.Core.Hardware.Cpu.Throttle;

namespace PCPerfSuite.Core.Tests;

/// <summary>PM table AMD : seules les versions cartographiées sont lues, et une lecture absurde est écartée.</summary>
public class PmTableLayoutsTests
{
    /// <summary>Table Vermeer au repos : PPT 142 W (30 W lus), TDC 95 A, THM 90 °C, EDC 140 A.</summary>
    private static float[] Vermeer() => [142f, 30f, 95f, 20f, 90f, 45f, 0f, 0f, 140f, 35f, 1.3f, 1.1f];

    private static ulong[] Pack(float[] values)
    {
        var qwords = new ulong[(values.Length + 1) / 2];
        for (int i = 0; i < values.Length; i++)
        {
            ulong bits = BitConverter.SingleToUInt32Bits(values[i]);
            qwords[i / 2] |= i % 2 == 0 ? bits : bits << 32;
        }
        return qwords;
    }

    [Theory]
    [InlineData(0x240903u, "Matisse")]
    [InlineData(0x380805u, "Vermeer")]
    [InlineData(0x400005u, "Cezanne")]
    public void KnownVersions_HaveALayout(uint version, string family)
    {
        Assert.Equal(family, PmTableLayouts.For(version)?.Family);
    }

    [Theory]
    [InlineData(0x540004u)] // Raphael : position des limites non publiée
    [InlineData(0x620205u)] // Granite Ridge
    [InlineData(0u)]
    public void UnknownVersions_HaveNoLayout(uint version)
    {
        Assert.Null(PmTableLayouts.For(version));
    }

    [Fact]
    public void Vermeer_DecodesValuesAndLimits()
    {
        PmTableLayout layout = PmTableLayouts.For(0x380805)!;

        AmdPowerLimits? limits = PmTableLayouts.Decode(layout, PmTableLayouts.ToFloats(Pack(Vermeer()), returned: 6));

        Assert.NotNull(limits);
        Assert.Equal(142f, limits.PptLimitWatts);
        Assert.Equal(30f, limits.PptWatts);
        Assert.Equal(95f, limits.TdcLimitAmps);
        Assert.Equal(140f, limits.EdcLimitAmps);
        Assert.Equal(90f, limits.ThmLimitC);
        Assert.False(limits.PptAtLimit);
    }

    [Fact]
    public void AtLimit_WhenTheValueReachesItsLimit()
    {
        float[] table = Vermeer();
        table[1] = 141f;  // PPT à sa limite
        table[5] = 89.5f; // THM à sa limite

        AmdPowerLimits limits = PmTableLayouts.Decode(PmTableLayouts.For(0x380805)!, table)!;

        Assert.True(limits.PptAtLimit);
        Assert.True(limits.ThmAtLimit);
        Assert.False(limits.TdcAtLimit);
    }

    [Fact]
    public void Cezanne_UsesTheSlowPpt()
    {
        float[] table = new float[18];
        table[0] = 35; table[2] = 54; table[4] = 45; table[5] = 44.5f; // STAPM, rapide, lente
        table[8] = 60; table[12] = 90; table[16] = 95; table[17] = 70;

        AmdPowerLimits limits = PmTableLayouts.Decode(PmTableLayouts.For(0x400005)!, table)!;

        Assert.Equal(45f, limits.PptLimitWatts);
        Assert.True(limits.PptAtLimit);
        Assert.Equal(95f, limits.ThmLimitC);
    }

    [Fact]
    public void ImplausibleLimits_AreRejected()
    {
        float[] table = Vermeer();
        table[0] = 5000f; // PPT absurde : mauvaise disposition

        Assert.Null(PmTableLayouts.Decode(PmTableLayouts.For(0x380805)!, table));
    }

    [Fact]
    public void AnImplausibleCurrentValue_IsOnlyDropped()
    {
        float[] table = Vermeer();
        table[3] = float.NaN;

        AmdPowerLimits limits = PmTableLayouts.Decode(PmTableLayouts.For(0x380805)!, table)!;

        Assert.Null(limits.TdcAmps);
        Assert.Equal(95f, limits.TdcLimitAmps);
    }

    [Fact]
    public void AShortReturnedSize_IsRejected()
    {
        PmTableLayout layout = PmTableLayouts.For(0x380805)!;

        Assert.Null(PmTableLayouts.Decode(layout, PmTableLayouts.ToFloats(Pack(Vermeer()), returned: 2)));
        Assert.Equal(5, layout.QwordsNeeded);
    }
}
