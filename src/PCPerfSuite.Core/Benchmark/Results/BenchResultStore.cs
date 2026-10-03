using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using PCPerfSuite.Core.SystemInfo;

namespace PCPerfSuite.Core.Benchmark.Results;

/// <summary>
/// Les sessions enregistrées : un fichier <c>bench-&lt;horodatage&gt;-&lt;id&gt;.json</c> par session dans
/// <see cref="AppDataPaths.BenchFolder"/> (relu à chaque écriture : mode portable). Lecture tolérante : un fichier
/// illisible est compté, jamais une exception ; une version plus récente est lue avec ce qu'on en comprend.
/// </summary>
public sealed class BenchResultStore
{
    public const string FilePrefix = "bench-";
    public const int DefaultMaxLoaded = 50;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    private readonly Func<string> _folder;

    public BenchResultStore(Func<string>? folder = null) => _folder = folder ?? (() => AppDataPaths.Current.BenchFolder);

    public string Folder => _folder();

    /// <summary>Écrit la session ; rend son chemin, ou null avec la raison.</summary>
    public string? Save(BenchSessionResult result, out string? error)
    {
        try
        {
            string folder = Folder;
            Directory.CreateDirectory(folder);
            string name = $"{FilePrefix}{result.StartedUtc.ToLocalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}-{Shorten(result.Id)}.json";
            string path = Path.Combine(folder, name);
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(result, Options));
            File.Move(temp, path, overwrite: true);
            error = null;
            return path;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>Les sessions, la plus récente d'abord (ordre des noms de fichiers), au plus <paramref name="max"/>.</summary>
    public IReadOnlyList<BenchSessionResult> LoadAll(out int unreadable, int max = DefaultMaxLoaded)
    {
        unreadable = 0;
        var results = new List<BenchSessionResult>();
        string folder;
        try
        {
            folder = Folder;
            if (!Directory.Exists(folder)) return results;
        }
        catch (Exception)
        {
            return results;
        }

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(folder, FilePrefix + "*.json").OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return results;
        }

        foreach (string file in files)
        {
            if (results.Count >= max) break;
            try
            {
                BenchSessionResult? result = TryParse(File.ReadAllText(file), out _);
                if (result is null) unreadable++;
                else results.Add(result);
            }
            catch (Exception)
            {
                unreadable++;
            }
        }
        return results;
    }

    /// <summary>Null, avec la raison, pour un JSON invalide ou un document sans version lisible.</summary>
    public static BenchSessionResult? TryParse(string json, out string? problem)
    {
        try
        {
            BenchSessionResult? result = JsonSerializer.Deserialize<BenchSessionResult>(json, Options);
            if (result is null)
            {
                problem = "document nul";
                return null;
            }
            if (result.Version <= 0)
            {
                problem = "version absente";
                return null;
            }
            result.Tests ??= new List<BenchTestResult>();
            result.Log ??= new List<string>();
            result.WorkerNotes ??= new List<string>();
            result.PreconditionNotes ??= new List<string>();
            result.Context ??= new BenchContextDocument();
            problem = null;
            return result;
        }
        catch (JsonException ex)
        {
            problem = $"JSON invalide ({ex.Message})";
            return null;
        }
    }

    public static string Serialize(BenchSessionResult result) => JsonSerializer.Serialize(result, Options);

    private static string Shorten(string id) => id.Length <= 8 ? id : id[..8];
}
