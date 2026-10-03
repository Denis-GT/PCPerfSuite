using PCPerfSuite.Core.Benchmark.Session;
using PCPerfSuite.Core.Profiles;
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
        // Bench (#10) : note le test interrompu et supprime le fichier du test disque resté sur son volume.
        new BenchRecoveryHandler(),

        // Groupes de profils (#8) : suspend un groupe suivi d'un incident, et décoche « Appliquer au démarrage ».
        new ProfileGroupRecoveryHandler(),

        // Bascule automatique (#9) : note au journal des bascules un incident qui a suivi une bascule (le groupe, lui,
        // est suspendu par le gestionnaire des groupes), pour prévenir au lancement.
        new AutoSwitchRecoveryHandler(),

        // Les recherches d'OC (#15, #14), l'essai d'écran (#17) et le test combiné (#11) inscriront ici leur gestionnaire.
    ];
}
