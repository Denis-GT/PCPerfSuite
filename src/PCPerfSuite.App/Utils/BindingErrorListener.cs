using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

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
/// Chaque liaison fautive n'est journalisée qu'une fois par session : une liaison cassée dans un modèle
/// de liste se reproduit à chaque ligne et à chaque relevé, et remplirait le journal en quelques secondes.
/// </summary>
public sealed class BindingErrorListener : TraceListener
{
    /// <summary>WPF nomme l'objet lié par son empreinte — « DataItem='ProcessRowViewModel'
    /// (HashCode=54637462) » — et elle change à chaque instance. Sans la neutraliser, la MÊME liaison
    /// cassée paraît neuve à chaque ligne d'une liste, et la déduplication ne dédupliquerait rien.</summary>
    private static readonly Regex InstanceStamp = new(@"HashCode=-?\d+", RegexOptions.Compiled);

    /// <summary>Plafond de sécurité : au-delà, on cesse de journaliser plutôt que de laisser un motif
    /// imprévu faire grossir l'ensemble — et le fichier — sans fin.</summary>
    private const int MaxDistinct = 50;

    private readonly HashSet<string> _seen = new();

    public override void TraceEvent(TraceEventCache? cache, string source, TraceEventType type, int id, string? message)
    {
        if (message is not { Length: > 0 }) return;

        lock (_seen)
        {
            if (_seen.Count >= MaxDistinct) return;
            if (!_seen.Add(InstanceStamp.Replace(message, "HashCode=*"))) return;
        }

        // Pas de remontée en « dernière erreur » du diagnostic : une liaison fautive est utile dans le
        // journal, mais elle ne doit pas masquer le plantage qu'on cherche.
        CrashLog.RecordMessage(message, "liaison de données", surfaceAsLastError: false);
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
