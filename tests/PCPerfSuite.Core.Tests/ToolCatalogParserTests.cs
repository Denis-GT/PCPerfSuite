using System.Text;
using PCPerfSuite.Core.Installations;

namespace PCPerfSuite.Core.Tests;

/// <summary>
/// Lecture tolérante du catalogue : un champ ou un outil inconnu est ignoré, une entrée invalide est écartée seule avec
/// sa raison, et seul un document illisible, d'un format plus récent ou sans numéro est refusé entier. Chaque adresse
/// est jugée contre les hôtes figés de l'outil, jamais contre ce que dit le catalogue.
/// </summary>
public class ToolCatalogParserTests
{
    private const string Sha = "8AE3B45D43D97E6CE19535C045CD1E5394C7A3A5334DC01F63EDD760ACE40827";

    private static ToolCatalogDocument? Parse(string json, out string? error)
        => ToolCatalogParser.Parse(Encoding.UTF8.GetBytes(json), ToolCatalog.All, out error);

    private static ToolCatalogDocument ParseOk(string json)
    {
        ToolCatalogDocument? document = Parse(json, out string? error);
        Assert.True(document is not null, error);
        return document!;
    }

    private static string Entry(string id = "cpu-z", string version = "3.01", string url = "https://download.cpuid.com/cpu-z/cpu-z_3.01-en.zip",
        string sha = Sha, long size = 5478310)
        => $$"""{ "id": "{{id}}", "version": "{{version}}", "url": "{{url}}", "sha256": "{{sha}}", "size": {{size}} }""";

    private static string Document(params string[] entries)
        => $$"""{ "format": 1, "sequence": 7, "generatedUtc": "2026-10-01T06:00:00Z", "tools": [ {{string.Join(",", entries)}} ] }""";

    [Fact]
    public void Un_catalogue_valide_donne_ses_versions()
    {
        ToolCatalogDocument document = ParseOk(Document(Entry()));

        Assert.Equal(7, document.Sequence);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 6, 0, 0, TimeSpan.Zero), document.GeneratedUtc);
        ToolRelease release = document.Releases["cpu-z"];
        Assert.Equal("3.01", release.Version);
        Assert.Equal("cpu-z_3.01-en.zip", release.FileName);
        Assert.Equal(5478310, release.Size);
        Assert.Empty(document.Rejected);
    }

    [Fact]
    public void Les_champs_inconnus_les_commentaires_et_les_virgules_finales_sont_toleres()
    {
        string json = """
            {
              // publié par l'Action
              "format": 1, "sequence": 3, "nouveauChamp": { "x": [1, 2] },
              "tools": [
                { "id": "cpu-z", "version": "3.01", "url": "https://download.cpuid.com/cpu-z/cpu-z_3.01-en.zip",
                  "sha256": "8ae3b45d43d97e6ce19535c045cd1e5394c7a3a5334dc01f63edd760ace40827", "size": 5478310, "notes": "x" },
              ],
            }
            """;

        ToolCatalogDocument document = ParseOk(json);

        Assert.Equal(Sha, document.Releases["cpu-z"].Sha256);
    }

    [Fact]
    public void Un_outil_inconnu_ou_en_page_officielle_est_ignore_sans_bruit()
    {
        ToolCatalogDocument document = ParseOk(Document(
            Entry(id: "outil-de-demain", url: "https://example.com/a.zip"),
            Entry(id: "gpu-z", url: "https://us1-dl.techpowerup.com/files/GPU-Z.exe"),
            Entry()));

        Assert.Single(document.Releases);
        Assert.Empty(document.Rejected);
    }

    [Theory]
    [InlineData("http://download.cpuid.com/cpu-z/cpu-z_3.01-en.zip", "HTTPS")]
    [InlineData("https://download.cpuid.com.evil.example/cpu-z_3.01-en.zip", "source autorisée")]
    [InlineData("https://evil.example/cpu-z_3.01-en.zip", "source autorisée")]
    [InlineData("https://download.cpuid.com:8443/cpu-z/cpu-z_3.01-en.zip", "inhabituelle")]
    [InlineData("https://user:pass@download.cpuid.com/cpu-z/cpu-z_3.01-en.zip", "inhabituelle")]
    [InlineData("pas une adresse", "HTTPS")]
    public void Une_adresse_hors_des_hotes_figes_est_ecartee(string url, string reason)
    {
        ToolCatalogDocument document = ParseOk(Document(Entry(url: url), Entry(id: "crystaldiskmark",
            version: "9.0.3", url: "https://downloads.sourceforge.net/project/crystaldiskmark/9.0.3/CrystalDiskMark9_0_3.zip", size: 3725706)));

        Assert.False(document.Releases.ContainsKey("cpu-z"));
        Assert.True(document.Releases.ContainsKey("crystaldiskmark"));
        Assert.Contains(document.Rejected, r => r.StartsWith("cpu-z") && r.Contains(reason));
    }

    [Theory]
    [InlineData("1234")]
    [InlineData("")]
    [InlineData("ZZE3B45D43D97E6CE19535C045CD1E5394C7A3A5334DC01F63EDD760ACE40827")]
    public void Une_empreinte_invalide_est_ecartee(string sha)
    {
        ToolCatalogDocument document = ParseOk(Document(Entry(sha: sha)));

        Assert.Empty(document.Releases);
        Assert.Contains(document.Rejected, r => r.Contains("SHA-256"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(500L * 1024 * 1024)]
    public void Une_taille_absente_ou_au_dela_de_la_limite_de_l_outil_est_ecartee(long size)
    {
        ToolCatalogDocument document = ParseOk(Document(Entry(size: size)));

        Assert.Empty(document.Releases);
        Assert.Contains(document.Rejected, r => r.Contains("taille"));
    }

    [Theory]
    [InlineData("../3.01")]
    [InlineData("3.01/../x")]
    [InlineData("3..01")]
    [InlineData("3.01 ")]
    [InlineData(".3")]
    [InlineData("3\\\\01")] // « 3\01 » une fois le JSON lu
    [InlineData("")]
    [InlineData("12345678901234567890123456789012345")]
    public void Une_version_qui_ne_ferait_pas_un_nom_de_dossier_sur_est_ecartee(string version)
    {
        ToolCatalogDocument document = ParseOk(Document(Entry(version: version)));

        Assert.Empty(document.Releases);
        Assert.Contains(document.Rejected, r => r.Contains("version"));
    }

    [Fact]
    public void Un_outil_present_deux_fois_ne_garde_que_sa_premiere_entree()
    {
        ToolCatalogDocument document = ParseOk(Document(Entry(), Entry(version: "9.99")));

        Assert.Equal("3.01", document.Releases["cpu-z"].Version);
        Assert.Contains(document.Rejected, r => r.Contains("deux fois"));
    }

    [Fact]
    public void Une_entree_incomplete_est_ecartee_sans_faire_tomber_les_autres()
    {
        ToolCatalogDocument document = ParseOk(Document("""{ "id": "cpu-z" }""", "42", """{ "version": "1" }""",
            Entry(id: "crystaldiskmark", version: "9.0.3",
                url: "https://downloads.sourceforge.net/project/crystaldiskmark/9.0.3/CrystalDiskMark9_0_3.zip", size: 3725706)));

        Assert.True(document.Releases.ContainsKey("crystaldiskmark"));
        Assert.Equal(3, document.Rejected.Count);
    }

    [Fact]
    public void Une_adresse_sans_nom_de_fichier_prend_le_nom_de_repli()
    {
        ToolCatalogDocument document = ParseOk(Document(Entry(id: "occt", version: "17.1.5.0",
            url: "https://www.ocbase.com/download/edition:Personal/version:17.1.5/os:Windows", size: 206724776)));

        Assert.Equal("OCCT.exe", document.Releases["occt"].FileName);
    }

    [Fact]
    public void Un_nom_de_fichier_d_un_autre_type_que_celui_attendu_est_ecarte()
    {
        // CPU-Z attend un zip : un .exe à la même adresse n'est pas ce que l'app sait vérifier.
        ToolCatalogDocument document = ParseOk(Document(Entry(url: "https://download.cpuid.com/cpu-z/cpu-z_3.01-en.exe")));

        Assert.Empty(document.Releases);
        Assert.Contains(document.Rejected, r => r.Contains("nom de fichier"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("pas du json")]
    [InlineData("[1, 2, 3]")]
    [InlineData("""{ "sequence": 3, "tools": [] }""")]
    [InlineData("""{ "format": 1, "tools": [] }""")]
    [InlineData("""{ "format": 1, "sequence": 0, "tools": [] }""")]
    [InlineData("""{ "format": "1", "sequence": 3 }""")]
    public void Un_document_illisible_ou_sans_numero_est_refuse_entier(string json)
    {
        Assert.Null(Parse(json, out string? error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void Un_format_plus_recent_que_l_app_est_refuse()
    {
        Assert.Null(Parse("""{ "format": 2, "sequence": 3, "tools": [] }""", out string? error));
        Assert.Contains("plus récent", error);
    }

    [Fact]
    public void Un_document_anormalement_gros_est_refuse_sans_etre_lu()
    {
        byte[] huge = new byte[ToolCatalogParser.MaxDocumentBytes + 1];

        Assert.Null(ToolCatalogParser.Parse(huge, ToolCatalog.All, out string? error));
        Assert.Contains("gros", error);
    }
}
