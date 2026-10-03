using System.Text.Json;
using System.Text.Json.Serialization;

namespace PCPerfSuite.Core.Benchmark.Protocol;

/// <summary>
/// Encodage des messages du tube : une ligne JSON par message (JSON échappe les sauts de ligne, le délimiteur est donc
/// sûr), lecture tolérante aux champs inconnus, refus net d'une autre version de protocole ou d'une ligne abîmée.
/// </summary>
public static class BenchMessageCodec
{
    /// <summary>Un résultat avec ses mesures reste très en dessous ; au-delà, c'est une ligne corrompue ou hostile.</summary>
    public const int MaxLineBytes = 4 * 1024 * 1024;

    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        WriteIndented = false,
    };

    public static string Encode(BenchMessage message) => JsonSerializer.Serialize(message, Options);

    /// <summary>Null, avec la raison, pour une ligne vide, un JSON invalide, une version absente ou autre, ou un type
    /// absent.</summary>
    public static BenchMessage? TryDecode(string? line, out string? problem)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            problem = "ligne vide";
            return null;
        }

        if (line.Length > MaxLineBytes)
        {
            problem = $"message trop long ({line.Length} caractères)";
            return null;
        }

        BenchMessage? message;
        try
        {
            message = JsonSerializer.Deserialize<BenchMessage>(line, Options);
        }
        catch (JsonException ex)
        {
            problem = $"JSON invalide ({ex.Message})";
            return null;
        }

        if (message is null)
        {
            problem = "message nul";
            return null;
        }

        if (message.Version <= 0)
        {
            problem = "version de protocole absente";
            return null;
        }

        if (message.Version != BenchVersion.Protocol)
        {
            problem = $"version de protocole {message.Version} au lieu de {BenchVersion.Protocol}";
            return null;
        }

        if (string.IsNullOrEmpty(message.Type))
        {
            problem = "type absent";
            return null;
        }

        problem = null;
        return message;
    }
}
