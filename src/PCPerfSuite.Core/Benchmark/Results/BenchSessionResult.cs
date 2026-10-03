using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using PCPerfSuite.Core.Benchmark.Protocol;
using PCPerfSuite.Core.Benchmark.Session;

namespace PCPerfSuite.Core.Benchmark.Results;

/// <summary>
/// Une session de bench enregistrée : un fichier JSON v1 par session dans le sous-dossier <c>bench\</c> du dossier de
/// données. Schéma stable et lu de façon tolérante (<c>[JsonExtensionData]</c> partout) : le diagnostic (#12) s'en
/// servira. Unités physiques et points côte à côte ; un score ne se compare qu'à la même <see cref="BenchVersion"/>.
/// </summary>
public sealed class BenchSessionResult : IJsonOnDeserializing
{
    public const int FormatVersion = 1;

    /// <summary><see cref="FormatVersion"/> pour une session créée ici ; à la lecture, 0 si le document n'a pas de « v »
    /// (refusé « version absente » par <see cref="BenchResultStore.TryParse"/>).</summary>
    [JsonPropertyName("v")]
    public int Version { get; set; } = FormatVersion;

    /// <summary>Avant la lecture des champs : sans cela, un document sans « v » garderait la version de l'initialiseur et
    /// passerait pour une session v1.</summary>
    void IJsonOnDeserializing.OnDeserializing() => Version = 0;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public int BenchVersion { get; set; } = Benchmark.BenchVersion.Bench;

    public string AppVersion { get; set; } = "";

    public string Runtime { get; set; } = "";

    public DateTimeOffset StartedUtc { get; set; }

    public DateTimeOffset EndedUtc { get; set; }

    public BenchContextDocument Context { get; set; } = new();

    public List<string> PreconditionNotes { get; set; } = new();

    /// <summary>Faux sur batterie : le portable se bride pour durer.</summary>
    public bool IsRepresentative { get; set; } = true;

    public bool OnBattery { get; set; }

    public string ThermalLimit { get; set; } = "";

    public List<BenchTestResult> Tests { get; set; } = new();

    public List<string> WorkerNotes { get; set; } = new();

    public List<string> Log { get; set; } = new();

    /// <summary>Raison d'un arrêt de sécurité (clé de <see cref="BenchStopReason"/>), null sinon.</summary>
    public string? StoppedBy { get; set; }

    public bool Cancelled { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>Résumé d'une ligne pour l'historique : « 3 tests · CPU un cœur 1 120 pts · Disque 980 pts ».</summary>
    public string Summary()
    {
        var parts = new List<string> { $"{Tests.Count} test{(Tests.Count > 1 ? "s" : "")}" };
        foreach (BenchTestResult test in Tests)
        {
            if (test.Points is { } points) parts.Add($"{test.Title} {points.ToString("N0", CultureInfo.CurrentCulture)} pts");
            else if (!test.Succeeded) parts.Add($"{test.Title} : échec");
        }
        if (StoppedBy is not null) parts.Add("arrêt de sécurité");
        else if (Cancelled) parts.Add("arrêté");
        return string.Join(" · ", parts);
    }

    public static BenchSessionResult From(BenchSessionOutcome outcome, BenchContext context, BenchPreconditionReport preconditions, BenchThermalLimits thermal)
    {
        return new BenchSessionResult
        {
            AppVersion = context.AppVersion,
            Runtime = context.Runtime,
            StartedUtc = outcome.StartedUtc,
            EndedUtc = outcome.EndedUtc,
            Context = BenchContextDocument.From(context),
            PreconditionNotes = preconditions.Notes.ToList(),
            IsRepresentative = preconditions.IsRepresentative,
            OnBattery = preconditions.OnBattery,
            ThermalLimit = thermal.Describe(),
            Tests = outcome.Tests.Select(BenchTestResult.From).ToList(),
            WorkerNotes = outcome.WorkerNotes.ToList(),
            Log = outcome.Log.ToList(),
            StoppedBy = outcome.StoppedBy?.ToString(),
            Cancelled = outcome.Cancelled,
        };
    }
}

/// <summary>Le contexte machine, à plat.</summary>
public sealed class BenchContextDocument
{
    public string Cpu { get; set; } = "";
    public string CpuVendor { get; set; } = "";
    public int CpuFamily { get; set; }
    public int CpuModel { get; set; }
    public bool CpuIsHybrid { get; set; }
    public int? PhysicalCores { get; set; }
    public int LogicalProcessors { get; set; }
    public double? LargestL3Mb { get; set; }
    public double? RamGb { get; set; }
    public string? RamType { get; set; }
    public int? RamSpeedMhz { get; set; }
    public int RamModules { get; set; }
    public List<string> Gpus { get; set; } = new();
    public string Manufacturer { get; set; } = "";
    public string Model { get; set; } = "";
    public string Chassis { get; set; } = "";
    public string Windows { get; set; } = "";
    public bool IsAdministrator { get; set; }
    public float? CpuSustainedWatts { get; set; }
    public float? CpuBurstWatts { get; set; }
    public float? GpuPowerLimitPercent { get; set; }
    public string? Fans { get; set; }
    public string? VendorMode { get; set; }
    public string? VendorModeReason { get; set; }
    public string? PcieLink { get; set; }
    public string? PcieLinkReason { get; set; }
    public string PowerMode { get; set; } = "";
    public string? PowerPlan { get; set; }
    public List<string> Problems { get; set; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    public static BenchContextDocument From(BenchContext c) => new()
    {
        Cpu = c.CpuName,
        CpuVendor = c.CpuVendor,
        CpuFamily = c.CpuFamily,
        CpuModel = c.CpuModel,
        CpuIsHybrid = c.CpuIsHybrid,
        PhysicalCores = c.PhysicalCores,
        LogicalProcessors = c.LogicalProcessors,
        LargestL3Mb = c.LargestL3Bytes is { } l3 ? l3 / (1024.0 * 1024) : null,
        RamGb = c.RamTotalGb,
        RamType = c.RamType,
        RamSpeedMhz = c.RamSpeedMhz,
        RamModules = c.RamModules,
        Gpus = c.GpuNames.ToList(),
        Manufacturer = c.Manufacturer,
        Model = c.Model,
        Chassis = c.Chassis,
        Windows = c.Windows,
        IsAdministrator = c.IsAdministrator,
        CpuSustainedWatts = c.CpuSustainedWatts,
        CpuBurstWatts = c.CpuBurstWatts,
        GpuPowerLimitPercent = c.GpuPowerLimitPercent,
        Fans = c.FansDescription,
        VendorMode = c.VendorMode,
        VendorModeReason = c.VendorModeUnavailable?.Reason,
        PcieLink = c.PcieLink,
        PcieLinkReason = c.PcieLinkUnavailable?.Reason,
        PowerMode = c.PowerMode.OverlayLabel,
        PowerPlan = c.PowerMode.ActiveSchemeName,
        Problems = c.Problems.ToList(),
    };
}

/// <summary>Un test de la session : son résultat brut (mesures, notes), ses points, et ce que les capteurs ont vu.</summary>
public sealed class BenchTestResult
{
    public string Kind { get; set; } = "";
    public string Title { get; set; } = "";
    public bool Succeeded { get; set; }
    public string? Error { get; set; }

    /// <summary>1000 = la machine de référence ; null sans référence, hors chemin de calcul comparable ou après une erreur de calcul.</summary>
    public double? Points { get; set; }

    public bool IsComparable { get; set; } = true;
    public bool ChecksumMismatch { get; set; }

    /// <summary>Au moins une mesure aux passes trop dispersées.</summary>
    public bool IsUnstable { get; set; }

    public double DurationSeconds { get; set; }
    public DateTimeOffset StartedUtc { get; set; }
    public DateTimeOffset EndedUtc { get; set; }
    public List<BenchMeasurement> Measurements { get; set; } = new();
    public Dictionary<string, string> Notes { get; set; } = new();
    public List<BenchSeriesDocument> Series { get; set; } = new();
    public string Cadence { get; set; } = "";
    public int CadenceSnapshots { get; set; }
    public double? CadenceMaxGapMs { get; set; }
    public int CadenceCpuGroupMisses { get; set; }
    public string Throttle { get; set; } = "";
    public int ThrottleThermal { get; set; }
    public int ThrottlePowerLimit { get; set; }
    public float? MaxCpuTempC { get; set; }
    public string? StopReason { get; set; }
    public string? StopDetail { get; set; }
    public string? IdleReturnNote { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    public BenchTestKind? KindValue => BenchTestKinds.Parse(Kind);

    public static BenchTestResult From(BenchTestOutcome outcome)
    {
        BenchJobResult r = outcome.Result;
        bool scorable = r.Succeeded && r.IsComparable && !r.ChecksumMismatch;
        return new BenchTestResult
        {
            Kind = BenchTestKinds.Key(outcome.Kind),
            Title = BenchTestKinds.Title(outcome.Kind),
            Succeeded = r.Succeeded,
            Error = r.Error,
            Points = scorable ? BenchScore.Points(r.Measurements, BenchReferences.For(outcome.Kind)) : null,
            IsComparable = r.IsComparable,
            ChecksumMismatch = r.ChecksumMismatch,
            IsUnstable = r.Measurements.Any(m => m.IsUnstable),
            DurationSeconds = r.DurationSeconds,
            StartedUtc = outcome.StartedUtc,
            EndedUtc = outcome.EndedUtc,
            Measurements = r.Measurements.ToList(),
            Notes = new Dictionary<string, string>(r.Notes),
            Series = outcome.Series.Where(s => s.Points.Count > 0).Select(BenchSeriesDocument.From).ToList(),
            Cadence = outcome.Cadence.Describe(),
            CadenceSnapshots = outcome.Cadence.Snapshots,
            CadenceMaxGapMs = outcome.Cadence.MaxGapMs,
            CadenceCpuGroupMisses = outcome.Cadence.CpuGroupMisses,
            Throttle = outcome.Throttle.Describe(),
            ThrottleThermal = outcome.Throttle.Thermal,
            ThrottlePowerLimit = outcome.Throttle.PowerLimit,
            MaxCpuTempC = outcome.MaxCpuTempC,
            StopReason = outcome.StopReason?.ToString(),
            StopDetail = outcome.StopDetail,
            IdleReturnNote = outcome.IdleReturnNote,
        };
    }
}

/// <summary>Une série de capteur à 1 Hz, en deux tableaux parallèles (compacts en JSON).</summary>
public sealed class BenchSeriesDocument
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string Unit { get; set; } = "";
    public List<double> Seconds { get; set; } = new();
    public List<float> Values { get; set; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    public static BenchSeriesDocument From(SensorSeries series) => new()
    {
        Key = series.Key,
        Label = series.Label,
        Unit = series.Unit,
        Seconds = series.Points.Select(p => Math.Round(p.Seconds, 2)).ToList(),
        Values = series.Points.Select(p => p.Value).ToList(),
    };
}
