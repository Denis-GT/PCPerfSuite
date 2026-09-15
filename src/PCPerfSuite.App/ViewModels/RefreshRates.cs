namespace PCPerfSuite.App.ViewModels;

public sealed record RefreshRateOption(string Label, int Milliseconds);

/// <summary>
/// Cadences proposées au monitoring et à l'overlay. Exposé via des propriétés d'instance sur les
/// ViewModels : une liste statique n'est pas atteignable par un {Binding} WPF classique (le moteur de
/// binding ne résout que les propriétés d'instance), ce qui viderait silencieusement le sélecteur.
/// </summary>
public static class RefreshRates
{
    public static IReadOnlyList<RefreshRateOption> Monitoring { get; } = new List<RefreshRateOption>
    {
        new("250 ms", 250),
        new("500 ms", 500),
        new("1 seconde", 1000),
        new("2 secondes", 2000),
        new("5 secondes", 5000),
    };

    /// <summary>L'overlay lit les relevés du monitoring : il ne peut pas aller plus vite que lui, mais
    /// il peut aller moins vite (texte plus stable à lire en jeu).</summary>
    public static IReadOnlyList<RefreshRateOption> Overlay { get; } = new List<RefreshRateOption>
    {
        new("250 ms", 250),
        new("500 ms", 500),
        new("1 seconde", 1000),
        new("2 secondes", 2000),
        new("5 secondes", 5000),
    };

    public static RefreshRateOption Resolve(IReadOnlyList<RefreshRateOption> options, int milliseconds)
        => options.FirstOrDefault(o => o.Milliseconds == milliseconds) ?? options[2];
}
