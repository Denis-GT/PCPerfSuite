namespace PCPerfSuite.App.ViewModels;

/// <summary>
/// Bornes de la cadence de rafraîchissement, saisie librement en millisecondes (Monitoring et overlay).
/// En dessous de 100 ms, la lecture des capteurs n'a pas le temps de se terminer entre deux relevés ;
/// au-delà d'une minute, l'affichage n'a plus rien de "temps réel".
/// </summary>
public static class RefreshRates
{
    public const int MinMs = 100;
    public const int MaxMs = 60_000;

    public const string Hint = "de 100 à 60000 ms";

    public static int Clamp(int milliseconds) => Math.Clamp(milliseconds, MinMs, MaxMs);
}
