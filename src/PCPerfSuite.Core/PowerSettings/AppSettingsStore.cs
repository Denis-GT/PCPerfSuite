using System.Text.Json;
using System.Text.Json.Serialization;
using PCPerfSuite.Core.Hardware;

namespace PCPerfSuite.Core.PowerSettings;

public sealed class AppSettings
{
    /// <summary>GUID du plan "Performances ultimes" une fois dupliqué, pour éviter d'en recréer un à chaque lancement
    /// (le nom du plan est localisé par Windows donc on ne peut pas le retrouver de façon fiable par son nom).</summary>
    public string? UltimatePerformanceGuid { get; set; }

    /// <summary>Intervalle de rafraîchissement du monitoring, en millisecondes.</summary>
    public int MonitoringRefreshMs { get; set; } = 1000;

    /// <summary>Identifiants (catalogue de métriques) des tuiles "Mes métriques" du Monitoring — null tant que
    /// l'utilisateur n'a rien personnalisé (sélection par défaut).</summary>
    public List<string>? MonitoringMetricIds { get; set; }

    /// <summary>Courbes de ventilation configurées par l'utilisateur, une par capteur de contrôle piloté.</summary>
    public List<FanCurveConfig> FanCurves { get; set; } = new();

    /// <summary>Réglages de contrôle GPU (limite de puissance + ventilateurs NVAPI).</summary>
    public GpuControlSettings Gpu { get; set; } = new();

    /// <summary>Réglages de l'overlay en jeu (poussé dans RTSS).</summary>
    public OverlaySettings Overlay { get; set; } = new();
}

public sealed class GpuControlSettings
{
    /// <summary>Null tant que l'utilisateur n'a jamais touché le slider — on n'impose alors rien au
    /// démarrage, la carte reste sur son réglage par défaut.</summary>
    public float? PowerLimitPercent { get; set; }

    public FanControlMode FanMode { get; set; } = FanControlMode.Auto;
    public float FanManualPercent { get; set; } = 60;
    public FanTempSource FanSource { get; set; } = FanTempSource.GpuCore;
    public List<FanCurvePoint> FanPoints { get; set; } = FanCurveMath.EquilibrePoints();
}

public sealed class OverlaySettings
{
    public bool Enabled { get; set; }

    /// <summary>Identifiants (catalogue de métriques) affichés dans l'OSD — null dans un fichier d'avant la
    /// sélection libre, dérivé alors de ShowCpu/ShowGpu/ShowRam.</summary>
    public List<string>? MetricIds { get; set; }

    /// <summary>Une ligne par métrique au lieu d'une ligne par catégorie.</summary>
    public bool OneLinePerMetric { get; set; }

    // Anciens interrupteurs, lus uniquement pour la migration : remis à null (donc retirés du JSON) dès
    // que MetricIds est enregistré.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? ShowCpu { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? ShowGpu { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? ShowRam { get; set; }
}

/// <summary>Petit stockage JSON local pour l'état de l'app (pas besoin d'une DB pour si peu).</summary>
public static class AppSettingsStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PCPerfSuite", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                string json = File.ReadAllText(FilePath);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch { /* fichier corrompu ou illisible : on repart d'un état vide */ }

        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            string json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
        }
        catch { /* best-effort */ }
    }
}
