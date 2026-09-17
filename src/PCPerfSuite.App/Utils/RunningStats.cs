namespace PCPerfSuite.App.Utils;

/// <summary>
/// Min, moyenne et max cumulés sur tous les relevés depuis la création ou la dernière remise à zéro.
/// Volontairement distinct de la fenêtre glissante de <see cref="SampleHistory"/> : la fenêtre sert à
/// dessiner la courbe et à en calculer l'échelle, ces statistiques-là gardent le pic d'un benchmark
/// longtemps après qu'il soit sorti de la courbe. Les valeurs absentes (NaN) sont ignorées, comme
/// partout ailleurs dans l'historique.
/// </summary>
public sealed class RunningStats
{
    private double _sum;

    /// <summary>Faux tant qu'aucune valeur présente n'a été relevée : l'affichage montre alors "--".</summary>
    public bool HasValue => Count > 0;

    /// <summary>Nombre de valeurs présentes prises en compte.</summary>
    public int Count { get; private set; }

    public double Minimum { get; private set; } = double.PositiveInfinity;
    public double Maximum { get; private set; } = double.NegativeInfinity;

    /// <summary>Moyenne des valeurs présentes, 0 tant qu'il n'y en a aucune (voir <see cref="HasValue"/>).</summary>
    public double Average => Count > 0 ? _sum / Count : 0;

    public void Push(double value)
    {
        if (double.IsNaN(value)) return;

        if (value < Minimum) Minimum = value;
        if (value > Maximum) Maximum = value;
        _sum += value;
        Count++;
    }

    public void Reset()
    {
        Minimum = double.PositiveInfinity;
        Maximum = double.NegativeInfinity;
        _sum = 0;
        Count = 0;
    }
}
