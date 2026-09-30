namespace PCPerfSuite.Core.Compatibility;

/// <summary>
/// Source de lignes du diagnostic « Compatibilité de ce PC ». Chaque fonction apporte les siennes par un fournisseur
/// rangé dans son propre dossier, inscrit dans la liste de MainViewModel : le diagnostic les affiche après ses lignes
/// historiques, dans l'ordre de la liste, et les reprend dans « Copier le rapport ». Jamais une méthode ni une
/// dépendance de plus dans CompatibilityViewModel.
///
/// Règle 2 de CLAUDE.md : un fournisseur qui lève ne casse rien, sa ligne devient « Lecture impossible » avec la
/// raison. Règle 3 : une absence dit toujours pourquoi (matériel, marque non prise en charge, droits).
/// </summary>
public interface ICompatibilityRowProvider
{
    /// <summary>Nom de la rubrique, repris par la ligne « Lecture impossible » si <see cref="GetRows"/> lève.</summary>
    string Title { get; }

    /// <summary>Lignes à afficher, dans l'ordre. Appelée sur le thread d'interface à chaque reconstruction du
    /// diagnostic : rapide, à partir de ce qui est déjà lu — ni WMI, ni réseau, ni fichier lent ici.</summary>
    IReadOnlyList<CompatibilityRow> GetRows();

    /// <summary>Lectures lentes (WMI, pilotes, fichiers), faites hors du thread d'interface à l'ouverture du diagnostic
    /// et par « Actualiser », avant <see cref="GetRows"/>. Rien à faire par défaut. Doit s'arrêter tôt quand
    /// <paramref name="cancellationToken"/> est annulé (onglet quitté).</summary>
    Task RefreshAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
