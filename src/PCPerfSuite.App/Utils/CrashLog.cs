using System.IO;
using System.Text;

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

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PCPerfSuite", "erreurs.log");

    /// <summary>Résumé de la dernière erreur journalisée dans cette session, repris par le diagnostic.
    /// Null tant que rien n'est arrivé.</summary>
    public static string? LastError { get; private set; }

    public static void Record(Exception? exception, string origin)
    {
        if (exception is null) return;

        lock (Gate)
        {
            LastError = $"{exception.GetType().Name} ({origin}) : {exception.Message}. Détail dans {FilePath}.";

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

                // Rotation la plus simple qui tienne : au-delà du plafond, on repart d'un fichier vide.
                // Garder une archive doublerait la place occupée pour un intérêt quasi nul.
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > MaxBytes) File.Delete(FilePath);

                var text = new StringBuilder();
                text.AppendLine($"===== {DateTime.Now:yyyy-MM-dd HH:mm:ss} — {origin} =====");
                text.AppendLine(exception.ToString());
                text.AppendLine();

                File.AppendAllText(FilePath, text.ToString(), Encoding.UTF8);
            }
            catch
            {
                // Journaliser est un service rendu, pas une obligation : si le disque est plein ou le
                // dossier inaccessible, on ne va pas ajouter une erreur à l'erreur.
            }
        }
    }
}
