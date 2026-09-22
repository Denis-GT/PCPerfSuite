namespace PCPerfSuite.Core.Hardware.Gpu;

/// <summary>
/// Garde-fou contre le seul plantage dont l'app ne peut pas se relever.
///
/// ADLX s'appelle par emplacement dans des tables de fonctions COM, dont les index sont recopiés des
/// en-têtes du SDK d'AMD. Si un pilote Adrenalin réordonne une interface, l'appel part dans de la mémoire
/// arbitraire : violation d'accès, que .NET ne laisse pas intercepter — un try/catch ne la voit même pas.
/// Le processus meurt d'un coup, et l'app ne démarre plus du tout tant que la carte et le pilote sont là.
///
/// On ne peut donc pas rattraper ce plantage. On peut seulement refuser de le rejouer : un témoin est posé
/// sur le disque juste avant la tentative et retiré juste après. Le retrouver au lancement suivant ne peut
/// vouloir dire qu'une chose — la tentative précédente n'est jamais revenue — et ADLX n'est alors plus
/// interrogé. L'app démarre, le contrôle GPU s'affiche indisponible, et le diagnostic dit pourquoi.
///
/// Le témoin est volontairement un fichier à part, et non un champ des réglages : il doit survivre à un
/// arrêt brutal au milieu de l'écriture, sans risquer d'emporter les réglages de l'utilisateur avec lui.
/// </summary>
public static class AdlxProbeGuard
{
    private static readonly string SentinelPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PCPerfSuite", "adlx-plantage.temoin");

    private static bool? _previousAttemptCrashed;

    /// <summary>Vrai si la tentative précédente n'est pas revenue. Évalué une seule fois, avant que cette
    /// session ne pose son propre témoin — sans quoi elle se prendrait elle-même pour la précédente.</summary>
    public static bool PreviousAttemptCrashed => _previousAttemptCrashed ??= SentinelExists();

    /// <summary>Chemin du témoin, donné dans le diagnostic : le supprimer autorise une nouvelle tentative,
    /// ce qui a du sens après une mise à jour du pilote.</summary>
    public static string SentinelFilePath => SentinelPath;

    /// <summary>Exécute la tentative d'initialisation sous témoin. Ne tente rien — et renvoie false — si la
    /// précédente n'est pas revenue.</summary>
    public static bool RunGuarded(Func<bool> probe)
    {
        if (PreviousAttemptCrashed) return false;

        try
        {
            Place();
            return probe();
        }
        finally
        {
            // Ce bloc ne s'exécute justement PAS après une violation d'accès : le témoin reste, et c'est
            // tout l'intérêt.
            Remove();
        }
    }

    private static bool SentinelExists()
    {
        try
        {
            return File.Exists(SentinelPath);
        }
        catch
        {
            // Sans certitude, on laisse la tentative avoir lieu : refuser le contrôle GPU à cause d'un
            // disque capricieux serait pire que le risque qu'on cherche à couvrir.
            return false;
        }
    }

    private static void Place()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SentinelPath)!);
            File.WriteAllText(SentinelPath, DateTime.Now.ToString("O"));
        }
        catch
        {
            // Pas de témoin possible : on tente quand même, comme avant l'existence de ce garde-fou.
        }
    }

    private static void Remove()
    {
        try
        {
            if (File.Exists(SentinelPath)) File.Delete(SentinelPath);
        }
        catch
        {
            // Un témoin qu'on n'arrive pas à retirer privera la session suivante du contrôle GPU. C'est le
            // sens d'erreur le moins coûteux des deux.
        }
    }
}
