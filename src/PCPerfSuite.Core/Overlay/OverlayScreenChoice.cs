using PCPerfSuite.Core.Hardware.Displays;

namespace PCPerfSuite.Core.Overlay;

/// <summary>Sur quel écran s'affiche l'overlay fenêtre.</summary>
public enum OverlayScreenMode
{
    /// <summary>L'écran principal de Windows : le comportement historique, et le défaut.</summary>
    Primary,

    /// <summary>Un écran choisi dans la liste.</summary>
    Fixed,

    /// <summary>L'écran de la fenêtre au premier plan (le jeu), hors fenêtres du shell et de PCPerfSuite.</summary>
    Game,
}

/// <summary>Écran retenu pour l'overlay, et ce qu'il faut en dire à l'utilisateur (repli, écran absent).
/// <paramref name="Monitor"/> est null seulement quand aucun écran n'a pu être lu.</summary>
public sealed record OverlayScreenTarget(DisplayMonitor? Monitor, string? Message);

/// <summary>Choix de l'écran de l'overlay, isolé ici pour être testé sans écran.</summary>
public static class OverlayScreenChoice
{
    public const string PrimaryKey = "primary";
    public const string FixedKey = "fixed";
    public const string GameKey = "game";

    /// <summary>Mode enregistré en chaîne (settings.json lisible, et rien ne se réinterprète si l'énumération
    /// grandit). Null ou inconnu : l'écran principal, comme les anciens fichiers.</summary>
    public static OverlayScreenMode ParseMode(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        FixedKey => OverlayScreenMode.Fixed,
        GameKey => OverlayScreenMode.Game,
        _ => OverlayScreenMode.Primary,
    };

    /// <summary>Chaîne à enregistrer ; null pour l'écran principal, qui est le défaut.</summary>
    public static string? ToSetting(OverlayScreenMode mode) => mode switch
    {
        OverlayScreenMode.Fixed => FixedKey,
        OverlayScreenMode.Game => GameKey,
        _ => null,
    };

    /// <param name="saved">Écran choisi (mode <see cref="OverlayScreenMode.Fixed"/>), null s'il n'y en a pas encore.</param>
    /// <param name="gameMonitor">Écran de la dernière fenêtre de jeu vue au premier plan (mode
    /// <see cref="OverlayScreenMode.Game"/>), null tant qu'aucune n'a été vue ou si elle n'est plus dans l'instantané.</param>
    public static OverlayScreenTarget Resolve(OverlayScreenMode mode, DisplayIdentity? saved, DisplayTopologySnapshot snapshot,
        IReadOnlyDictionary<string, string> serialHashes, DisplayMonitor? gameMonitor)
    {
        DisplayMonitor? primary = snapshot.Primary;
        if (primary is null)
        {
            string reason = snapshot.Problem is { } problem ? $" ({problem})" : "";
            return new OverlayScreenTarget(null,
                $"La liste des écrans n'a pas pu être lue sur ce PC{reason} : l'overlay reste sur l'écran principal.");
        }

        switch (mode)
        {
            case OverlayScreenMode.Game:
                return new OverlayScreenTarget(gameMonitor ?? primary, null);

            case OverlayScreenMode.Fixed when saved is null:
                return new OverlayScreenTarget(primary, "Choisis un écran dans la liste : en attendant, l'overlay s'affiche sur l'écran principal.");

            case OverlayScreenMode.Fixed:
                DisplayResolution resolution = DisplayIdentityResolver.Resolve(saved, snapshot, serialHashes);
                if (resolution.Monitor is { } found) return new OverlayScreenTarget(found, null);

                string name = string.IsNullOrWhiteSpace(saved.FriendlyName) ? "choisi" : $"« {saved.FriendlyName} »";
                return new OverlayScreenTarget(primary, resolution.Match == DisplayMatch.Ambiguous
                    ? $"Plusieurs écrans identiques à l'écran {name} sont branchés, et rien ne dit lequel était choisi : "
                      + "l'overlay s'affiche sur l'écran principal. Choisis de nouveau l'écran dans la liste."
                    : $"L'écran {name} n'est pas branché : l'overlay s'affiche sur l'écran principal en attendant, et le retrouvera dès qu'il sera rebranché.");

            default:
                return new OverlayScreenTarget(primary, null);
        }
    }
}
