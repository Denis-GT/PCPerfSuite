using PCPerfSuite.Core.Compatibility;

namespace PCPerfSuite.Core.Benchmark.Session;

/// <summary>
/// Fenêtre glissante de la charge processeur de fond, en logique pure : avant un test, la machine doit être calme
/// (charge sous <see cref="QuietThresholdPercent"/> pendant <see cref="Window"/>), sinon les passes seront instables et
/// le résultat le dira. Seuils expérimentaux (règle 6).
/// </summary>
public sealed class BackgroundLoadWindow
{
    public const double QuietThresholdPercent = 5;
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(10);

    private readonly List<(DateTimeOffset At, double Load)> _samples = new();

    public void Note(DateTimeOffset now, double? loadPercent)
    {
        if (loadPercent is not { } load || double.IsNaN(load)) return;
        _samples.Add((now, load));
        _samples.RemoveAll(s => now - s.At > Window);
    }

    /// <summary>Durée couverte par les échantillons gardés.</summary>
    public TimeSpan Covered => _samples.Count < 2 ? TimeSpan.Zero : _samples[^1].At - _samples[0].At;

    public bool HasEnoughHistory => Covered >= Window - TimeSpan.FromSeconds(1);

    public double? AveragePercent => _samples.Count == 0 ? null : _samples.Average(s => s.Load);

    public double? MaxPercent => _samples.Count == 0 ? null : _samples.Max(s => s.Load);

    /// <summary>Calme : assez d'historique et charge moyenne sous le seuil.</summary>
    public bool IsQuiet => HasEnoughHistory && AveragePercent is { } average && average < QuietThresholdPercent;

    public string Describe()
    {
        if (AveragePercent is not { } average) return "charge de fond non lue";
        string level = $"{average:0.#} % en moyenne sur {Covered.TotalSeconds:0} s (pointe {MaxPercent:0.#} %)";
        if (!HasEnoughHistory) return $"charge de fond : {level}, historique trop court";
        return IsQuiet ? $"machine calme : {level}" : $"activité en arrière-plan : {level}";
    }
}

/// <summary>Ce qu'on sait de la machine au moment de décider si un bench peut partir.</summary>
public sealed record BenchPreconditionInputs(
    bool? PowerOnline,
    double? BatteryPercent,
    BackgroundLoadWindow BackgroundLoad,
    string? WorkerProblem,
    Unavailable? RamBandwidthProblem,
    Unavailable? RamLatencyProblem,
    Unavailable? DiskProblem);

/// <summary>Verdict des préconditions : ce qui empêche chaque test, et ce qui rend un résultat « non représentatif ».</summary>
public sealed record BenchPreconditionReport(
    IReadOnlyDictionary<BenchTestKind, Unavailable?> PerTest,
    bool OnBattery,
    double? BatteryPercent,
    bool IsRepresentative,
    IReadOnlyList<string> Notes)
{
    public Unavailable? For(BenchTestKind kind) => PerTest.GetValueOrDefault(kind);

    public bool IsAvailable(BenchTestKind kind) => For(kind) is null;

    public bool AnyAvailable => BenchTestKinds.All.Any(IsAvailable);
}

/// <summary>
/// Préconditions du bench, en logique pure (décisions de Denis du 02/10/2026) : sur batterie, le bench est autorisé mais
/// « non représentatif » ; sous <see cref="BatteryStopPercent"/> de batterie, aucun test ne part (c'est aussi le seuil
/// d'arrêt de sécurité) ; sans worker lançable, rien ne part ; chaque test mémoire ou disque porte en plus sa raison
/// propre (RAM libre, volume). La charge de fond ne bloque pas : elle est notée, et les passes diront « instable ».
/// </summary>
public static class BenchPreconditions
{
    public const double BatteryStopPercent = 30;

    public static BenchPreconditionReport Evaluate(BenchPreconditionInputs inputs)
    {
        var notes = new List<string>();
        bool onBattery = inputs.PowerOnline == false;
        Unavailable? common = null;

        if (inputs.WorkerProblem is { } worker)
        {
            common = new Unavailable(UnavailableCause.HardwareOrDriver, $"worker de charge impossible à lancer : {worker}");
        }
        else if (onBattery && inputs.BatteryPercent is { } percent && percent < BatteryStopPercent)
        {
            common = new Unavailable(UnavailableCause.HardwareOrDriver, $"batterie à {percent:0} % : branche le secteur (le bench s'arrête sous {BatteryStopPercent:0} %)");
        }

        if (onBattery) notes.Add("sur batterie : résultat non représentatif (le portable se bride pour durer)");
        else if (inputs.PowerOnline is null) notes.Add("alimentation : non lue (PC de bureau ou pile absente)");
        else notes.Add("sur secteur");
        notes.Add(inputs.BackgroundLoad.Describe());

        var perTest = new Dictionary<BenchTestKind, Unavailable?>();
        foreach (BenchTestKind kind in BenchTestKinds.All)
        {
            perTest[kind] = common ?? kind switch
            {
                BenchTestKind.RamBandwidth => inputs.RamBandwidthProblem,
                BenchTestKind.RamLatency => inputs.RamLatencyProblem,
                BenchTestKind.Disk => inputs.DiskProblem,
                _ => null,
            };
        }

        return new BenchPreconditionReport(perTest, onBattery, inputs.BatteryPercent, !onBattery, notes);
    }
}
