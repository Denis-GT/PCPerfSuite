using System.Globalization;

namespace PCPerfSuite.Core.Hardware.Gpu;

/// <summary>
/// Garde-fou contre le seul plantage dont l'app ne peut pas se relever.
///
/// ADLX s'appelle par emplacement dans des tables de fonctions COM, dont les index sont recopiés des
/// en-têtes du SDK d'AMD. Si un pilote Adrenalin réordonne une interface, l'appel part dans de la mémoire
/// arbitraire : violation d'accès, que .NET ne laisse pas intercepter — un try/catch ne la voit même pas.
/// Le processus meurt d'un coup, et l'app ne démarre plus du tout tant que la carte et le pilote sont là.
///
/// On ne peut donc pas rattraper ce plantage. On peut seulement refuser de le rejouer indéfiniment : un
/// témoin est posé sur le disque juste avant la tentative et retiré juste après. Le retrouver au lancement
/// suivant signifie que la tentative précédente n'est pas revenue.
///
/// Mais « pas revenue » ne veut pas dire « ADLX a planté » : le processus a aussi pu être tué depuis le
/// Gestionnaire des tâches, ou Windows redémarrer pour une mise à jour, pendant les quelques centaines de
/// millisecondes que dure le sondage. Condamner le contrôle GPU sur un seul non-retour accuserait le
/// pilote AMD pour un arrêt qui n'a rien à voir, définitivement et sur une machine parfaitement saine.
/// D'où un compteur : il faut <see cref="AttemptsBeforeGivingUp"/> non-retours CONSÉCUTIFS pour renoncer.
/// Une violation d'accès due à des emplacements décalés se reproduit à chaque lancement — elle atteint
/// donc le seuil — alors qu'un arrêt accidental ne se reproduit pas, et le premier sondage qui aboutit
/// remet le compteur à zéro.
///
/// PORTÉE : seule l'INITIALISATION est couverte. Les appels d'écriture (limite de puissance, décalages
/// d'horloge, courbe de ventilation) et la restauration faite à la fermeture exercent d'autres
/// emplacements, hors témoin. Les y étendre coûterait une écriture disque à chaque action de
/// l'utilisateur ; le risque résiduel est un pilote qui décalerait un emplacement de réglage sans décaler
/// aucun de ceux lus au démarrage.
///
/// Le témoin est volontairement un fichier à part, et non un champ des réglages : il doit survivre à un
/// arrêt brutal au milieu de l'écriture, sans risquer d'emporter les réglages de l'utilisateur avec lui.
/// </summary>
public static class AdlxProbeGuard
{
    private static readonly string SentinelPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PCPerfSuite", "adlx-plantage.temoin");

    /// <summary>Deux : de quoi absorber un arrêt accidentel sans laisser un vrai plantage se rejouer plus
    /// d'une fois de trop.</summary>
    private const int AttemptsBeforeGivingUp = 2;

    private static int? _recordedFailures;

    /// <summary>Non-retours déjà enregistrés. Évalué une seule fois, avant que cette session ne pose son
    /// propre témoin — sans quoi elle se compterait elle-même.</summary>
    private static int RecordedFailures => _recordedFailures ??= ReadFailures();

    /// <summary>Vrai quand le seuil est atteint : ADLX n'est plus interrogé du tout.</summary>
    public static bool PreviousAttemptCrashed => RecordedFailures >= AttemptsBeforeGivingUp;

    /// <summary>Chemin du témoin, donné dans le diagnostic : le supprimer rend sa chance à ADLX, ce qui a
    /// du sens après une mise à jour du pilote.</summary>
    public static string SentinelFilePath => SentinelPath;

    /// <summary>Exécute la tentative d'initialisation sous témoin. Ne tente rien — et renvoie false — une
    /// fois le seuil atteint.</summary>
    public static bool RunGuarded(Func<bool> probe)
    {
        if (PreviousAttemptCrashed) return false;

        try
        {
            Place(RecordedFailures + 1);
            return probe();
        }
        finally
        {
            // Ce bloc ne s'exécute justement PAS après une violation d'accès : le témoin reste, et c'est
            // tout l'intérêt. Un sondage qui revient, même en échec, prouve qu'ADLX n'a tué personne.
            Remove();
        }
    }

    private static int ReadFailures()
    {
        try
        {
            if (!File.Exists(SentinelPath)) return 0;

            // Un témoin illisible ou écrit par une version antérieure compte pour un : il atteste bien un
            // non-retour, sans prouver qu'il y en a eu deux.
            return int.TryParse(File.ReadAllText(SentinelPath), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int failures) && failures > 0
                ? failures
                : 1;
        }
        catch
        {
            // Sans certitude, on laisse la tentative avoir lieu : refuser le contrôle GPU à cause d'un
            // disque capricieux serait pire que le risque qu'on cherche à couvrir.
            return 0;
        }
    }

    private static void Place(int failures)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SentinelPath)!);
            File.WriteAllText(SentinelPath, failures.ToString(CultureInfo.InvariantCulture));
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
            // Un témoin qu'on n'arrive pas à retirer coûtera une tentative à la session suivante. C'est le
            // sens d'erreur le moins coûteux des deux.
        }
    }
}
