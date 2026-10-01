using System.Globalization;
using System.Text.Json;

namespace PCPerfSuite.Core.Installations;

/// <summary>Une version publiée d'un outil, validée contre sa définition figée : adresse HTTPS sur un hôte autorisé,
/// empreinte, taille dans les limites, version utilisable comme nom de dossier. <paramref name="FileName"/> est le nom
/// du fichier téléchargé (tiré de l'adresse, ou le nom de repli de la définition).</summary>
public sealed record ToolRelease(string ToolId, string Version, Uri Url, string Sha256, long Size, string FileName);

/// <summary>Un catalogue lu : son numéro (croissant à chaque publication, pour refuser un retour en arrière), sa date,
/// les versions acceptées par outil, et ce qui a été écarté, avec la raison.</summary>
public sealed record ToolCatalogDocument(
    int Format,
    long Sequence,
    DateTimeOffset? GeneratedUtc,
    IReadOnlyDictionary<string, ToolRelease> Releases,
    IReadOnlyList<string> Rejected);

/// <summary>
/// Lecture du catalogue JSON (format 1) :
/// <code>
/// { "format": 1, "sequence": 12, "generatedUtc": "2026-10-01T06:00:00Z",
///   "tools": [ { "id": "cpu-z", "version": "3.01", "url": "https://…", "sha256": "…", "size": 5478310 } ] }
/// </code>
/// Tolérante : un champ inconnu est ignoré, un outil inconnu de cette version de l'app aussi (un catalogue plus récent
/// peut en ajouter), et une entrée invalide est écartée seule, avec sa raison, sans faire tomber les autres. Le
/// document entier n'est refusé que s'il est illisible, d'un format plus récent, ou sans numéro.
/// </summary>
public static class ToolCatalogParser
{
    public const int SupportedFormat = 1;

    /// <summary>Un catalogue fait quelques Ko : au-delà, ce n'est pas lui.</summary>
    public const int MaxDocumentBytes = 256 * 1024;

    private const int MaxVersionLength = 32;

    private static readonly JsonDocumentOptions Options = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = 16,
    };

    /// <summary>Le document, ou null avec la raison. Ne lève jamais.</summary>
    public static ToolCatalogDocument? Parse(ReadOnlySpan<byte> json, IReadOnlyList<ToolDefinition> definitions, out string? error)
    {
        if (json.Length == 0)
        {
            error = "catalogue vide";
            return null;
        }

        if (json.Length > MaxDocumentBytes)
        {
            error = "catalogue anormalement gros";
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json.ToArray(), Options);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "catalogue mal formé";
                return null;
            }

            if (!TryGetInt64(root, "format", out long format) || format < 1)
            {
                error = "catalogue sans numéro de format";
                return null;
            }

            if (format > SupportedFormat)
            {
                error = $"catalogue au format {format}, plus récent que cette version de PCPerfSuite (format {SupportedFormat})";
                return null;
            }

            if (!TryGetInt64(root, "sequence", out long sequence) || sequence < 1)
            {
                error = "catalogue sans numéro de publication";
                return null;
            }

            DateTimeOffset? generated = root.TryGetProperty("generatedUtc", out JsonElement date) && date.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(date.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed)
                    ? parsed
                    : null;

            var releases = new Dictionary<string, ToolRelease>(StringComparer.Ordinal);
            var rejected = new List<string>();

            if (root.TryGetProperty("tools", out JsonElement tools) && tools.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement entry in tools.EnumerateArray())
                {
                    ReadEntry(entry, definitions, releases, rejected);
                }
            }

            error = null;
            return new ToolCatalogDocument((int)format, sequence, generated, releases, rejected);
        }
        catch (JsonException ex)
        {
            error = $"catalogue illisible ({ex.Message})";
            return null;
        }
        catch (Exception ex)
        {
            error = $"catalogue illisible ({ex.GetType().Name})";
            return null;
        }
    }

    private static void ReadEntry(JsonElement entry, IReadOnlyList<ToolDefinition> definitions,
        Dictionary<string, ToolRelease> releases, List<string> rejected)
    {
        if (entry.ValueKind != JsonValueKind.Object)
        {
            rejected.Add("entrée qui n'est pas un objet");
            return;
        }

        string? id = GetString(entry, "id");
        if (string.IsNullOrWhiteSpace(id))
        {
            rejected.Add("entrée sans identifiant");
            return;
        }

        // Un outil que cette version de l'app ne connaît pas, ou qu'elle n'ouvre qu'en page officielle : rien à valider,
        // ses hôtes et son éditeur n'étant pas figés ici.
        ToolDefinition? definition = definitions.FirstOrDefault(d => d.Id == id);
        if (definition is null || !definition.HasDirectDownload) return;

        if (releases.ContainsKey(id))
        {
            rejected.Add($"{id} : présent deux fois, seule la première entrée est gardée");
            return;
        }

        long size = TryGetInt64(entry, "size", out long value) ? value : -1;
        string? reason = Validate(definition, GetString(entry, "version"), GetString(entry, "url"), GetString(entry, "sha256"), size,
            out ToolRelease? release);

        if (release is not null) releases[id] = release;
        else rejected.Add($"{id} : {reason}");
    }

    /// <summary>Raison d'écarter cette version, ou null si <paramref name="release"/> est valide.</summary>
    public static string? Validate(ToolDefinition definition, string? version, string? url, string? sha256, long size, out ToolRelease? release)
    {
        release = null;

        if (!IsSafeVersion(version)) return "numéro de version absent ou invalide";
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps) return "adresse absente ou pas en HTTPS";
        if (!uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo)) return "adresse inhabituelle (port ou identifiants)";
        if (!OfficialInstaller.IsAllowedHost(uri, definition.AllowedHosts)) return $"« {uri.Host} » n'est pas une source autorisée pour cet outil";
        if (!OfficialInstaller.IsSha256Hex(sha256)) return "empreinte SHA-256 absente ou invalide";
        if (size <= 0 || size > definition.MaxBytes) return "taille absente ou au-delà de la limite de cet outil";

        string? fileName = FileNameFrom(uri, definition);
        if (fileName is null) return "impossible de déduire un nom de fichier de l'adresse";

        release = new ToolRelease(definition.Id, version!, uri, sha256!.ToUpperInvariant(), size, fileName);
        return null;
    }

    /// <summary>Version utilisable telle quelle comme nom de dossier : chiffres, lettres, « . _ + - », sans « .. ».</summary>
    public static bool IsSafeVersion(string? version)
        => version is { Length: > 0 and <= MaxVersionLength }
           && char.IsLetterOrDigit(version[0]) && char.IsLetterOrDigit(version[^1])
           && !version.Contains("..", StringComparison.Ordinal)
           && version.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '+' or '-');

    /// <summary>Dernier segment de l'adresse s'il fait un nom de fichier du bon type, sinon le nom de repli.</summary>
    internal static string? FileNameFrom(Uri uri, ToolDefinition definition)
    {
        string extension = definition.FileKind switch
        {
            InstallerFileKind.Msi => ".msi",
            InstallerFileKind.Zip => ".zip",
            _ => ".exe",
        };

        string last = Uri.UnescapeDataString(uri.Segments.Length > 0 ? uri.Segments[^1] : "");
        if (IsSafeFileName(last) && last.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) return last;

        return definition.FallbackFileName is { } fallback && IsSafeFileName(fallback) ? fallback : null;
    }

    private static bool IsSafeFileName(string name)
        => name.Length is > 4 and <= 120 && name == Path.GetFileName(name) && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
           && !name.StartsWith('.') && !name.EndsWith('.') && !name.EndsWith(' ');

    private static string? GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool TryGetInt64(JsonElement element, string name, out long value)
    {
        value = 0;
        return element.TryGetProperty(name, out JsonElement property) && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt64(out value);
    }
}
