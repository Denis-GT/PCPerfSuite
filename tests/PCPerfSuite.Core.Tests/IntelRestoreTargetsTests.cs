using PCPerfSuite.Core.Hardware.Cpu;

namespace PCPerfSuite.Core.Tests;

/// <summary>« Limites d'origine » : ce que l'app réécrit dans le processeur pour rendre la main au BIOS.</summary>
public class IntelRestoreTargetsTests
{
    [Fact]
    public void BiosUnlimited_IsWrittenBackAsIs_NotCappedAtTheProposedMaximum()
    {
        // Carte mère qui écrit 4095 W : avant, « Limites d'origine » écrivait 400 W (le plafond de saisie)
        // tout en annonçant 4095 W rétablis.
        (float sustained, float burst) = IntelPowerLimitBackend.RestoreTargets(4095f, 4095f, 5f);

        Assert.Equal(4095f, sustained);
        Assert.Equal(4095f, burst);
    }

    [Fact]
    public void OrdinaryLimits_AreWrittenBackUnchanged()
    {
        (float sustained, float burst) = IntelPowerLimitBackend.RestoreTargets(125f, 181f, 5f);

        Assert.Equal(125f, sustained);
        Assert.Equal(181f, burst);
    }

    [Fact]
    public void BurstLimitOfZero_IsLiftedToTheSustainedLimit()
    {
        // Une PL2 à 0 W, une fois « activée » par l'écriture, brideraient le processeur à presque rien.
        (float sustained, float burst) = IntelPowerLimitBackend.RestoreTargets(125f, 0f, 5f);

        Assert.Equal(125f, sustained);
        Assert.Equal(125f, burst);
    }

    [Fact]
    public void BurstBelowSustained_IsLiftedToTheSustainedLimit()
    {
        (float sustained, float burst) = IntelPowerLimitBackend.RestoreTargets(125f, 100f, 5f);

        Assert.Equal(125f, sustained);
        Assert.Equal(125f, burst);
    }

    [Fact]
    public void LimitsBelowTheFloor_AreRaisedToIt()
    {
        (float sustained, float burst) = IntelPowerLimitBackend.RestoreTargets(1f, 2f, 5f);

        Assert.Equal(5f, sustained);
        Assert.Equal(5f, burst);
    }
}
