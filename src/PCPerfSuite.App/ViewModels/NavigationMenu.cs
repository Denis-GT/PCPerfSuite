namespace PCPerfSuite.App.ViewModels;

/// <summary>Sections de la barre latérale, dans l'ordre d'affichage. <see cref="Footer"/> : hors de la liste
/// (Paramètres, en bas).</summary>
public enum NavSection
{
    Monitor,
    Tune,
    Diagnose,
    Tools,
    Footer,
}

/// <summary>Page du menu : clé stable (<see cref="PageKeys"/>), section, titre affiché et glyphe de Segoe Fluent Icons.
/// <paramref name="LaptopOnly"/> : page sans objet sur un PC de bureau, retirée du menu sur un PC de bureau avéré (elle reste
/// sur un portable et sur un châssis indéterminé, où la page dit elle-même ce qu'elle trouve).</summary>
public sealed record NavPage(string Key, NavSection Section, string Title, string Icon, bool LaptopOnly = false);

/// <summary>Entrée de la navigation latérale : une page du menu et le ViewModel qui la sert.</summary>
public sealed record NavEntry(NavPage Page, object ViewModel)
{
    public string Key => Page.Key;
    public string Title => Page.Title;
    public string Icon => Page.Icon;

    /// <summary>En-tête sous lequel la barre latérale range l'entrée.</summary>
    public string SectionTitle => NavigationMenu.SectionTitle(Page.Section);

    /// <summary>Page pas encore livrée : ComingSoonView l'affiche avec ce contenu. Null pour une vraie page.</summary>
    public ComingSoonViewModel? ComingSoon => ViewModel as ComingSoonViewModel;

    public bool IsComingSoon => ComingSoon is not null;
}

/// <summary>
/// Le menu de la barre latérale : toutes les pages, livrées ou à venir, dans l'ordre d'affichage. La barre les range
/// par section (en-têtes Surveiller, Régler, Diagnostiquer, Outils) ; une page qui grossit reçoit des sous-onglets
/// (PillSelector) plutôt qu'une entrée de plus. Recette pour ajouter ou livrer une page : docs/navigation.md.
/// </summary>
public static class NavigationMenu
{
    /// <summary>Au-delà, le titre serait tronqué dans la barre latérale (environ 150 px en 13,5 px).</summary>
    public const int MaxTitleLength = 20;

    public static IReadOnlyList<NavPage> Pages { get; } = new NavPage[]
    {
        new(PageKeys.Monitoring, NavSection.Monitor, "Monitoring", Glyph(0xE9D9)),
        new(PageKeys.Processes, NavSection.Monitor, "Processus", Glyph(0xE9F5)),
        new(PageKeys.Overlay, NavSection.Monitor, "Overlay", Glyph(0xE7FC)),

        new(PageKeys.Cpu, NavSection.Tune, "Processeur", Glyph(0xE964)),
        new(PageKeys.Gpu, NavSection.Tune, "GPU", Glyph(0xE950)),
        new(PageKeys.Fans, NavSection.Tune, "Ventilateurs", Glyph(0xE9CA)),
        new(PageKeys.Profiles, NavSection.Tune, "Profils", Glyph(0xE8F1)),
        new(PageKeys.AutoOverclock, NavSection.Tune, "OC automatique", Glyph(0xE945)),
        new(PageKeys.Displays, NavSection.Tune, "Écrans", Glyph(0xE7F4)),
        new(PageKeys.Lighting, NavSection.Tune, "Éclairage", Glyph(0xE790)),
        new(PageKeys.LaptopGpu, NavSection.Tune, "GPU portable", Glyph(0xE7F8), LaptopOnly: true),

        new(PageKeys.BenchDiagnostic, NavSection.Diagnose, "Bench et diagnostic", Glyph(0xE916)),

        new(PageKeys.Optimization, NavSection.Tools, "Optimisation Windows", Glyph(0xEC4A)),
        new(PageKeys.Cleanup, NavSection.Tools, "Nettoyage", Glyph(0xE74D)),
        new(PageKeys.Storage, NavSection.Tools, "Stockage", Glyph(0xEDA2)),
        new(PageKeys.Devices, NavSection.Tools, "Périphériques", Glyph(0xE772)),
        new(PageKeys.Toolbox, NavSection.Tools, "Boîte à outils", Glyph(0xE90F)),
        new(PageKeys.Memory, NavSection.Tools, "Mémoire", Glyph(0xE81E)),
    };

    /// <summary>Bouton « Paramètres » en bas de la barre latérale, hors de la liste.</summary>
    public static NavPage Settings { get; } = new(PageKeys.Settings, NavSection.Footer, "Paramètres", Glyph(0xE713));

    public static string SectionTitle(NavSection section) => section switch
    {
        NavSection.Monitor => "Surveiller",
        NavSection.Tune => "Régler",
        NavSection.Diagnose => "Diagnostiquer",
        NavSection.Tools => "Outils",
        _ => "",
    };

    /// <summary>
    /// Les entrées de la liste, dans l'ordre de <see cref="Pages"/>. Une page absente de
    /// <paramref name="viewModels"/> n'est pas encore livrée : elle s'affiche « bientôt disponible ». Une page
    /// <see cref="NavPage.LaptopOnly"/> est retirée sur un PC de bureau avéré.
    ///
    /// Une clé inconnue, ou une page qui n'a ni ViewModel ni texte d'attente, est une erreur de développement : elle
    /// lève, pour se voir au premier lancement plutôt que de laisser une page manquer sans un mot.
    /// </summary>
    public static IReadOnlyList<NavEntry> Build(IReadOnlyDictionary<string, object> viewModels, bool isDesktop)
    {
        foreach (string key in viewModels.Keys)
        {
            if (!Pages.Any(p => p.Key == key)) throw new ArgumentException($"Page inconnue du menu : « {key} ».", nameof(viewModels));
        }

        var entries = new List<NavEntry>();
        foreach (NavPage page in Pages)
        {
            if (page.LaptopOnly && isDesktop) continue;

            object viewModel = viewModels.TryGetValue(page.Key, out object? provided)
                ? provided
                : ComingSoonPages.Create(page)
                  ?? throw new InvalidOperationException($"La page « {page.Key} » n'a ni ViewModel ni texte « bientôt disponible ».");
            entries.Add(new NavEntry(page, viewModel));
        }

        return entries;
    }

    private static string Glyph(int codePoint) => char.ConvertFromUtf32(codePoint);
}
