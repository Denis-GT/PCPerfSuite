using PCPerfSuite.Core.Hardware;

namespace PCPerfSuite.Core.Profiles;

/// <summary>
/// Configurations de démarrage mises de côté pendant qu'un groupe est appliqué « sans en faire l'état de démarrage »
/// (bascule automatique, #9). L'onglet Ventilateurs écrit dans settings.json ses configurations vivantes : sans ce
/// mémo, l'état transitoire deviendrait celui du prochain lancement.
///
/// Pour chaque ventilateur touché, la configuration d'avant est gardée (la plus ancienne : deux applications
/// transitoires de suite ne la remplacent pas) ; ce qui part sur le disque est cette copie, à la place de la
/// configuration vivante. Une modification à la main du ventilateur, ou une application qui fait l'état de démarrage,
/// retire sa copie : la configuration vivante redevient celle du démarrage.
/// </summary>
public sealed class FanStartupOverrides
{
    private readonly Dictionary<string, FanCurveConfig> _startup = new(StringComparer.Ordinal);

    public bool IsEmpty => _startup.Count == 0;

    public IReadOnlyCollection<string> FanIds => _startup.Keys;

    /// <summary>Met de côté la configuration de démarrage de ce ventilateur, si ce n'est pas déjà fait.</summary>
    public void Hold(string fanId, FanCurveConfig startupConfig) => _startup.TryAdd(fanId, startupConfig.Clone());

    /// <summary>Ce ventilateur a été modifié à la main, ou appliqué comme état de démarrage.</summary>
    public bool Release(string fanId) => _startup.Remove(fanId);

    public void Clear() => _startup.Clear();

    /// <summary>Ce qui part sur le disque : les configurations vivantes, sauf celles mises de côté.</summary>
    public List<FanCurveConfig> Resolve(IEnumerable<FanCurveConfig> live)
        => live.Select(config => _startup.TryGetValue(config.ControlSensorId, out FanCurveConfig? held) ? held.Clone() : config).ToList();
}
