using PCPerfSuite.App.Metrics;

namespace PCPerfSuite.App.Overlay;

/// <summary>
/// Couleur des libellés de catégorie : ce qui est enregistré dans settings.json, et ce qui n'est en fait qu'un
/// ancien défaut.
///
/// Jusqu'ici, l'app enregistrait la couleur de <em>toutes</em> les catégories, y compris celles que personne
/// n'avait touchées. Un nouveau défaut n'aurait donc atteint aucun réglage existant : chaque couleur enregistrée
/// passerait pour un choix. <see cref="Resolve"/> reconnaît ces anciens défauts et les remplace par le défaut
/// actuel ; l'app n'enregistre désormais que les couleurs réellement changées.
/// </summary>
public static class OverlayColorDefaults
{
    /// <summary>Défauts de l'overlay après le premier assombrissement (avant lui, c'était
    /// <see cref="MetricCategory.DefaultColor"/>). À compléter à chaque nouveau changement de défaut : la valeur
    /// remplacée y va, pour qu'elle ne compte pas comme un choix de l'utilisateur.</summary>
    private static readonly IReadOnlyDictionary<string, string> FirstDarkening = new Dictionary<string, string>
    {
        ["cpu"] = "#2FA3E0",
        ["gpu"] = "#4CB85F",
        ["ram"] = "#9A62E0",
        ["mb"] = "#E0932B",
        ["storage"] = "#E0B030",
        ["net"] = "#25B5A0",
        ["game"] = "#E0587A",
        ["sys"] = "#8C97B3",
        ["power"] = "#E0C030",
    };

    /// <summary>La couleur à afficher pour une catégorie : celle qu'a enregistrée l'utilisateur, sauf si ce n'est
    /// que le défaut d'une version précédente, auquel cas c'est le défaut actuel.</summary>
    public static string Resolve(MetricCategory category, string? saved)
    {
        if (string.IsNullOrWhiteSpace(saved)) return category.OverlayColor;
        return IsFormerDefault(category, saved) ? category.OverlayColor : saved;
    }

    /// <summary>Vrai si <paramref name="hex"/> est le défaut actuel de la catégorie ou l'un de ses anciens défauts.</summary>
    private static bool IsFormerDefault(MetricCategory category, string hex)
    {
        if (SameColor(hex, category.OverlayColor) || SameColor(hex, category.DefaultColor)) return true;
        return FirstDarkening.TryGetValue(category.Key, out string? former) && SameColor(hex, former);
    }

    /// <summary>Même couleur, quelle que soit l'écriture (« #2fa3e0 », « 2FA3E0 », « FF2FA3E0 »). Deux chaînes qui
    /// ne sont pas des couleurs se comparent telles quelles.</summary>
    public static bool SameColor(string? a, string? b)
    {
        if (OverlayPalette.TryParse(a, out var first) && OverlayPalette.TryParse(b, out var second)) return first == second;
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
