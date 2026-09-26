namespace PCPerfSuite.Core.Hardware.Cpu;

/// <summary>
/// Ce qu'une fonction de réglage vaut sur CE PC : lisible, modifiable, et sinon pourquoi. La raison est
/// affichée telle quelle à l'utilisateur — l'app doit dire "N/D parce que…" plutôt que de masquer une
/// fonction sans explication, ou de laisser croire à une panne.
/// </summary>
public sealed record CpuCapability(bool CanRead, bool CanWrite, string? Reason)
{
    public static CpuCapability Full { get; } = new(true, true, null);
    public static CpuCapability ReadOnly(string reason) => new(true, false, reason);
    public static CpuCapability No(string reason) => new(false, false, reason);
}

/// <summary>
/// Les limites de puissance du processeur, en watts. Le vocabulaire diffère selon le fabricant mais la
/// mécanique est la même : une limite soutenue, tenue indéfiniment, et une limite courte durée, tolérée
/// le temps que le processeur encaisse une pointe de charge.
///
/// Intel : PL1 (soutenue) et PL2 (courte durée). AMD : PPT (soutenue) et, sur les portables, la
/// "fast limit" qui joue le même rôle que PL2.
/// </summary>
public sealed class CpuPowerLimitSnapshot
{
    public required float SustainedWatts { get; init; }

    /// <summary>Null quand le processeur n'expose qu'une seule limite.</summary>
    public float? BurstWatts { get; init; }

    /// <summary>Valeurs relevées avant toute écriture de l'app : ce à quoi "Rétablir" revient.</summary>
    public required float DefaultSustainedWatts { get; init; }
    public float? DefaultBurstWatts { get; init; }

    /// <summary>Bornes proposées à l'utilisateur, déjà bridées par les garde-fous du backend.</summary>
    public required float MinWatts { get; init; }
    public required float MaxWatts { get; init; }

    /// <summary>D'où vient <see cref="MaxWatts"/> : la limite du processeur lui-même, ou un repli — et dans ce
    /// cas pourquoi. Affiché à côté des champs et dans le diagnostic de compatibilité.</summary>
    public required CpuMaxWattsInfo MaxWattsInfo { get; init; }
}

/// <summary>
/// Réglage bas niveau du processeur, propre à chaque fabricant : un backend Intel (registres MSR), un
/// backend AMD (SMU), et un backend "indisponible" pour tout le reste (Snapdragon, PawnIO absent…).
///
/// Aucune méthode ne lève : un refus du pilote ou du processeur est une information à afficher, pas une
/// erreur à faire remonter. Les implémentations bornent aussi toutes les valeurs écrites : c'est le
/// dernier rempart avant le matériel, il ne doit pas dépendre de l'interface qui appelle.
/// </summary>
public interface ICpuTuningBackend : IDisposable
{
    /// <summary>Comment ce backend parle au processeur, affiché dans la carte "Compatibilité".</summary>
    string Description { get; }

    /// <summary>Disponibilité de la limite de puissance en watts.</summary>
    CpuCapability PowerLimit { get; }

    /// <summary>Relit les limites de puissance. Null si elles ne sont pas lisibles sur ce PC.</summary>
    CpuPowerLimitSnapshot? ReadPowerLimits();

    /// <summary>
    /// Applique une limite soutenue (et une limite courte durée quand le processeur en a une). Retourne
    /// false avec un message expliquant le refus. Les valeurs sont bornées par le backend, puis relues
    /// pour vérifier que le processeur a bien retenu la consigne : un SMU ou un MSR peut accepter une
    /// écriture et n'en rien faire.
    /// </summary>
    bool TrySetPowerLimits(float sustainedWatts, float? burstWatts, out string message);

    /// <summary>Remet les limites relevées au premier accès, c'est-à-dire celles d'avant l'app.</summary>
    bool TryRestoreDefaults(out string message);
}

/// <summary>
/// Backend des plateformes sans réglage bas niveau possible : Snapdragon (fréquence et tension
/// verrouillées par le firmware Qualcomm, aucune API publique), processeur non reconnu, ou pilote PawnIO
/// absent. Il ne fait rien, mais porte la raison exacte à afficher.
/// </summary>
public sealed class UnsupportedCpuBackend : ICpuTuningBackend
{
    private readonly string _reason;

    public UnsupportedCpuBackend(string reason) => _reason = reason;

    public string Description => _reason;

    public CpuCapability PowerLimit => CpuCapability.No(_reason);

    public CpuPowerLimitSnapshot? ReadPowerLimits() => null;

    public bool TrySetPowerLimits(float sustainedWatts, float? burstWatts, out string message)
    {
        message = _reason;
        return false;
    }

    public bool TryRestoreDefaults(out string message)
    {
        message = _reason;
        return false;
    }

    public void Dispose()
    {
    }
}
