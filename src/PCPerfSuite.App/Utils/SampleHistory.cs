namespace PCPerfSuite.App.Utils;

/// <summary>
/// Fenêtre glissante des derniers relevés d'une valeur. Tenue par le ViewModel plutôt que par le
/// graphique, pour que l'historique ne dépende pas de la vue et serve aussi aux statistiques.
/// Une valeur absente est gardée comme NaN pour que plusieurs séries restent alignées dans le temps.
/// Chaque relevé porte son heure et un numéro d'ordre : un repère posé sur le graphique désigne ainsi
/// un relevé précis, pas une position à l'écran que le défilement de la courbe rendrait fausse.
/// </summary>
public sealed class SampleHistory
{
    private readonly double[] _values;
    private readonly DateTime[] _times;
    private int _next;

    public SampleHistory(int capacity = 90)
    {
        _values = new double[capacity];
        _times = new DateTime[capacity];
    }

    /// <summary>Levé à chaque ajout ou remise à zéro, sur le thread qui a modifié l'historique.</summary>
    public event Action? Changed;

    /// <summary>Min, moyenne et max depuis le début, au-delà de la fenêtre dessinée : le pic d'une charge
    /// reste lisible longtemps après être sorti de la courbe. Remis à zéro à part, sur demande, car ce
    /// n'est pas le même geste que vider la fenêtre.</summary>
    public RunningStats Stats { get; } = new();

    public int Capacity => _values.Length;
    public int Count { get; private set; }

    /// <summary>Nombre de relevés poussés depuis la création. Sert de numérotation stable : le relevé
    /// d'indice i porte le numéro <see cref="TotalPushed"/> - <see cref="Count"/> + i, un numéro qui ne
    /// bouge pas quand la fenêtre glisse, alors que son indice, lui, diminue à chaque ajout.</summary>
    public long TotalPushed { get; private set; }

    /// <summary>Relevé n° <paramref name="index"/>, du plus ancien (0) au plus récent (Count - 1). NaN si absent.</summary>
    public double this[int index] => _values[(_next - Count + index + Capacity) % Capacity];

    /// <summary>Heure du relevé d'indice <paramref name="index"/>.</summary>
    public DateTime TimeAt(int index) => _times[(_next - Count + index + Capacity) % Capacity];

    /// <summary>Numéro du relevé d'indice <paramref name="index"/>.</summary>
    public long SequenceAt(int index) => TotalPushed - Count + index;

    /// <summary>Indice du relevé numéro <paramref name="sequence"/>, ou -1 s'il est déjà sorti de la fenêtre.</summary>
    public int IndexOf(long sequence)
    {
        long index = sequence - (TotalPushed - Count);
        return index >= 0 && index < Count ? (int)index : -1;
    }

    public void Push(double? value) => Push(value, DateTime.Now);

    /// <summary>Ajoute un relevé daté de l'instant fourni. Un appelant qui pousse des dizaines de capteurs
    /// pour un même relevé passe l'heure de ce relevé : une seule lecture d'horloge, et des points
    /// réellement contemporains entre courbes.</summary>
    public void Push(double? value, DateTime timestamp)
    {
        _values[_next] = value ?? double.NaN;
        _times[_next] = timestamp;
        // Ici plutôt que chez l'appelant : un seul point d'entrée pour tous les capteurs, affichés ou non,
        // donc une tuile qu'on active arrive avec ses statistiques déjà faites.
        if (value is { } present) Stats.Push(present);
        _next = (_next + 1) % Capacity;
        if (Count < Capacity) Count++;
        TotalPushed++;
        Changed?.Invoke();
    }

    /// <summary>Vide la fenêtre dessinée. <see cref="Stats"/> n'est pas touché : effacer la courbe et
    /// repartir de zéro pour les min/moyenne/max sont deux gestes distincts.</summary>
    public void Clear()
    {
        _next = 0;
        Count = 0;
        // TotalPushed n'est volontairement pas remis à zéro : le numéro d'un relevé reste unique pour la vie
        // de l'historique, donc un repère posé sur un ancien relevé s'efface au lieu de retomber par hasard
        // sur un relevé neuf et d'afficher une valeur qui n'est pas la sienne.
        Changed?.Invoke();
    }

    /// <summary>Plus grande valeur présente, 0 s'il n'y en a aucune. Part de l'infini négatif : une série
    /// entièrement négative (une décharge de batterie, par exemple) doit rendre son maximum réel, pas 0 —
    /// une valeur qu'elle n'a jamais prise.</summary>
    public double Max()
    {
        double max = double.NegativeInfinity;
        for (int i = 0; i < Count; i++)
        {
            // NaN > max est toujours faux : les valeurs absentes sont ignorées d'office.
            if (this[i] > max) max = this[i];
        }
        return double.IsNegativeInfinity(max) ? 0 : max;
    }

    /// <summary>Plus grande valeur absolue présente, 0 s'il n'y en a aucune : échelle d'une valeur signée.</summary>
    public double MaxAbs()
    {
        double max = 0;
        for (int i = 0; i < Count; i++)
        {
            if (Math.Abs(this[i]) > max) max = Math.Abs(this[i]);
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
