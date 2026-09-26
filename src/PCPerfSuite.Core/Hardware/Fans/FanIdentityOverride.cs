using System.Text.Json.Serialization;

namespace PCPerfSuite.Core.Hardware.Fans;

/// <summary>
/// Ce que l'utilisateur a corrigé à la main pour un ventilateur : son nom et sa catégorie. Nécessaire quand le
/// matériel ne donne aucun nom (« Fan #6 »), ou quand plusieurs ventilateurs partagent un connecteur (hub : « Boîtier
/// (hub ×4) »). Un champ absent garde la valeur détectée.
///
/// <see cref="FanId"/> est l'identifiant stable du ventilateur, le même que <see cref="FanCurveConfig.ControlSensorId"/> :
/// identifiant du capteur de commande de la puce (« /lpc/nct6798d/0/control/5 »), « gpu:N » pour un cooler de carte
/// graphique. Il ne change ni d'un lancement à l'autre ni quand on branche ou débranche un ventilateur — c'est ce qui
/// permet de rattacher des réglages à un ventilateur précis.
/// </summary>
public sealed class FanIdentityOverride
{
    public required string FanId { get; init; }

    /// <summary>Nom choisi par l'utilisateur, null pour garder le nom détecté.</summary>
    public string? Name { get; set; }

    /// <summary>Catégorie choisie, sous la forme de <see cref="FanCategoryInfo.Key"/>, null pour garder la détectée.</summary>
    public string? Category { get; set; }

    /// <summary>Plus rien à retenir : l'entrée peut être retirée du fichier.</summary>
    [JsonIgnore]
    public bool IsEmpty => string.IsNullOrWhiteSpace(Name) && Category is null;
}
