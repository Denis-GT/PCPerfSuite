# PCPerfSuite

App WPF (.NET 8) de monitoring et de tuning PC, destinée à une diffusion publique : elle doit
fonctionner sur toutes les marques de PC (bureau et portables, Intel/AMD, NVIDIA/AMD/Intel).
Code, commentaires et interface en français.

## Compatibilité : règle pour toute fonction liée au matériel

1. **Détecter à l'exécution** si la fonction est prise en charge sur ce PC. Ne jamais supposer un
   fabricant, un modèle ou un pilote.
2. **Ne jamais planter** et ne jamais laisser une valeur vide sans explication. Toute méthode qui
   touche au matériel est « best-effort » : elle renvoie null/false plutôt que de lever.
3. **Dire pourquoi c'est absent**, en distinguant :
   - la limite du matériel ou du pilote (ex. température mémoire GPU, seulement en GDDR6X) ;
   - la marque ou le modèle pas encore pris en charge (ex. ventilateurs d'un portable Dell) ;
   - l'app lancée sans administrateur.
   Les métriques affichent `--` (pas encore lue) ou `N/D` (ce PC ne la fournit pas, voir
   `MetricReading.Unavailable` et `MetricDefinition.UnavailableHint` dans
   `src/PCPerfSuite.App/Metrics/MetricCatalog.cs`). Une fonction entière indisponible affiche un
   message adapté au PC (ex. `GpuControlViewModel.UnavailableMessage`, `FanCurvesViewModel.NoFansMessage`).
4. **L'ajouter au diagnostic** « Paramètres › Compatibilité de ce PC »
   (`src/PCPerfSuite.App/ViewModels/CompatibilityViewModel.cs`), qui sert aussi de rapport de bug.
5. **Contrôleur embarqué des portables : lecture seule.** Ne jamais y écrire (ventilateurs,
   alimentation) : une mauvaise écriture peut laisser la machine sans refroidissement.
6. Une prise en charge qui n'a pas été vérifiée sur une vraie machine est marquée
   « expérimental » (voir `ILaptopFanProvider.IsVerified`).

Les sources propres à une marque vivent dans des modules séparés, choisis d'après
`MachineInfo.Current` (`src/PCPerfSuite.Core/Environment/MachineInfo.cs`). Exemple :
`src/PCPerfSuite.Core/Hardware/LaptopFans/`.
