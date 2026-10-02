using System.Text.Json;
using PCPerfSuite.Core.PowerSettings;
using PCPerfSuite.Core.Processes;
using PCPerfSuite.Core.Profiles;

namespace PCPerfSuite.Core.Tests;

/// <summary>Bloc AutoSwitch de settings.json, règles par application et choix du groupe d'un usage.</summary>
public sealed class AutoSwitchSettingsTests
{
    private const string Game = @"C:\Jeux\game.exe";
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 20, 0, 0, TimeSpan.Zero);

    private static AutoSwitchRule Rule(string? usage = ProfileGroupUsage.HeavyGaming, string? groupId = null, bool enabled = true, string path = Game)
        => new() { Match = ApplicationMatch.For(path), Usage = usage, GroupId = groupId, Enabled = enabled, Label = "game" };

    // ---- Règles ----

    [Fact]
    public void La_premiere_regle_qui_correspond_l_emporte()
    {
        List<AutoSwitchRule> rules = [Rule(ProfileGroupUsage.LightGaming), Rule(ProfileGroupUsage.HeavyGaming)];

        UsageRuleTarget target = AutoSwitchRules.Find(rules, Game, null, _ => false)!;

        Assert.Equal(ProfileGroupUsage.LightGaming, target.Usage);
        Assert.Equal("game", target.Label);
        Assert.Equal(rules[0].Id, target.RuleId);
    }

    [Fact]
    public void Une_regle_desactivee_inutilisable_ou_sans_cible_est_sautee()
    {
        List<AutoSwitchRule> rules =
        [
            Rule(enabled: false),
            new AutoSwitchRule { Match = new ApplicationMatch { Mode = "empreinte", Path = Game }, Usage = ProfileGroupUsage.Office },
            Rule(usage: "usage-futur"),
            Rule(usage: null, groupId: "disparu"),
            Rule(ProfileGroupUsage.Office),
        ];

        Assert.Equal(ProfileGroupUsage.Office, AutoSwitchRules.Find(rules, Game, null, _ => false)!.Usage);
    }

    [Fact]
    public void Une_regle_vers_un_groupe_existant_vise_ce_groupe()
    {
        UsageRuleTarget target = AutoSwitchRules.Find([Rule(usage: ProfileGroupUsage.Office, groupId: "g1")], Game, null, id => id == "g1")!;

        Assert.Equal("g1", target.GroupId);
        Assert.Null(target.Usage);
    }

    [Fact]
    public void Aucune_regle_pour_une_autre_application_ou_sans_application()
    {
        Assert.Null(AutoSwitchRules.Find([Rule()], @"D:\autre.exe", null, _ => false));
        Assert.Null(AutoSwitchRules.Find([Rule()], null, null, _ => false));
        Assert.Null(AutoSwitchRules.Find(null, Game, null, _ => false));
    }

    [Fact]
    public void L_editeur_n_est_demande_que_si_une_regle_l_exige()
    {
        AutoSwitchRule withPublisher = Rule();
        withPublisher.Match = ApplicationMatch.For(Game, publisher: "Studio SAS");

        Assert.False(AutoSwitchRules.AnyRequiresPublisher([Rule()]));
        Assert.True(AutoSwitchRules.AnyRequiresPublisher([withPublisher]));
        Assert.Null(AutoSwitchRules.Find([withPublisher], Game, _ => null, _ => false));
        Assert.NotNull(AutoSwitchRules.Find([withPublisher], Game, _ => "Studio SAS", _ => false));
    }

    // ---- Bloc de réglages ----

    [Fact]
    public void Normaliser_remet_d_aplomb_un_bloc_edite_a_la_main()
    {
        var settings = new AutoSwitchSettings { Rules = null, Explanations = null };
        Assert.True(settings.Normalize());
        Assert.NotNull(settings.Rules);
        Assert.NotNull(settings.Explanations);

        AutoSwitchRule a = Rule(), b = Rule();
        b.Id = a.Id;
        settings.Rules!.AddRange([a, null!, b]);
        settings.Explanations!["g1"] = null!;

        Assert.True(settings.Normalize());
        Assert.Equal(2, settings.Rules.Count);
        Assert.NotEqual(settings.Rules[0].Id, settings.Rules[1].Id);
        Assert.Empty(settings.Explanations);
        Assert.False(settings.Normalize());
    }

    [Fact]
    public void Le_bloc_par_defaut_est_desactive_avec_notification()
    {
        var settings = new AppSettings();

        Assert.False(settings.AutoSwitch.Enabled);
        Assert.True(settings.AutoSwitch.NotifyEachSwitch);
        Assert.False(settings.AutoSwitch.IncludeBiosFans);
    }

    [Fact]
    public void Un_fichier_d_avant_ou_d_apres_se_relit_sans_rien_perdre()
    {
        AppSettings old = JsonSerializer.Deserialize<AppSettings>("{}")!;
        Assert.False(old.AutoSwitch.Enabled);

        const string json = """{"AutoSwitch":{"Enabled":true,"Futur":[1],"Rules":[{"Id":"r1","Match":{"Mode":"nom","FileName":"game.exe"},"Usage":"gaming-leger","Autre":"x"}]}}""";
        AppSettings settings = JsonSerializer.Deserialize<AppSettings>(json)!;
        string written = JsonSerializer.Serialize(settings);

        Assert.True(settings.AutoSwitch.Enabled);
        Assert.Equal("gaming-leger", AutoSwitchRules.Find(settings.AutoSwitch.Rules, @"E:\x\game.exe", null, _ => false)!.Usage);
        Assert.Contains("\"Futur\"", written);
        Assert.Contains("\"Autre\"", written);
    }

    [Fact]
    public void Le_nom_affiche_d_une_regle_se_deduit_de_son_chemin()
    {
        var rule = new AutoSwitchRule { Match = ApplicationMatch.For(Game) };

        Assert.Equal("game", rule.DisplayLabel);
        Assert.Equal("application", new AutoSwitchRule().DisplayLabel);
    }

    // ---- Groupe d'un usage ----

    private static ProfileGroup Group(string id, string? usage, bool generated, DateTimeOffset? updated = null) => new()
    {
        Id = id,
        Name = id,
        Usage = usage,
        Origin = generated ? ProfileGroupOrigin.Generated : ProfileGroupOrigin.Manual,
        UpdatedUtc = updated ?? T0,
        Gpu = ProfileGroupEditor.GpuOrigin(),
    };

    [Fact]
    public void Un_groupe_manuel_l_emporte_sur_le_groupe_genere_du_meme_usage()
    {
        var store = new ProfileGroupsSettings
        {
            Groups = { Group("auto", ProfileGroupUsage.HeavyGaming, true, T0.AddDays(1)), Group("mien", ProfileGroupUsage.HeavyGaming, false) },
        };

        UsageGroupChoice choice = UsageGroupResolver.Resolve(store, new UsageTarget(ProfileGroupUsage.HeavyGaming, null));

        Assert.Equal("mien", choice.Group!.Id);
        Assert.Equal(2, choice.Candidates);
        Assert.False(choice.Suspended);
    }

    [Fact]
    public void A_egalite_le_plus_recent_l_emporte()
    {
        var store = new ProfileGroupsSettings
        {
            Groups = { Group("ancien", ProfileGroupUsage.Office, false, T0), Group("recent", ProfileGroupUsage.Office, false, T0.AddHours(1)) },
        };

        Assert.Equal("recent", UsageGroupResolver.Resolve(store, UsageTarget.Office).Group!.Id);
    }

    [Fact]
    public void Un_groupe_suspendu_est_evite_et_signale_s_il_est_le_seul()
    {
        var store = new ProfileGroupsSettings
        {
            Groups = { Group("a", ProfileGroupUsage.Office, true), Group("b", ProfileGroupUsage.Office, false) },
            Suspensions = { ["b"] = new ProfileGroupSuspension { Cause = "écran bleu" } },
        };

        Assert.Equal("a", UsageGroupResolver.Resolve(store, UsageTarget.Office).Group!.Id);

        store.Suspensions["a"] = new ProfileGroupSuspension { Cause = "TDR" };
        UsageGroupChoice choice = UsageGroupResolver.Resolve(store, UsageTarget.Office);
        Assert.True(choice.Suspended);
        Assert.NotNull(choice.Group);
    }

    [Fact]
    public void Un_groupe_vide_ou_sans_usage_n_est_pas_retenu()
    {
        var store = new ProfileGroupsSettings
        {
            Groups = { new ProfileGroup { Id = "vide", Usage = ProfileGroupUsage.Office }, Group("autre", null, false) },
        };

        Assert.Null(UsageGroupResolver.Resolve(store, UsageTarget.Office).Group);
        Assert.Null(UsageGroupResolver.Resolve(store, new UsageTarget(null, null)).Group);
    }

    [Fact]
    public void Une_regle_vers_un_groupe_le_retient_meme_sans_usage()
    {
        var store = new ProfileGroupsSettings { Groups = { Group("g1", null, false) } };

        Assert.Equal("g1", UsageGroupResolver.Resolve(store, new UsageTarget(null, "g1")).Group!.Id);
        Assert.Null(UsageGroupResolver.Resolve(store, new UsageTarget(null, "disparu")).Group);
    }
}
