using PCPerfSuite.App.Utils;
using PCPerfSuite.App.ViewModels;
using PCPerfSuite.Core.Installations;

namespace PCPerfSuite.App.Tests;

/// <summary>
/// Une ligne de la Boîte à outils propose le bon bouton selon l'outil et ce qui est sur le PC : rien tant que l'état n'est
/// pas lu, jamais « Lancer » pour un outil non signé, jamais d'installation sans lien vérifié, « Mettre à jour »
/// seulement quand le catalogue est sûrement plus récent. Les valeurs attendues viennent de la copie intégrée, pas de
/// numéros figés : elle sera régénérée.
/// </summary>
public class ToolItemViewModelTests
{
    private static readonly ToolCatalogStore Store = new(ToolCatalog.All);

    private static ToolRelease ReleaseOf(string id) => Store.ReleaseOf(id)!;

    private static ToolItemViewModel Item(string id, ToolInstallState? state = null, bool withRelease = true, bool checkedState = true)
    {
        var page = new ToolboxViewModel(Store);
        ToolItemViewModel item = page.Items.Single(i => i.Definition.Id == id);
        item.Release = withRelease ? Store.ReleaseOf(id) : null;
        if (state is not null) item.State = state;
        item.IsChecked = checkedState;
        return item;
    }

    /// <summary>Une version plus ancienne que celle du catalogue (« 3.01 » → « 3.0 », « 18.1.5.3 » → « 18.1.5.2 »).</summary>
    private static string Older(string version)
    {
        string[] parts = version.Split('.');
        int last = Array.FindLastIndex(parts, p => int.TryParse(p, out int n) && n > 0);
        parts[last] = (int.Parse(parts[last]) - 1).ToString();
        return string.Join('.', parts);
    }

    [Fact]
    public void Tant_que_l_etat_n_est_pas_lu_la_ligne_attend_et_ne_propose_rien()
    {
        ToolItemViewModel item = Item("cpu-z", checkedState: false);

        Assert.Equal("Vérification…", item.StatusText);
        Assert.Null(item.PrimaryLabel);
        Assert.False(item.CanDownload);
        Assert.False(item.CanRemove);
        Assert.False(item.CanUpdate);
        Assert.False(item.PrimaryCommand.CanExecute(null));
    }

    [Fact]
    public void Un_outil_portable_absent_s_installe_puis_se_lance_et_se_supprime()
    {
        string version = ReleaseOf("cpu-z").Version;
        ToolItemViewModel absent = Item("cpu-z");
        ToolItemViewModel present = Item("cpu-z", new ToolInstallState(true, version, @"C:\x\cpuz_x64.exe", true, @"C:\x"));

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
        string latest = ReleaseOf("ddu").Version;
        ToolItemViewModel item = Item("ddu", new ToolInstallState(true, Older(latest), @"C:\DDU\Display Driver Uninstaller.exe", false));

        Assert.True(item.CanUpdate);
        Assert.Contains($"{latest} disponible", item.StatusText);
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
        ToolItemViewModel item = Item("openrgb", new ToolInstallState(true, "0.1", @"C:\Program Files\OpenRGB\OpenRGB.exe", false));

        Assert.Equal("Télécharger", item.PrimaryLabel);
        Assert.False(item.CanUpdate);
    }

    [Fact]
    public void Un_outil_sans_lien_direct_ouvre_sa_page_et_dit_pourquoi()
    {
        ToolItemViewModel item = Item("gpu-z");

        Assert.Equal("Page officielle", item.PrimaryLabel);
        Assert.Equal("Pas de lien direct", item.StatusText);
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
        Assert.False(item.CopyLinkCommand.CanExecute(null));
        Assert.Contains("Aucun lien direct", item.VersionText);
    }

    [Fact]
    public void Le_lien_apparu_apres_coup_peut_etre_copie()
    {
        ToolItemViewModel item = Item("cpu-z", withRelease: false);
        bool notified = false;
        item.CopyLinkCommand.CanExecuteChanged += (_, _) => notified = true;

        item.Release = ReleaseOf("cpu-z");

        Assert.True(notified);
        Assert.True(item.CopyLinkCommand.CanExecute(null));
    }

    [Fact]
    public void Un_pilote_installe_n_a_rien_a_lancer()
    {
        ToolItemViewModel item = Item("pawnio", new ToolInstallState(true, ReleaseOf("pawnio").Version, null, false));

        Assert.Null(item.PrimaryLabel);
        Assert.True(item.CanDownload);
    }

    [Fact]
    public void Le_lien_direct_et_la_taille_sont_affiches()
    {
        ToolRelease release = ReleaseOf("cpu-z");
        ToolItemViewModel item = Item("cpu-z");

        Assert.Equal(release.Url.AbsoluteUri, item.DirectLink);
        Assert.Equal($"Version {release.Version} · {ByteFormatter.Format(release.Size)}", item.VersionText);
    }

    [Fact]
    public void Annuler_n_est_propose_qu_au_cours_d_une_operation()
    {
        ToolItemViewModel item = Item("cpu-z");

        Assert.False(item.CanCancel);
        Assert.False(item.CancelCommand.CanExecute(null));
    }

    [Fact]
    public void Les_rubriques_reprennent_tous_les_outils_dans_l_ordre()
    {
        var page = new ToolboxViewModel(Store);

        Assert.Equal(new[] { "Diagnostic", "Stress et bench", "GPU et pilotes", "Divers" }, page.Groups.Select(g => g.Title));
        Assert.Equal(ToolCatalog.All.Count, page.Groups.Sum(g => g.Items.Count));
    }
}
