using System.Globalization;

namespace PCPerfSuite.App.Utils;

/// <summary>
/// Recherche « comme on tape » : insensible à la casse et aux accents, pour qu'un « telemetrie » trouve
/// « Télémétrie ». Passe par CompareInfo plutôt que par string.Contains, seul moyen d'ignorer les signes
/// diacritiques sans normaliser les chaînes à chaque frappe.
/// </summary>
public static class TextSearch
{
    private const CompareOptions Options = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;

    /// <summary>Vrai si <paramref name="haystack"/> contient <paramref name="needle"/>. Une recherche vide
    /// laisse tout passer : c'est l'état d'un champ de recherche qu'on vient d'effacer.</summary>
    public static bool Contains(string? haystack, string? needle)
    {
        if (string.IsNullOrEmpty(needle)) return true;
        if (string.IsNullOrEmpty(haystack)) return false;

        // La culture est relue à chaque appel plutôt que capturée dans un champ statique : la figer au
        // chargement du type la laisserait fausse si la culture de l'app changeait ensuite.
        return CultureInfo.CurrentCulture.CompareInfo.IndexOf(haystack, needle, Options) >= 0;
    }
}
