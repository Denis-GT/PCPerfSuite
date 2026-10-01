using PCPerfSuite.App.ViewModels;
using PCPerfSuite.Core.Installations;

namespace PCPerfSuite.App.Tests;

/// <summary>
/// Une ligne de la Boîte à outils propose le bon bouton selon l'outil et ce qui est sur le PC : jamais « Lancer » pour un
/// outil non signé, jamais d'installation sans lien vérifié, « Mettre à jour » seulement quand le catalogue est
/// sûrement plus récent.
/// </summary>
public class ToolItemViewModelTests
{
    private static readonly ToolCatalogStore Store = new(ToolCatalog.All);

    private static ToolItemViewModel Item(string id, ToolInstallState? state = null, bool withRelease = true)
    {
        var page = new ToolboxViewModel(Store);
        ToolItemViewModel item = page.Items.Single(i => i.Definition.Id == id);
        item.Release = withRelease ? Store.ReleaseOf(id) : null;
        if (state is not null) item.State = state;
        return item;
    }

    [Fact]
    public void Un_outil_portable_absent_s_installe_puis_se_lance_et_se_supprime()
    {
        ToolItemViewModel absent = Item("cpu-z");
        ToolItemViewModel present = Item("cpu-z", new ToolInstallState(true, "3.01", @"C:\x\cpuz_x64.exe", true, @"C:\x"));

        Assert.Equal("Installer (portable)", absent.PrimaryLabel);
        Assert.False(absent.CanRemove);
        Assert.Equal("Lancer", present.PrimaryLabel);
        Assert.True(present.CanRemove);
        Assert.True(present.IsPresent);
        Assert.False(present.CanUpdate);
    }

    [Fact]
    public void Une_version_plus_recente_au_catalogue_propose_la_mise_a_jour()
    {
        ToolItemViewModel item = Item("ddu", new ToolInstallState(true, "18.1.5.2", @"C:\DDU\Display Driver Uninstaller.exe", false));

        Assert.True(item.CanUpdate);
        Assert.Contains("18.1.5.3 disponible", item.StatusText);
        Assert.Equal("Lancer", item.PrimaryLabel);
    }

    [Fact]
    public void Une_version_installee_illisible_ne_propose_pas_de_mise_a_jour()
    {
        Assert.False(Item("ddu", new ToolInstallState(true, null, null, false)).CanUpdate);
    }

    [Fact]
    public void Un_outil_non_signe_n_est_que_telecharge()
    {
        ToolItemViewModel item = Item("prime95");

        Assert.Equal("Télécharger", item.PrimaryLabel);
        Assert.True(item.IsUnsigned);
        Assert.False(item.CanDownload);
        Assert.Contains("ne le lance jamais", item.Note);
    }

    [Fact]
    public void Un_outil_non_signe_deja_installe_ne_se_lance_pas_depuis_l_app()
    {
        ToolItemViewModel item = Item("openrgb", new ToolInstallState(true, "1.0", @"C:\Program Files\OpenRGB\OpenRGB.exe", false));

        Assert.Equal("Télécharger", item.PrimaryLabel);
        Assert.False(item.CanUpdate);
    }

    [Fact]
    public void Un_outil_sans_lien_direct_ouvre_sa_page_et_dit_pourquoi()
    {
        ToolItemViewModel item = Item("gpu-z");

        Assert.Equal("Page officielle", item.PrimaryLabel);
        Assert.False(item.ShowPageButton);
        Assert.False(item.IsExperimental);
        Assert.Contains("24 heures", item.Note);
    }

    [Fact]
    public void Sans_lien_verifie_au_catalogue_rien_ne_s_installe()
    {
        ToolItemViewModel item = Item("cpu-z", withRelease: false);

        Assert.Equal("Page officielle", item.PrimaryLabel);
        Assert.False(item.CanDownload);
        Assert.Null(item.DirectLink);
        Assert.Contains("Aucun lien direct", item.VersionText);
    }

    [Fact]
    public void Un_pilote_installe_n_a_rien_a_lancer()
    {
        ToolItemViewModel item = Item("pawnio", new ToolInstallState(true, "2.2.0", null, false));

        Assert.Null(item.PrimaryLabel);
        Assert.True(item.CanDownload);
    }

    [Fact]
    public void Le_lien_direct_et_la_taille_sont_affiches()
    {
        ToolItemViewModel item = Item("cpu-z");

        Assert.Equal("https://download.cpuid.com/cpu-z/cpu-z_3.01-en.zip", item.DirectLink);
        Assert.Equal("Version 3.01 · 5,2 Mo", item.VersionText);
    }

    [Fact]
    public void Les_rubriques_reprennent_tous_les_outils_dans_l_ordre()
    {
        var page = new ToolboxViewModel(Store);

        Assert.Equal(new[] { "Diagnostic", "Stress et bench", "GPU et pilotes", "Divers" }, page.Groups.Select(g => g.Title));
        Assert.Equal(ToolCatalog.All.Count, page.Groups.Sum(g => g.Items.Count));
    }
}
