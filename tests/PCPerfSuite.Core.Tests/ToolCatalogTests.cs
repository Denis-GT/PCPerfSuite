using System.Text.RegularExpressions;
using PCPerfSuite.Core.Installations;

namespace PCPerfSuite.Core.Tests;

/// <summary>
/// Le catalogue figé de la Boîte à outils : chaque outil doit avoir de quoi être téléchargé en sécurité (HTTPS, hôtes
/// autorisés, éditeur ou « non signé » assumé, nom de fichier sans chemin), et la copie intégrée doit être valide pour
/// chacun. Aucun accès réseau : c'est ce que l'app vérifie avant le moindre téléchargement.
/// </summary>
public class ToolCatalogTests
{
    public static IEnumerable<object[]> Tools => ToolCatalog.All.Select(t => new object[] { t.Id });

    private static ToolDefinition Tool(string id) => ToolCatalog.Find(id)!;

    private static ToolCatalogDocument Embedded()
        => ToolCatalogParser.Parse(ToolCatalogStore.EmbeddedBytes(), ToolCatalog.All, out _)!;

    [Fact]
    public void Les_identifiants_sont_uniques_et_en_kebab_case()
    {
        Assert.Equal(ToolCatalog.All.Count, ToolCatalog.All.Select(t => t.Id).Distinct().Count());
        Assert.All(ToolCatalog.All, t => Assert.Matches("^[a-z0-9]+(-[a-z0-9]+)*$", t.Id));
    }

    [Theory]
    [MemberData(nameof(Tools))]
    public void La_page_officielle_est_en_HTTPS(string id)
    {
        ToolDefinition tool = Tool(id);
        if (tool.Delivery == ToolDelivery.BuiltIn) return;

        Assert.True(Uri.TryCreate(tool.OfficialPage, UriKind.Absolute, out Uri? page));
        Assert.Equal(Uri.UriSchemeHttps, page!.Scheme);
    }

    [Theory]
    [MemberData(nameof(Tools))]
    public void Un_outil_telechargeable_a_ses_hotes_et_un_editeur_ou_n_est_jamais_lance(string id)
    {
        ToolDefinition tool = Tool(id);
        if (!tool.HasDirectDownload) return;

        Assert.NotEmpty(tool.AllowedHosts);
        Assert.All(tool.AllowedHosts, host => Assert.Matches(@"^(\*\.)?[a-z0-9-]+(\.[a-z0-9-]+)+$", host));

        // Sans éditeur figé, l'outil ne peut qu'être téléchargé : c'est la seule voie qui ne lance rien.
        if (!tool.IsSigned) Assert.Equal(ToolDelivery.DownloadOnly, tool.Delivery);
        else Assert.False(string.IsNullOrWhiteSpace(tool.ExpectedPublisher));

        Assert.InRange(tool.MaxBytes, 1, OfficialInstaller.MaxAllowedBytes);
        Assert.InRange(tool.DownloadTimeout, TimeSpan.FromSeconds(1), OfficialInstaller.MaxAllowedDownloadTimeout);
    }

    [Theory]
    [MemberData(nameof(Tools))]
    public void Les_noms_de_fichiers_figes_n_ont_pas_de_chemin(string id)
    {
        ToolDefinition tool = Tool(id);

        if (tool.FallbackFileName is { } fallback) Assert.Equal(Path.GetFileName(fallback), fallback);

        if (tool.LaunchFile is { } launch)
        {
            Assert.False(Path.IsPathRooted(launch));
            Assert.DoesNotContain("..", launch);
            Assert.DoesNotContain(':', launch);
        }

        if (tool.Delivery is ToolDelivery.ZipInstaller or ToolDelivery.BuiltIn or ToolDelivery.PortableExe)
        {
            Assert.Equal(Path.GetFileName(tool.LaunchFile), tool.LaunchFile);
        }
    }

    [Theory]
    [MemberData(nameof(Tools))]
    public void Chaque_mode_de_livraison_a_ce_qu_il_lui_faut(string id)
    {
        ToolDefinition tool = Tool(id);
        switch (tool.Delivery)
        {
            case ToolDelivery.PortableExe:
                Assert.True(tool.IsSigned);
                Assert.Equal(InstallerFileKind.Exe, tool.FileKind);
                Assert.Equal(ToolDetectionKind.PortableFolder, tool.Detection);
                Assert.NotNull(tool.LaunchFile);
                break;

            case ToolDelivery.PortableZip:
                Assert.True(tool.IsSigned);
                Assert.Equal(InstallerFileKind.Zip, tool.FileKind);
                Assert.Equal(ToolDetectionKind.PortableFolder, tool.Detection);
                Assert.NotNull(tool.LaunchFile);
                Assert.True(tool.MaxExtractedBytes > 0);
                break;

            case ToolDelivery.Installer:
                Assert.True(tool.IsSigned);
                Assert.NotEqual(InstallerFileKind.Zip, tool.FileKind);
                Assert.NotEqual(ToolDetectionKind.None, tool.Detection);
                break;

            case ToolDelivery.ZipInstaller:
                Assert.True(tool.IsSigned);
                Assert.Equal(InstallerFileKind.Zip, tool.FileKind);
                Assert.Contains('*', tool.LaunchFile!);
                Assert.NotEqual(ToolDetectionKind.None, tool.Detection);
                break;

            case ToolDelivery.OfficialPageOnly:
                Assert.False(string.IsNullOrWhiteSpace(tool.NoDirectLinkReason));
                break;

            case ToolDelivery.BuiltIn:
                Assert.NotNull(tool.LaunchFile);
                break;
        }
    }

    [Theory]
    [MemberData(nameof(Tools))]
    public void La_licence_est_dite_et_l_identifiant_winget_est_sur(string id)
    {
        ToolDefinition tool = Tool(id);

        Assert.False(string.IsNullOrWhiteSpace(tool.Licence.Summary));
        Assert.False(string.IsNullOrWhiteSpace(tool.Purpose));
        if (tool.WingetId is { } winget) Assert.True(WingetFallback.IsValidId(winget), winget);
    }

    [Theory]
    [InlineData("hwinfo")]
    [InlineData("occt")]
    [InlineData("furmark")]
    [InlineData("y-cruncher")]
    [InlineData("cinebench")]
    public void Les_outils_payants_en_usage_commercial_portent_le_badge(string id)
    {
        Assert.True(Tool(id).Licence.RequiresProLicence);
    }

    [Theory]
    [InlineData("prime95")]
    [InlineData("y-cruncher")]
    [InlineData("furmark")]
    [InlineData("7-zip")]
    [InlineData("openrgb")]
    public void Les_outils_non_signes_ne_sont_que_telecharges(string id)
    {
        ToolDefinition tool = Tool(id);

        Assert.False(tool.IsSigned);
        Assert.Equal(ToolDelivery.DownloadOnly, tool.Delivery);
    }

    [Fact]
    public void La_copie_integree_est_valide_pour_chaque_outil_telechargeable()
    {
        ToolCatalogDocument document = Embedded();

        Assert.True(document.Sequence >= 1);
        Assert.Empty(document.Rejected);
        foreach (ToolDefinition tool in ToolCatalog.All.Where(t => t.HasDirectDownload))
        {
            Assert.True(document.Releases.ContainsKey(tool.Id), $"{tool.Id} manque dans la copie intégrée");
        }
    }

    [Fact]
    public void Chaque_version_integree_donne_une_source_acceptee_par_OfficialInstaller()
    {
        foreach (ToolRelease release in Embedded().Releases.Values)
        {
            ToolDefinition tool = Tool(release.ToolId);
            OfficialInstallerSource source = ToolboxActions.SourceFor(tool, release);

            OfficialInstaller.ValidateSource(source);
            Assert.Equal(Uri.UriSchemeHttps, release.Url.Scheme);
            Assert.True(OfficialInstaller.IsAllowedHost(release.Url, tool.AllowedHosts), release.Url.Host);
            Assert.Equal(Path.GetFileName(release.FileName), release.FileName);
            Assert.Matches("^[0-9A-F]{64}$", release.Sha256);
        }
    }

    [Fact]
    public void Le_nom_de_fichier_vient_de_l_adresse_ou_du_repli()
    {
        ToolCatalogDocument document = Embedded();

        Assert.Equal("OCCT.exe", document.Releases["occt"].FileName);
        Assert.Equal("DDU v18.1.5.3_setup.exe", document.Releases["ddu"].FileName);
        Assert.StartsWith("[Guru3D]-RTSSSetup", document.Releases["rtss"].FileName);
    }

    [Fact]
    public void Les_rubriques_ont_un_titre()
    {
        foreach (ToolCategory category in Enum.GetValues<ToolCategory>())
        {
            Assert.False(string.IsNullOrWhiteSpace(ToolCatalog.CategoryTitle(category)));
        }

        Assert.Equal("Stress et bench", ToolCatalog.CategoryTitle(ToolCategory.StressAndBench));
    }

    [Fact]
    public void Le_catalogue_en_ligne_vient_du_seul_hote_brut_de_GitHub()
    {
        Assert.Equal(Uri.UriSchemeHttps, ToolCatalogTrust.CatalogUrl.Scheme);
        Assert.True(OfficialInstaller.IsAllowedHost(ToolCatalogTrust.CatalogUrl, ToolCatalogTrust.Hosts));
        Assert.True(OfficialInstaller.IsAllowedHost(ToolCatalogTrust.SignatureUrl, ToolCatalogTrust.Hosts));
        Assert.Equal(new[] { "raw.githubusercontent.com" }, ToolCatalogTrust.Hosts);
        Assert.Matches(new Regex(@"\.sig$"), ToolCatalogTrust.SignatureUrl.AbsolutePath);
    }
}
