using System.Text.Json.Serialization;

namespace PCPerfSuite.Core.Safety;

/// <summary>État d'une opération du journal de session.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SessionEntryState>))]
public enum SessionEntryState
{
    /// <summary>Commencée, pas encore terminée (« EnCours ») : restée ainsi au lancement suivant, l'app a été
    /// interrompue pendant l'opération.</summary>
    InProgress,

    /// <summary>Terminée normalement (« Terminé »).</summary>
    Completed,

    /// <summary>Échouée ou abandonnée (« Échoué ») ; la cause dit pourquoi.</summary>
    Failed,
}

/// <summary>
/// Une opération du journal de session, dans son dernier état. <paramref name="StartedUtc"/>, le démarrage de Windows
/// (<paramref name="BootUtc"/>), le processus, le composant, l'action et les valeurs viennent de la ligne qui l'a
/// ouverte ; l'état, la cause et <paramref name="UpdatedUtc"/> de sa dernière ligne.
/// </summary>
public sealed record SessionJournalEntry(
    Guid Id,
    string Component,
    string Action,
    IReadOnlyDictionary<string, string> Values,
    DateTimeOffset StartedUtc,
    DateTimeOffset? BootUtc,
    int? ProcessId,
    SessionEntryState State,
    string? Cause,
    DateTimeOffset UpdatedUtc);

/// <summary>Contenu lu du journal : les opérations dans leur dernier état, dans l'ordre de leur ouverture, et le nombre
/// de lignes ignorées (fin arrachée par une coupure, ligne abîmée). <paramref name="Problem"/> dit pourquoi le fichier
/// n'a pas pu être lu ; un fichier absent n'est pas un problème.</summary>
public sealed record SessionJournalContent(IReadOnlyList<SessionJournalEntry> Entries, int IgnoredLines, string? Problem)
{
    public static SessionJournalContent Empty { get; } = new([], 0, null);

    public IEnumerable<SessionJournalEntry> Pending => Entries.Where(entry => entry.State == SessionEntryState.InProgress);
}

/// <summary>Une ligne du fichier, telle qu'elle est écrite (format v1, voir <see cref="SessionJournal"/>).</summary>
internal sealed class SessionJournalLine
{
    [JsonPropertyName("v")] public int Version { get; set; }
    [JsonPropertyName("id")] public Guid Id { get; set; }
    [JsonPropertyName("timeUtc")] public DateTimeOffset TimeUtc { get; set; }
    [JsonPropertyName("boot")] public DateTimeOffset? BootUtc { get; set; }
    [JsonPropertyName("pid")] public int? ProcessId { get; set; }
    [JsonPropertyName("component")] public string? Component { get; set; }
    [JsonPropertyName("action")] public string? Action { get; set; }
    [JsonPropertyName("values")] public Dictionary<string, string>? Values { get; set; }
    [JsonPropertyName("state")] public SessionEntryState State { get; set; }
    [JsonPropertyName("cause")] public string? Cause { get; set; }
}

/// <summary>
/// Ce qui entre dans le journal : des clés courtes et des nombres, jamais un nom d'application, de fichier ou de
/// dossier (le journal part dans les rapports). Un texte qui y ressemble est remplacé par <see cref="Masked"/>, une clé
/// hors du format kebab-case est ramenée à ce format, et tout est borné pour qu'une ligne reste courte.
/// </summary>
internal static class SessionJournalText
{
    public const string Masked = "[masqué]";

    public const int MaxKeyLength = 40;
    public const int MaxValueLength = 48;
    public const int MaxCauseLength = 200;
    public const int MaxValues = 12;

    /// <summary>Composant ou action : minuscules, chiffres et tirets (« test-combine »).</summary>
    public static string Key(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "inconnu";

        var chars = text.Trim().ToLowerInvariant()
            .Select(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') ? c : '-')
            .ToArray();
        string key = new string(chars).Trim('-');
        if (key.Length > MaxKeyLength) key = key[..MaxKeyLength];
        return key.Length == 0 ? "inconnu" : key;
    }

    /// <summary>Une valeur : un nombre ou un mot, jamais un chemin (« \ », « / », « : ») ni un exécutable.</summary>
    public static string Value(string? text)
    {
        if (text is null) return "";
        string value = text.Trim();
        if (LooksLikePath(value) || value.Contains(':')) return Masked;
        return value.Length > MaxValueLength ? value[..MaxValueLength] : value;
    }

    /// <summary>Une cause : une phrase de l'app, bornée, sans chemin ni exécutable.</summary>
    public static string? Cause(string? text)
    {
        if (text is null) return null;
        string cause = text.Trim();
        if (LooksLikePath(cause)) return Masked;
        return cause.Length > MaxCauseLength ? cause[..MaxCauseLength] : cause;
    }

    public static Dictionary<string, string>? Values(IReadOnlyDictionary<string, string>? values)
    {
        if (values is null || values.Count == 0) return null;

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string key, string value) in values)
        {
            if (result.Count >= MaxValues) break;
            result[Key(key)] = Value(value);
        }
        return result;
    }

    private static bool LooksLikePath(string text)
        => text.Contains('\\') || text.Contains('/') || text.Contains(".exe", StringComparison.OrdinalIgnoreCase);
}
