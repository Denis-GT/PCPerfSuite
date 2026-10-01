using System.Security.Cryptography;
using System.Text;
using PCPerfSuite.Core.Installations;

namespace PCPerfSuite.Core.Tests;

/// <summary>
/// Confiance dans le catalogue en ligne : signature ECDSA P-256 sur un contexte et les octets exacts, puis numéro de
/// publication croissant (un ancien catalogue signé, servi de nouveau, est refusé). Clés générées par les tests,
/// sans réseau ni disque.
/// </summary>
public sealed class ToolCatalogTrustTests : IDisposable
{
    /// <summary>Un dossier par test (xUnit crée une instance par test) : le plancher n'est jamais celui de l'app.</summary>
    private readonly TempDirectory _floor = new();

    public void Dispose() => _floor.Dispose();

    private ToolCatalogStore Store(ECDsa key, byte[] embedded)
        => new(ToolCatalog.All, PublicKeyOf(key), embedded, floorFile: _floor.File("plancher"));

    private const string Context = ToolCatalogTrust.SignatureContext;

    private static readonly byte[] Content = Encoding.UTF8.GetBytes("""{ "format": 1, "sequence": 2, "tools": [] }""");

    private static string PublicKeyOf(ECDsa key) => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());

    private static byte[] Catalog(long sequence, string version = "3.01")
        => Encoding.UTF8.GetBytes($$"""
            { "format": 1, "sequence": {{sequence}}, "tools": [
              { "id": "cpu-z", "version": "{{version}}", "url": "https://download.cpuid.com/cpu-z/cpu-z_{{version}}-en.zip",
                "sha256": "8AE3B45D43D97E6CE19535C045CD1E5394C7A3A5334DC01F63EDD760ACE40827", "size": 5478310 } ] }
            """);

    [Fact]
    public void Une_signature_valide_est_acceptee()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string signature = CatalogSignature.Sign(Content, Context, key);

        Assert.True(CatalogSignature.Verify(Content, signature, Context, PublicKeyOf(key)));
    }

    [Fact]
    public void Un_octet_change_invalide_la_signature()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string signature = CatalogSignature.Sign(Content, Context, key);
        byte[] tampered = (byte[])Content.Clone();
        tampered[^3] ^= 1;

        Assert.False(CatalogSignature.Verify(tampered, signature, Context, PublicKeyOf(key)));
    }

    [Fact]
    public void Une_signature_faite_pour_un_autre_fichier_signe_ne_vaut_pas_ici()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string signature = CatalogSignature.Sign(Content, "PCPerfSuite/scores-reference/v1", key);

        Assert.False(CatalogSignature.Verify(Content, signature, Context, PublicKeyOf(key)));
    }

    [Fact]
    public void Une_autre_cle_ne_vaut_pas()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using ECDsa other = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        Assert.False(CatalogSignature.Verify(Content, CatalogSignature.Sign(Content, Context, other), Context, PublicKeyOf(key)));
    }

    [Fact]
    public void Une_cle_qui_n_est_pas_P256_est_refusee()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        string signature = CatalogSignature.Sign(Content, Context, key);

        Assert.False(CatalogSignature.Verify(Content, signature, Context, PublicKeyOf(key)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("pas du base64 !")]
    [InlineData("AAAA")]
    public void Une_signature_mal_formee_vaut_non_sans_lever(string? signature)
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        Assert.False(CatalogSignature.Verify(Content, signature, Context, PublicKeyOf(key)));
        Assert.False(CatalogSignature.Verify(Content, CatalogSignature.Sign(Content, Context, key), Context, "pas une clé"));
        Assert.False(CatalogSignature.Verify(Content, CatalogSignature.Sign(Content, Context, key), Context, null));
    }

    [Fact]
    public void Une_signature_au_format_DER_est_refusee()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] signed = Encoding.UTF8.GetBytes(Context + "\n").Concat(Content).ToArray();
        string der = Convert.ToBase64String(key.SignData(signed, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));

        Assert.False(CatalogSignature.Verify(Content, der, Context, PublicKeyOf(key)));
    }

    [Fact]
    public void Un_catalogue_signe_et_plus_recent_remplace_la_copie_integree()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var store = Store(key, Catalog(5));
        byte[] newer = Catalog(6, "3.02");

        CatalogComparison? result = store.Accept(newer, CatalogSignature.Sign(newer, Context, key), ToolCatalogOrigin.Online, out _);

        Assert.Equal(CatalogComparison.Newer, result);
        Assert.Equal(6, store.Status.Document.Sequence);
        Assert.Equal(ToolCatalogOrigin.Online, store.Status.Origin);
        Assert.Equal("3.02", store.ReleaseOf("cpu-z")!.Version);
    }

    [Fact]
    public void Un_catalogue_signe_mais_plus_ancien_est_refuse()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var store = Store(key, Catalog(5));
        byte[] older = Catalog(4, "2.99");

        CatalogComparison? result = store.Accept(older, CatalogSignature.Sign(older, Context, key), ToolCatalogOrigin.Online, out _);

        Assert.Equal(CatalogComparison.Older, result);
        Assert.Equal(5, store.Status.Document.Sequence);
        Assert.Equal("3.01", store.ReleaseOf("cpu-z")!.Version);
    }

    [Fact]
    public void Le_meme_numero_avec_un_autre_contenu_est_refuse()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var store = Store(key, Catalog(5));
        byte[] same = Catalog(5);
        byte[] conflicting = Catalog(5, "3.99");

        Assert.Equal(CatalogComparison.Same, store.Accept(same, CatalogSignature.Sign(same, Context, key), ToolCatalogOrigin.Online, out _));
        Assert.Equal(CatalogComparison.Conflicting,
            store.Accept(conflicting, CatalogSignature.Sign(conflicting, Context, key), ToolCatalogOrigin.Online, out _));
        Assert.Equal("3.01", store.ReleaseOf("cpu-z")!.Version);
    }

    [Fact]
    public void Un_catalogue_mal_signe_n_est_pas_lu()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using ECDsa attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var store = Store(key, Catalog(5));
        byte[] forged = Catalog(99, "6.66");

        CatalogComparison? result = store.Accept(forged, CatalogSignature.Sign(forged, Context, attacker), ToolCatalogOrigin.Online, out string? why);

        Assert.Null(result);
        Assert.Contains("signature", why);
        Assert.Equal(5, store.Status.Document.Sequence);
    }

    [Fact]
    public async Task Sans_cle_inscrite_le_catalogue_en_ligne_n_est_pas_demande()
    {
        var store = new ToolCatalogStore(ToolCatalog.All, publicKey: null, Catalog(5));

        ToolCatalogStatus status = await store.RefreshOnlineAsync(CancellationToken.None);

        Assert.False(store.IsOnlineEnabled);
        Assert.Equal(ToolCatalogOrigin.Embedded, status.Origin);
        Assert.Contains("pas encore activé", status.OnlineMessage);
        Assert.False(status.OnlineRefusalIsSuspicious);
    }

    [Fact]
    public void Une_copie_integree_illisible_laisse_la_place_a_tout_catalogue_signe()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var store = Store(key, Encoding.UTF8.GetBytes("abîmé"));
        byte[] online = Catalog(1);

        Assert.Equal(0, store.Status.Document.Sequence);
        Assert.NotEmpty(store.Status.Document.Rejected);
        Assert.Equal(CatalogComparison.Newer, store.Accept(online, CatalogSignature.Sign(online, Context, key), ToolCatalogOrigin.Online, out _));
    }

    // Cache et plancher sur disque, dans un dossier de test : jamais ceux de l'app.

    private sealed class Disk : IDisposable
    {
        private readonly TempDirectory _temp = new();

        public Disk()
        {
            Paths = new PCPerfSuite.Core.SystemInfo.AppDataPaths(Path.Combine(_temp.Root, "donnees"));
            Directory.CreateDirectory(Paths.Root);
            FloorFile = Path.Combine(_temp.Root, "plancher");
        }

        public PCPerfSuite.Core.SystemInfo.AppDataPaths Paths { get; }
        public string FloorFile { get; }

        public void WriteCache(byte[] json, string signature)
        {
            File.WriteAllBytes(Paths.ToolCatalogCacheFile, json);
            File.WriteAllText(Paths.ToolCatalogCacheSignatureFile, signature);
        }

        public void Dispose() => _temp.Dispose();
    }

    [Fact]
    public void Un_cache_signe_et_plus_recent_que_la_copie_integree_sert_au_lancement()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var disk = new Disk();
        byte[] cached = Catalog(8, "3.02");
        disk.WriteCache(cached, CatalogSignature.Sign(cached, Context, key));
        var store = new ToolCatalogStore(ToolCatalog.All, PublicKeyOf(key), Catalog(5), disk.Paths, disk.FloorFile);

        store.LoadCache();

        Assert.Equal(ToolCatalogOrigin.Cache, store.Status.Origin);
        Assert.Equal(8, store.Status.Document.Sequence);
        Assert.Equal("8", File.ReadAllText(disk.FloorFile));
    }

    [Fact]
    public void Un_cache_mal_signe_ou_incoherent_est_ignore()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var disk = new Disk();
        byte[] cached = Catalog(8, "3.02");
        disk.WriteCache(cached, CatalogSignature.Sign(Catalog(9), Context, key));
        var store = new ToolCatalogStore(ToolCatalog.All, PublicKeyOf(key), Catalog(5), disk.Paths, disk.FloorFile);

        store.LoadCache();

        Assert.Equal(ToolCatalogOrigin.Embedded, store.Status.Origin);
        Assert.Equal(5, store.Status.Document.Sequence);
    }

    [Fact]
    public void Un_ancien_catalogue_signe_remis_dans_le_cache_ne_passe_pas_le_plancher()
    {
        // Le cache est dans le dossier de l'utilisateur : un programme non élevé peut y remettre un ancien catalogue
        // signé. Le plancher, réservé aux administrateurs, se souvient du n° 12 déjà accepté.
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var disk = new Disk();
        File.WriteAllText(disk.FloorFile, "12");
        byte[] old = Catalog(8, "2.99");
        disk.WriteCache(old, CatalogSignature.Sign(old, Context, key));
        var store = new ToolCatalogStore(ToolCatalog.All, PublicKeyOf(key), Catalog(5), disk.Paths, disk.FloorFile);

        store.LoadCache();

        Assert.Equal(5, store.Status.Document.Sequence);
        Assert.True(store.Status.OnlineRefusalIsSuspicious);
        Assert.Contains("plus ancienne", store.Status.OnlineMessage);
    }

    private const string Revision = "0123456789abcdef0123456789abcdef01234567";

    private static FakeHttp Server(byte[] json, string signature, bool resolveRevision = true)
        => new(uri =>
        {
            if (uri.Host == "api.github.com")
            {
                return resolveRevision
                    ? FakeHttp.Ok(Encoding.UTF8.GetBytes($$"""{ "ref": "refs/heads/main", "object": { "sha": "{{Revision}}", "type": "commit" } }"""))
                    : FakeHttp.Status(System.Net.HttpStatusCode.Forbidden);
            }

            return uri.AbsolutePath.EndsWith(".sig", StringComparison.Ordinal)
                ? FakeHttp.Ok(Encoding.ASCII.GetBytes(signature))
                : FakeHttp.Ok(json);
        });

    [Fact]
    public async Task Le_catalogue_en_ligne_et_sa_signature_se_lisent_a_la_meme_revision()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var disk = new Disk();
        byte[] online = Catalog(9, "3.03");
        FakeHttp server = Server(online, CatalogSignature.Sign(online, Context, key));
        var store = new ToolCatalogStore(ToolCatalog.All, PublicKeyOf(key), Catalog(5), disk.Paths, disk.FloorFile, server.Client());

        ToolCatalogStatus status = await store.RefreshOnlineAsync(CancellationToken.None);

        Assert.Equal(ToolCatalogOrigin.Online, status.Origin);
        Assert.Equal(9, status.Document.Sequence);
        Assert.All(server.Requests.Where(u => u.Host == "raw.githubusercontent.com"), u => Assert.Contains($"/{Revision}/", u.AbsolutePath));
        Assert.True(File.Exists(disk.Paths.ToolCatalogCacheFile));
        Assert.Equal("9", File.ReadAllText(disk.FloorFile));
    }

    [Fact]
    public async Task Sans_revision_lisible_la_branche_sert_encore()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var disk = new Disk();
        byte[] online = Catalog(9);
        FakeHttp server = Server(online, CatalogSignature.Sign(online, Context, key), resolveRevision: false);
        var store = new ToolCatalogStore(ToolCatalog.All, PublicKeyOf(key), Catalog(5), disk.Paths, disk.FloorFile, server.Client());

        ToolCatalogStatus status = await store.RefreshOnlineAsync(CancellationToken.None);

        Assert.Equal(9, status.Document.Sequence);
        Assert.Contains(server.Requests, u => u.AbsolutePath.Contains("/main/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Un_catalogue_en_ligne_mal_signe_est_refuse_et_signale()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using ECDsa attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var disk = new Disk();
        byte[] forged = Catalog(50, "6.66");
        var store = new ToolCatalogStore(ToolCatalog.All, PublicKeyOf(key), Catalog(5), disk.Paths, disk.FloorFile,
            Server(forged, CatalogSignature.Sign(forged, Context, attacker)).Client());

        ToolCatalogStatus status = await store.RefreshOnlineAsync(CancellationToken.None);

        Assert.Equal(5, status.Document.Sequence);
        Assert.True(status.OnlineRefusalIsSuspicious);
        Assert.False(File.Exists(disk.Paths.ToolCatalogCacheFile));
        Assert.False(File.Exists(disk.FloorFile));
    }

    [Fact]
    public async Task Un_catalogue_en_ligne_injoignable_n_est_pas_un_probleme_de_securite()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var disk = new Disk();
        var server = new FakeHttp(_ => FakeHttp.Status(System.Net.HttpStatusCode.NotFound));
        var store = new ToolCatalogStore(ToolCatalog.All, PublicKeyOf(key), Catalog(5), disk.Paths, disk.FloorFile, server.Client());

        ToolCatalogStatus status = await store.RefreshOnlineAsync(CancellationToken.None);

        Assert.Equal(ToolCatalogOrigin.Embedded, status.Origin);
        Assert.False(status.OnlineRefusalIsSuspicious);
        Assert.Contains("injoignable", status.OnlineMessage);
    }

    [Fact]
    public void La_copie_livree_avec_l_app_est_lisible()
    {
        var store = new ToolCatalogStore(ToolCatalog.All);

        Assert.True(store.Status.Document.Sequence >= 1);
        Assert.Equal(ToolCatalogOrigin.Embedded, store.Status.Origin);
        Assert.NotNull(store.ReleaseOf("pawnio"));
    }
}
