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

    [Theory]
    [InlineData("https://github.com/namazso/PawnIO.Setup/releases/tag/2.2.0", "2.2.0")]
    [InlineData("https://github.com/namazso/PawnIO.Setup/releases/tag/v2.1.0", "2.1.0")]
    [InlineData("https://github.com/namazso/PawnIO.Setup/releases/tag/2.2.0/", "2.2.0")]
    public void La_version_publiee_se_lit_dans_la_redirection_de_la_derniere_version(string location, string expected)
    {
        Assert.Equal(Version.Parse(expected), OfficialInstaller.ReleaseVersionFromLocation(new Uri(location)));
    }

    [Theory]
    [InlineData("https://github.com/namazso/PawnIO.Setup/releases")]
    [InlineData("https://github.com/namazso/PawnIO.Setup/releases/tag/nightly")]
    [InlineData("https://github.com/login?return_to=releases/tag/2.2.0")]
    public void Une_redirection_sans_numero_de_version_ne_donne_aucune_version(string location)
    {
        Assert.Null(OfficialInstaller.ReleaseVersionFromLocation(new Uri(location)));
    }

    [Theory]
    [InlineData("2.2.0", "2.2.0", true)]
    [InlineData("2.2.0", "2.2.0.0", true)]
    [InlineData("2.2", "2.2.0", true)]
    [InlineData("2.3.0", "2.2.0", true)]
    [InlineData("2.1.0", "2.2.0", false)]
    [InlineData("2.2.0", "2.2.1", false)]
    public void PawnIO_est_a_jour_quand_sa_version_atteint_la_derniere_publiee(string installed, string latest, bool expected)
    {
        Assert.Equal(expected, PawnIoDriver.IsUpToDate(installed, Version.Parse(latest)));
    }

    [Theory]
    [InlineData(null, "2.2.0")]
    [InlineData("", "2.2.0")]
    [InlineData("inconnue", "2.2.0")]
    [InlineData("2.2.0", null)]
    public void Dans_le_doute_PawnIO_n_est_pas_declare_a_jour(string? installed, string? latest)
    {
        Assert.False(PawnIoDriver.IsUpToDate(installed, latest is null ? null : Version.Parse(latest)));
    }

    [Fact]
    public void Un_code_de_sortie_connu_de_Windows_est_explique()
    {
        // ERROR_ALREADY_EXISTS, que l'installeur de PawnIO rend pour une version déjà en place. Le texte dépend de la
        // langue de Windows : seule sa présence est vérifiée.
        string text = OfficialInstaller.DescribeExitCode(PawnIoDriver.SetupAlreadyInstalledExitCode);

        Assert.StartsWith("code 183 : ", text);
        Assert.True(text.Length > "code 183 : ".Length);
    }

    [Fact]
    public void Un_code_de_sortie_inconnu_de_Windows_reste_affiche_seul()
    {
        Assert.Equal("code 987654", OfficialInstaller.DescribeExitCode(987654));
    }

    [Fact]
    public void La_page_de_telechargement_de_RTSS_est_en_HTTPS()
    {
        Assert.StartsWith("https://", RtssInstallation.DownloadPageUrl);
    }

    private const string ValidSha = "1F519A22E47187F70A1379A48CA604981C4FCF694F4E65B734AAA74A9FBA3032";
    private const string GitHubAsset = "https://github.com/namazso/PawnIO.Setup/releases/download/2.2.0/PawnIO_setup.exe";

    [Fact]
    public async Task Un_fichier_non_signe_n_est_jamais_lance_meme_avec_son_empreinte()
    {
        var source = new OfficialInstallerSource(new Uri(GitHubAsset), OfficialInstaller.GitHubHosts, "setup.exe", "", ExpectedPublisher: null)
        {
            ExpectedSha256 = ValidSha,
        };

        InstallOutcome outcome = await Run(source);

        Assert.False(outcome.Succeeded);
        Assert.Contains("jamais", outcome.Message);
    }

    [Fact]
    public async Task Une_archive_ne_se_lance_pas()
    {
        InstallOutcome outcome = await Run(Source(GitHubAsset) with { Kind = InstallerFileKind.Zip });

        Assert.False(outcome.Succeeded);
        Assert.Contains("zip", outcome.Message);
    }

    [Fact]
    public void Une_source_ni_signee_ni_accompagnee_de_son_empreinte_est_refusee()
    {
        var source = new OfficialInstallerSource(new Uri(GitHubAsset), OfficialInstaller.GitHubHosts, "a.exe", "", ExpectedPublisher: null);

        Exception error = Assert.ThrowsAny<Exception>(() => OfficialInstaller.ValidateSource(source));
        Assert.Contains("empreinte", error.Message);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(OfficialInstaller.MaxAllowedBytes + 1)]
    public void Une_taille_maximale_hors_limites_est_refusee(long maxBytes)
    {
        Assert.ThrowsAny<Exception>(() => OfficialInstaller.ValidateSource(Source(GitHubAsset) with { MaxBytes = maxBytes }));
    }

    [Fact]
    public void Une_taille_attendue_au_dela_de_la_limite_ou_une_empreinte_mal_formee_sont_refusees()
    {
        Assert.ThrowsAny<Exception>(() => OfficialInstaller.ValidateSource(Source(GitHubAsset) with { ExpectedSize = OfficialInstaller.DefaultMaxBytes + 1 }));
        Assert.ThrowsAny<Exception>(() => OfficialInstaller.ValidateSource(Source(GitHubAsset) with { ExpectedSha256 = "abc" }));
        Assert.ThrowsAny<Exception>(() => OfficialInstaller.ValidateSource(Source(GitHubAsset) with { DownloadTimeout = TimeSpan.FromHours(3) }));
    }

    [Fact]
    public async Task Un_telechargement_refuse_ne_cree_aucun_fichier()
    {
        using var temp = new TempDirectory();
        string destination = temp.File("a.exe");

        DownloadOutcome outcome = await OfficialInstaller.DownloadToFileAsync(Source("https://example.com/a.exe"), destination, null, CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Equal(DownloadFailureKind.Refused, outcome.Failure);
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task Un_fichier_deja_present_n_est_ni_ecrase_ni_supprime()
    {
        using var temp = new TempDirectory();
        string destination = temp.File("a.exe");
        File.WriteAllText(destination, "à moi");

        DownloadOutcome outcome = await OfficialInstaller.DownloadToFileAsync(Source(GitHubAsset), destination, null, CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Equal("à moi", File.ReadAllText(destination));
    }

    private static HttpResponseMessage Response(byte[] body) => new(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    private static string Sha256Of(byte[] data) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data));

    [Fact]
    public async Task Le_fichier_dont_l_empreinte_correspond_est_garde()
    {
        using var temp = new TempDirectory();
        byte[] body = "MZ contenu de l'installeur"u8.ToArray();
        string destination = temp.File("ok.exe");

        await OfficialInstaller.SaveAsync(Response(body), destination, Source(GitHubAsset) with { ExpectedSha256 = Sha256Of(body).ToLowerInvariant(), ExpectedSize = body.Length },
            null, CancellationToken.None);

        Assert.Equal(body, File.ReadAllBytes(destination));
    }

    [Fact]
    public async Task Un_fichier_dont_l_empreinte_differe_est_refuse()
    {
        using var temp = new TempDirectory();
        byte[] body = "MZ contenu remplacé"u8.ToArray();

        Exception error = await Assert.ThrowsAnyAsync<Exception>(() => OfficialInstaller.SaveAsync(Response(body), temp.File("ko.exe"),
            Source(GitHubAsset) with { ExpectedSha256 = ValidSha }, null, CancellationToken.None));

        Assert.Contains("SHA-256", error.Message);
    }

    [Fact]
    public async Task Une_taille_differente_de_celle_attendue_est_refusee_avant_ecriture()
    {
        using var temp = new TempDirectory();
        byte[] body = new byte[100];
        string destination = temp.File("taille.exe");

        Exception error = await Assert.ThrowsAnyAsync<Exception>(() => OfficialInstaller.SaveAsync(Response(body), destination,
            Source(GitHubAsset) with { ExpectedSha256 = ValidSha, ExpectedSize = 99 }, null, CancellationToken.None));

        Assert.Contains("attendus", error.Message);
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task Un_fichier_plus_gros_que_la_limite_de_la_source_est_refuse()
    {
        using var temp = new TempDirectory();

        Exception error = await Assert.ThrowsAnyAsync<Exception>(() => OfficialInstaller.SaveAsync(Response(new byte[2048]), temp.File("gros.exe"),
            Source(GitHubAsset) with { MaxBytes = 1024 }, null, CancellationToken.None));

        Assert.Contains("gros", error.Message);
    }

    [Theory]
    [InlineData("netix.dl.sourceforge.net", true)]
    [InlineData("dl.sourceforge.net", false)]
    [InlineData("evildl.sourceforge.net", false)]
    [InlineData("downloads.sourceforge.net", true)]
    [InlineData("sourceforge.net", false)]
    public void Un_suffixe_autorise_n_accepte_que_ses_sous_domaines(string host, bool expected)
    {
        var hosts = new[] { "downloads.sourceforge.net", "*.dl.sourceforge.net" };

        Assert.Equal(expected, OfficialInstaller.IsAllowedHost(new Uri($"https://{host}/f.zip"), hosts));
    }

    [Fact]
    public void Un_fichier_qui_n_a_pas_l_en_tete_attendu_est_refuse()
    {
        using var temp = new TempDirectory();
        string notExe = temp.File("faux.exe");
        File.WriteAllText(notExe, "<html>page d'erreur</html>");
        string notMsi = temp.File("faux.msi");
        File.WriteAllBytes(notMsi, "MZ\0\0"u8.ToArray());

        Assert.False(OfficialInstaller.TryVerifySignedFile(notExe, InstallerFileKind.Exe, "CPUID", out string? exeError));
        Assert.Contains("programme Windows", exeError);
        Assert.False(OfficialInstaller.TryVerifySignedFile(notMsi, InstallerFileKind.Msi, "CPUID", out string? msiError));
        Assert.Contains("Windows Installer", msiError);
        Assert.False(OfficialInstaller.TryVerifySignedFile(temp.File("absent.exe"), InstallerFileKind.Exe, "CPUID", out _));
    }

    [Fact]
    public void Un_exe_signe_n_est_accepte_que_pour_son_editeur()
    {
        // dotnet.exe, signé par Microsoft, est là partout où ces tests tournent.
        string dotnet = Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "dotnet.exe"));
        if (!File.Exists(dotnet)) return;

        Assert.True(OfficialInstaller.TryVerifySignedFile(dotnet, InstallerFileKind.Exe, "Microsoft Corporation", out string? error), error);
        Assert.False(OfficialInstaller.TryVerifySignedFile(dotnet, InstallerFileKind.Exe, "OCBASE", out string? wrong));
        Assert.Contains("Microsoft Corporation", wrong);
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
