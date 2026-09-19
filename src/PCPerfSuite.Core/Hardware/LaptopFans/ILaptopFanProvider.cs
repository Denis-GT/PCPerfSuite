namespace PCPerfSuite.Core.Hardware.LaptopFans;

/// <summary>Rôle d'un ventilateur de portable, quand l'interface du constructeur le précise.</summary>
public enum LaptopFanRole
{
    Cpu,
    Gpu,
    Other,
}

/// <summary>Vitesse d'un ventilateur lue dans le contrôleur embarqué du portable.</summary>
public sealed class LaptopFanReading
{
    /// <summary>Clé stable dans le module (cpu, gpu, mid, fan1...), reprise dans l'identifiant du capteur.</summary>
    public required string Key { get; init; }

    public required string Name { get; init; }
    public LaptopFanRole Role { get; init; }
    public float? Rpm { get; init; }

    /// <summary>% de la vitesse maximale, seulement quand l'interface permet de le calculer ; null sinon.</summary>
    public float? Percent { get; init; }
}

/// <summary>
/// Lecture des ventilateurs d'une marque de portables via l'interface WMI/ACPI de son contrôleur embarqué
/// (celle qu'utilise l'utilitaire du constructeur). En lecture seule : on ne pilote jamais le contrôleur
/// embarqué d'un portable, une mauvaise écriture pouvant laisser la machine sans refroidissement.
/// </summary>
internal interface ILaptopFanProvider
{
    string Vendor { get; }

    /// <summary>Vrai quand le décodage a été vérifié sur une vraie machine de cette marque ; faux pour une
    /// interface reprise des sources de référence mais pas encore confirmée (affichée "expérimental").</summary>
    bool IsVerified { get; }

    /// <summary>Vrai si l'interface existe et répond sur cette machine. Appelé une seule fois.</summary>
    bool TryDetect();

    /// <summary>Relit les ventilateurs. Peut lever : l'appelant garde alors les valeurs précédentes.</summary>
    IReadOnlyList<LaptopFanReading> Read();
}
