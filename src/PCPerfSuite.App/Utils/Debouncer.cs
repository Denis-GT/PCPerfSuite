using System.Windows.Threading;

namespace PCPerfSuite.App.Utils;

/// <summary>
/// Retarde l'application d'un réglage le temps que le curseur se stabilise.
///
/// Un Slider WPF lève ValueChanged à chaque pixel de déplacement. Sans ce délai, glisser un curseur
/// d'un bout à l'autre déclenche des dizaines d'écritures matérielles (MSR, boîte aux lettres SMU,
/// pilote graphique), chacune suivie d'une relecture de vérification et d'un enregistrement complet
/// du fichier de réglages — et, pour les réglages d'alimentation Windows, d'une réapplication du plan
/// d'alimentation entier au système.
///
/// Seule l'action coûteuse passe par ici : l'affichage continue de suivre le curseur immédiatement.
/// </summary>
public sealed class Debouncer
{
    /// <summary>Assez court pour rester imperceptible une fois le curseur lâché, assez long pour
    /// avaler la rafale d'événements d'un glissement.</summary>
    public static readonly TimeSpan DefaultDelay = TimeSpan.FromMilliseconds(250);

    private readonly DispatcherTimer _timer;

    /// <summary>Actions en attente, une par réglage et dans leur ordre d'arrivée. Une seule action ne
    /// suffirait pas : un même minuteur sert plusieurs curseurs d'un onglet, et programmer la limite de
    /// température ne doit pas effacer la limite de puissance encore en attente.</summary>
    private readonly List<(string Key, Action Action)> _pending = new();

    public Debouncer(TimeSpan? delay = null)
    {
        _timer = new DispatcherTimer { Interval = delay ?? DefaultDelay };
        _timer.Tick += (_, _) => Flush();
    }

    /// <summary>Programme l'action du réglage <paramref name="key"/> et relance le compte à rebours. Une
    /// action déjà en attente pour la même clé est remplacée : les positions intermédiaires d'un curseur
    /// n'intéressent personne.</summary>
    public void Schedule(string key, Action action)
    {
        int index = _pending.FindIndex(entry => entry.Key == key);
        if (index >= 0) _pending[index] = (key, action);
        else _pending.Add((key, action));

        _timer.Stop();
        _timer.Start();
    }

    /// <summary>Abandonne l'action en attente pour ce réglage — par exemple quand un « rétablir » vient
    /// de remettre la valeur d'origine, qu'une application retardataire écraserait.</summary>
    public void Cancel(string key) => _pending.RemoveAll(entry => entry.Key == key);

    /// <summary>Exécute tout de suite ce qui est en attente. À appeler à la fermeture de l'onglet ou de
    /// l'app, pour ne pas perdre le réglage d'un utilisateur qui quitte juste après avoir lâché un curseur.</summary>
    public void Flush()
    {
        _timer.Stop();
        if (_pending.Count == 0) return;

        var actions = _pending.Select(entry => entry.Action).ToList();
        _pending.Clear();
        foreach (Action action in actions) action();
    }
}
