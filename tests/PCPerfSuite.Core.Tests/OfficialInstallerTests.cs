using PCPerfSuite.Core.Hardware.Cpu;
using PCPerfSuite.Core.Installations;
using PCPerfSuite.Core.Overlay;

namespace PCPerfSuite.Core.Tests;

/// <summary>
/// Les garde-fous du téléchargement des installeurs : tout ce qui n'est pas une source officielle en HTTPS doit être
/// refusé AVANT le moindre accès réseau. Ces tests n'en font aucun et ne lancent jamais rien : ils protègent la seule
/// partie de l'app qui exécute un fichier téléchargé, avec les droits administrateur.
/// </summary>
public class OfficialInstallerTests
{
    private static OfficialInstallerSource Source(string url, string fileName = "setup.exe")
        => new(new Uri(url), OfficialInstaller.GitHubHosts, fileName, "-install", "namazso");

    private static Task<InstallOutcome> Run(OfficialInstallerSource source)
        => OfficialInstaller.DownloadAndRunAsync(source, progress: null, CancellationToken.None);

    [Theory]
    [InlineData("http://github.com/namazso/PawnIO.Setup/releases/latest/download/PawnIO_setup.exe")]
    [InlineData("ftp://github.com/setup.exe")]
    public async Task Une_adresse_qui_n_est_pas_en_HTTPS_est_refusee(string url)
    {
        InstallOutcome outcome = await Run(Source(url));

        Assert.False(outcome.Succeeded);
        Assert.Contains("HTTPS", outcome.Message);
    }

    [Theory]
    [InlineData("https://example.com/setup.exe")]
    [InlineData("https://github.com.evil.example/setup.exe")]
    [InlineData("https://evilgithub.com/setup.exe")]
    [InlineData("https://githubusercontent.com/setup.exe")]
    [InlineData("https://evilgithubusercontent.com/setup.exe")]
    [InlineData("https://codeload.github.com/namazso/PawnIO.Setup/zip/refs/heads/main")]
    public async Task Un_hote_qui_n_est_pas_une_source_connue_est_refuse(string url)
    {
        InstallOutcome outcome = await Run(Source(url));

        Assert.False(outcome.Succeeded);
        Assert.Contains("source officielle", outcome.Message);
    }

    [Theory]
    [InlineData(@"..\evil.exe")]
    [InlineData(@"C:\Windows\evil.exe")]
    [InlineData("sous-dossier/evil.exe")]
    public async Task Un_nom_de_fichier_qui_contient_un_chemin_est_refuse(string fileName)
    {
        InstallOutcome outcome = await Run(Source("https://github.com/namazso/PawnIO.Setup/releases/latest/download/PawnIO_setup.exe", fileName));

        Assert.False(outcome.Succeeded);
        Assert.Contains("invalide", outcome.Message);
    }

    [Fact]
    public void La_source_officielle_de_PawnIO_est_en_HTTPS_sur_un_hote_autorise_et_attend_son_auteur()
    {
        OfficialInstallerSource source = PawnIoDriver.SetupSource;

        Assert.Equal(Uri.UriSchemeHttps, source.Url.Scheme);
        Assert.Contains(source.Url.Host, source.AllowedHosts);
        Assert.Equal("namazso", source.ExpectedPublisher);
        Assert.Equal(Path.GetFileName(source.FileName), source.FileName);
    }

    [Theory]
    [InlineData("http://example.com")]
    [InlineData("file:///C:/Windows/notepad.exe")]
    [InlineData("notepad.exe")]
    [InlineData("")]
    public void Un_lien_qui_n_est_pas_en_HTTPS_n_est_jamais_ouvert(string url)
    {
        bool opened = ExternalLink.TryOpen(url, out string? error);

        Assert.False(opened);
        Assert.Contains("HTTPS", error);
    }

    [Fact]
    public void La_page_de_telechargement_de_RTSS_est_en_HTTPS()
    {
        Assert.StartsWith("https://", RtssInstallation.DownloadPageUrl);
    }

    [Fact]
    public void La_detection_des_logiciels_externes_ne_leve_jamais()
    {
        // Le résultat dépend du PC qui exécute les tests : seule l'absence d'exception est vérifiée.
        RtssStatus rtss = RtssInstallation.Detect();
        PawnIoInstallation pawnIo = PawnIoDriver.ReadInstallation();

        Assert.NotNull(rtss);
        Assert.Equal(pawnIo.LibraryPath is not null, pawnIo.IsOnDisk);
    }
}
