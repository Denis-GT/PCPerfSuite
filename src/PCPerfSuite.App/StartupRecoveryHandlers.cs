using PCPerfSuite.Core.Safety;

namespace PCPerfSuite.App;

/// <summary>
/// Gestionnaires de la reprise au lancement (<see cref="StartupRecovery"/>), un par fonction qui journalise des
/// opérations risquées dans le journal de session. C'est le seul endroit où en inscrire un : chaque conversation qui
/// en livre un y ajoute sa ligne, avec son étape (<see cref="RecoveryStage"/>) ; l'ordre d'appel suit les étapes, pas
/// l'ordre de cette liste.
/// </summary>
internal static class StartupRecoveryHandlers
{
    public static IReadOnlyList<IStartupRecoveryHandler> Create() =>
    [
        // Aucun encore : les recherches d'OC (#15, #14), l'essai d'écran (#17), le test combiné (#11), le bench (#10),
        // les groupes de profils (#8) et la bascule automatique (#9) inscriront ici leur gestionnaire.
    ];
}
