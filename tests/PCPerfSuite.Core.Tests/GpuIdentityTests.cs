using System.Text.Json;
using PCPerfSuite.Core.Hardware;
using PCPerfSuite.Core.Hardware.Gpu;
using PCPerfSuite.Core.PowerSettings;

namespace PCPerfSuite.Core.Tests;

/// <summary>Reconnaître la carte sur laquelle un overclock a été enregistré.</summary>
public class GpuIdentityTests
{
    private static readonly GpuIdentity Rtx5070Ti = new(GpuVendor.Nvidia, "NVIDIA GeForce RTX 5070 Ti", 0x10DE, 0x2C05, 0x89F21043);

    [Fact]
    public void SameCard_Matches()
        => Assert.True(GpuIdentity.Matches(Rtx5070Ti, Rtx5070Ti with { }));

    [Fact]
    public void OtherVendor_NeverMatches()
        => Assert.False(GpuIdentity.Matches(Rtx5070Ti, new GpuIdentity(GpuVendor.Amd)));

    [Fact]
    public void OtherName_DoesNotMatch()
        => Assert.False(GpuIdentity.Matches(Rtx5070Ti, Rtx5070Ti with { Name = "NVIDIA GeForce RTX 5080", PciDeviceId = null }));

    [Fact]
    public void Name_IgnoresCaseAndSpacing()
        => Assert.True(GpuIdentity.Matches(Rtx5070Ti, Rtx5070Ti with { Name = "  nvidia geforce  RTX 5070 ti " }));

    [Fact]
    public void OtherDevice_DoesNotMatch()
        => Assert.False(GpuIdentity.Matches(Rtx5070Ti, Rtx5070Ti with { PciDeviceId = 0x2C02 }));

    [Fact]
    public void SameModelFromAnotherBoardPartner_DoesNotMatch()
        => Assert.False(GpuIdentity.Matches(Rtx5070Ti, Rtx5070Ti with { PciSubsystemId = 0x51721462 }));

    [Fact]
    public void LegacySettingsWithVendorOnly_MatchTheSameVendor()
    {
        // Réglages d'une version précédente : seule la marque était enregistrée.
        Assert.True(GpuIdentity.Matches(new GpuIdentity(GpuVendor.Nvidia), Rtx5070Ti));
        Assert.False(GpuIdentity.Matches(new GpuIdentity(GpuVendor.Intel), Rtx5070Ti));
    }

    [Fact]
    public void FieldUnknownOnOneSide_DoesNotBlock()
        => Assert.True(GpuIdentity.Matches(Rtx5070Ti, new GpuIdentity(GpuVendor.Nvidia, "NVIDIA GeForce RTX 5070 Ti")));

    [Fact]
    public void Luid_IsNotCompared()
        => Assert.True(GpuIdentity.Matches(Rtx5070Ti with { Luid = 1 }, Rtx5070Ti with { Luid = 2 }));

    [Fact]
    public void IsSameCard_SameCard()
        => Assert.True(GpuIdentity.IsSameCard(Rtx5070Ti, Rtx5070Ti with { }));

    [Fact]
    public void IsSameCard_VendorOnly_IsNotEnough()
    {
        // Une identité qui n'a que la marque ne dit pas de quelle carte il s'agit : des décalages ne s'y posent pas.
        Assert.False(GpuIdentity.IsSameCard(new GpuIdentity(GpuVendor.Nvidia), Rtx5070Ti));
        Assert.False(GpuIdentity.IsSameCard(Rtx5070Ti, new GpuIdentity(GpuVendor.Nvidia)));
    }

    [Fact]
    public void IsSameCard_SameVendorOtherCard_IsRefused()
        => Assert.False(GpuIdentity.IsSameCard(Rtx5070Ti, new GpuIdentity(GpuVendor.Nvidia, "NVIDIA GeForce RTX 4070", 0x10DE, 0x2786)));

    [Fact]
    public void IsSameCard_SameChipFromAnotherBoardPartner_IsRefused()
        => Assert.False(GpuIdentity.IsSameCard(Rtx5070Ti, Rtx5070Ti with { PciSubsystemId = 0x51721462 }));

    [Fact]
    public void IsSameCard_DeviceIdOnBothSides_IsEnough()
        => Assert.True(GpuIdentity.IsSameCard(
            new GpuIdentity(GpuVendor.Amd, PciVendorId: 0x1002, PciDeviceId: 0x73BF),
            new GpuIdentity(GpuVendor.Amd, "AMD Radeon RX 6800 XT", 0x1002, 0x73BF)));

    [Fact]
    public void IsSameCard_Null_IsRefused()
    {
        Assert.False(GpuIdentity.IsSameCard(null, Rtx5070Ti));
        Assert.False(GpuIdentity.IsSameCard(Rtx5070Ti, null));
    }

    [Theory]
    [InlineData("0x73BF", 0x73BFu)]
    [InlineData("73bf", 0x73BFu)]
    [InlineData(" 1002 ", 0x1002u)]
    public void ParsePciId_ReadsHex(string text, uint expected)
        => Assert.Equal(expected, GpuIdentity.ParsePciId(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("zz")]
    public void ParsePciId_Unreadable_IsNull(string? text)
        => Assert.Null(GpuIdentity.ParsePciId(text));

    [Fact]
    public void Describe_ShowsNameAndIds()
        => Assert.Equal("NVIDIA GeForce RTX 5070 Ti (PCI 10DE:2C05, sous-système 1043:89F2)", Rtx5070Ti.Describe());

    [Fact]
    public void Json_StoresTheVendorAsAString_AndRoundTrips()
    {
        var settings = new GpuControlSettings { OverclockGpu = Rtx5070Ti, OverclockVendor = GpuVendor.Nvidia };

        string json = JsonSerializer.Serialize(settings);
        GpuControlSettings? back = JsonSerializer.Deserialize<GpuControlSettings>(json);

        Assert.Contains("\"Vendor\":\"Nvidia\"", json);
        Assert.Equal(Rtx5070Ti, back?.OverclockGpu);
    }

    [Fact]
    public void Json_FileFromBefore_HasNoIdentity()
    {
        GpuControlSettings? old = JsonSerializer.Deserialize<GpuControlSettings>("{\"OverclockVendor\":1}");

        Assert.Null(old?.OverclockGpu);
        Assert.Equal(GpuVendor.Amd, old?.OverclockVendor);
    }
}
