using System.IO;
using System.Text;
using PCPerfSuite.Core.SystemInfo;

namespace PCPerfSuite.App.Utils;

/// <summary>
/// Journal des erreurs inattendues, à côté du fichier de réglages.
///
/// PCPerfSuite est destinée à une diffusion publique et son diagnostic « Compatibilité de ce PC » sert
/// de rapport de bug : une pile d'appel écrite sur le disque vaut infiniment mieux qu'un message jeté à
/// l'écran puis perdu. Le fichier est plafonné et repart de zéro quand il déborde — un journal qui
/// remplit le disque de quelqu'un serait pire que pas de journal du tout.
/// </summary>
public static class CrashLog
{
    private static readonly object Gate = new();

    /// <summary>Au-delà, le fichier repart de zéro : de quoi garder plusieurs sessions d'erreurs sans
    /// jamais peser sur le disque.</summary>
    private const long MaxBytes = 512 * 1024;

    /// <summary>Relu à chaque écriture : la racine du dossier de données se choisit au démarrage (mode portable).</summary>
    public static string FilePath => AppDataPaths.Current.CrashLogFile;

    /// <summary>Résumé de la dernière erreur journalisée dans cette session, repris par le diagnostic.
    /// Null tant que rien n'est arrivé.</summary>
    public static string? LastError { get; private set; }

    public static void Record(Exception? exception, string origin)
    {
        if (exception is null) return;

        Write($"{exception.GetType().Name} ({origin}) : {exception.Message}", exception.ToString(), origin,
            surfaceAsLastError: true);
    }

    /// <summary>
    /// Journalise une anomalie qui n'est pas une exception. WPF ne signale la plupart des liaisons de
    /// données cassées que par une trace silencieuse : sans ce chemin, elles n'atteindraient jamais le
    /// rapport de bug.
    ///
    /// <paramref name="surfaceAsLastError"/> décide si l'anomalie a le droit de devenir la « dernière
    /// erreur » du diagnostic. Il n'y a qu'un emplacement : une trace de liaison bénigne, arrivée une
    /// seconde après un vrai plantage, en chasserait le message — précisément celui qu'on demande à
    /// l'utilisateur de coller dans son signalement.
    /// </summary>
    public static void RecordMessage(string message, string origin, bool surfaceAsLastError)
    {
        if (message is not { Length: > 0 }) return;

        Write($"{origin} : {message}", message, origin, surfaceAsLastError);
    }

    private static void Write(string summary, string detail, string origin, bool surfaceAsLastError)
    {
        lock (Gate)
        {
            string path = FilePath;
            if (surfaceAsLastError) LastError = $"{summary}. Détail dans {path}.";

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                // Rotation la plus simple qui tienne : au-delà du plafond, on repart d'un fichier vide.
                // Garder une archive doublerait la place occupée pour un intérêt quasi nul.
                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes) File.Delete(path);

                var text = new StringBuilder();
                text.AppendLine($"===== {DateTime.Now:yyyy-MM-dd HH:mm:ss} — {origin} =====");
                text.AppendLine(detail);
                text.AppendLine();

                File.AppendAllText(path, text.ToString(), Encoding.UTF8);
            }
            catch
            {
                // Journaliser est un service rendu, pas une obligation : si le disque est plein ou le
                // dossier inaccessible, on ne va pas ajouter une erreur à l'erreur.
            }
        }
    }
}
