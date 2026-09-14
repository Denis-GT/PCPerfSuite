using System.Globalization;
using PCPerfSuite.App.Utils;
using PCPerfSuite.Core.Hardware;
using PCPerfSuite.Core.Overlay;

namespace PCPerfSuite.App.Metrics;

/// <summary>Tout ce qu'un tick de rafraîchissement a relevé : la source que lisent toutes les métriques.</summary>
public sealed class MetricSample
{
    public required HardwareSnapshot Hardware { get; init; }

    /// <summary>Statistiques RTSS de l'application au premier plan — null hors jeu ou sans RTSS.</summary>
    public RtssFrameStats? Game { get; init; }

    public required DateTime LocalTime { get; init; }
}

/// <summary>Catégorie : nom affiché dans l'app, et libellé court (ASCII) en tête de ligne dans l'OSD.</summary>
public sealed record MetricCategory(string Name, string OsdLabel);

/// <summary>Valeur formatée. Value et Unit restent séparés pour les tuiles (unité en plus petit) ; Text les
/// combine pour l'OSD. Number alimente la jauge des métriques en %.</summary>
public readonly record struct MetricReading(double? Number, string Value, string Unit)
{
    public static MetricReading Missing => new(null, "--", "");

    public string Text => Unit switch
    {
        "" => Value,
        "%" or "°C" => Value + Unit,
        _ => $"{Value} {Unit}",
    };
}

public sealed class MetricDefinition
{
    /// <summary>Identifiant stable, persisté dans settings.json : ne jamais le renommer.</summary>
    public required string Id { get; init; }

    public required MetricCategory Category { get; init; }
    public required string Label { get; init; }

    /// <summary>Libellé court (ASCII) pour le mode OSD "une ligne par métrique".</summary>
    public required string OsdLabel { get; init; }

    public bool IsPercent { get; init; }
    public required Func<MetricSample, MetricReading> Read { get; init; }
}

/// <summary>
/// Catalogue unique des métriques proposées par l'overlay et par "Mes métriques" (Monitoring), dans l'ordre
/// d'affichage. Chaque entrée s'appuie sur un capteur LibreHardwareMonitor relevé par HardwareMonitorService,
/// sur RTSS ou sur l'horloge ; une valeur absente sur cette config s'affiche "--".
/// </summary>
public static class MetricCatalog
{
    private static readonly MetricCategory Cpu = new("CPU", "CPU");
    private static readonly MetricCategory Gpu = new("GPU", "GPU");
    private static readonly MetricCategory Ram = new("RAM", "RAM");
    private static readonly MetricCategory Motherboard = new("Carte mère", "CM");
    private static readonly MetricCategory Storage = new("Stockage", "DISQUE");
    private static readonly MetricCategory Network = new("Réseau", "NET");
    private static readonly MetricCategory Game = new("Jeu (RTSS)", "JEU");
    private static readonly MetricCategory Sys = new("Système", "SYS");

    public static IReadOnlyList<MetricDefinition> All { get; } = new[]
    {
        Numeric("cpu.load", Cpu, "Charge totale", "charge", "%", "0", s => s.Hardware.Cpu.LoadPercent),
        Numeric("cpu.load.maxcore", Cpu, "Charge du cœur le plus sollicité", "coeur max", "%", "0", s => s.Hardware.Cpu.MaxCoreLoadPercent),
        Numeric("cpu.temp.package", Cpu, "Température package", "temp", "°C", "0", s => s.Hardware.Cpu.PackageTempC),
        Numeric("cpu.temp.maxcore", Cpu, "Température max cœur", "temp coeur", "°C", "0", s => s.Hardware.Cpu.MaxCoreTempC),
        Numeric("cpu.power", Cpu, "Puissance", "puiss", "W", "0", s => s.Hardware.Cpu.PowerWatts),
        Numeric("cpu.clock.max", Cpu, "Fréquence max", "freq", "MHz", "0", s => s.Hardware.Cpu.MaxClockMhz),
        Numeric("cpu.voltage.core", Cpu, "Tension cœur", "vcore", "V", "0.000", s => s.Hardware.Cpu.CoreVoltage),

        Numeric("gpu.load", Gpu, "Charge", "charge", "%", "0", s => s.Hardware.Gpu?.LoadPercent),
        Numeric("gpu.temp.core", Gpu, "Température cœur", "temp", "°C", "0", s => s.Hardware.Gpu?.CoreTempC),
        Numeric("gpu.temp.hotspot", Gpu, "Hot spot", "hotspot", "°C", "0", s => s.Hardware.Gpu?.HotSpotTempC),
        Numeric("gpu.temp.memory", Gpu, "Température mémoire (junction)", "temp mem", "°C", "0", s => s.Hardware.Gpu?.MemoryJunctionTempC),
        Numeric("gpu.power", Gpu, "Puissance", "puiss", "W", "0", s => s.Hardware.Gpu?.PowerWatts),
        Numeric("gpu.clock.core", Gpu, "Horloge cœur", "freq", "MHz", "0", s => s.Hardware.Gpu?.CoreClockMhz),
        Numeric("gpu.clock.memory", Gpu, "Horloge mémoire", "freq mem", "MHz", "0", s => s.Hardware.Gpu?.MemoryClockMhz),
        Numeric("gpu.vram.used", Gpu, "VRAM utilisée", "vram", "Mo", "0", s => s.Hardware.Gpu?.VramUsedMb),
        Numeric("gpu.vram.load", Gpu, "VRAM (%)", "vram", "%", "0", s => VramPercent(s.Hardware.Gpu)),
        Numeric("gpu.fan.rpm", Gpu, "Ventilateur (RPM)", "ventilo", "RPM", "0", s => s.Hardware.Gpu?.FanRpm),
        Numeric("gpu.fan.percent", Gpu, "Ventilateur (%)", "ventilo", "%", "0", s => s.Hardware.Gpu?.FanPercent),

        Numeric("ram.load", Ram, "Charge", "charge", "%", "0", s => s.Hardware.Memory.LoadPercent),
        Numeric("ram.used", Ram, "Utilisée", "util", "Go", "0.0", s => s.Hardware.Memory.UsedGb),
        Numeric("ram.available", Ram, "Disponible", "dispo", "Go", "0.0", s => s.Hardware.Memory.AvailableGb),
        Numeric("ram.virtual.used", Ram, "Mémoire virtuelle utilisée", "virt", "Go", "0.0", s => s.Hardware.Memory.VirtualUsedGb),

        Numeric("mb.temp.system", Motherboard, "Température système", "temp", "°C", "0", s => s.Hardware.Motherboard.SystemTempC),
        Numeric("mb.temp.vrm", Motherboard, "Température VRM", "vrm", "°C", "0", s => s.Hardware.Motherboard.VrmTempC),

        Rate("storage.read", Storage, "Débit lecture total", "lect", s => SumOrNull(s.Hardware.Disks.Select(d => d.ReadRateBytesPerSecond))),
        Rate("storage.write", Storage, "Débit écriture total", "ecr", s => SumOrNull(s.Hardware.Disks.Select(d => d.WriteRateBytesPerSecond))),
        Numeric("storage.temp.max", Storage, "Température disque max", "temp", "°C", "0", s => s.Hardware.Disks.Max(d => d.TemperatureC)),

        Rate("net.upload", Network, "Débit montant total", "envoi", s => s.Hardware.Network.UploadBytesPerSecond),
        Rate("net.download", Network, "Débit descendant total", "recep", s => s.Hardware.Network.DownloadBytesPerSecond),

        Numeric("game.fps", Game, "FPS", "fps", "FPS", "0", s => s.Game?.Fps),
        Numeric("game.frametime", Game, "Temps de frame", "frame", "ms", "0.0", s => s.Game?.FrameTimeMs),

        new MetricDefinition
        {
            Id = "system.time",
            Category = Sys,
            Label = "Heure",
            OsdLabel = "heure",
            Read = s => new MetricReading(null, s.LocalTime.ToString("HH:mm", CultureInfo.InvariantCulture), ""),
        },
    };

    public static IReadOnlyList<string> DefaultMonitoringIds { get; } = new[]
    {
        "cpu.load", "cpu.temp.package", "gpu.load", "gpu.temp.core", "ram.load", "net.download",
    };

    private static MetricDefinition Numeric(string id, MetricCategory category, string label, string osdLabel,
        string unit, string format, Func<MetricSample, double?> get) => new()
    {
        Id = id,
        Category = category,
        Label = label,
        OsdLabel = osdLabel,
        IsPercent = unit == "%",
        Read = s => get(s) is { } v
            ? new MetricReading(v, v.ToString(format, CultureInfo.CurrentCulture), unit)
            : MetricReading.Missing,
    };

    /// <summary>Débit en octets/seconde, mis à l'échelle (o/s, Ko/s, Mo/s...).</summary>
    private static MetricDefinition Rate(string id, MetricCategory category, string label, string osdLabel,
        Func<MetricSample, double?> getBytesPerSecond) => new()
    {
        Id = id,
        Category = category,
        Label = label,
        OsdLabel = osdLabel,
        Read = s =>
        {
            if (getBytesPerSecond(s) is not { } v) return MetricReading.Missing;
            (string value, string unit) = ByteFormatter.Split(Math.Max(0, v));
            return new MetricReading(v, value, unit + "/s");
        },
    };

    private static double? VramPercent(GpuSnapshot? gpu)
        => gpu is { VramUsedMb: { } used, VramTotalMb: { } total } && total > 0 ? used / total * 100 : null;

    private static double? SumOrNull(IEnumerable<float?> values)
    {
        double? total = null;
        foreach (float? value in values)
        {
            if (value is { } v) total = (total ?? 0) + v;
        }
        return total;
    }
}
