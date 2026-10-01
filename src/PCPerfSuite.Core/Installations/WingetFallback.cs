using System.Text.RegularExpressions;
using PCPerfSuite.Core.SystemInfo;

namespace PCPerfSuite.Core.Installations;

/// <summary>
/// Repli quand le lien direct d'un installeur signé ne mène plus au fichier vérifié (lien mort, empreinte qui a
/// changé) : « winget install --id … --exact », dans une fenêtre visible, lancé par le shell avec les droits de la
/// personne connectée. winget vérifie lui-même l'empreinte de son manifeste, montre les conditions à accepter, et chaque
/// installeur demande son autorisation à Windows. Jamais proposé pour un outil non signé : il serait lancé.
///
/// winget n'existe pas partout : absent avant la première ouverture de session d'un compte, dans Windows Sandbox et
/// sur certaines éditions LTSC. Il n'est alors pas proposé. Quand PCPerfSuite tourne sous un autre compte que la
/// personne devant l'écran, sa présence chez elle ne peut pas être vérifiée : pas de repli non plus.
/// </summary>
public static partial class WingetFallback
{
    /// <summary>Alias d'exécution de winget pour le compte de l'app, null s'il est absent ou invérifiable.</summary>
    public static string? FindExecutable()
    {
        try
        {
            if (SessionUser.IsOtherProfile) return null;
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WindowsApps", "winget.exe");
            return File.Exists(path) ? path : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Identifiant winget plausible (« CPUID.CPU-Z ») : rien qui puisse passer pour une autre option.</summary>
    public static bool IsValidId(string? id) => id is { Length: > 2 and <= 128 } && IdPattern().IsMatch(id);

    /// <summary>Arguments passés à winget pour cet outil.</summary>
    public static string ArgumentsFor(string wingetId) => $"install --id {wingetId} --exact --source winget";

    /// <summary>Lance winget pour cet outil. Passe par le shell (appel COM hors processus) : hors du thread d'interface.</summary>
    public static ToolActionOutcome Install(ToolDefinition tool)
    {
        if (!tool.IsSigned || !IsValidId(tool.WingetId)) return new ToolActionOutcome(false, "winget n'est pas proposé pour cet outil.");
        if (FindExecutable() is not { } winget) return new ToolActionOutcome(false, "winget n'est pas disponible sur ce compte.");

        return UnelevatedLauncher.TryLaunch(winget, ArgumentsFor(tool.WingetId!), out string? error)
            ? new ToolActionOutcome(true, "winget est lancé dans sa propre fenêtre : suis ses questions, puis reviens ici, l'état se met à jour.")
            : new ToolActionOutcome(false, error ?? "winget n'a pas pu être lancé.");
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9.+_\-]*$")]
    private static partial Regex IdPattern();
}
