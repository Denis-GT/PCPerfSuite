using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace PCPerfSuite.Core.Installations;

/// <summary>D'où vient le catalogue en usage.</summary>
public enum ToolCatalogOrigin
{
    /// <summary>Copie livrée avec l'app : toujours là, même hors ligne.</summary>
    Embedded,

    /// <summary>Dernier catalogue en ligne accepté, gardé sur le disque et revérifié à chaque lecture.</summary>
    Cache,

    /// <summary>Catalogue en ligne lu à l'instant.</summary>
    Online,
}

/// <summary>Catalogue en usage, son origine, et pourquoi le catalogue en ligne n'a pas été pris (null s'il l'a été ou
/// s'il n'a pas encore été demandé). <paramref name="OnlineRefusalIsSuspicious"/> : la raison n'est pas un simple
/// réseau coupé, mais une signature fausse ou un retour en arrière, qui méritent d'être signalés.</summary>
public sealed record ToolCatalogStatus(
    ToolCatalogDocument Document,
    ToolCatalogOrigin Origin,
    string? OnlineMessage,
    bool OnlineRefusalIsSuspicious = false);

/// <summary>Ce que donne la comparaison d'un catalogue candidat avec celui en usage.</summary>
public enum CatalogComparison
{
    /// <summary>Plus récent : il remplace celui en usage.</summary>
    Newer,

    /// <summary>Le même (mêmes versions, adresses et empreintes).</summary>
    Same,

    /// <summary>Numéro plus petit que celui déjà accepté : refusé (protection contre un retour en arrière).</summary>
    Older,

    /// <summary>Même numéro, contenu différent : refusé, deux publications ne partagent jamais un numéro.</summary>
    Conflicting,
}

/// <summary>
/// Le catalogue d'outils : copie embarquée, copie en cache, catalogue en ligne signé.
///
/// Confiance : le catalogue en ligne n'est pris que si sa signature vaut pour la clé publique embarquée
/// (<see cref="ToolCatalogTrust.PublicKey"/>) ET si son numéro dépasse celui déjà connu. Le dernier catalogue accepté
/// (avec sa signature) et le plus haut numéro accepté sont gardés dans %ProgramData%\PCPerfSuite, que seuls les
/// administrateurs peuvent modifier, et partagés par tous les comptes du PC : un programme non élevé ne peut ni y
/// remettre un ancien catalogue signé, aux adresses peut-être vulnérables, ni effacer la mémoire du dernier accepté. Le
/// cache est revérifié à chaque lecture comme s'il venait du réseau.
///
/// Le catalogue et sa signature sont lus à la même révision du dépôt (SHA du commit de main) : le CDN de GitHub met
/// chaque fichier en cache séparément, et lire « main » deux fois pourrait apparier un catalogue neuf et une ancienne
/// signature. Même accepté, un catalogue ne peut rien changer d'autre que versions, adresses, empreintes et tailles :
/// hôtes et éditeurs restent ceux figés dans <see cref="ToolCatalog"/>.
/// </summary>
public sealed class ToolCatalogStore
{
    private const string EmbeddedResourceSuffix = "catalogue-outils.json";
    private const int MaxSignatureBytes = 1024;
    private const int MaxRefBytes = 4096;
    private static readonly TimeSpan OnlineTimeout = TimeSpan.FromSeconds(15);

    private readonly IReadOnlyList<ToolDefinition> _definitions;
    private readonly string? _publicKey;
    private readonly string? _cacheFolder;
    private readonly HttpClient? _client;
    private readonly object _gate = new();
    private ToolCatalogStatus _status;
    private long _floor = -1;

    public ToolCatalogStore(IReadOnlyList<ToolDefinition> definitions)
        : this(definitions, ToolCatalogTrust.PublicKey, LoadEmbeddedBytes())
    {
    }

    /// <summary>Pour les tests : clé, copie embarquée, dossier du cache et du plancher, client HTTP donnés. Null : ceux de
    /// l'app (%ProgramData%\PCPerfSuite, le client d'OfficialInstaller).</summary>
    internal ToolCatalogStore(IReadOnlyList<ToolDefinition> definitions, string? publicKey, byte[] embedded,
        string? cacheFolder = null, HttpClient? client = null)
    {
        _definitions = definitions;
        _publicKey = publicKey;
        _cacheFolder = cacheFolder;
        _client = client;
        ToolCatalogDocument document = ToolCatalogParser.Parse(embedded, definitions, out string? error)
            ?? new ToolCatalogDocument(ToolCatalogParser.SupportedFormat, 0, null, new Dictionary<string, ToolRelease>(),
                new[] { $"copie intégrée illisible ({error})" });
        _status = new ToolCatalogStatus(document, ToolCatalogOrigin.Embedded, null);
    }

    /// <summary>Instantané immuable du catalogue en usage : lisible depuis n'importe quel thread.</summary>
    public ToolCatalogStatus Status
    {
        get { lock (_gate) return _status; }
    }

    public ToolRelease? ReleaseOf(string toolId)
        => Status.Document.Releases.TryGetValue(toolId, out ToolRelease? release) ? release : null;

    /// <summary>Vrai quand la clé de signature est inscrite dans l'app (<see cref="ToolCatalogTrust.PublicKey"/>). Faux :
    /// le catalogue en ligne est ignoré, et seule la copie intégrée sert.</summary>
    public bool IsOnlineEnabled => !string.IsNullOrWhiteSpace(_publicKey);

    /// <summary>Lit la copie en cache, si elle est signée, plus récente que la copie intégrée et pas plus ancienne que le
    /// plus haut numéro déjà accepté. Lecture de fichiers seulement : à appeler hors du thread d'interface. Ne lève jamais.</summary>
    public void LoadCache()
    {
        if (!IsOnlineEnabled) return;

        try
        {
            if (CacheFolder(create: false) is not { } folder) return;
            string jsonFile = Path.Combine(folder, ProgramDataFolder.CatalogCacheFileName);
            string signatureFile = Path.Combine(folder, ProgramDataFolder.CatalogCacheSignatureFileName);
            if (!File.Exists(jsonFile) || !File.Exists(signatureFile)) return;

            // Plus ancien que la copie intégrée (l'app a été mise à jour depuis) : rien d'anormal, la copie intégrée sert.
            // Plus ancien que le plancher : le cache a été remplacé, ce qui mérite d'être dit.
            CatalogComparison? comparison = Accept(File.ReadAllBytes(jsonFile), File.ReadAllText(signatureFile), ToolCatalogOrigin.Cache,
                out string? note, out long sequence);
            if (comparison == CatalogComparison.Older && sequence < Floor())
            {
                SetOnlineMessage($"copie du catalogue gardée sur ce PC ({note}) plus ancienne que le n° {Floor()} déjà accepté : ignorée",
                    suspicious: true);
            }
        }
        catch
        {
            // Cache illisible : la copie intégrée reste en usage, et le catalogue en ligne le remplacera.
        }
    }

    /// <summary>Lit le catalogue en ligne et le prend s'il est signé et plus récent ; sinon, garde celui en usage et
    /// dit pourquoi dans <see cref="ToolCatalogStatus.OnlineMessage"/>. Ne lève jamais.</summary>
    public async Task<ToolCatalogStatus> RefreshOnlineAsync(CancellationToken cancellationToken)
    {
        if (!IsOnlineEnabled)
        {
            return SetOnlineMessage("catalogue en ligne pas encore activé (clé de signature à inscrire dans l'app) : " +
                                    "PCPerfSuite utilise la liste intégrée", suspicious: false);
        }

        byte[] json;
        string signature;
        try
        {
            // Une révision introuvable (quota de l'API de GitHub atteint) n'empêche rien : la branche sert alors, au
            // risque d'un décalage passager entre les deux fichiers, que la vérification de signature rattrape.
            string revision = await ResolveRevisionAsync(cancellationToken).ConfigureAwait(false) ?? ToolCatalogTrust.Branch;
            json = await OfficialInstaller.DownloadBytesAsync(ToolCatalogTrust.FileUrl(revision, ToolCatalogTrust.CatalogFileName),
                ToolCatalogTrust.Hosts, ToolCatalogParser.MaxDocumentBytes, OnlineTimeout, cancellationToken, _client).ConfigureAwait(false);
            byte[] signatureBytes = await OfficialInstaller.DownloadBytesAsync(ToolCatalogTrust.FileUrl(revision, ToolCatalogTrust.SignatureFileName),
                ToolCatalogTrust.Hosts, MaxSignatureBytes, OnlineTimeout, cancellationToken, _client).ConfigureAwait(false);
            signature = Encoding.ASCII.GetString(signatureBytes);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Status;
        }
        catch (Exception ex)
        {
            string reason = ex is HttpRequestException ? "réseau indisponible" : ex.Message.TrimEnd('.');
            return SetOnlineMessage($"catalogue en ligne injoignable ({reason}) : PCPerfSuite utilise sa dernière liste connue", suspicious: false);
        }

        switch (Accept(json, signature, ToolCatalogOrigin.Online, out string? note, out _))
        {
            case null:
                return SetOnlineMessage($"catalogue en ligne refusé ({note}) : PCPerfSuite garde sa dernière liste connue", suspicious: true);

            case CatalogComparison.Newer:
                SaveCache(json, signature);
                return Status;

            case CatalogComparison.Same:
                lock (_gate) _status = _status with { Origin = ToolCatalogOrigin.Online, OnlineMessage = null, OnlineRefusalIsSuspicious = false };
                return Status;

            case CatalogComparison.Older:
                return SetOnlineMessage(
                    $"catalogue en ligne {note} plus ancien que le n° {Math.Max(Status.Document.Sequence, Floor())} déjà accepté : refusé, " +
                    "par protection contre un retour en arrière", suspicious: true);

            default:
                return SetOnlineMessage(
                    $"catalogue en ligne {note} différent de celui déjà connu sous ce numéro : refusé", suspicious: true);
        }
    }

    /// <summary>Vérifie un catalogue signé et le prend s'il est plus récent que celui en usage et pas plus ancien que le
    /// plancher. Null s'il n'est pas digne de confiance (<paramref name="note"/> dit pourquoi) ; sinon le résultat de la
    /// comparaison, et <paramref name="note"/> son numéro (« n° 12 »).</summary>
    internal CatalogComparison? Accept(byte[] json, string signature, ToolCatalogOrigin origin, out string? note, out long sequence)
    {
        sequence = 0;
        if (Trusted(json, signature, out string? refusal) is not { } document)
        {
            note = refusal;
            return null;
        }

        sequence = document.Sequence;
        note = $"n° {document.Sequence}";
        long floor = Floor();
        CatalogComparison comparison;
        lock (_gate)
        {
            comparison = document.Sequence < floor ? CatalogComparison.Older : Compare(_status.Document, document);
            if (comparison == CatalogComparison.Newer) _status = new ToolCatalogStatus(document, origin, null);
        }

        if (comparison == CatalogComparison.Newer && document.Sequence > floor) WriteFloor(document.Sequence);
        return comparison;
    }

    /// <summary>Compare un candidat au catalogue en usage, par numéro de publication, puis par contenu.</summary>
    public static CatalogComparison Compare(ToolCatalogDocument current, ToolCatalogDocument candidate)
    {
        if (candidate.Sequence > current.Sequence) return CatalogComparison.Newer;
        if (candidate.Sequence < current.Sequence) return CatalogComparison.Older;
        return SameReleases(current, candidate) ? CatalogComparison.Same : CatalogComparison.Conflicting;
    }

    /// <summary>Signature valable et document lisible, ou null avec la raison.</summary>
    private ToolCatalogDocument? Trusted(byte[] json, string signature, out string? refusal)
    {
        if (!CatalogSignature.Verify(json, signature, ToolCatalogTrust.SignatureContext, _publicKey))
        {
            refusal = "signature invalide";
            return null;
        }

        ToolCatalogDocument? document = ToolCatalogParser.Parse(json, _definitions, out string? error);
        refusal = error;
        return document;
    }

    /// <summary>SHA du dernier commit de la branche, null s'il ne peut pas être lu.</summary>
    private async Task<string?> ResolveRevisionAsync(CancellationToken cancellationToken)
    {
        try
        {
            byte[] body = await OfficialInstaller.DownloadBytesAsync(ToolCatalogTrust.BranchRefUrl, ToolCatalogTrust.Hosts, MaxRefBytes,
                OnlineTimeout, cancellationToken, _client).ConfigureAwait(false);
            using JsonDocument reference = JsonDocument.Parse(body);
            string? sha = reference.RootElement.GetProperty("object").GetProperty("sha").GetString();
            return sha is { Length: 40 } && sha.All(Uri.IsHexDigit) ? sha : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Plus haut numéro déjà accepté (0 si aucun), lu une fois.</summary>
    private long Floor()
    {
        lock (_gate)
        {
            if (_floor >= 0) return _floor;
        }

        long floor = 0;
        try
        {
            string? path = FloorPath(create: false);
            if (path is not null && File.Exists(path)
                && long.TryParse(File.ReadAllText(path).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long read))
            {
                floor = read;
            }
        }
        catch
        {
            // Plancher illisible : la copie intégrée reste le plancher, comme avant le premier catalogue en ligne.
        }

        lock (_gate)
        {
            if (_floor < 0) _floor = floor;
            return _floor;
        }
    }

    private void WriteFloor(long sequence)
    {
        lock (_gate) _floor = Math.Max(_floor, sequence);

        try
        {
            if (FloorPath(create: true) is { } path) WriteAtomically(path, Encoding.ASCII.GetBytes(sequence.ToString(CultureInfo.InvariantCulture)));
        }
        catch
        {
            // best-effort : le plancher reste en mémoire pour la session, le cache et la copie intégrée après.
        }
    }

    /// <summary>Fichier du plancher, dans le dossier du cache.</summary>
    private string? FloorPath(bool create)
        => CacheFolder(create) is { } folder ? Path.Combine(folder, ProgramDataFolder.CatalogFloorFileName) : null;

    /// <summary>Dossier du cache et du plancher : celui des tests, ou %ProgramData%\PCPerfSuite (vérifié, créé si besoin).</summary>
    private string? CacheFolder(bool create)
        => _cacheFolder ?? (create ? ProgramDataFolder.TryEnsure().Path : ProgramDataFolder.TryGetExisting());

    private ToolCatalogStatus SetOnlineMessage(string message, bool suspicious)
    {
        lock (_gate)
        {
            _status = _status with { OnlineMessage = message, OnlineRefusalIsSuspicious = suspicious };
            return _status;
        }
    }

    /// <summary>Mêmes versions, adresses et empreintes pour chaque outil.</summary>
    private static bool SameReleases(ToolCatalogDocument a, ToolCatalogDocument b)
        => a.Releases.Count == b.Releases.Count
           && a.Releases.All(pair => b.Releases.TryGetValue(pair.Key, out ToolRelease? other) && other == pair.Value);

    /// <summary>Écriture atomique du catalogue et de sa signature. Une coupure entre les deux laisse une paire qui ne se
    /// vérifie pas : le cache est alors ignoré, jamais pris à tort.</summary>
    private void SaveCache(byte[] json, string signature)
    {
        try
        {
            if (CacheFolder(create: true) is not { } folder) return;
            WriteAtomically(Path.Combine(folder, ProgramDataFolder.CatalogCacheSignatureFileName), Encoding.ASCII.GetBytes(signature));
            WriteAtomically(Path.Combine(folder, ProgramDataFolder.CatalogCacheFileName), json);
        }
        catch
        {
            // best-effort : sans cache, le catalogue en ligne sera relu à la prochaine ouverture.
        }
    }

    private static void WriteAtomically(string path, byte[] content)
    {
        string temp = path + ".tmp";
        File.WriteAllBytes(temp, content);
        File.Move(temp, path, overwrite: true);
    }

    private static byte[] LoadEmbeddedBytes()
    {
        try
        {
            Assembly assembly = typeof(ToolCatalogStore).Assembly;
            string? name = assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(EmbeddedResourceSuffix, StringComparison.Ordinal));
            if (name is null) return Array.Empty<byte>();

            using Stream stream = assembly.GetManifestResourceStream(name)!;
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }
        catch
        {
            return Array.Empty<byte>();
        }
    }

    /// <summary>Octets de la copie intégrée, pour les tests.</summary>
    internal static byte[] EmbeddedBytes() => LoadEmbeddedBytes();
}

/// <summary>Où lire le catalogue en ligne, et avec quelle clé le vérifier (décision D8 : dépôt GitHub public dédié,
/// signé par Denis avec une clé hors ligne).</summary>
public static class ToolCatalogTrust
{
    /// <summary>Contexte signé avec le catalogue (voir <see cref="CatalogSignature"/>).</summary>
    public const string SignatureContext = "PCPerfSuite/catalogue-outils/v1";

    /// <summary>Clé publique ECDSA P-256 (SubjectPublicKeyInfo DER, en base64), sortie par
    /// « dotnet run tools/catalogue/depot/outils/signer.cs -- nouvelle-cle ». Null tant que Denis ne l'a pas créée : le
    /// catalogue en ligne est alors ignoré, et seule la copie intégrée sert.</summary>
    public static readonly string? PublicKey = null;

    public const string Repository = "Denis-GT/PCPerfSuite-catalogue";
    public const string Branch = "main";
    public const string CatalogFileName = "catalogue-outils.json";
    public const string SignatureFileName = "catalogue-outils.json.sig";

    /// <summary>Référence de la branche, pour lire les deux fichiers à la même révision.</summary>
    public static readonly Uri BranchRefUrl = new($"https://api.github.com/repos/{Repository}/git/ref/heads/{Branch}");

    public static readonly IReadOnlyList<string> Hosts = new[] { "raw.githubusercontent.com", "api.github.com" };

    /// <summary>Adresse d'un fichier du dépôt à une révision (SHA de commit ou nom de branche).</summary>
    public static Uri FileUrl(string revision, string fileName) => new($"https://raw.githubusercontent.com/{Repository}/{revision}/{fileName}");
}
