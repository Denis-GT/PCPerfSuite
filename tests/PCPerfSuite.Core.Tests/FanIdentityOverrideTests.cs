using System.Text.Json;
using PCPerfSuite.Core.Hardware;
using PCPerfSuite.Core.Hardware.Fans;
using PCPerfSuite.Core.PowerSettings;

namespace PCPerfSuite.Core.Tests;

public class FanIdentityOverrideTests
{
    [Fact]
    public void Chaque_categorie_se_relit_depuis_sa_cle()
    {
        foreach (FanCategory category in Enum.GetValues<FanCategory>())
        {
            Assert.True(FanCategoryInfo.TryParseKey(FanCategoryInfo.Key(category), out FanCategory parsed));
            Assert.Equal(category, parsed);
        }
    }

    [Fact]
    public void Les_cles_sont_distinctes_et_lisibles_dans_le_fichier()
    {
        string[] keys = Enum.GetValues<FanCategory>().Select(FanCategoryInfo.Key).ToArray();

        Assert.Equal(keys.Length, keys.Distinct().Count());
        Assert.Equal("pump", FanCategoryInfo.Key(FanCategory.Pump));
    }

    [Theory]
    [InlineData("PUMP")]
    [InlineData("Pump")]
    public void La_casse_de_la_cle_est_ignoree(string key)
    {
        Assert.True(FanCategoryInfo.TryParseKey(key, out FanCategory category));
        Assert.Equal(FanCategory.Pump, category);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("radiateur")]
    public void Une_cle_inconnue_n_est_pas_reconnue(string? key)
        => Assert.False(FanCategoryInfo.TryParseKey(key, out _));

    [Fact]
    public void Une_entree_sans_nom_ni_categorie_est_vide()
    {
        Assert.True(new FanIdentityOverride { FanId = "gpu:1" }.IsEmpty);
        Assert.True(new FanIdentityOverride { FanId = "gpu:1", Name = "   " }.IsEmpty);
        Assert.False(new FanIdentityOverride { FanId = "gpu:1", Name = "Hub" }.IsEmpty);
        Assert.False(new FanIdentityOverride { FanId = "gpu:1", Category = "case" }.IsEmpty);
    }

    [Fact]
    public void Les_identites_font_l_aller_retour_dans_le_fichier_de_reglages()
    {
        var settings = new AppSettings();
        settings.FanIdentities.Add(new FanIdentityOverride { FanId = "/lpc/nct6798d/0/control/6", Name = "Boîtier (hub ×4)", Category = "case" });

        string json = JsonSerializer.Serialize(settings);
        AppSettings loaded = JsonSerializer.Deserialize<AppSettings>(json)!;

        FanIdentityOverride identity = Assert.Single(loaded.FanIdentities);
        Assert.Equal("/lpc/nct6798d/0/control/6", identity.FanId);
        Assert.Equal("Boîtier (hub ×4)", identity.Name);
        Assert.Equal("case", identity.Category);
        Assert.DoesNotContain("IsEmpty", json);
    }

    [Fact]
    public void Un_fichier_d_avant_les_identites_se_lit_sans_erreur()
    {
        // Le settings.json d'un utilisateur existant ne contient pas encore FanIdentities.
        const string json = """{"MonitoringRefreshMs":1000,"FanCurves":[{"ControlSensorId":"gpu:1","Mode":0}]}""";

        AppSettings loaded = JsonSerializer.Deserialize<AppSettings>(json)!;

        Assert.Empty(loaded.FanIdentities);
        Assert.Equal("gpu:1", Assert.Single(loaded.FanCurves).ControlSensorId);
    }
}
