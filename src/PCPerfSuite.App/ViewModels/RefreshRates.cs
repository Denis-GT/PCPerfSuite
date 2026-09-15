namespace PCPerfSuite.App.ViewModels;

/// <summary>
/// Bornes de la cadence de rafraîchissement, saisie librement en millisecondes (Monitoring et overlay).
/// Minimum de 100 ms : plus vite n'apporte rien de lisible à l'écran, alors que la lecture complète du CPU
/// par LibreHardwareMonitor coûte déjà 30 à 80 ms. Les capteurs lents ou coûteux ont en plus leur propre
/// cadence (voir HardwareMonitorService). Au-delà d'une minute, l'affichage n'a plus rien de "temps réel".
/// </summary>
public static class RefreshRates
{
    public const int MinMs = 100;
    public const int MaxMs = 60_000;

    public const string Hint = "de 100 à 60000 ms";

    public static int Clamp(int milliseconds) => Math.Clamp(milliseconds, MinMs, MaxMs);
}
