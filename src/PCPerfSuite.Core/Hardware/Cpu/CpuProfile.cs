namespace PCPerfSuite.Core.Hardware.Cpu;

/// <summary>
/// Jeu de réglages processeur enregistré sous un nom : les réglages d'alimentation Windows (mode boost,
/// états min/max, fréquence maximale, arbitrage performance/économie) et, quand la machine les expose,
/// les limites de puissance en watts.
///
/// Un profil est volontairement tolérant : il peut avoir été écrit sur une autre machine, sur une autre
/// plateforme, ou par une version de l'app qui exposait d'autres réglages. Une entrée qui ne correspond
/// à rien sur ce PC est ignorée à l'application, et signalée plutôt que subie. De même, une valeur nulle
/// veut dire « ne pas toucher à ce réglage ».
/// </summary>
public sealed class CpuProfile
{
    public string Name { get; set; } = "Profil";

    /// <summary>Clé = <see cref="CpuPowerSetting.Id"/> ("boost", "max-state", "epp"…). Des chaînes et non
    /// des GUID ni des enums : settings.json doit rester lisible et modifiable à la main, et un identifiant
    /// inconnu doit pouvoir être ignoré sans casser la relecture du reste.</summary>
    public Dictionary<string, CpuProfilePowerValue> PowerSettings { get; set; } = new();

    /// <summary>Limite soutenue en watts — null si la machine ne la gérait pas au moment de l'enregistrement.</summary>
    public float? SustainedWatts { get; set; }

    /// <summary>Limite de pointe en watts.</summary>
    public float? BurstWatts { get; set; }
}

/// <summary>Les deux valeurs d'un réglage d'alimentation : sur secteur et sur batterie. Sur une machine
/// sans batterie, seule la première a un sens.</summary>
public sealed class CpuProfilePowerValue
{
    public uint? Ac { get; set; }
    public uint? Battery { get; set; }
}
