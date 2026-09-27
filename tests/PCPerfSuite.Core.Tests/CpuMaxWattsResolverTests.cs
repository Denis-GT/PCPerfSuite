using PCPerfSuite.Core.Hardware.Cpu;

namespace PCPerfSuite.Core.Tests;

/// <summary>
/// Chaque branche du choix du maximum de limite de puissance. Rien ne touche au matériel : le résolveur ne
/// fait que trancher entre des valeurs déjà lues, ce qui permet de simuler ici un processeur qui publie sa
/// puissance maximale, un qui ne la publie pas, une carte mère qui écrit 4095 W, un refus du pilote…
/// </summary>
public class CpuMaxWattsResolverTests
{
    /// <summary>Un Core de bureau ordinaire : PL1 125 W, PL2 181 W, puissance de base 125 W. Chaque test ne
    /// change que ce qui l'intéresse.</summary>
    private static IntelPowerReadings Intel(
        float sustained = 125f, float burst = 181f, float min = 5f,
        float? tdp = 125f, float? infoMax = null, string? infoError = null,
        bool pl4Exposed = false, float? pl4 = null, string? pl4Error = null)
        => new(sustained, burst, min, tdp, infoMax, infoError, pl4Exposed, pl4, pl4Error);

    // ---- Intel : 1. puissance maximale publiée par le processeur -------------------------------------

    [Fact]
    public void Intel_UsesProcessorMaxPower_WhenPublished()
    {
        CpuMaxWattsInfo info = CpuMaxWattsResolver.ForIntel(Intel(infoMax: 181f));

        Assert.Equal(181f, info.Watts);
        Assert.Equal(CpuMaxWattsSource.ProcessorMaxPower, info.Source);
        Assert.True(info.FromProcessor);
        Assert.True(info.IsExperimental);
    }

    [Fact]
    public void Intel_ProcessorMaxPower_WinsOverPl4()
    {
        CpuMaxWattsInfo info = CpuMaxWattsResolver.ForIntel(Intel(infoMax: 181f, pl4Exposed: true, pl4: 253f));

        Assert.Equal(CpuMaxWattsSource.ProcessorMaxPower, info.Source);
        Assert.Equal(181f, info.Watts);
    }

    [Fact]
    public void Intel_ProcessorMaxPower_IsUsedEvenWhenBiosSaysUnlimited()
    {
        // Carte mère qui écrit 4095 W partout : aucune base de calcul côté BIOS, mais le processeur, lui, parle.
        CpuMaxWattsInfo info = CpuMaxWattsResolver.ForIntel(Intel(sustained: 4095f, burst: 4095f, infoMax: 253f));

        Assert.Equal(253f, info.Watts);
        Assert.Equal(CpuMaxWattsSource.ProcessorMaxPower, info.Source);
    }

    [Fact]
    public void Intel_ProcessorMaxPower_IsCappedAtOneAndAHalfTimesBiosLimit()
    {
        // Un maximum publié très au-dessus de ce que le BIOS a posé : on garde le plus petit des deux, comme avant.
        CpuMaxWattsInfo info = CpuMaxWattsResolver.ForIntel(Intel(sustained: 65f, burst: 65f, tdp: 65f, infoMax: 200f));

        Assert.Equal(97.5f, info.Watts);
        Assert.Equal(CpuMaxWattsSource.FactoryLimit, info.Source);
        Assert.True(info.IsExperimental); // la comparaison porte sur une valeur lue dans le processeur
        Assert.Contains("200", info.Explanation);
    }

    [Theory]
    [InlineData(0f)]      // registre vide : le cas courant sur les processeurs grand public
    [InlineData(4095f)]   // « sans limite »
    [InlineData(65f)]     // sous la puissance de base de 125 W : incohérent
    public void Intel_ProcessorMaxPower_IsRejectedWhenNotAPower(float infoMax)
    {
        CpuMaxWattsInfo info = CpuMaxWattsResolver.ForIntel(Intel(infoMax: infoMax));

        Assert.NotEqual(CpuMaxWattsSource.ProcessorMaxPower, info.Source);
        Assert.Contains("0x614", info.Explanation);
    }

    [Fact]
    public void Intel_ProcessorMaxPower_BelowBasePowerSaysSo()
    {
        CpuMaxWattsInfo info = CpuMaxWattsResolver.ForIntel(Intel(infoMax: 65f));

        Assert.Contains("sous la puissance de base", info.Explanation);
    }

    [Fact]
    public void Intel_UnreadableProcessorMaxPower_ReportsTheRefusal()
    {
        CpuMaxWattsInfo info = CpuMaxWattsResolver.ForIntel(Intel(infoMax: null, infoError: "écriture refusée par le module PawnIO"));

        Assert.Contains("lecture refusée", info.Explanation);
        Assert.Contains("écriture refusée par le module PawnIO", info.Explanation);
    }

    // ---- Intel : 2. PL4 ----------------------------------------------------------------------------

    [Fact]
    public void Intel_FallsBackToPl4_WhenProcessorMaxPowerIsEmpty()
    {
        CpuMaxWattsInfo info = CpuMaxWattsResolver.ForIntel(Intel(infoMax: 0f, pl4Exposed: true, pl4: 253f));

        Assert.Equal(253f, info.Watts);
        Assert.Equal(CpuMaxWattsSource.PeakLimitPl4, info.Source);
        Assert.True(info.FromProcessor);
        Assert.True(info.IsExperimental);
        Assert.Contains("registre vide", info.Explanation); // dit pourquoi la puissance maximale n'a pas servi
    }

    [Fact]
    public void Intel_Pl4_IsUsedWhenBiosSaysUnlimited()
    {
        // Le cas d'un PC de bureau ASUS dont la carte mère écrit 4095 W : sans PL4, ce serait le plafond fixe.
        CpuMaxWattsInfo info = CpuMaxWattsResolver.ForIntel(
            Intel(sustained: 4095f, burst: 4095f, infoMax: 0f, pl4Exposed: true, pl4: 253f));

        Assert.Equal(253f, info.Watts);
        Assert.Equal(CpuMaxWattsSource.PeakLimitPl4, info.Source);
    }

    [Fact]
    public void Intel_Pl4_IsCappedAtOneAndAHalfTimesBiosLimit()
    {
        CpuMaxWattsInfo info = CpuMaxWattsResolver.ForIntel(
            Intel(sustained: 65f, burst: 65f, tdp: 65f, infoMax: 0f, pl4Exposed: true, pl4: 253f));

        Assert.Equal(97.5f, info.Watts);
        Assert.Equal(CpuMaxWattsSource.FactoryLimit, info.Source);
    }

    [Fact]
    public void Intel_Pl4LiftedByMotherboard_IsIgnored_AndFallsBackToSafetyCap()
    {
        // 0x1FFF = 1023,875 W : PL4 « levée » par la carte mère, pas une puissance.
        CpuMaxWattsInfo info = CpuMaxWattsResolver.ForIntel(
            Intel(sustained: 4095f, burst: 4095f, infoMax: 0f, pl4Exposed: true, pl4: 1023.875f));

        Assert.Equal(400f, info.Watts);
        Assert.Equal(CpuMaxWattsSource.SafetyCap, info.Source);
        Assert.False(info.FromProcessor);
        Assert.False(info.IsExperimental);
        Assert.Contains("PL4", info.Explanation);
    }

    [Fact]
    public void Intel_Pl4BelowBasePower_IsIgnored()
    {
        CpuMaxWattsInfo info = CpuMaxWattsResolver.ForIntel(Intel(infoMax: 0f, pl4Exposed: true, pl4: 40f));

        Assert.NotEqual(CpuMaxWattsSource.PeakLimitPl4, info.Source);
    }

    [Fact]
    public void Intel_UnreadablePl4_ReportsTheRefusal()
    {
        CpuMaxWattsInfo info = CpuMaxWattsResolver.ForIntel(
            Intel(infoMax: 0f, pl4Exposed: true, pl4: null, pl4Error: "paramètre invalide"));

        Assert.NotEqual(CpuMaxWattsSource.PeakLimitPl4, info.Source);
        Assert.Contains("paramètre invalide", info.Explanation);
    }

    [Fact]
    public void Intel_GenerationWithoutPl4_SaysSo()
    {
        CpuMaxWattsInfo info = CpuMaxWattsResolver.ForIntel(Intel(infoMax: 0f, pl4Exposed: false));

        Assert.Contains("ne l'expose pas", info.Explanation);
    }

    // ---- Intel : 3 et 4. replis --------------------------------------------------------------------

    [Fact]
    public void Intel_FallsBackToBiosLimitTimesOneAndAHalf()
    {
        CpuMaxWattsInfo info = CpuMaxWattsResolver.ForIntel(Intel(infoMax: 0f));

        Assert.Equal(271.5f, info.Watts); // 181 × 1,5
        Assert.Equal(CpuMaxWattsSource.FactoryLimit, info.Source);
        Assert.False(info.FromProcessor);
        Assert.False(info.IsExperimental);
    }

    [Fact]
    public void Intel_FallsBackToSafetyCap_WhenBiosSaysUnlimited_AndProcessorSaysNothing()
    {
        CpuMaxWattsInfo info = CpuMaxWattsResolver.ForIntel(Intel(sustained: 4095f, burst: 4095f, infoMax: 0f));

        Assert.Equal(400f, info.Watts);
        Assert.Equal(CpuMaxWattsSource.SafetyCap, info.Source);
        Assert.False(info.IsExperimental);
        Assert.Contains("400", info.Explanation);
    }

    [Fact]
    public void Intel_OneLimitUnlimited_TheOtherReal_UsesTheBiggerOne()
    {
        // PL1 réelle, PL2 à 4095 W : max(PL1, PL2) est « sans limite » → plafond fixe, pas 6142 W.
        CpuMaxWattsInfo info = CpuMaxWattsResolver.ForIntel(Intel(sustained: 125f, burst: 4095f, infoMax: 0f));

        Assert.Equal(CpuMaxWattsSource.SafetyCap, info.Source);
        Assert.Equal(400f, info.Watts);
    }

    [Fact]
    public void Intel_NeverGoesBelowMinimumPlusOne()
    {
        CpuMaxWattsInfo fromProcessor = CpuMaxWattsResolver.ForIntel(Intel(tdp: null, infoMax: 3f, min: 5f));
        Assert.Equal(6f, fromProcessor.Watts);

        CpuMaxWattsInfo fromBios = CpuMaxWattsResolver.ForIntel(Intel(sustained: 2f, burst: 2f, tdp: null, infoMax: 0f, min: 5f));
        Assert.Equal(6f, fromBios.Watts); // 2 × 1,5 = 3 < 6
    }

    [Fact]
    public void Intel_RawValues_ShowEveryReading_AndMarkMissingOnes()
    {
        CpuMaxWattsInfo info = CpuMaxWattsResolver.ForIntel(
            Intel(sustained: 125f, burst: 4095f, tdp: 125f, infoMax: null, pl4Exposed: true, pl4: 253f));

        Assert.Contains("PL1 125 W", info.RawValues);
        Assert.Contains("PL2 4095 W", info.RawValues);
        Assert.Contains("base (0x614) 125 W", info.RawValues);
        Assert.Contains("max (0x614) illisible", info.RawValues);
        Assert.Contains("PL4 (0x601) 253 W", info.RawValues);
    }

    // ---- AMD ---------------------------------------------------------------------------------------

    [Fact]
    public void Amd_IsBiosLimitTimesOneAndAHalf_AndSaysWhy()
    {
        CpuMaxWattsInfo info = CpuMaxWattsResolver.ForAmd(88f, 65f, 5f);

        Assert.Equal(132f, info.Watts);
        Assert.Equal(CpuMaxWattsSource.FactoryLimit, info.Source);
        Assert.False(info.FromProcessor);
        Assert.False(info.IsExperimental);
        Assert.Contains("table SMU", info.Explanation);
        Assert.Contains("puissance maximale", info.Explanation);
    }

    [Fact]
    public void Amd_NeverGoesBelowMinimumPlusOne()
    {
        Assert.Equal(6f, CpuMaxWattsResolver.ForAmd(2f, 2f, 5f).Watts);
    }

    // ---- Quels Intel exposent PL4 -------------------------------------------------------------------

    [Theory]
    [InlineData(6, 0xB7)]  // Raptor Lake : Core de 13e et 14e génération, dont le i5-14600K
    [InlineData(6, 0xBA)]  // Raptor Lake-P
    [InlineData(6, 0x97)]  // Alder Lake
    [InlineData(6, 0x9A)]  // Alder Lake-L
    [InlineData(6, 0xAA)]  // Meteor Lake-L
    [InlineData(6, 0xC5)]  // Arrow Lake-H
    [InlineData(6, 0x7E)]  // Ice Lake-L
    [InlineData(18, 0x01)] // Nova Lake : famille 18
    public void HasPl4Register_IsTrue_ForListedGenerations(int family, int model)
        => Assert.True(CpuMaxWattsResolver.HasPl4Register(family, model));

    [Theory]
    [InlineData(6, 0x9E)]  // Coffee Lake : 0x601 y contient un courant, pas une puissance
    [InlineData(6, 0xA5)]  // Comet Lake
    [InlineData(6, 0xBF)]  // absent de la liste du noyau Linux
    [InlineData(6, 0xC6)]  // Arrow Lake de bureau : absent de la liste du noyau Linux
    [InlineData(15, 0xB7)] // bon modèle, mauvaise famille : le couple compte, pas le modèle seul
    [InlineData(0, 0)]     // ARM, ou registre illisible
    public void HasPl4Register_IsFalse_Otherwise(int family, int model)
        => Assert.False(CpuMaxWattsResolver.HasPl4Register(family, model));

    // ---- Libellés ----------------------------------------------------------------------------------

    [Theory]
    [InlineData(CpuMaxWattsSource.ProcessorMaxPower, true)]
    [InlineData(CpuMaxWattsSource.PeakLimitPl4, true)]
    [InlineData(CpuMaxWattsSource.FactoryLimit, false)]
    [InlineData(CpuMaxWattsSource.SafetyCap, false)]
    public void FromProcessor_IsTrueOnlyForProcessorSources(CpuMaxWattsSource source, bool expected)
    {
        var info = new CpuMaxWattsInfo(100f, source, "x", false, "");

        Assert.Equal(expected, info.FromProcessor);
        Assert.False(string.IsNullOrWhiteSpace(info.SourceLabel));
    }
}
