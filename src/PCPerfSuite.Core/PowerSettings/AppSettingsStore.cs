using System.Text.Json;
using PCPerfSuite.Core.Hardware;

namespace PCPerfSuite.Core.PowerSettings;

public sealed class AppSettings
{
    /// <summary>GUID du plan "Performances ultimes" une fois dupliqué, pour éviter d'en recréer un à chaque lancement
    /// (le nom du plan est localisé par Windows donc on ne peut pas le retrouver de façon fiable par son nom).</summary>
    public string? UltimatePerformanceGuid { get; set; }

    /// <summary>Intervalle de rafraîchissement du monitoring, en millisecondes.</summary>
    public int MonitoringRefreshMs { get; set; } = 1000;

    /// <summary>Courbes de ventilation configurées par l'utilisateur, une par capteur de contrôle piloté.</summary>
    public List<FanCurveConfig> FanCurves { get; set; } = new();
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
