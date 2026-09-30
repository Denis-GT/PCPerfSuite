namespace PCPerfSuite.App.ViewModels;

/// <summary>
/// Page (ou sous-onglet) qui veut savoir si elle est sous les yeux de l'utilisateur : pour charger ses données à la
/// première ouverture plutôt qu'au démarrage, et ne relever en direct que tant qu'elle est affichée.
///
/// Toutes les vues restent créées pour la durée de l'app (elles gardent leur état, comme l'historique des courbes du
/// Monitoring) : une vue masquée est seulement repliée, et rien d'autre ne lui dit qu'elle ne sert plus.
///
/// Ce n'est pas le mode éco : une page masquée reçoit toujours SnapshotUpdated et MetricsUpdated. Un abonné qui ne fait
/// qu'afficher sort tout seul si MonitoringViewModel.IsBackgroundMode ; ce qui doit tourner fenêtre cachée passe par
/// <see cref="IBackgroundSensorConsumer"/>.
/// </summary>
public interface IPageLifecycle
{
    /// <summary>Vrai quand la page est la page courante ET que la fenêtre est visible (ni réduite, ni rangée dans la
    /// zone de notification). Posée par MainViewModel pour chaque page, Paramètres compris ; pour un sous-onglet, par
    /// sa page. Peut être posée plusieurs fois de suite avec la même valeur : ne réagir qu'aux changements.</summary>
    bool IsPageShown { set; }
}

/// <summary>Qui est affiché, isolé ici pour être testé sans fenêtre.</summary>
public static class PageLifecycle
{
    /// <summary>Masque d'abord les autres pages, puis affiche la page courante : deux pages qui se partagent une
    /// ressource (trace ETW, sondage coûteux) ne se chevauchent jamais.</summary>
    public static void Update(IEnumerable<NavEntry> entries, string? currentKey, bool isWindowShown)
    {
        List<NavEntry> all = entries.ToList();

        foreach (NavEntry entry in all)
        {
            if (entry.Key != currentKey && entry.ViewModel is IPageLifecycle page) page.IsPageShown = false;
        }

        foreach (NavEntry entry in all)
        {
            if (entry.Key == currentKey && entry.ViewModel is IPageLifecycle page) page.IsPageShown = isWindowShown;
        }
    }
}
