using System.Text.Json;
using System.Text.Json.Serialization;

namespace PCPerfSuite.Core.Benchmark.Protocol;

/// <summary>Types de messages du tube. Dans les deux sens sauf mention contraire.</summary>
public static class BenchMessageTypes
{
    /// <summary>Worker → app, dès la connexion : son pid et sa version du bench.</summary>
    public const string Hello = "bonjour";

    /// <summary>App → worker : une demande de test (<see cref="BenchJobRequest"/>).</summary>
    public const string Start = "demarrer";

    /// <summary>Worker → app : avancement d'un test.</summary>
    public const string Progress = "progression";

    /// <summary>Worker → app : résultat d'un test (<see cref="BenchJobResult"/>).</summary>
    public const string Result = "resultat";

    /// <summary>Worker → app : échec d'un test ou du worker lui-même.</summary>
    public const string Error = "erreur";

    /// <summary>App → worker, toutes les 500 ms : « je suis toujours là ». Sans lui pendant 2 s, le worker coupe tout.</summary>
    public const string Heartbeat = "battement";

    /// <summary>App → worker : arrêter le test en cours.</summary>
    public const string Stop = "arreter";
}

/// <summary>
/// Un message du tube entre l'app et son worker de charge : une ligne JSON, versionnée. Les champs inconnus sont gardés
/// (<see cref="Extra"/>) ; une autre version de protocole est refusée net par <see cref="BenchMessageCodec"/>.
/// </summary>
public sealed class BenchMessage
{
    [JsonPropertyName("v")]
    public int Version { get; set; } = Benchmark.BenchVersion.Protocol;

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("pid")]
    public int? ProcessId { get; set; }

    [JsonPropertyName("bench")]
    public int? BenchVersion { get; set; }

    [JsonPropertyName("jobId")]
    public string? JobId { get; set; }

    [JsonPropertyName("job")]
    public BenchJobRequest? Job { get; set; }

    [JsonPropertyName("progress")]
    public BenchProgress? Progress { get; set; }

    [JsonPropertyName("result")]
    public BenchJobResult? Result { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    /// <summary>Avec le bonjour : ce que le worker a pu régler sur son processus (EcoQoS, priorité).</summary>
    [JsonPropertyName("notes")]
    public List<string>? Notes { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    public static BenchMessage Hello(int processId)
        => new() { Type = BenchMessageTypes.Hello, ProcessId = processId, BenchVersion = Benchmark.BenchVersion.Bench };

    public static BenchMessage Start(BenchJobRequest job) => new() { Type = BenchMessageTypes.Start, JobId = job.Id, Job = job };

    public static BenchMessage ProgressOf(BenchProgress progress) => new() { Type = BenchMessageTypes.Progress, JobId = progress.JobId, Progress = progress };

    public static BenchMessage ResultOf(BenchJobResult result) => new() { Type = BenchMessageTypes.Result, JobId = result.JobId, Result = result };

    public static BenchMessage ErrorOf(string? jobId, string error) => new() { Type = BenchMessageTypes.Error, JobId = jobId, Error = error };

    public static BenchMessage Heartbeat() => new() { Type = BenchMessageTypes.Heartbeat };

    public static BenchMessage Stop(string? jobId) => new() { Type = BenchMessageTypes.Stop, JobId = jobId };
}

/// <summary>Avancement d'un test : la phase en cours (« préchauffe », « passe 2/3 »), le pourcentage, et une valeur
/// partielle à afficher en direct quand elle a un sens.</summary>
public sealed class BenchProgress
{
    public string JobId { get; set; } = "";

    public string Phase { get; set; } = "";

    public double Percent { get; set; }

    public string? Detail { get; set; }

    public double? Value { get; set; }

    public string? Unit { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}
