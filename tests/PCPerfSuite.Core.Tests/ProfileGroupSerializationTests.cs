using System.Text.Json;
using PCPerfSuite.Core.Hardware;
using PCPerfSuite.Core.Hardware.Cpu;
using PCPerfSuite.Core.Hardware.Fans;
using PCPerfSuite.Core.Hardware.Gpu;
using PCPerfSuite.Core.PowerSettings;
using PCPerfSuite.Core.Profiles;

namespace PCPerfSuite.Core.Tests;

/// <summary>Un groupe se relit tel qu'une autre version, ou une main, l'a écrit dans settings.json.</summary>
public class ProfileGroupSerializationTests
{
    private static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json)!;

    private static string Write<T>(T value) => JsonSerializer.Serialize(value);

    [Fact]
    public void Une_propriete_absente_garde_sa_valeur_par_defaut()
    {
        ProfileGroup group = Read<ProfileGroup>("""{"Id":"a","Name":"Jeu"}""");

        Assert.Equal(ProfileGroupOrigin.Manual, group.Origin);
        Assert.Null(group.Usage);
        Assert.False(group.EditedByUser);
        Assert.Null(group.Cpu);
        Assert.Null(group.Gpu);
        Assert.Null(group.Fans);
        Assert.True(group.IsEmpty);
    }

    [Fact]
    public void Une_propriete_inconnue_du_groupe_est_conservee()
    {
        ProfileGroup group = Read<ProfileGroup>("""{"Id":"a","Name":"Jeu","Eclairage":{"Couleur":"#FF0000"}}""");

        string again = Write(group);

        Assert.Contains("\"Eclairage\":{\"Couleur\":\"#FF0000\"}", again);
    }

    [Fact]
    public void Une_propriete_inconnue_d_une_partie_est_conservee()
    {
        ProfileGroup group = Read<ProfileGroup>(
            """{"Id":"a","Gpu":{"Kind":"valeurs","Values":{"Name":"OC","CoreClockOffsetMhz":150},"Futur":3}}""");

        Assert.Equal(150, group.Gpu!.Values!.CoreClockOffsetMhz);
        Assert.Contains("\"Futur\":3", Write(group));
    }

    [Fact]
    public void Une_propriete_inconnue_du_bloc_est_conservee()
    {
        ProfileGroupsSettings settings = Read<ProfileGroupsSettings>("""{"Groups":[],"BasculeAuto":{"Active":true}}""");

        Assert.Contains("\"BasculeAuto\":{\"Active\":true}", Write(settings));
    }

    [Fact]
    public void Une_dimension_nulle_reste_nulle()
    {
        var group = new ProfileGroup { Cpu = null, Gpu = new ProfileGroupGpuPart { Kind = ProfilePartKinds.Origin } };

        ProfileGroup again = Read<ProfileGroup>(Write(group));

        Assert.Null(again.Cpu);
        Assert.Null(again.Fans);
        Assert.Equal(ProfilePartKind.Origin, again.Gpu!.ParsedKind);
    }

    [Theory]
    [InlineData(null, true, ProfilePartKind.Values)]
    [InlineData(null, false, ProfilePartKind.Empty)]
    [InlineData("valeurs", false, ProfilePartKind.Empty)]
    [InlineData("VALEURS", true, ProfilePartKind.Values)]
    [InlineData("origine", false, ProfilePartKind.Origin)]
    [InlineData("demi-mesure", true, ProfilePartKind.Unknown)]
    public void Le_genre_d_une_partie_se_lit_en_chaine(string? kind, bool hasValues, ProfilePartKind expected)
        => Assert.Equal(expected, ProfilePartKinds.Parse(kind, hasValues));

    [Fact]
    public void Les_enums_des_configurations_integrees_restent_en_nombres()
    {
        var group = new ProfileGroup
        {
            Fans = new ProfileGroupFansPart
            {
                Values = new FanProfile
                {
                    Fans = { new FanCurveConfig { ControlSensorId = "gpu:0", Mode = FanControlMode.Curve, Source = FanTempSource.GpuCore } },
                },
            },
        };

        string json = Write(group);

        Assert.Contains("\"Mode\":2", json);
        Assert.Contains("\"Source\":1", json);
    }

    [Fact]
    public void Un_mode_de_ventilateur_inconnu_se_relit_puis_retombe_sur_auto()
    {
        ProfileGroup group = Read<ProfileGroup>(
            """{"Id":"a","Fans":{"Values":{"Fans":[{"ControlSensorId":"gpu:0","Mode":7,"Source":1}]}}}""");

        FanCurveConfig entry = group.Fans!.Values!.Fans.Single();
        Assert.Equal((FanControlMode)7, entry.Mode);
        Assert.Equal(FanControlMode.Auto, FanProfileMatcher.Sanitize(entry).Config!.Mode);
    }

    [Fact]
    public void Les_identites_partent_en_chaines()
    {
        var group = new ProfileGroup
        {
            Cpu = new ProfileGroupCpuPart
            {
                Values = new CpuProfile { SustainedWatts = 65 },
                CapturedOn = new CpuIdentity { Vendor = "Intel", Family = 6, Model = 183, Name = "Intel Core i5-14600K" },
            },
            Gpu = new ProfileGroupGpuPart
            {
                Values = new GpuOverclockProfile { CoreClockOffsetMhz = 100 },
                CapturedOn = new GpuIdentity(GpuVendor.Nvidia, "NVIDIA GeForce RTX 5070 Ti"),
            },
        };

        string json = Write(group);
        ProfileGroup again = Read<ProfileGroup>(json);

        Assert.Contains("\"Vendor\":\"Intel\"", json);
        Assert.Contains("\"Vendor\":\"Nvidia\"", json);
        Assert.Equal("Intel Core i5-14600K", again.Cpu!.CapturedOn!.Name);
        Assert.Equal(GpuVendor.Nvidia, again.Gpu!.CapturedOn!.Vendor);
    }

    [Fact]
    public void Un_fabricant_de_processeur_inconnu_ne_casse_pas_la_relecture()
    {
        ProfileGroup group = Read<ProfileGroup>("""{"Id":"a","Cpu":{"CapturedOn":{"Vendor":"RiscV","Name":"X"}}}""");

        Assert.Equal("RiscV", group.Cpu!.CapturedOn!.Vendor);
    }

    [Fact]
    public void Un_fichier_d_avant_les_groupes_donne_un_bloc_vide()
    {
        AppSettings settings = Read<AppSettings>("""{"MonitoringRefreshMs":1000}""");

        Assert.NotNull(settings.ProfileGroups);
        Assert.Empty(settings.ProfileGroups.Groups);
        Assert.Empty(settings.ProfileGroupPowerOrigins);
    }

    [Fact]
    public void Les_reglages_complets_font_l_aller_retour()
    {
        var settings = new AppSettings();
        settings.ProfileGroups.Groups.Add(new ProfileGroup { Name = "Jeu", Usage = ProfileGroupUsage.HeavyGaming });
        settings.ProfileGroupPowerOrigins["scheme"] = new Dictionary<string, uint> { ["epp/ac"] = 33 };

        AppSettings again = Read<AppSettings>(Write(settings));

        Assert.Equal("Jeu", again.ProfileGroups.Groups.Single().Name);
        Assert.Equal(ProfileGroupUsage.HeavyGaming, again.ProfileGroups.Groups.Single().Usage);
        Assert.Equal(33u, again.ProfileGroupPowerOrigins["scheme"]["epp/ac"]);
    }

    [Fact]
    public void La_copie_est_profonde_et_garde_les_extensions()
    {
        ProfileGroup group = Read<ProfileGroup>(
            """{"Id":"a","Name":"Jeu","Futur":1,"Cpu":{"Values":{"PowerSettings":{"epp":{"Ac":20}}}}}""");

        ProfileGroup copy = group.Clone();
        copy.Cpu!.Values!.PowerSettings["epp"].Ac = 80;

        Assert.Equal(20u, group.Cpu!.Values!.PowerSettings["epp"].Ac);
        Assert.Contains("\"Futur\":1", Write(copy));
    }

    [Fact]
    public void Normaliser_remet_d_aplomb_un_bloc_edite_a_la_main()
    {
        ProfileGroupsSettings settings = Read<ProfileGroupsSettings>(
            """
            {"Groups":[null,{"Id":"a","Name":""},{"Id":"a","Name":"Double"},{"Name":"Sans id","Origin":""}],
             "Active":{"GroupId":"disparu"},
             "Suspensions":{"disparu":{"Cause":"x"},"a":{"Cause":"écran bleu"}}}
            """);

        Assert.True(settings.Normalize());

        Assert.Equal(3, settings.Groups.Count);
        Assert.Equal(3, settings.Groups.Select(g => g.Id).Distinct().Count());
        Assert.Equal("Groupe", settings.Groups[0].Name);
        Assert.Equal(ProfileGroupOrigin.Manual, settings.Groups[2].Origin);
        Assert.Null(settings.Active);
        Assert.Equal(new[] { "a" }, settings.Suspensions.Keys);
        Assert.False(settings.Normalize());
    }

    [Fact]
    public void Normaliser_complete_une_partie_processeur_sans_liste_de_reglages()
    {
        ProfileGroupsSettings settings = Read<ProfileGroupsSettings>(
            """{"Groups":[{"Id":"a","Name":"Jeu","Cpu":{"Kind":"valeurs","Values":{"PowerSettings":null,"SustainedWatts":65}}}]}""");

        Assert.True(settings.Normalize());

        Assert.Empty(settings.Groups[0].Cpu!.Values!.PowerSettings);
        Assert.Equal(65f, settings.Groups[0].Cpu!.Values!.SustainedWatts);
    }

    [Theory]
    [InlineData(ProfileGroupUsage.Office, "Bureautique")]
    [InlineData(ProfileGroupUsage.LightGaming, "Jeu léger")]
    [InlineData(ProfileGroupUsage.HeavyGaming, "Jeu exigeant")]
    [InlineData("futur", "futur")]
    [InlineData(null, null)]
    public void Un_usage_s_affiche_en_clair(string? usage, string? expected)
        => Assert.Equal(expected, ProfileGroupUsage.Label(usage));
}
