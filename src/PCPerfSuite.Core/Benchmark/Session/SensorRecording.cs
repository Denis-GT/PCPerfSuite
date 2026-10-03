using PCPerfSuite.Core.Hardware;
using PCPerfSuite.Core.Hardware.Fans;

namespace PCPerfSuite.Core.Benchmark.Session;

/// <summary>Un point d'une série : secondes depuis le début du test, valeur.</summary>
public sealed record SensorPoint(double Seconds, float Value);

/// <summary>Une série de capteur sous-échantillonnée à 1 Hz (le dernier relevé de chaque seconde).</summary>
public sealed class SensorSeries
{
    public SensorSeries(string key, string label, string unit)
    {
        Key = key;
        Label = label;
        Unit = unit;
    }

    public string Key { get; }

    public string Label { get; }

    public string Unit { get; }

    public List<SensorPoint> Points { get; } = new();

    public float? Max => Points.Count == 0 ? null : Points.Max(p => p.Value);

    public float? Min => Points.Count == 0 ? null : Points.Min(p => p.Value);

    public float? Last => Points.Count == 0 ? null : Points[^1].Value;

    internal void Add(double seconds, float? value)
    {
        if (value is not { } v || float.IsNaN(v)) return;
        int second = (int)Math.Floor(seconds);
        if (Points.Count > 0 && (int)Math.Floor(Points[^1].Seconds) == second) Points[^1] = new SensorPoint(seconds, v);
        else Points.Add(new SensorPoint(seconds, v));
    }
}

/// <summary>Cadence réellement obtenue du relevé pendant le test : ce que le worker en priorité High a laissé.</summary>
public sealed record RecordingCadence(int Snapshots, int Duplicates, double Seconds, double? MeanIntervalMs, double? MaxGapMs, int CpuGroupMisses)
{
    public string Describe()
    {
        if (Snapshots == 0) return "aucun relevé pendant le test";
        string gap = MaxGapMs is { } g ? $"trou max {g / 1000:0.0} s" : "trou max inconnu";
        string misses = CpuGroupMisses > 0 ? $", {CpuGroupMisses} relevé(s) sans le groupe CPU" : "";
        return $"{Snapshots} relevés en {Seconds:0} s (toutes les {MeanIntervalMs:0} ms, {gap}{misses})";
    }
}

/// <summary>Bridage compté pendant le test, d'après le socle de signaux (#4).</summary>
public sealed record ThrottleTally(int Snapshots, int Thermal, int PowerLimit, int CurrentLimit, int Prochot)
{
    public bool Any => Thermal + PowerLimit + CurrentLimit + Prochot > 0;

    public string Describe()
    {
        if (Snapshots == 0) return "bridage non lu";
        if (!Any) return $"aucun bridage sur {Snapshots} relevés";
        var parts = new List<string>();
        if (Thermal > 0) parts.Add($"thermique {Thermal}");
        if (PowerLimit > 0) parts.Add($"puissance {PowerLimit}");
        if (CurrentLimit > 0) parts.Add($"courant {CurrentLimit}");
        if (Prochot > 0) parts.Add($"PROCHOT {Prochot}");
        return $"bridage sur {Snapshots} relevés : {string.Join(", ", parts)}";
    }
}

/// <summary>
/// Enregistrement des capteurs pendant un test, en logique pure : relevés dédoublonnés par <c>CapturedAtUtc</c> (un
/// relevé resservi après une exception ne compte qu'une fois), séries à 1 Hz (température, puissance et fréquence du
/// processeur, température et puissance du GPU, ventilateur CPU, batterie), bridage compté, et la cadence obtenue
/// (nombre de relevés, intervalle moyen, plus grand trou, relevés sans le groupe CPU : c'est le piège du worker High
/// qui affame le relevé, à mesurer).
/// </summary>
public sealed class SensorRecording
{
    private readonly DateTime _startUtc;
    private DateTime? _lastCapturedUtc;
    private double _maxGapSeconds;
    private double _sumIntervals;
    private int _intervals;
    private int _throttleSnapshots, _thermal, _power, _current, _prochot;

    public SensorRecording(DateTime startUtc)
    {
        _startUtc = startUtc;
        Series =
        [
            new SensorSeries("cpu-temp", "Température CPU", "°C"),
            new SensorSeries("cpu-puissance", "Puissance CPU", "W"),
            new SensorSeries("cpu-frequence", "Fréquence CPU max", "MHz"),
            new SensorSeries("cpu-charge", "Charge CPU", "%"),
            new SensorSeries("gpu-temp", "Température GPU", "°C"),
            new SensorSeries("gpu-puissance", "Puissance GPU", "W"),
            new SensorSeries("ventilateur-cpu", "Ventilateur CPU", "tr/min"),
            new SensorSeries("batterie", "Batterie", "%"),
        ];
    }

    public IReadOnlyList<SensorSeries> Series { get; }

    public int Snapshots { get; private set; }

    public int Duplicates { get; private set; }

    public int CpuGroupMisses { get; private set; }

    public DateTime? LastCapturedUtc => _lastCapturedUtc;

    public SensorSeries this[string key] => Series.First(s => s.Key == key);

    /// <summary>Vrai si le relevé était nouveau (pas une resserte).</summary>
    public bool Add(HardwareSnapshot snapshot)
    {
        if (_lastCapturedUtc is { } last && snapshot.CapturedAtUtc <= last)
        {
            Duplicates++;
            return false;
        }

        if (_lastCapturedUtc is { } previous)
        {
            double gap = (snapshot.CapturedAtUtc - previous).TotalSeconds;
            _maxGapSeconds = Math.Max(_maxGapSeconds, gap);
            _sumIntervals += gap;
            _intervals++;
        }
        _lastCapturedUtc = snapshot.CapturedAtUtc;
        Snapshots++;
        if (snapshot.GroupsRead.Count > 0 && !snapshot.GroupsRead.Contains(SensorGroup.Cpu)) CpuGroupMisses++;

        double seconds = Math.Max(0, (snapshot.CapturedAtUtc - _startUtc).TotalSeconds);
        this["cpu-temp"].Add(seconds, snapshot.Cpu.PackageTempC ?? snapshot.Cpu.MaxCoreTempC);
        this["cpu-puissance"].Add(seconds, snapshot.Cpu.PowerWatts);
        this["cpu-frequence"].Add(seconds, snapshot.Cpu.MaxClockMhz);
        this["cpu-charge"].Add(seconds, snapshot.Cpu.LoadPercent);
        this["gpu-temp"].Add(seconds, snapshot.Gpu?.CoreTempC);
        this["gpu-puissance"].Add(seconds, snapshot.Gpu?.PowerWatts);
        List<FanReading> cpuFans = snapshot.Fans.Where(f => f.Category == FanCategory.Cpu && f.Rpm is not null).ToList();
        this["ventilateur-cpu"].Add(seconds, cpuFans.Count == 0 ? null : cpuFans.Max(f => f.Rpm));
        this["batterie"].Add(seconds, (float?)BenchSafetySample.BatteryPercentOf(snapshot.Battery));

        if (snapshot.CpuThrottle is { } throttle)
        {
            _throttleSnapshots++;
            if (throttle.Thermal == true) _thermal++;
            if (throttle.PowerLimit == true) _power++;
            if (throttle.CurrentLimit == true) _current++;
            if (throttle.Prochot == true) _prochot++;
        }
        return true;
    }

    public RecordingCadence Cadence(DateTime endUtc)
    {
        double seconds = Math.Max(0, (endUtc - _startUtc).TotalSeconds);
        return new RecordingCadence(Snapshots, Duplicates, seconds,
            _intervals > 0 ? _sumIntervals / _intervals * 1000 : null,
            _intervals > 0 ? _maxGapSeconds * 1000 : null,
            CpuGroupMisses);
    }

    public ThrottleTally Throttle => new(_throttleSnapshots, _thermal, _power, _current, _prochot);
}
