using PCPerfSuite.Core.SystemInfo;

namespace PCPerfSuite.Core.Tests;

/// <summary>Le dossier de données : un seul endroit, racine choisie une fois pour la session.</summary>
public class AppDataPathsTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "PCPerfSuite-tests", "donnees");

    [Fact]
    public void Tous_les_fichiers_sont_sous_la_racine_injectee_avec_leurs_noms_historiques()
    {
        var paths = new AppDataPaths(Root);

        Assert.Equal(Path.Combine(Root, "settings.json"), paths.SettingsFile);
        Assert.Equal(Path.Combine(Root, "erreurs.log"), paths.CrashLogFile);
        Assert.Equal(Path.Combine(Root, "diagnostic-ventilateurs.log"), paths.FanChipDiagnosticFile);
        Assert.Equal(Path.Combine(Root, "adlx-plantage.temoin"), paths.AdlxSentinelFile);
    }

    [Fact]
    public void La_racine_par_defaut_est_celle_d_avant_dans_le_profil_local()
    {
        string expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCPerfSuite");

        Assert.Equal(expected, AppDataPaths.DefaultRoot);
    }

    [Fact]
    public void Une_racine_relative_est_rendue_absolue_et_une_racine_vide_refusee()
    {
        Assert.True(Path.IsPathFullyQualified(new AppDataPaths("donnees").Root));
        Assert.Throws<ArgumentException>(() => new AppDataPaths(" "));
    }

    [Fact]
    public void Un_choix_fait_avant_la_premiere_lecture_est_retenu()
    {
        var choice = new AppDataRootChoice(() => new AppDataPaths(Path.Combine(Root, "defaut")));

        Assert.True(choice.TryChoose(new AppDataPaths(Path.Combine(Root, "cle-usb"))));
        Assert.Equal(Path.Combine(Root, "cle-usb"), choice.Current.Root);
    }

    [Fact]
    public void Apres_la_premiere_lecture_la_racine_ne_change_plus()
    {
        // Les fichiers seraient sinon écrits à deux endroits dans la même session.
        var choice = new AppDataRootChoice(() => new AppDataPaths(Path.Combine(Root, "defaut")));
        AppDataPaths first = choice.Current;

        Assert.False(choice.TryChoose(new AppDataPaths(Path.Combine(Root, "cle-usb"))));
        Assert.Same(first, choice.Current);
        Assert.Equal(Path.Combine(Root, "defaut"), choice.Current.Root);
    }
}
