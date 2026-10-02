using PCPerfSuite.Core.Hardware;

namespace PCPerfSuite.App.ViewModels;

/// <summary>
/// Fenêtre cachée, un consommateur qui n'a besoin de ses capteurs que de loin en loin (la bascule automatique : toutes
/// les 5 s) ne les demande qu'à ce rythme, pour que le relevé retombe entre-temps sur son rythme de repos. Le délai ne
/// repart qu'une fois relus TOUS les groupes qu'il a demandés, même à des relevés différents (un groupe lent est espacé) :
/// un autre consommateur qui relit l'un d'eux chaque seconde (l'overlay et la charge CPU) ne doit pas le faire repartir,
/// sinon les autres (batterie, FPS) ne seraient plus jamais demandés. Un groupe que ce PC n'a encore jamais lu (aucun GPU
/// vu par le relevé) n'est pas attendu : il ne doit pas annuler le mode éco (règle 1). Sur le fil d'interface.
/// </summary>
public sealed class EcoSampleGate
{
    private readonly TimeSpan _interval;
    private readonly HashSet<SensorGroup> _readSince = [];
    private readonly HashSet<SensorGroup> _everRead = [];
    private HashSet<SensorGroup> _requested = [];
    private DateTimeOffset? _lastSampleUtc;

    public EcoSampleGate(TimeSpan interval) => _interval = interval;

    /// <summary>Vrai s'il est temps de demander les groupes.</summary>
    public bool IsDue(DateTimeOffset now) => _lastSampleUtc is not { } last || now - last >= _interval || now < last;

    /// <summary>Les groupes demandés pour ce relevé-ci.</summary>
    public void Requested(IEnumerable<SensorGroup> groups) => _requested = new HashSet<SensorGroup>(groups);

    /// <summary>Les groupes d'un relevé, fenêtre affichée ou cachée : ce que ce PC sait lire.</summary>
    public void Saw(IEnumerable<SensorGroup> read) => _everRead.UnionWith(read);

    /// <summary>Un relevé : une fois relus tous les groupes demandés depuis le dernier échantillon, le délai repart.</summary>
    public void OnRead(IEnumerable<SensorGroup> read, DateTimeOffset now)
    {
        if (_requested.Count == 0) return;

        Saw(read);
        _readSince.UnionWith(read);
        if (!_requested.Where(_everRead.Contains).All(_readSince.Contains)) return;

        _lastSampleUtc = now;
        _readSince.Clear();
    }
}
