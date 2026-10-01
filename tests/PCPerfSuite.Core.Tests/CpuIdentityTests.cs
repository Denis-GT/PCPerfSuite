using PCPerfSuite.Core.Hardware.Cpu;

namespace PCPerfSuite.Core.Tests;

/// <summary>Des watts ne se posent que sur le processeur où ils ont été relevés.</summary>
public class CpuIdentityTests
{
    private static readonly CpuIdentity I5 = new() { Vendor = "Intel", Family = 6, Model = 183, Name = "Intel(R) Core(TM) i5-14600K" };

    [Fact]
    public void Le_meme_processeur_correspond()
        => Assert.True(CpuIdentity.Matches(I5, I5 with { Name = "  intel(r) core(tm)  i5-14600k " }));

    [Fact]
    public void Un_autre_modele_de_la_meme_famille_ne_correspond_pas()
        => Assert.False(CpuIdentity.Matches(I5, I5 with { Name = "Intel(R) Core(TM) i9-14900K" }));

    [Fact]
    public void Un_autre_fabricant_ne_correspond_jamais()
        => Assert.False(CpuIdentity.Matches(I5, I5 with { Vendor = "Amd" }));

    [Fact]
    public void Une_autre_famille_ne_correspond_pas()
        => Assert.False(CpuIdentity.Matches(I5, I5 with { Model = 191 }));

    [Fact]
    public void Sans_nom_d_un_cote_on_ne_conclut_pas()
    {
        Assert.False(CpuIdentity.Matches(I5 with { Name = null }, I5));
        Assert.False(CpuIdentity.Matches(I5, I5 with { Name = CpuPlatformDetector.UnknownName }));
    }

    [Fact]
    public void Famille_inconnue_d_un_cote_ne_bloque_pas()
        => Assert.True(CpuIdentity.Matches(I5 with { Family = null, Model = null }, I5));

    [Fact]
    public void Une_identite_absente_ne_correspond_pas()
    {
        Assert.False(CpuIdentity.Matches(null, I5));
        Assert.False(CpuIdentity.Matches(I5, null));
    }

    [Fact]
    public void L_identite_se_lit_sur_la_plateforme()
    {
        CpuIdentity identity = CpuIdentity.Of(new CpuPlatform { Vendor = CpuVendor.Amd, Name = "AMD Ryzen 7 7800X3D ", Family = 25, Model = 97 });

        Assert.Equal("Amd", identity.Vendor);
        Assert.Equal("AMD Ryzen 7 7800X3D", identity.Name);
        Assert.Equal(25, identity.Family);
    }

    [Fact]
    public void Un_processeur_sans_nom_ni_famille_reste_inconnu()
    {
        CpuIdentity identity = CpuIdentity.Of(new CpuPlatform { Vendor = CpuVendor.Qualcomm, Name = CpuPlatformDetector.UnknownName });

        Assert.Null(identity.Name);
        Assert.Null(identity.Family);
        Assert.Equal("processeur Qualcomm", identity.Describe());
    }
}
