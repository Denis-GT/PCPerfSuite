namespace PCPerfSuite.Core.Compatibility;

/// <summary>
/// Pourquoi une donnée manque sur ce PC (règle 3 du CLAUDE.md) : chaque lecture « N/D » porte l'une de ces causes et
/// un texte qui la dit en clair. Le diagnostic (#12) s'en sert pour ranger ce qui n'est pas mesurable.
/// </summary>
public enum UnavailableCause
{
    /// <summary>Le matériel ou son pilote ne fournit pas la donnée (ex. aucune raison de bridage chez AMD).</summary>
    HardwareOrDriver,

    /// <summary>La marque, le modèle ou la version n'est pas encore pris en charge par PCPerfSuite (ex. version de PM
    /// table inconnue).</summary>
    UnsupportedModel,

    /// <summary>L'app n'a pas les droits : lancée sans administrateur, ou pilote PawnIO absent ou refusé.</summary>
    MissingRights,
}

/// <summary>Une absence expliquée : sa cause et le texte affiché.</summary>
public sealed record Unavailable(UnavailableCause Cause, string Reason);
