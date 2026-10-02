using System.Text.Json;
using PCPerfSuite.Core.Processes;

namespace PCPerfSuite.Core.Tests;

/// <summary>Règles par application : chemin complet normalisé par défaut, nom seul sur choix, éditeur facultatif.</summary>
public sealed class ApplicationMatchTests
{
    private const string Game = @"C:\Jeux\Studio\game.exe";

    [Theory]
    [InlineData(@"C:\Jeux\Studio\game.exe", @"C:\Jeux\Studio\game.exe")]
    [InlineData(@"  ""C:\Jeux\Studio\game.exe""  ", @"C:\Jeux\Studio\game.exe")]
    [InlineData(@"C:/Jeux/Studio/game.exe", @"C:\Jeux\Studio\game.exe")]
    [InlineData(@"\\?\C:\Jeux\Studio\game.exe", @"C:\Jeux\Studio\game.exe")]
    [InlineData(@"C:\Jeux\Autre\..\Studio\.\game.exe", @"C:\Jeux\Studio\game.exe")]
    [InlineData(@"\\?\UNC\serveur\partage\game.exe", @"\\serveur\partage\game.exe")]
    public void Un_chemin_est_normalise(string raw, string expected)
        => Assert.Equal(expected, ApplicationPaths.Normalize(raw));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"Jeux\game.exe")]
    [InlineData("game.exe")]
    [InlineData(@"C:\")]
    [InlineData("C:\\Jeux\\ga\0me.exe")]
    public void Un_chemin_relatif_vide_ou_invalide_est_refuse(string? raw)
        => Assert.Null(ApplicationPaths.Normalize(raw));

    [Fact]
    public void Par_defaut_le_chemin_complet_compte_et_la_casse_non()
    {
        ApplicationMatch match = ApplicationMatch.For(Game)!;

        Assert.Equal(ApplicationMatchModes.Path, match.Mode);
        Assert.True(match.Matches(@"c:\JEUX\studio\GAME.EXE"));
        Assert.False(match.Matches(@"D:\Autre\game.exe"));
        Assert.False(match.Matches(null));
    }

    [Fact]
    public void Le_nom_seul_correspond_dans_tout_dossier()
    {
        ApplicationMatch match = ApplicationMatch.For(Game, nameOnly: true)!;

        Assert.True(match.Matches(@"D:\Autre\Dossier\GAME.exe"));
        Assert.False(match.Matches(@"C:\Jeux\Studio\launcher.exe"));
        Assert.Contains("dans tout dossier", match.Describe());
    }

    [Fact]
    public void L_editeur_exige_doit_etre_celui_de_la_signature_validee()
    {
        ApplicationMatch match = ApplicationMatch.For(Game, publisher: "Studio SAS")!;

        Assert.True(match.Matches(Game, _ => "studio sas"));
        Assert.False(match.Matches(Game, _ => "Autre Editeur"));
        Assert.False(match.Matches(Game, _ => null));
        Assert.False(match.Matches(Game));
    }

    [Fact]
    public void L_editeur_n_est_consulte_que_si_l_executable_correspond()
    {
        ApplicationMatch match = ApplicationMatch.For(Game, publisher: "Studio SAS")!;
        int lookups = 0;

        Assert.False(match.Matches(@"D:\autre.exe", _ => { lookups++; return "Studio SAS"; }));
        Assert.Equal(0, lookups);
    }

    [Fact]
    public void Un_mode_inconnu_ne_correspond_a_rien_et_reste_lisible()
    {
        var match = new ApplicationMatch { Mode = "empreinte", Path = Game, FileName = "game.exe" };

        Assert.Equal(ApplicationMatchMode.Unknown, match.ParsedMode);
        Assert.False(match.IsUsable);
        Assert.False(match.Matches(Game));
        Assert.Contains("version plus récente", match.Describe());
    }

    [Fact]
    public void Un_mode_absent_vaut_le_chemin_et_un_nom_absent_se_deduit_du_chemin()
    {
        var byPath = new ApplicationMatch { Mode = null, Path = Game };
        var byName = new ApplicationMatch { Mode = "nom", Path = Game, FileName = null };

        Assert.True(byPath.Matches(Game));
        Assert.True(byName.Matches(@"E:\game.exe"));
    }

    [Fact]
    public void Une_regle_vide_n_est_pas_utilisable()
    {
        Assert.False(new ApplicationMatch().IsUsable);
        Assert.False(new ApplicationMatch { Mode = "nom" }.IsUsable);
        Assert.Null(ApplicationMatch.For("relatif.exe"));
    }

    [Fact]
    public void Ce_qu_une_version_plus_recente_a_ecrit_est_conserve()
    {
        const string json = """{"Mode":"chemin","Path":"C:\\Jeux\\game.exe","Futur":{"a":1}}""";

        ApplicationMatch match = JsonSerializer.Deserialize<ApplicationMatch>(json)!;
        string written = JsonSerializer.Serialize(match.Clone());

        Assert.True(match.Matches(@"C:\Jeux\game.exe"));
        Assert.Contains("\"Futur\"", written);
    }

    [Theory]
    [InlineData(@"C:\Program Files\WindowsApps\Editeur.Appli_1.2.3.0_x64__8wekyb3d8bbwe\Appli.exe", true)]
    [InlineData(@"C:\Users\moi\AppData\Local\Discord\app-1.0.9163\Discord.exe", true)]
    [InlineData(@"C:\Programmes\Outil\120.0.6099.71\outil.exe", true)]
    [InlineData(@"C:\Programmes\Outil\v2.3\outil.exe", true)]
    [InlineData(@"C:\Jeux\Studio\game.exe", false)]
    [InlineData(@"C:\Jeux\Studio 2024\game.exe", false)]
    [InlineData("relatif.exe", false)]
    public void Un_dossier_versionne_suggere_le_nom_seul(string path, bool expected)
        => Assert.Equal(expected, ApplicationPaths.SuggestsNameMode(path));

    [Fact]
    public void Le_nom_affiche_retire_l_extension()
        => Assert.Equal("game", ApplicationPaths.DisplayName(Game));
}
