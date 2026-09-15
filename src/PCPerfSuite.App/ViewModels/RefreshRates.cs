namespace PCPerfSuite.App.ViewModels;

/// <summary>
/// Bornes de la cadence de rafraîchissement, saisie librement en millisecondes (Monitoring et overlay).
/// Minimum de 50 ms, à confirmer avec le tableau "Temps de lecture des capteurs" : un relevé coûtait
/// ~45 ms en moyenne et ~100 ms au pire avant que le CPU soit relu toutes les 500 ms et que la mémoire
/// RTSS reste ouverte. Un tick qui tombe pendant une lecture est de toute façon sauté. Au-delà d'une
/// minute, l'affichage n'a plus rien de "temps réel".
/// </summary>
public static class RefreshRates
{
    public const int MinMs = 50;
    public const int MaxMs = 60_000;

    public const string Hint = "de 50 à 60000 ms";

    public static int Clamp(int milliseconds) => Math.Clamp(milliseconds, MinMs, MaxMs);
}
