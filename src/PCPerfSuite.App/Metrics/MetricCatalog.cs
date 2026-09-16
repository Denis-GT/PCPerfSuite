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

/// <summary>Catégorie : identifiant stable (clé des couleurs persistées), nom affiché dans l'app,
/// libellé court (ASCII) en tête de ligne dans l'OSD, et couleur par défaut de ce libellé dans
/// l'overlay — une teinte par catégorie pour les repérer d'un coup d'œil en jeu.</summary>
public sealed record MetricCategory(string Key, string Name, string OsdLabel, string DefaultColor);

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

    /// <summary>Haut de l'échelle fixe du graphique (pourcentages, températures) ; null : échelle calée sur le pic visible.</summary>
    public double? GraphMaximum { get; init; }

    /// <summary>Plancher de l'échelle automatique, pour qu'une valeur au repos ne remplisse pas tout le graphique.</summary>
    public double GraphMinimumScale { get; init; } = 1;

    /// <summary>Faux pour une valeur sans courbe possible (l'heure) : elle n'est pas proposée en tuile graphique.</summary>
    public bool HasGraph { get; init; } = true;

    /// <summary>Groupe de capteurs dont la lecture renouvelle cette valeur : sa courbe n'avance qu'à ce moment-là.
    /// Null pour une valeur qui change à chaque relevé (l'heure).</summary>
    public SensorGroup? ReadGroup { get; init; }

    /// <summary>Met en forme une valeur d'historique (un point du graphique) exactement comme <see cref="Read"/>
    /// met en forme la valeur courante — <see cref="Read"/>, lui, part d'un relevé complet et ne sait donc rien
    /// formater du passé. Null pour une métrique sans courbe (l'heure).</summary>
    public Func<double, MetricReading>? FormatNumber { get; init; }
}

/// <summary>
/// Catalogue unique des métriques proposées par l'overlay et par "Mes métriques" (Monitoring), dans l'ordre
/// d'affichage. Chaque entrée s'appuie sur un capteur LibreHardwareMonitor relevé par HardwareMonitorService,
/// sur RTSS ou sur l'horloge ; une valeur absente sur cette config s'affiche "--".
/// </summary>
public static class MetricCatalog
{
    internal static readonly MetricCategory Cpu = new("cpu", "CPU", "CPU", "#4CC2FF");
    internal static readonly MetricCategory Gpu = new("gpu", "GPU", "GPU", "#7BE38B");
    internal static readonly MetricCategory Ram = new("ram", "RAM", "RAM", "#C08CFF");
    internal static readonly MetricCategory Motherboard = new("mb", "Carte mère", "CM", "#FFB74D");
    internal static readonly MetricCategory Storage = new("storage", "Stockage", "DISQUE", "#FFD166");
    internal static readonly MetricCategory Network = new("net", "Réseau", "NET", "#4DD9C0");
    internal static readonly MetricCategory Game = new("game", "Jeu (RTSS)", "JEU", "#FF7A9C");
    internal static readonly MetricCategory Sys = new("sys", "Système", "SYS", "#B7C0D8");

    /// <summary>1 Mo/s : plancher des graphiques de débit disque.</summary>
    internal const double DiskRateFloor = 1_048_576;

    /// <summary>1 Mbit/s : plancher des graphiques de débit réseau.</summary>
    internal const double NetworkRateFloor = 125_000;

    /// <summary>Catégories dans l'ordre du catalogue — sert au réglage des couleurs de l'overlay.</summary>
    public static IReadOnlyList<MetricCategory> Categories { get; } = new[]
    {
        Cpu, Gpu, Ram, Motherboard, Storage, Network, Game, Sys,
    };

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

        Rate("storage.read", Storage, "Débit lecture total", "lect", DiskRateFloor, s => SumOrNull(s.Hardware.Disks.Select(d => d.ReadRateBytesPerSecond))),
        Rate("storage.write", Storage, "Débit écriture total", "ecr", DiskRateFloor, s => SumOrNull(s.Hardware.Disks.Select(d => d.WriteRateBytesPerSecond))),
        Numeric("storage.temp.max", Storage, "Température disque max", "temp", "°C", "0", s => s.Hardware.Disks.Max(d => d.TemperatureC)),

        Rate("net.upload", Network, "Débit montant total", "envoi", NetworkRateFloor, s => s.Hardware.Network.UploadBytesPerSecond),
        Rate("net.download", Network, "Débit descendant total", "recep", NetworkRateFloor, s => s.Hardware.Network.DownloadBytesPerSecond),

        Numeric("game.fps", Game, "FPS", "fps", "FPS", "0", s => s.Game?.Fps),
        Numeric("game.fps.avg", Game, "FPS moyen", "moy", "FPS", "0", s => s.Game?.AverageFps),
        Numeric("game.fps.low1", Game, "FPS 1% low", "1%", "FPS", "0", s => s.Game?.OnePercentLowFps),
        Numeric("game.fps.low01", Game, "FPS 0.1% low", "0.1%", "FPS", "0", s => s.Game?.PointOnePercentLowFps),
        Numeric("game.frametime", Game, "Temps de frame", "frame", "ms", "0.0", s => s.Game?.FrameTimeMs),

        new MetricDefinition
        {
            Id = "system.time",
            Category = Sys,
            Label = "Heure",
            OsdLabel = "heure",
            HasGraph = false,
            Read = s => new MetricReading(null, s.LocalTime.ToString("HH:mm", CultureInfo.InvariantCulture), ""),
        },
    };

    /// <summary>Tuiles graphiques affichées par défaut dans le Monitoring : ce que montraient les anciennes cartes par composant.</summary>
    public static IReadOnlyList<string> DefaultMonitoringIds { get; } = new[]
    {
        "cpu.load", "cpu.temp.package", "cpu.power",
        "gpu.load", "gpu.temp.core", "gpu.power",
        "ram.load",
        "mb.temp.system",
        "storage.read", "storage.write",
        "net.download", "net.upload",
    };

    internal static MetricDefinition Numeric(string id, MetricCategory category, string label, string osdLabel,
        string unit, string format, Func<MetricSample, double?> get, SensorGroup? group = null)
    {
        // Une seule expression de mise en forme, partagée par la valeur courante et par un point d'historique :
        // le repère du graphique ne peut donc pas afficher un nombre différent du grand chiffre de la tuile.
        MetricReading Format(double value) => new(value, value.ToString(format, CultureInfo.CurrentCulture), unit);

        return new MetricDefinition
        {
            Id = id,
            Category = category,
            Label = label,
            OsdLabel = osdLabel,
            IsPercent = unit == "%",
            ReadGroup = group ?? GroupFor(id, category),
            // Pourcentages et températures sur une échelle fixe de 0 à 100 ; le reste calé sur le pic visible,
            // avec un plancher de l'ordre d'une valeur au repos.
            GraphMaximum = unit is "%" or "°C" ? 100 : null,
            GraphMinimumScale = unit switch
            {
                "W" => 10,
                "RPM" or "MHz" => 1000,
                "FPS" => 60,
                "ms" => 20,
                _ => 1,
            },
            Read = s => get(s) is { } v ? Format(v) : MetricReading.Missing,
            FormatNumber = Format,
        };
    }

    /// <summary>Débit en octets/seconde, mis à l'échelle (o/s, Ko/s, Mo/s...).</summary>
    internal static MetricDefinition Rate(string id, MetricCategory category, string label, string osdLabel,
        double graphMinimumScale, Func<MetricSample, double?> getBytesPerSecond)
    {
        MetricReading Format(double bytesPerSecond)
        {
            (string value, string unit) = ByteFormatter.Split(Math.Max(0, bytesPerSecond));
            return new MetricReading(bytesPerSecond, value, unit + "/s");
        }

        return new MetricDefinition
        {
            Id = id,
            Category = category,
            Label = label,
            OsdLabel = osdLabel,
            GraphMinimumScale = graphMinimumScale,
            ReadGroup = GroupFor(id, category),
            Read = s => getBytesPerSecond(s) is { } v ? Format(v) : MetricReading.Missing,
            FormatNumber = Format,
        };
    }

    /// <summary>Groupe de lecture déduit de la catégorie ; la charge CPU a le sien, bien moins coûteux que le reste du CPU.</summary>
    private static SensorGroup? GroupFor(string id, MetricCategory category) => category.Key switch
    {
        "cpu" => id == "cpu.load" ? SensorGroup.CpuLoad : SensorGroup.Cpu,
        "gpu" => SensorGroup.Gpu,
        "ram" => SensorGroup.Memory,
        "mb" => SensorGroup.Motherboard,
        "storage" => SensorGroup.Storage,
        "net" => SensorGroup.Network,
        "game" => SensorGroup.Fps,
        _ => null,
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
