namespace PCPerfSuite.App.Utils;

/// <summary>
/// Fenêtre glissante des derniers relevés d'une valeur. Tenue par le ViewModel plutôt que par le
/// graphique, pour que l'historique ne dépende pas de la vue et serve aussi aux statistiques.
/// Une valeur absente est gardée comme NaN pour que plusieurs séries restent alignées dans le temps.
/// </summary>
public sealed class SampleHistory
{
    private readonly double[] _values;
    private int _next;

    public SampleHistory(int capacity = 90)
    {
        _values = new double[capacity];
    }

    /// <summary>Levé à chaque ajout ou remise à zéro, sur le thread qui a modifié l'historique.</summary>
    public event Action? Changed;

    public int Capacity => _values.Length;
    public int Count { get; private set; }

    /// <summary>Relevé n° <paramref name="index"/>, du plus ancien (0) au plus récent (Count - 1). NaN si absent.</summary>
    public double this[int index] => _values[(_next - Count + index + Capacity) % Capacity];

    public void Push(double? value)
    {
        _values[_next] = value ?? double.NaN;
        _next = (_next + 1) % Capacity;
        if (Count < Capacity) Count++;
        Changed?.Invoke();
    }

    public void Clear()
    {
        _next = 0;
        Count = 0;
        Changed?.Invoke();
    }

    /// <summary>Plus grande valeur présente, 0 s'il n'y en a aucune.</summary>
    public double Max()
    {
        double max = 0;
        for (int i = 0; i < Count; i++)
        {
            // NaN > max est toujours faux : les valeurs absentes sont ignorées d'office.
            if (this[i] > max) max = this[i];
        }
        return max;
    }

    /// <summary>Moyenne des valeurs présentes, null s'il n'y en a aucune.</summary>
    public double? Average()
    {
        double sum = 0;
        int present = 0;
        for (int i = 0; i < Count; i++)
        {
            if (double.IsNaN(this[i])) continue;
            sum += this[i];
            present++;
        }
        return present > 0 ? sum / present : null;
    }
}
