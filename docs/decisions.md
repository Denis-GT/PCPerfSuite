# Décisions transverses et briques partagées

À lire **avant de coder**, par chaque conversation de la feuille de route (`docs/etudes/prompts-conversations.md`) :

- une question tranchée ici ne se repose pas ;
- une brique listée ici se réutilise sous son nom et à son emplacement ;
- la conversation qui livre une brique met sa ligne à jour (état « livrée », emplacement réel) ; celle qui crée une
  brique non prévue l'ajoute ;
- une décision qui change passe par Denis, et sa ligne garde l'ancienne réponse barrée avec la date.

La navigation (où va chaque page, recette pour en ajouter une) est dans `docs/navigation.md`.

## Décisions transverses

Tranchées par Denis le 30/09/2026 (conversation #1).

| # | Sujet | Décision | Conséquences | Conversations concernées |
|---|---|---|---|---|
| D1 | Licence de PCPerfSuite | **Propriétaire, diffusion gratuite**, tous droits réservés (`LICENSE`). Une **licence pro de PCPerfSuite** est prévue plus tard, dans une conversation à part (hors des 24) : sans elle, certaines fonctions seront limitées, **notamment les fonctions IA**. Rien n'est codé d'ici là. | Chaque dépendance ajoutée est inscrite dans `THIRD-PARTY-NOTICES.md` (licence relevée dans le .nuspec). Les DLL LGPL (NvAPIWrapper, modules PawnIO) restent des fichiers à part : pas de publication en fichier unique qui les engloberait. La couche IA de #13 reste séparable, pour pouvoir la réserver à la licence pro. | #2, #11, #13, #23, #24, toute nouvelle dépendance |
| D2 | Signature de code | **Au nom d'une société**, certificat pris avant la première diffusion publique (Azure Artifact Signing est ouvert aux organisations de l'UE, pas aux particuliers français). | D'ici là, **un seul exe avec des modes secondaires** (worker de bench, `--watchdog`…), donc un seul binaire à signer. Pas d'exe séparé pour le bench ni pour le chien de garde. | #10, #11, #13, #15, #16, #17 |
| D3 | Sens de « IA » | **Un moteur déterministe et explicable décide de tout** : bascule de profils, verdicts, valeurs d'OC. Un **LLM facultatif rédige seulement** le compte rendu (#13), à partir des constats, sans jamais changer un verdict ni une valeur. | Aucun LLM ne choisit une valeur ni ne déclenche une écriture matérielle. Les verdicts et les recherches d'OC sont testables et reproductibles. | #9, #12, #13, #15, #16 |
| D4 | Portée de la règle 5 (contrôleur embarqué des portables) | **Lecture seule en V1** pour : les modes constructeur Silence/Turbo, le mode GPU Eco/MUX, le rétroéclairage du clavier par le contrôleur embarqué, la taille de la mémoire du GPU intégré écrite par le pilote AMD. **Toute exception, c'est Denis qui l'écrit dans CLAUDE.md.** | #22 endort le GPU dédié sans aucune écriture firmware. #20 affiche la taille de la mémoire du GPU intégré et renvoie vers AMD Software. Les profils (#8, #9) et le bench (#10) lisent le mode constructeur, sans le changer. | #8, #9, #10, #20, #22, #23 |
| D5 | Public | **Particuliers et techniciens** (usage commercial, F3). | Dans la Boîte à outils (#7), un badge « licence pro requise » signale les outils tiers dont la version gratuite interdit l'usage commercial (HWiNFO, OCCT Personal, Cinebench, y-cruncher…). | #7, #10, #13 |
| D6 | Fonctions du mode technicien | **Aucune liste d'interdits** : particuliers et techniciens ont accès à tout, y compris l'OC, le scan SMBus du RGB, les opérations destructives sur les disques et l'installation de pilotes en lot. En contrepartie, **chaque action risquée affiche son avertissement à chaque fois** : pas de case « ne plus demander », « Non » par défaut. La licence pro (D1) pourra plus tard limiter certaines fonctions. | Voir « Règles communes › Actions risquées ». Aucune fonction n'est masquée en mode technicien ; le parcours guidé de #13 propose, il n'impose pas. | #7, #13, #15, #16, #18, #19, #23 |
| D7 | À la fermeture | **Les réglages volatils sont remis d'origine** en quittant (watts CPU, OC GPU, ventilateurs, et demain limites par processus), comme aujourd'hui, sauf un réglage CPU ou GPU dont « Appliquer au démarrage » est coché. **Les réglages permanents restent** (plan d'alimentation, tweaks d'Optimisation, parking, animations…), **et l'app le dit**. | Un groupe de profils (#8) ou une bascule (#9) qui touche un réglage permanent l'annonce. #21 lève ses limites en quittant. Les modifications permanentes s'inscrivent au registre des modifications (voir les briques). | #8, #9, #21 |
| D8 | Hébergement du catalogue et de la table de référence | **Un dépôt GitHub public dédié.** Une GitHub Action régénère le catalogue d'outils depuis winget et ouvre une PR ; **Denis la relit et signe en local, avec une clé hors ligne qui n'entre jamais dans la CI** : une CI compromise ne peut pas publier un catalogue malveillant. La table de scores de référence (#12) vit dans le même dépôt. | Pour #7 : l'app vérifie la signature du catalogue (clé publique embarquée) avant de s'en servir. | #7, #12 |
| D9 | Plateformes | **Windows 10 22H2 et Windows 11, en x64.** ARM64 hors V1. **Aucune télémétrie** : l'app n'envoie rien ; un rapport ne part que si l'utilisateur le copie ou l'envoie lui-même. | Publication `win-x64` (#13). Pas de serveur de collecte. | toutes, #13 |
| D10 | TFM et WinRT | **`net8.0-windows` partout.** Le code WinRT vit dans un **assemblage isolé, chargé à la demande**. | #24 crée cet assemblage pour Dynamic Lighting ; une autre fonction qui a besoin de WinRT (écrans ?) le réutilise, sans changer le TFM des autres projets. | #3, #17, #24 |
| D11 | Navigation | **Sections + sous-onglets.** Les pages à venir sont **visibles dès maintenant, marquées « bientôt disponible »** (ComingSoonView). Mémoire est une page d'Outils ; l'Overlay une page de Surveiller ; l'OC automatique une page de Régler, avec deux sous-onglets GPU et CPU. L'onglet Paramètres › Thèmes (« en préparation ») reste. | Placement de chaque fonction, clés et recette : `docs/navigation.md`. | toutes les pages |
| D12 | Parking des cœurs et profils | **Réglage indépendant** (Denis, 01/10/2026, conversation #5) : CPMINCORES, CPMAXCORES, leurs variantes « 1 » et l'ordonnancement hybride ne sont **ni dans `CpuProfile` ni dans les groupes de profils**. Ils se règlent dans Processeur › Cœurs et par le tweak « core-parking » d'Optimisation Windows (sa façade). | #8 n'ajoute pas le parking à ses groupes ; #9 ne le bascule pas. Le catalogue de `CpuPowerTuningService` (donc `CpuProfile`) ne reçoit pas ces réglages ; `CoreParkingCatalog` les garde à part. | #8, #9 |

## Règles communes

- **Dossier de données** : tout fichier écrit par l'app passe par `AppDataPaths.Current`
  (`src/PCPerfSuite.Core/Environment/AppDataPaths.cs`, namespace `PCPerfSuite.Core.SystemInfo`), jamais par un chemin
  `%LOCALAPPDATA%\PCPerfSuite` codé en dur. On ajoute une **propriété nommée** (fichier ou sous-dossier) dans
  AppDataPaths, jamais un `Path.Combine` sur `Root` ailleurs ; on relit le chemin au moment d'écrire, sans le figer
  dans un champ statique. Noms réservés : `bench\` (#10), `rapports\` (#12, rapports et étalons), `usage.json` (#9),
  le journal de session (#4, nom et format choisis et documentés ici par #4), un journal des opérations disque (#19),
  `sauvegardes-pilotes\` comme dossier proposé par défaut (#18). La racine se choisit une seule fois, en tête
  d'`App.OnStartup`, par `AppDataPaths.TryUseRoot` (#13 : racine portable si `portable.flag` est à côté de l'exe).
  **Hors de ce dossier, volontairement** : `%ProgramData%\PCPerfSuite` (dossier sécurisé de #7 : `Installations\` pour
  les dossiers de travail d'OfficialInstaller, `Tools\` pour les outils portables, le dernier catalogue d'outils accepté
  `catalogue-outils.json` et `.sig` avec son plancher `catalogue-outils.plancher`, partagés par tous les comptes, demain
  les fichiers de bench) et, seulement si l'app tourne sans élévation, `%TEMP%\PCPerfSuite` pour ses installeurs. Tout
  fichier téléchargé, qu'il soit lancé ensuite ou remis à l'utilisateur, est vérifié dans un dossier que seuls les
  administrateurs peuvent modifier : jamais sous AppDataPaths, modifiable par tout programme de la session et qui peut
  être une clé USB sans ACL (mode portable de #13).
- **Réglages** : toute lecture-modification-écriture passe par `AppSettingsStore.Update()`, avec seulement des
  mutations dans le lambda (le verrou est tenu pendant). Pas de `Load()` puis `Save()`. Quand la suite ne doit pas
  avoir lieu sans trace sur le disque (une valeur d'origine avant de modifier Windows), `AppSettingsStore.TryUpdate()`
  dit si l'enregistrement a réussi. Les nouveaux modèles
  enregistrent leurs enums en chaînes. `WindowsPerformanceSettingsService.RememberOriginalValue` est converti (#5),
  comme les onglets Processeur et GPU (#2).
- **Diagnostic « Compatibilité de ce PC »** : une nouvelle ligne est un **fournisseur**
  (`ICompatibilityRowProvider`, `src/PCPerfSuite.Core/Compatibility/`) rangé dans le dossier de sa fonction et
  inscrit dans `MainViewModel._compatibilityRows`, jamais une méthode ni une dépendance de plus dans
  `CompatibilityViewModel`. `GetRows()` est rapide (thread d'interface) ; les lectures lentes vont dans
  `RefreshAsync()`, appelée hors du thread d'interface à l'ouverture de l'onglet et par « Actualiser ». Une donnée
  personnelle (nom du PC, compte, numéro de série) est en `IsPersonal` ; le rapport copié masque aussi les chemins du
  profil Windows. Les lignes historiques restent dans CompatibilityViewModel jusqu'à #12.
- **Modifications durables de Windows** : chaque fonction qui modifie Windows au-delà de la session implémente
  `ISystemChangeOwner` et s'inscrit à `MainViewModel.SystemChanges` (`src/PCPerfSuite.Core/SystemChanges/`). Elle garde
  elle-même l'état d'origine, sous AppDataPaths ou dans ses réglages. `Describe()` liste toujours ce qui est modifié,
  en disant ce qui ne pourra pas revenir (`CanRestore = false`). `RestoreAll()` rend ce qui peut l'être, avec un
  résultat typé, et ne lève jamais.
- **Actions risquées** (D6) : confirmation à **chaque** fois, par une boîte de dialogue au bouton « Non » par défaut
  (modèle : `ShowMessage` de ProcessesViewModel et StorageViewModel), qui dit le risque en clair et ce qui ne pourra
  pas être annulé. Pas de « ne plus demander ». Une opération qui peut faire planter la machine le dit avant.
- **Pages** : une page qui charge ou relève quelque chose implémente `IPageLifecycle` et charge à la première
  ouverture, jamais dans son constructeur (voir `docs/navigation.md`).
- **Journal de session** (#4) : toute opération risquée qui doit être reprise après un plantage (palier d'OC, essai
  d'écran, test combiné, bench, groupe ou bascule appliqués, limites par processus actives) s'inscrit dans
  `SessionJournal.Current` (`src/PCPerfSuite.Core/Safety/SessionJournal.cs`), et nulle part ailleurs : **aucun autre
  « marqueur » ni « témoin de session »**. Le seul témoin antérieur, `AdlxProbeGuard` (initialisation d'ADLX), reste
  tel quel ; #14 pourra le migrer.
  - Fichier `AppDataPaths.SessionJournalFile` = `journal-session.jsonl`, JSON Lines v1, une ligne par écriture, jamais
    réécrite pendant une opération : `{"v":1,"id":GUID,"timeUtc":…,"boot":…,"pid":…,"component":"test-combine",
    "action":"debut","values":{clé:chaîne},"state":"InProgress"|"Completed"|"Failed","cause":…}`. L'état d'une
    opération est celui de sa dernière ligne ; `boot` (démarrage de Windows) dit si le PC a redémarré depuis.
  - `Begin(composant, action, valeurs)` → `SessionOperation` (`Complete()`, `Fail(cause)`, `Dispose()` sans clôture =
    échouée ; `IsDurable` faux = ligne pas sur le disque, l'opération ne doit pas commencer). Composant et action en
    kebab-case ; valeurs = nombres ou mots, jamais un nom d'application ou de fichier (masqués).
  - Écriture courte en ajout, `WriteThrough` puis `Flush(true)`, précédée d'un saut de ligne ; lecture tolérante (fin
    arrachée ignorée). Rétention 30 jours, compactage au lancement seulement.
  - Une exception non gérée qui tue l'app clôt ses opérations ouvertes (`AbandonAll`). Limite connue : un processus tué
    de l'extérieur (Gestionnaire des tâches) laisse son opération en cours, et un arrêt anormal de Windows survenu
    ensuite, avant tout redémarrage, lui est imputé.
  - Reprise : un gestionnaire `IStartupRecoveryHandler` (étape `RecoveryStage`, composants) s'inscrit dans
    `src/PCPerfSuite.App/StartupRecoveryHandlers.cs`. Il reçoit, avant toute fenêtre, ses opérations restées en cours
    avec leur `IncidentQualification`, ne montre aucune fenêtre, et peut lever sans gêner les autres. Le bilan
    (`StartupRecoveryReport`) est passé à `MainViewModel.StartupRecovery`.
- **Groupes de profils** (#8, décisions de Denis du 01/10/2026) :
  - Un groupe applique aussi les réglages du plan de sa partie CPU (permanents, annoncés, origine notée, propriétaire
    au registre) ; le parking reste à part (D12).
  - On garde les cases « Appliquer au démarrage » par onglet : appliquer un groupe en fait les dernières valeurs des
    onglets, sauf `MakeStartupState = false`.
  - Ordre : une hausse de la limite de puissance visée → ventilateurs puis CPU/GPU ; tout en baisse → l'inverse ;
    indéterminé ou mixte → ventilateurs d'abord.
  - Bail tenu par un autre : écriture manuelle refusée avec la raison, onglets grisés ; les sécurités thermiques
    agissent toujours.
  - Seule la page Profils écrit `AppSettings.ProfileGroups`, avec une exception : `ProfileGroupRecoveryHandler` y
    écrit les suspensions au lancement, avant que la page n'existe.
  - Limites connues : une app tuée de l'extérieur laisse la période probatoire ouverte (même limite que le journal) ;
    au lancement, si la ligne `demarrage` ne peut pas être écrite, les onglets reposent quand même leur état (leurs
    cases font foi) ; une partie GPU ou des watts relevés sur un autre matériel ne sont pas posés, même de même marque.
- **Bascule automatique** (#9, décisions de Denis du 01/10/2026) :
  - Groupes générés dès l'activation (seulement ceux qui manquent) ; l'affinage d'après l'historique n'est que proposé
    (« Régénérer », au bout de 3 jours), et ne touche jamais un groupe `EditedByUser`.
  - Règles par application dès la V1 : `ApplicationMatch` → un usage ou un groupe, prioritaires (entrée 10 s, sortie 2 min).
  - Une bulle discrète à chaque bascule, désactivable ; verrou, incident et suspension toujours signalés ; jamais de nom
    d'application dans une bulle.
  - Les groupes de jeu reprennent l'OC GPU et les watts enregistrés dans les onglets avec « Appliquer au démarrage »
    (même carte, watts dans les bornes) ; la bureautique remet le GPU d'origine (`origine`, jamais null) et ne dépasse
    pas les watts enregistrés.
  - Ventilateurs laissés au BIOS : pris en main seulement sur demande (`AutoSwitch.IncludeBiosFans`, décoché par défaut).
  - Charge processeur soutenue hors jeu (75 % ou plus sur 2 min) → « jeu exigeant ».
  - Le groupe d'un usage se déduit de `ProfileGroup.Usage` (non suspendu, manuel avant généré, puis le plus récent) :
    aucune table usage → groupe.
  - `ProfileGroupsSettings.StartupState` : seul l'état fait « état de démarrage » sert à la prudence au lancement ; une
    bascule, transitoire, remplace `Active` sans y toucher (repli sur `Active` pour un fichier d'avant).
  - Pause propre à #9 : 10 min après un geste manuel (`TuningStatusViewModel.NoteManualWrite`, levé au geste, pas à
    l'écriture différée), et l'usage en cours est adopté ; le bail reste le seul moyen pour une autre fonction de
    suspendre la bascule.
  - Verrou après `CpuControlService.EmergencyRestored` ou la sécurité thermique GPU, jusqu'à « Déverrouiller » ou la
    relance ; les planificateurs de #8 refusent en plus toute hausse automatique dans la session.
  - Mode éco : CpuLoad, Fps, Battery, et Gpu sauf sur batterie hors jeu ; jamais le groupe Cpu (coûteux) : la
    température CPU n'entre dans l'historique que si un autre besoin la lit déjà.
  - `usage.json` v1 (`AppDataPaths.UsageFile`) : agrégats par jour et par usage (histogrammes à 1 °C → p50/p95), 200
    applications vues, 200 lignes de journal, 30 jours ; écrit toutes les 5 min, après une bascule et à la fermeture,
    hors du fil d'interface. C'est l'historique de l'utilisateur, pas un témoin de session ; rien n'est relevé tant que
    la bascule est désactivée ; il contient des chemins d'exécutables (local seulement).
  - Prudence : la ligne `groupe-profils` du journal de session porte `demandeur` ; `AutoSwitchRecoveryHandler` (étape
    `AutoSwitch`) note au journal des bascules un incident qui a suivi une bascule ; la suspension reste celle de #8.
  - À la fermeture, le PC reste sur les réglages de plan du dernier groupe posé (D7), et la page le dit.
  - Après la relecture (02/10/2026) : un incident qui suit une bascule décoche aussi « Appliquer au démarrage » quand l'OC
    ou les watts du groupe sont ceux enregistrés dans l'onglet (`ProfileGroupStartupCheck.MatchesSavedGpu` /
    `MatchesSavedWatts`, au lancement comme sur TDR en session), et un groupe qui partage les valeurs relevées d'un
    groupe suspendu n'est plus posé (`UsageGroupResolver.SuspendedBy`). À confirmer par Denis : la case décochée est
    celle que l'utilisateur avait cochée lui-même.
  - Réveil et réactivation : le verdict d'avant est oublié (`UsageClassifier.Reset`) ; démarrage de 35 s à l'activation ;
    une adoption après un réglage manuel est gardée au réveil. L'état du moteur vit dans `AutoSwitchSession` (pur, testé).
  - Mode éco : la bascule ne demande ses groupes que toutes les 5 s ; chaque mesure est pondérée depuis sa propre lecture,
    et une mesure de moins de 10 s vaut pour les signes instantanés. Sur batterie, le GPU n'est interrogé que si RTSS
    mesure des images, en plein écran exclusif ou jeu déjà reconnu.
  - usage.json intact mais illisible un instant : rien n'est relevé ni écrit, nouvel essai 5 min plus tard ; l'élagage à
    30 jours a lieu à chaque enregistrement ; « Effacer l'historique » supprime aussi `usage.json.corrupt`.
  - Fermeture : `AutoProfileSwitcher.Stop` en tête (plus de bascule, écriture lancée sans attendre),
    `AutoProfileSwitcher.Dispose` en tout dernier (attente de l'écriture, 2 s au plus).
  - Les signaux absents (charge GPU jamais lue, aucune image RTSS) sont dits dans la page et dans la ligne du diagnostic.
- **Cause d'une absence** (#4) : une lecture « N/D » porte un `Unavailable` (`UnavailableCause` : `HardwareOrDriver`,
  `UnsupportedModel`, `MissingRights`, `src/PCPerfSuite.Core/Compatibility/UnavailableCause.cs`) et un texte, pour que
  le diagnostic (#12) range ce qui n'est pas mesurable sans réinventer les trois causes de la règle 3.

## Briques partagées

État au 30/09/2026. « prévue » : nom et emplacement proposés, à reprendre tels quels.

| Brique | Propriétaire | Emplacement et nom | État |
|---|---|---|---|
| Clés de page `PageKeys`, menu `NavigationMenu` (sections, pages à venir) | #1 | `src/PCPerfSuite.App/ViewModels/PageKeys.cs`, `NavigationMenu.cs`, `ComingSoonPages.cs` | livrée |
| Cycle de vie des pages `IPageLifecycle` et `PageLifecycle.Update` | #1 | `src/PCPerfSuite.App/ViewModels/IPageLifecycle.cs` | livrée |
| Dossier de données `AppDataPaths` | #1 | `src/PCPerfSuite.Core/Environment/AppDataPaths.cs` (namespace `PCPerfSuite.Core.SystemInfo`) | livrée ; racine portable ajoutée par #13 |
| Lignes de diagnostic `ICompatibilityRowProvider`, `CompatibilityRow`, `CompatibilityRows`, masquage `ReportPrivacy` | #1 | `src/PCPerfSuite.Core/Compatibility/` | livrée ; lignes historiques déplacées par #12 |
| Registre des modifications `ISystemChangeOwner`, `SystemChangeRegistry` | #1 | `src/PCPerfSuite.Core/SystemChanges/`, instance `MainViewModel.SystemChanges` | livrée ; inscrits : `core-parking` (#5), `animations` (#6), `boite-a-outils` (#7), `groupes-plan-alimentation` (#8) |
| `GpuIdentity` (marque en chaîne, nom, `PciVendorId`, `PciDeviceId`, `PciSubsystemId` au format du registre PCI, `Luid` facultatif) : `Matches(saved, current)` (marque égale, chaque champ connu des deux côtés égal, LUID non comparé), `ParsePciId`, `Describe()`. Lue par `GpuControlService.Identity` (NVAPI, ADLX, IGCL), enregistrée dans `GpuControlSettings.OverclockGpu` ; ligne `GpuIdentityRowProvider` (« Carte GPU pilotée ») | #2 | `src/PCPerfSuite.Core/Hardware/Gpu/GpuIdentity.cs` | livrée ; `IsSameCard` ajouté par #8 ; LUID rempli par #11 ; à compléter par #14, #15, #22 |
| Carte dédiée d'après le nom Windows : `DedicatedGpuName.VendorOf(name)` / `VendorsOf(names)` → `GpuVendor?` (null pour un GPU intégré, virtuel ou inconnu). Distingue les Arc dédiées (A770, B580, Pro A40) des Arc intégrées (« Arc Graphics », « 140V GPU », « B390 GPU »), et les Radeon RX/Pro des iGPU AMD (« Radeon Graphics », « 780M », « RX Vega 11 Graphics »). Repli quand aucune API constructeur n'a répondu, seule source aujourd'hui ; si `GpuIdentity` porte un jour un indicateur « intégré » lu par un backend, il passera avant | correctif | `src/PCPerfSuite.Core/Hardware/Gpu/DedicatedGpuName.cs` | livrée ; lue par `GpuControlViewModel.UnavailableMessage` |
| Sécurité thermique, en logique pure : `ThermalGuard` (seuils `ThermalLimit` par capteur, délai, perte de lecture facultative, heure injectée, verdict `ThermalState`), partagée par le CPU (98 °C / 15 s) et le GPU. `GpuThermalSafety` (cœur ≥ 90 °C 15 s, point chaud ≥ 105 °C s'il est lu ; retour d'origine, relecture, évènement `EmergencyRestored` ; `Restart()` au réveil, `NoteNoReading()` pour un chien de garde ; `ReadingWatchdog` distingue une veille d'une panne de relevé, sur une heure monotone), armée par `GpuControlService.RefreshArming` d'après `GpuOverclockRaise.IsRaised` sur les seuls blocs écrits par l'app ; cible `IGpuOverclockTarget` ; ligne `GpuThermalSafetyRowProvider`. Décision au réveil commune CPU/GPU : `TuningResume.Decide` (relire / ne pas réappliquer après un déclenchement / réappliquer) | #2 | `src/PCPerfSuite.Core/Safety/` | livrée ; reprise par #10, #11, #15, #16 avec leurs seuils |
| Appliquer puis relire l'OC GPU : `GpuControlService.ApplyAndVerify(GpuOverclockRequest)` → `GpuApplyReport` (demandé / retenu / `GpuApplyStatus` par réglage), comparaison pure `GpuApplyComparison.Compare` ; plages de repli NVAPI signalées par `GpuOverclockSnapshot.CoreOffsetRangeIsFallback` / `MemoryOffsetRangeIsFallback` | #2 | `src/PCPerfSuite.Core/Hardware/Gpu/GpuOverclockApply.cs` | livrée ; pour #8, #15 |
| Fonctions d'un module PawnIO : `PawnIoModuleFunctions.Parse` (chaînes `ioctl_…` du module), `PawnIoModuleInfo` (source, version, fonctions, SHA-256), `PawnIoDriver.LoadedModules` et `PawnIoDriver.LoadFailures` (dernier échec par module) ; modules livrés par l'app `ShippedPawnIoModules` (IntelMSR 0.2.11 dans `PawnIO\`, choisi avant celui de LHM par `PawnIoModuleChoice`) ; ligne `PawnIoModulesRowProvider` | #2 | `src/PCPerfSuite.Core/Hardware/Cpu/PawnIoModules.cs` | livrée ; reprise par #4 |
| Inventaire des écrans `DisplayTopology.Read()` → `DisplayTopologySnapshot` : `DisplayMonitor` (HMONITOR, bornes et zone de travail en pixels physiques, DPI et échelle, principal, nom du GPU) et un `DisplayTarget` par écran physique, plusieurs si dupliqués (nom convivial, chemin du moniteur, EDID fabricant en code PNP et produit, sortie `DisplayOutputKind`, connecteur, LUID et chemin de l'adaptateur, fréquence). Filtre des fenêtres du premier plan `IsIgnoredForegroundWindow` (PCPerfSuite, barre des tâches, bureau, menu Démarrer, recherche, Alt+Tab) et fenêtre → écran `MonitorFromWindow` / `FindMonitor`, pour #9. Identité enregistrée `DisplayIdentity` (chemin, EDID, nom, empreinte du numéro de série ; jamais `\\.\DISPLAYn`) et `DisplayIdentityResolver` ; numéros de série WMI `MonitorSerials` (empreinte seulement) ; noms `DisplayNames` ; ligne « Écrans » `DisplaysRowProvider`. Win32 seulement, aucun WinRT (D10) | #3 | `src/PCPerfSuite.Core/Hardware/Displays/` | livrée ; pour #9, #17, #22 |
| `SessionJournal` (format et règles : voir « Journal de session » plus haut) ; qualification d'incident `IncidentClassifier.Qualify` (même démarrage de Windows = interrompu ; sinon événements en direct avant le premier redémarrage, événements d'après coup entre ce redémarrage et le suivant) et `ClassifyAll` (incidents des 30 derniers jours) ; lecture du journal Système `SystemEventReader` (Kernel-Power 41, WER 1001, 6008, démarrages et arrêts propres, WHEA 17/18/19/47, Display 4101, disk 7/51/153, Kernel-Processor-Power 37 ; champs techniques seulement, jamais le texte des messages) ; lignes « Journal de session » et « Journaux Windows » | #4 | `src/PCPerfSuite.Core/Safety/SessionJournal.cs`, `src/PCPerfSuite.Core/Safety/Events/` | livrée ; pour #8 à #21 |
| Étape `StartupRecovery` : logique pure `StartupRecovery` (ordre fixe des étapes, gestionnaires isolés, journal Système lu 2 s au plus, opérations closes avec leur qualification, compactage), appelée par `App.OnStartup` après le mutex d'instance et avant la fenêtre ; gestionnaires dans `StartupRecoveryHandlers` | #4 | `src/PCPerfSuite.Core/Safety/StartupRecovery.cs`, `src/PCPerfSuite.App/App.xaml.cs`, `src/PCPerfSuite.App/StartupRecoveryHandlers.cs` | livrée ; inscrits : `groupe-profils` (#8), `bascule-auto` (#9) |
| Lecteur CfgMgr32 (graine du service des périphériques de #18) | #4 | `src/PCPerfSuite.Core/Devices/` | prévue (pas encore fait par #4) |
| Bail de cadence `HardwareMonitorService.RequestCadence(demandeur, intervalle, groupes)` → IDisposable, logique pure `CadenceLeases` : le plus rapide l'emporte sur la cadence imposée, l'automatique et le plafond GPU, sans les modifier ; groupe sous bail relu en mode éco ; tick minimal 100 ms (`MinTickInterval`) ; `IsCadenceLeased(groupe)` | #4 | `src/PCPerfSuite.Core/Hardware/HardwareMonitorService.Cadence.cs`, `CadenceLeases.cs` | livrée ; pour #10, #11 |
| Bridage CPU dans le relevé `HardwareSnapshot.CpuThrottle` (`CpuThrottleReading` : thermique, PROCHOT, puissance, courant, TjMax et décalage TCC, fréquences, valeurs AMD, source et cause) : MSR Intel (IntelMSR, 0x64F tenté), PM table AMD par version (`PmTableLayouts`), compteurs Windows toujours ; balayage par cœur seulement sous bail ; ligne « Raisons de bridage CPU » ; dernier relevé `HardwareMonitorService.LastSnapshot` | #4 | `src/PCPerfSuite.Core/Hardware/Cpu/Throttle/` | livrée ; pour #10, #11, #12 |
| Bridage GPU dans le relevé, verrou des appels au pilote dans `GpuControlService`, liens PCIe, contrôles de configuration, carte « Bridage » de Monitoring | #4 | voir le plan de #4 | prévue (pas encore fait par #4) |
| `CpuTopology` : `Read()` → `CpuTopologyRead` (topologie ou `Unavailable`), en logique pure sur les tampons de `GetSystemCpuSetInformation` (`ParseCpuSets` : `LogicalProcessor` avec `LogicalProcessorId` groupe,index, cœur physique, L3, classe d'efficacité, parqué) et de `GetLogicalProcessorInformationEx` (`ParseCaches` : tailles de L3). `Build` range en `CacheCluster` (L3/CCD, `HasLargerL3` pour le CCD avec 3D V-Cache) › `EfficiencyClassGroup` (la plus performante d'abord) › `PhysicalCore` › fils SMT ; `IsHybrid`, `TopEfficiencyClass`, `HasMixedL3Sizes`, `ReadParkedFlags()`. `CpuPlatformDetector.IsHybrid` en vient | #5 | `src/PCPerfSuite.Core/Hardware/Cpu/CpuTopology.cs` | livrée ; pour #10, #16, #21 |
| `CoreActivityReader` (PDH, sans administrateur, une requête, noms anglais) : `Sample()` → `CoreActivitySample` par `LogicalProcessorId` : `% Processor Utility` plafonné à 100 (comme le Gestionnaire des tâches), `Parking Status`, `% Processor Performance` ; `HasUtility` / `HasParkingStatus` / `HasPerformance`, `QueryUnavailable` ; `TryParseInstance` (« groupe,index », totaux ignorés). `ParkedShareWindow` : part du temps parqué sur les N derniers relevés. Appels PDH communs dans `PdhNative` | #5 | `src/PCPerfSuite.Core/Hardware/Cpu/CoreActivityReader.cs` | livrée ; pour #10 |
| Parking des cœurs `CoreParkingService` (`ISystemChangeOwner` « core-parking ») : catalogue `CoreParkingCatalog` (CPMINCORES, CPMAXCORES, CPMINCORES1, CPMAXCORES1, SCHEDPOLICY, SHORTSCHEDPOLICY, HETEROPOLICY), `Write` (origine notée avant la première écriture sous les clés `OriginalPowerValues` du tweak, plan retenu dans `CoreParkingOriginScheme`, une seule réactivation du plan, relecture), `Restore` / `RestoreAll`, `Describe` ; préréglages `CoreParkingPresets` ; `CoreTopologySupport` (expérimental, X3D bi-CCD), `AmdVCacheDriver` ; ligne « Cœurs et parking ». Lecture/écriture des plans par powrprof : `PowerPlanValues` (`IPowerPlanValues`, aussi utilisé par `CpuPowerTuningService`) | #5 | `src/PCPerfSuite.Core/Hardware/Cpu/CoreParking/`, `src/PCPerfSuite.Core/PowerSettings/PowerPlanValues.cs` | livrée ; façade : tweak « core-parking » |
| Effets visuels de Windows `WindowsAnimationSettings` (propriétaire `animations` du registre des modifications) : catalogue `WindowsAnimationCatalog` (SPI 0x10xx, ANIMATIONINFO, DRAGFULLWINDOWS, et deux DWORD de HKCU), accès `IAnimationSettingsAccess` (réel : `Win32AnimationSettingsAccess`, SPIF_UPDATEINIFILE\|SPIF_SENDCHANGE, jamais UserPreferencesMask), origine `IAnimationOriginStore` dans `AppSettings.OriginalAnimationValues` (clé présente = modifié par l'app) ; `Apply(cibles)` en un passage puis relecture complète, `ApplyResponsivePreset()`, `RestoreAll()` ; ligne `WindowsAnimationsRowProvider`. Un groupe de profils (#8) qui touche aux animations passe par `Apply`, jamais par SPI directement | #6 | `src/PCPerfSuite.Core/PowerSettings/Animations/` | livrée ; expérimental, à vérifier sur une vraie machine |
| Réglage du profil grisé sous un autre compte : `SessionUser.OtherProfileSettingMessage` (et `DescribeOtherProfileSetting`, pur), `PerformanceTweak.TargetsUserProfile` (grise l'interrupteur dans Optimisation Windows). À reprendre par tout réglage HKCU ou SystemParametersInfo | #6 | `src/PCPerfSuite.Core/Environment/SessionUser.cs`, `src/PCPerfSuite.Core/PowerSettings/PerformanceTweak.cs` | livrée |
| `OfficialInstaller` paramétré par source : `OfficialInstallerSource` (+ `Kind` exe/msi/zip vérifié sur l'en-tête, `ExpectedSha256`, `ExpectedSize`, `MaxBytes` jusqu'à 1,5 Go, `DownloadTimeout` jusqu'à 60 min ; `ExpectedPublisher` null = non signé, jamais lancé, empreinte alors obligatoire) ; `DownloadAndRunAsync`, `DownloadToFileAsync` (téléchargement seul, vérifié, `DownloadOutcome` avec `DownloadFailureKind`), `RunVerifiedAsync` (msi par msiexec de System32 ; une fois l'installeur lancé, plus rien ne l'interrompt), `TryVerifySignedFile`, `CreateWorkFolder` (`%ProgramData%\PCPerfSuite\Installations\<guid>`, créé avec sa liste d'accès ; `%TEMP%` seulement sans élévation), `PurgeStaleWorkFolders`, `InstallOutcome.Failure`. L'éditeur est lu sur le certificat validé par WinVerifyTrust (`AuthenticodeVerifier`), plus sur le premier certificat du fichier | #7 | `src/PCPerfSuite.Core/Installations/OfficialInstaller.cs`, `AuthenticodeVerifier.cs` | livrée ; pour #10, #13, #18, #23 |
| Dossier sécurisé `ProgramDataFolder` : `%ProgramData%\PCPerfSuite` créé avec une ACL protégée (SYSTEM et Administrateurs en écriture, Utilisateurs en lecture) et l'intégrité « Élevé » ; un dossier existant n'est accepté que s'il appartient aux Administrateurs, à SYSTEM ou à TrustedInstaller sans écriture pour un autre compte, et aucun élément du chemin ne peut être une jonction. `TryEnsure(segments)`, `TryGetExisting`, `Inspect` (diagnostic). Outils portables dans `Tools\<id>\<version>` | #7 | `src/PCPerfSuite.Core/Installations/ProgramDataFolder.cs` | livrée ; pour #10, #13, #18, #23 |
| Extraction d'archive `SafeZipExtractor` (`ExtractAll`, `ExtractSingle` par motif `Wildcard`) : zip-slip, lecteurs, flux « : », noms réservés refusés avant toute écriture, taille décompressée et nombre d'entrées bornés | #7 | `src/PCPerfSuite.Core/Installations/SafeZipExtractor.cs` | livrée ; pour #18 (pilotes en archive), #23 |
| Signature des fichiers publiés par Denis `CatalogSignature` (ECDSA P-256, IEEE P1363, contexte signé avec le contenu) ; outil `tools/catalogue/depot/outils/signer.cs` | #7 | `src/PCPerfSuite.Core/Installations/CatalogSignature.cs` | livrée ; pour la table de scores de #12 (même clé, autre contexte) |
| Lancement sans élévation `UnelevatedLauncher.TryLaunch`, `TryShowInExplorer` (ShellExecute par le shell du bureau ; aucun repli lancé par le processus élevé, pas même explorer.exe : sans shell, échec avec le chemin) | #7 | `src/PCPerfSuite.Core/Installations/UnelevatedLauncher.cs` | livrée ; pour #18 (DDU), #23 (OpenRGB) |
| Catalogue d'outils : définitions figées `ToolCatalog` (hôtes, éditeur relevé sur le vrai fichier ou non signé, type, détection, licence), versions signées `ToolCatalogStore` (copie intégrée, cache revérifié, catalogue en ligne pris seulement s'il est signé et plus récent), actions `ToolboxActions`, détection `ToolDetection`, registre `ToolboxChanges`, ligne `ToolboxRowProvider`. Relevé du 01/10/2026 : non signés Prime95, FurMark 2, y-cruncher, 7-Zip et **OpenRGB 1.0 (MSI et zip)** ; OpenRGB publie les mêmes fichiers sur GitHub (CalcProgrammer1, que suit winget) et sur Codeberg | #7 | `src/PCPerfSuite.Core/Installations/ToolCatalog*.cs`, `Toolbox*.cs` ; génération et signature : `tools/catalogue/` | livrée (expérimental) ; #18 (DDU), #23 (OpenRGB : à installer par l'utilisateur, faute de signature) |
| Dépôt d'un fichier dans les Téléchargements de la personne connectée `UserDownloads.TryDeposit` : copie sous le jeton du shell de la session (explorer.exe), jamais en administrateur, donc seulement là où l'utilisateur peut écrire lui-même (Téléchargements redirigés, jonctions, noms courts : Windows juge) ; nom libre, marque « venu d'Internet » ; compte connecté même si l'app tourne sous un autre compte | #7 | `src/PCPerfSuite.Core/Installations/UserDownloads.cs` | livrée ; pour #18 (sauvegardes proposées), #13 |
| Groupes de profils : modèle `ProfileGroup` (parties CPU, GPU, ventilation ; `Kind` « valeurs » ou « origine » en chaîne, partie null = ne pas toucher ; `Usage`, `Origin` « manuel »/« genere », `EditedByUser`, `Revision`, `[JsonExtensionData]` partout), bloc `AppSettings.ProfileGroups` (`ProfileGroupsSettings` : groupes, groupe actif `ProfileGroupActiveState` = état **retenu** relu, suspensions) ; planification pure `CpuGroupPlanner`, `GpuGroupPlanner`, `FanGroupPlanner`, ordre `ApplyDirection`, rapport `ProfileGroupReport`, conformité `ProfileGroupConformity`, modifications `ProfileGroupEditor` | #8 | `src/PCPerfSuite.Core/Profiles/` | livrée (expérimental) ; `StartupState` ajouté par #9 ; pour #15, #16, #22, #24 |
| API des onglets pour les groupes : `ICpuGroupTarget`, `IGpuGroupTarget`, `IFanGroupTarget` (`ReadState`, `Apply(plan, contexte)` → rapport + état retenu, `FlushPendingManualWrites`, `IsRaised`, `WhenReady` pour les ventilateurs), implémentées dans `CpuControlViewModel.Groups.cs`, `GpuControlViewModel.Groups.cs`, `FanCurvesViewModel.Groups.cs` ; les commandes « Appliquer » des onglets y passent aussi. Orchestrateur `ProfileGroupApplier.ApplyAsync(groupe, ProfileGroupApplyOptions)` (bail, attente des ventilateurs, ordre, période probatoire, rapport consolidé), sur le fil d'interface. `MakeStartupState = false` : rien n'est enregistré comme état de démarrage et tout est rendu à la fermeture (D7) | #8 | `src/PCPerfSuite.Core/Profiles/ProfileGroupTargets.cs`, `ProfileGroupApplier.cs` ; onglets dans `src/PCPerfSuite.App/ViewModels/` | livrée (expérimental) ; pour #9 |
| Bail de réglage `TuningLease` : `TryAcquire(demandeur, libellé, raison, isAlive)` → poignée IDisposable, un seul détenteur, jamais d'éviction ; `CanWrite`, `RefusalText`, `Sweep` (détenteur mort libéré). Instance unique `MainViewModel` ; bannière et grisage des onglets par `TuningStatusViewModel` + vue `TuningBanner`. C'est le seul moyen de suspendre #9 | #8 | `src/PCPerfSuite.Core/Profiles/TuningLease.cs`, `src/PCPerfSuite.App/ViewModels/TuningStatusViewModel.cs` | livrée ; pour #9, #10, #11, #14, #15, #16 |
| Identité du processeur `CpuIdentity.Of(CpuPlatform)` ; `Matches(saved, current)` strict (noms connus des deux côtés et égaux) : des watts ne se posent que sur le même processeur. `GpuIdentity.IsSameCard` : même carte (nom ou `PciDeviceId` connus des deux côtés, sous-système compris), plus strict que `Matches` | #8 | `src/PCPerfSuite.Core/Hardware/Cpu/CpuIdentity.cs`, `src/PCPerfSuite.Core/Hardware/Gpu/GpuIdentity.cs` | livrée |
| Réglages du plan écrits par un groupe : `ProfileGroupPowerPlanChanges` (`ISystemChangeOwner` « groupes-plan-alimentation ») : origine notée **par plan** avant la première écriture (`AppSettings.ProfileGroupPowerOrigins`), un seul `TryWrite` par lot, relecture, `RestoreActiveScheme` pour « origine ». Les écritures manuelles de l'onglet Processeur n'y passent pas | #8 | `src/PCPerfSuite.Core/Profiles/ProfileGroupPowerPlanChanges.cs` | livrée |
| Ventilateurs appliqués sans en faire l'état de démarrage : `FanStartupOverrides` (configuration d'avant mise de côté, rendue à l'enregistrement jusqu'à une modification à la main) | #8 | `src/PCPerfSuite.Core/Profiles/FanStartupOverrides.cs` | livrée ; pour #9 |
| Prudence des groupes : `ProfileGroupProbation` (composant `groupe-profils`, actions `application` et `demarrage`, 30 min ; TDR relu chaque minute par la page, échec sur sécurité thermique, clôture à la fermeture propre), `ProfileGroupStartupCheck` (période ouverte avant la construction du GPU quand les onglets reposent l'état risqué du groupe actif), gestionnaire `ProfileGroupRecoveryHandler` + politique `ProfileGroupIncidentPolicy` (décision 5 de Denis : arrêt anormal, écran bleu, TDR à moins de 30 min, cause non établie) | #8 | `src/PCPerfSuite.Core/Profiles/ProfileGroupProbation.cs`, `ProfileGroupRecovery.cs` | livrée ; #15 ajoute ses règles |
| `ApplicationMatch` (mode `chemin` ou `nom` en chaîne, chemin complet normalisé par `ApplicationPaths.Normalize`, éditeur facultatif, `ApplicationPaths.SuggestsNameMode` pour un dossier versionné) ; lecteur du premier plan `ForegroundAppReader` (une lecture par relevé, processus relu au changement de fenêtre par un handle PROCESS_QUERY_LIMITED_INFORMATION refermé aussitôt, shell et PCPerfSuite écartés par `DisplayTopology.IsIgnoredForegroundWindow`, applications du Store rapportées à leur processus, plein écran par le shell ou par le cadre DWM qui couvre son écran) ; éditeurs `ApplicationPublisherCache` (Authenticode hors du fil d'interface, gardé tant que le fichier ne change pas) | #9 | `src/PCPerfSuite.Core/Processes/` | livrée ; pour #21, #22 |
| Bascule automatique : classifieur `UsageClassifier` (pur, seuils `UsageThresholds` expérimentaux), historique `UsageHistory`, `UsageHistoryStore`, `UsageHistoryWriter` (`AppDataPaths.UsageFile`), générateur `UsageProfileGenerator` et `FanCurveShaper`, politique `AutoSwitchPolicy` et état de session `AutoSwitchSession`, réglages `AppSettings.AutoSwitch` (`AutoSwitchSettings`, règles `AutoSwitchRule`, `AutoSwitchRules.Find`), choix du groupe `UsageGroupResolver`, reprise `AutoSwitchRecoveryHandler`, ligne `AutoSwitchRowProvider` ; moteur `AutoProfileSwitcher` et sous-onglet `AutoProfilesViewModel` | #9 | `src/PCPerfSuite.Core/Profiles/`, `src/PCPerfSuite.App/ViewModels/` | livrée (expérimental) |
| Emplacement OC des groupes générés : `IUsageOverclockSource.For(usage, gpu)` → `UsageOverclock(Gpu, Explanation)`, prioritaire sur l'OC enregistré dans l'onglet GPU ; vide dans #9 (`EmptyUsageOverclockSource`) | #9 | `src/PCPerfSuite.Core/Profiles/UsageProfileGenerator.cs` | prévu ; #15 remplit la partie GPU, #16 ajoute un membre processeur |
| Signal « réglage manuel » `TuningStatusViewModel.ManualWrite` / `NoteManualWrite(source)` : levé au geste dans les onglets Processeur, GPU et Ventilateurs, par un profil d'onglet et par une application à la main de la page Profils ; jamais par une relecture, une réapplication au lancement ou au réveil, ni un groupe | #9 | `src/PCPerfSuite.App/ViewModels/TuningStatusViewModel.cs` | livrée ; pour tout pilote automatique |
| Modes secondaires de l'exe (liste fermée, lus avant le mutex d'instance unique ; `--demarrage-windows` inchangé) | #10 | `src/PCPerfSuite.App/App.xaml.cs` | prévue |
| Moteur de charge et noyaux CPU vérifiés | #10 | à préciser par #10 | prévue |
| Noyaux GPU vérifiés | #11 | à préciser par #11 | prévue |
| `DiagnosticReport` (JSON v1, rendus HTML et texte) | #12 | à préciser par #12 | prévue |
| Sessions NVAPI, ADLX et IGCL partagées, à compteur de références | #14 | `src/PCPerfSuite.Core/Hardware/Gpu/` (`NvApiSession`, `AdlxSession`, `IgclSession`) | prévue |
| Chien de garde `Watchdog` (mode `--watchdog`) et essai à retour automatique `TrialGuard` | #15, phase 1 (ou #17 s'il passe avant, mêmes noms et emplacement) | `src/PCPerfSuite.Core/Safety/` | prévue |
| Service des périphériques | #18 | `src/PCPerfSuite.Core/Devices/` | prévue |
| Socle d'éclairage | #23 | `src/PCPerfSuite.Core/Hardware/Lighting/` | prévue |
