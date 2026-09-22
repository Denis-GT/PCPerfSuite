using System.Diagnostics;
using System.Globalization;

namespace PCPerfSuite.App.Utils;

/// <summary>
/// Écoute les erreurs de liaison de données de WPF et les écrit dans <see cref="CrashLog"/>.
///
/// WPF ne lève que sur une poignée de liaisons fautives — une liaison TwoWay vers une propriété sans
/// setter, par exemple. Toutes les autres (chemin introuvable, convertisseur en échec, DataContext
/// inattendu) se contentent d'une trace que personne ne lit en dehors de Visual Studio : la valeur
/// n'apparaît pas à l'écran, et rien n'explique pourquoi. Sur une app diffusée publiquement, dont le
/// diagnostic sert de rapport de bug, c'est justement ce qu'il faut pouvoir constater à distance.
///
/// Chaque message n'est journalisé qu'une fois par session : une liaison cassée dans un modèle de liste
/// se reproduit à chaque ligne et à chaque relevé, et remplirait le journal en quelques secondes.
/// </summary>
public sealed class BindingErrorListener : TraceListener
{
    private readonly HashSet<string> _seen = new();

    public override void TraceEvent(TraceEventCache? cache, string source, TraceEventType type, int id, string? message)
    {
        if (message is not { Length: > 0 }) return;

        lock (_seen)
        {
            if (!_seen.Add(message)) return;
        }

        CrashLog.RecordMessage(message, "liaison de données");
    }

    public override void TraceEvent(TraceEventCache? cache, string source, TraceEventType type, int id,
        string? format, params object?[]? args)
    {
        TraceEvent(cache, source, type, id, Compose(format, args));
    }

    /// <summary>Le format vient de WPF, mais les arguments viennent des données : une valeur inattendue ne
    /// doit pas faire échouer la journalisation de l'erreur qu'on essaie justement de noter.</summary>
    private static string? Compose(string? format, object?[]? args)
    {
        if (format is null || args is null || args.Length == 0) return format;

        try
        {
            return string.Format(CultureInfo.InvariantCulture, format, args);
        }
        catch (FormatException)
        {
            return format;
        }
    }

    // Les traces de WPF arrivent toutes par TraceEvent ; Write/WriteLine ne servent qu'à l'en-tête, dont
    // le contenu est déjà dans le message.
    public override void Write(string? message) { }

    public override void WriteLine(string? message) { }
}
