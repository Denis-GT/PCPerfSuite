using PCPerfSuite.Core.Compatibility;
using PCPerfSuite.Core.Hardware;
using PCPerfSuite.Core.Hardware.Gpu;
using PCPerfSuite.Core.PowerSettings;
using PCPerfSuite.Core.Safety;

namespace PCPerfSuite.Core.Tests;

/// <summary>Décision au réveil de veille (CPU et GPU) et diagnostic de la carte GPU pilotée.</summary>
public class TuningResumeTests
{
    [Theory]
    [InlineData(false, true, true, false, ResumeAction.ReadOnly)]
    [InlineData(true, false, true, false, ResumeAction.ReadOnly)]
    [InlineData(true, true, false, false, ResumeAction.ReadOnly)]
    [InlineData(true, true, true, true, ResumeAction.SkipAfterEmergency)]
    [InlineData(true, true, true, false, ResumeAction.Reapply)]
    [InlineData(false, true, true, true, ResumeAction.ReadOnly)]
    public void Decide(bool applyAtStartup, bool sameHardware, bool allowed, bool emergency, ResumeAction expected)
        => Assert.Equal(expected, TuningResume.Decide(applyAtStartup, sameHardware, allowed, emergency));

    private static readonly GpuIdentity Current = new(GpuVendor.Nvidia, "NVIDIA GeForce RTX 4070", 0x10DE, 0x2786, 0x88A01043);

    [Fact]
    public void IdentityRow_SameCard_SaysItIsReapplied()
    {
        var saved = new GpuControlSettings { ApplyOverclockAtStartup = true, OverclockGpu = Current };

        CompatibilityRow row = GpuIdentityRowProvider.BuildRow(Current, saved);

        Assert.True(row.IsSupported);
        Assert.Equal("Réglages de cette carte", row.Status);
        Assert.Contains("PCI 10DE:2786", row.Detail);
    }

    [Fact]
    public void IdentityRow_OtherBoardPartner_SaysWhyNothingIsReapplied()
    {
        var saved = new GpuControlSettings { ApplyOverclockAtStartup = true, OverclockGpu = Current with { PciSubsystemId = 0x51721462 } };

        CompatibilityRow row = GpuIdentityRowProvider.BuildRow(Current, saved);

        Assert.False(row.IsSupported);
        Assert.Equal("Réglages d'une autre carte", row.Status);
        Assert.Contains("n'est pas réappliqué", row.Detail);
    }

    [Fact]
    public void IdentityRow_LegacySettingsWithVendorOnly_MatchTheSameVendor()
    {
        var saved = new GpuControlSettings { ApplyOverclockAtStartup = true, OverclockVendor = null };

        Assert.True(GpuIdentityRowProvider.BuildRow(Current, saved).IsSupported);
    }

    [Fact]
    public void IdentityRow_NoGpu()
        => Assert.Equal("Aucune", GpuIdentityRowProvider.BuildRow(null, null).Status);
}
