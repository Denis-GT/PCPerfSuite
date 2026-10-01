using System.Reflection;
using PCPerfSuite.Core.SystemInfo;

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

    /// <summary>Le même, octet pour octet.</summary>
    Same,

    /// <summary>Numéro plus petit : refusé (protection contre un retour à une version plus ancienne).</summary>
    Older,

    /// <summary>Même numéro, contenu différent : refusé, deux publications ne partagent jamais un numéro.</summary>
    Conflicting,
}

/// <summary>
/// Le catalogue d'outils : copie embarquée, copie en cache, catalogue en ligne signé.
///
/// Confiance : le catalogue en ligne n'est pris que si sa signature vaut pour la clé publique embarquée
/// (<see cref="ToolCatalogTrust.PublicKey"/>) ET si son numéro dépasse celui déjà connu (embarqué ou en cache) : un
/// serveur qui servirait un ancien catalogue signé, aux adresses peut-être vulnérables, est refusé. Le cache, écrit
/// dans le dossier de données (<see cref="AppDataPaths"/>), est revérifié à chaque lecture comme s'il venait du réseau.
/// Même accepté, un catalogue ne peut rien changer d'autre que versions, adresses, empreintes et tailles : hôtes et
/// éditeurs restent ceux figés dans <see cref="ToolCatalog"/>.
/// </summary>
public sealed class ToolCatalogStore
{
    private const string EmbeddedResourceSuffix = "catalogue-outils.json";
    private const int MaxSignatureBytes = 1024;
    private static readonly TimeSpan OnlineTimeout = TimeSpan.FromSeconds(15);

    private readonly IReadOnlyList<ToolDefinition> _definitions;
    private readonly string? _publicKey;
    private readonly object _gate = new();
    private ToolCatalogStatus _status;

    public ToolCatalogStore(IReadOnlyList<ToolDefinition> definitions)
        : this(definitions, ToolCatalogTrust.PublicKey, LoadEmbeddedBytes())
    {
    }

    /// <summary>Pour les tests : clé et copie embarquée données.</summary>
    internal ToolCatalogStore(IReadOnlyList<ToolDefinition> definitions, string? publicKey, byte[] embedded)
    {
        _definitions = definitions;
        _publicKey = publicKey;
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

    /// <summary>Vrai tant que la clé de signature n'est pas inscrite dans l'app : le catalogue en ligne est alors
    /// ignoré, et seule la copie intégrée sert.</summary>
    public bool IsOnlineEnabled => !string.IsNullOrWhiteSpace(_publicKey);

    /// <summary>Lit la copie en cache, si elle est valide et plus récente que la copie intégrée. Lecture de fichiers
    /// seulement : à appeler hors du thread d'interface. Ne lève jamais.</summary>
    public void LoadCache()
    {
        if (!IsOnlineEnabled) return;

        try
        {
            AppDataPaths paths = AppDataPaths.Current;
            if (!File.Exists(paths.ToolCatalogCacheFile) || !File.Exists(paths.ToolCatalogCacheSignatureFile)) return;

            byte[] json = File.ReadAllBytes(paths.ToolCatalogCacheFile);
            string signature = File.ReadAllText(paths.ToolCatalogCacheSignatureFile);
            Accept(json, signature, ToolCatalogOrigin.Cache, out _);
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
            json = await OfficialInstaller.DownloadBytesAsync(ToolCatalogTrust.CatalogUrl, ToolCatalogTrust.Hosts,
                ToolCatalogParser.MaxDocumentBytes, OnlineTimeout, cancellationToken).ConfigureAwait(false);
            byte[] signatureBytes = await OfficialInstaller.DownloadBytesAsync(ToolCatalogTrust.SignatureUrl, ToolCatalogTrust.Hosts,
                MaxSignatureBytes, OnlineTimeout, cancellationToken).ConfigureAwait(false);
            signature = System.Text.Encoding.ASCII.GetString(signatureBytes);
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

        if (Accept(json, signature, ToolCatalogOrigin.Online, out string? refusal) is not { } comparison)
        {
            return SetOnlineMessage($"catalogue en ligne refusé ({refusal}) : PCPerfSuite garde sa dernière liste connue", suspicious: true);
        }

        switch (comparison)
        {
            case CatalogComparison.Newer:
                SaveCache(json, signature);
                return Status;

            case CatalogComparison.Same:
                lock (_gate) _status = _status with { Origin = ToolCatalogOrigin.Online, OnlineMessage = null, OnlineRefusalIsSuspicious = false };
                return Status;

            case CatalogComparison.Older:
                return SetOnlineMessage(
                    $"catalogue en ligne {refusal} plus ancien que le n° {Status.Document.Sequence} déjà connu : refusé, " +
                    "par protection contre un retour en arrière", suspicious: true);

            default:
                return SetOnlineMessage(
                    $"catalogue en ligne {refusal} différent de celui déjà connu sous ce numéro : refusé", suspicious: true);
        }
    }

    /// <summary>Vérifie un catalogue signé et le prend s'il est plus récent que celui en usage. Null s'il n'est pas digne
    /// de confiance (<paramref name="note"/> dit pourquoi) ; sinon le résultat de la comparaison, et
    /// <paramref name="note"/> son numéro (« n° 12 »).</summary>
    internal CatalogComparison? Accept(byte[] json, string signature, ToolCatalogOrigin origin, out string? note)
    {
        if (Trusted(json, signature, out string? refusal) is not { } document)
        {
            note = refusal;
            return null;
        }

        note = $"n° {document.Sequence}";
        return Offer(document, origin);
    }

    /// <summary>Compare un candidat au catalogue en usage, par numéro de publication, puis octet pour octet.</summary>
    public static CatalogComparison Compare(ToolCatalogDocument current, byte[]? currentBytes, ToolCatalogDocument candidate, byte[]? candidateBytes)
    {
        if (candidate.Sequence > current.Sequence) return CatalogComparison.Newer;
        if (candidate.Sequence < current.Sequence) return CatalogComparison.Older;
        return currentBytes is not null && candidateBytes is not null && currentBytes.AsSpan().SequenceEqual(candidateBytes)
            ? CatalogComparison.Same
            : SameReleases(current, candidate) ? CatalogComparison.Same : CatalogComparison.Conflicting;
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

    private CatalogComparison Offer(ToolCatalogDocument candidate, ToolCatalogOrigin origin)
    {
        lock (_gate)
        {
            CatalogComparison comparison = Compare(_status.Document, null, candidate, null);
            if (comparison == CatalogComparison.Newer) _status = new ToolCatalogStatus(candidate, origin, null);
            return comparison;
        }
    }

    private ToolCatalogStatus SetOnlineMessage(string message, bool suspicious)
    {
        lock (_gate)
        {
            _status = _status with { OnlineMessage = message, OnlineRefusalIsSuspicious = suspicious };
            return _status;
        }
    }

    /// <summary>Mêmes versions, adresses et empreintes pour chaque outil : deux copies d'un même catalogue relues par
    /// des chemins différents (intégrée, cache) se comparent ainsi sans garder leurs octets.</summary>
    private static bool SameReleases(ToolCatalogDocument a, ToolCatalogDocument b)
        => a.Releases.Count == b.Releases.Count
           && a.Releases.All(pair => b.Releases.TryGetValue(pair.Key, out ToolRelease? other) && other == pair.Value);

    /// <summary>Écriture atomique du catalogue et de sa signature (fichier temporaire puis remplacement).</summary>
    private static void SaveCache(byte[] json, string signature)
    {
        try
        {
            AppDataPaths paths = AppDataPaths.Current;
            Directory.CreateDirectory(Path.GetDirectoryName(paths.ToolCatalogCacheFile)!);
            WriteAtomically(paths.ToolCatalogCacheSignatureFile, System.Text.Encoding.ASCII.GetBytes(signature));
            WriteAtomically(paths.ToolCatalogCacheFile, json);
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

    public static readonly Uri CatalogUrl = new("https://raw.githubusercontent.com/Denis-GT/PCPerfSuite-catalogue/main/catalogue-outils.json");
    public static readonly Uri SignatureUrl = new("https://raw.githubusercontent.com/Denis-GT/PCPerfSuite-catalogue/main/catalogue-outils.json.sig");
    public static readonly IReadOnlyList<string> Hosts = new[] { "raw.githubusercontent.com" };
}
