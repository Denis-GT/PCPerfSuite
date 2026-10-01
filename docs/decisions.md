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

## Règles communes

- **Dossier de données** : tout fichier écrit par l'app passe par `AppDataPaths.Current`
  (`src/PCPerfSuite.Core/Environment/AppDataPaths.cs`, namespace `PCPerfSuite.Core.SystemInfo`), jamais par un chemin
  `%LOCALAPPDATA%\PCPerfSuite` codé en dur. On ajoute une **propriété nommée** (fichier ou sous-dossier) dans
  AppDataPaths, jamais un `Path.Combine` sur `Root` ailleurs ; on relit le chemin au moment d'écrire, sans le figer
  dans un champ statique. Noms réservés : `bench\` (#10), `rapports\` (#12, rapports et étalons), `usage.json` (#9),
  le journal de session (#4, nom et format choisis et documentés ici par #4), un journal des opérations disque (#19),
  `sauvegardes-pilotes\` comme dossier proposé par défaut (#18). La racine se choisit une seule fois, en tête
  d'`App.OnStartup`, par `AppDataPaths.TryUseRoot` (#13 : racine portable si `portable.flag` est à côté de l'exe).
  **Hors de ce dossier, volontairement** : `%TEMP%\PCPerfSuite` (téléchargements d'OfficialInstaller) et
  `%ProgramData%\PCPerfSuite` (outils portables et fichiers de bench, dossier sécurisé de #7).
- **Réglages** : toute lecture-modification-écriture passe par `AppSettingsStore.Update()`, avec seulement des
  mutations dans le lambda (le verrou est tenu pendant). Pas de `Load()` puis `Save()`. Les nouveaux modèles
  enregistrent leurs enums en chaînes. Reste en `Load()`/`Save()`, à convertir par son propriétaire :
  `WindowsPerformanceSettingsService.RememberOriginalValue` (#5, #6). Les onglets Processeur et GPU sont convertis (#2).
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
  - Reprise : un gestionnaire `IStartupRecoveryHandler` (étape `RecoveryStage`, composants) s'inscrit dans
    `src/PCPerfSuite.App/StartupRecoveryHandlers.cs`. Il reçoit, avant toute fenêtre, ses opérations restées en cours
    avec leur `IncidentQualification`, ne montre aucune fenêtre, et peut lever sans gêner les autres. Le bilan
    (`StartupRecoveryReport`) est passé à `MainViewModel.StartupRecovery`.
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
| Registre des modifications `ISystemChangeOwner`, `SystemChangeRegistry` | #1 | `src/PCPerfSuite.Core/SystemChanges/`, instance `MainViewModel.SystemChanges` | livrée ; aucun propriétaire inscrit |
| `GpuIdentity` (marque en chaîne, nom, `PciVendorId`, `PciDeviceId`, `PciSubsystemId` au format du registre PCI, `Luid` facultatif) : `Matches(saved, current)` (marque égale, chaque champ connu des deux côtés égal, LUID non comparé), `ParsePciId`, `Describe()`. Lue par `GpuControlService.Identity` (NVAPI, ADLX, IGCL), enregistrée dans `GpuControlSettings.OverclockGpu` ; ligne `GpuIdentityRowProvider` (« Carte GPU pilotée ») | #2 | `src/PCPerfSuite.Core/Hardware/Gpu/GpuIdentity.cs` | livrée ; LUID rempli par #11 ; à compléter par #8, #14, #15, #22 |
| Carte dédiée d'après le nom Windows : `DedicatedGpuName.VendorOf(name)` / `VendorsOf(names)` → `GpuVendor?` (null pour un GPU intégré, virtuel ou inconnu). Distingue les Arc dédiées (A770, B580, Pro A40) des Arc intégrées (« Arc Graphics », « 140V GPU », « B390 GPU »), et les Radeon RX/Pro des iGPU AMD (« Radeon Graphics », « 780M », « RX Vega 11 Graphics »). Repli quand aucune API constructeur n'a répondu, seule source aujourd'hui ; si `GpuIdentity` porte un jour un indicateur « intégré » lu par un backend, il passera avant | correctif | `src/PCPerfSuite.Core/Hardware/Gpu/DedicatedGpuName.cs` | livrée ; lue par `GpuControlViewModel.UnavailableMessage` |
| Sécurité thermique, en logique pure : `ThermalGuard` (seuils `ThermalLimit` par capteur, délai, perte de lecture facultative, heure injectée, verdict `ThermalState`), partagée par le CPU (98 °C / 15 s) et le GPU. `GpuThermalSafety` (cœur ≥ 90 °C 15 s, point chaud ≥ 105 °C s'il est lu ; retour d'origine, relecture, évènement `EmergencyRestored` ; `Restart()` au réveil, `NoteNoReading()` pour un chien de garde ; `ReadingWatchdog` distingue une veille d'une panne de relevé, sur une heure monotone), armée par `GpuControlService.RefreshArming` d'après `GpuOverclockRaise.IsRaised` sur les seuls blocs écrits par l'app ; cible `IGpuOverclockTarget` ; ligne `GpuThermalSafetyRowProvider`. Décision au réveil commune CPU/GPU : `TuningResume.Decide` (relire / ne pas réappliquer après un déclenchement / réappliquer) | #2 | `src/PCPerfSuite.Core/Safety/` | livrée ; reprise par #10, #11, #15, #16 avec leurs seuils |
| Appliquer puis relire l'OC GPU : `GpuControlService.ApplyAndVerify(GpuOverclockRequest)` → `GpuApplyReport` (demandé / retenu / `GpuApplyStatus` par réglage), comparaison pure `GpuApplyComparison.Compare` ; plages de repli NVAPI signalées par `GpuOverclockSnapshot.CoreOffsetRangeIsFallback` / `MemoryOffsetRangeIsFallback` | #2 | `src/PCPerfSuite.Core/Hardware/Gpu/GpuOverclockApply.cs` | livrée ; pour #8, #15 |
| Fonctions d'un module PawnIO : `PawnIoModuleFunctions.Parse` (chaînes `ioctl_…` du module), `PawnIoModuleInfo` (source, version, fonctions, SHA-256), `PawnIoDriver.LoadedModules` et `PawnIoDriver.LoadFailures` (dernier échec par module) ; modules livrés par l'app `ShippedPawnIoModules` (IntelMSR 0.2.11 dans `PawnIO\`, choisi avant celui de LHM par `PawnIoModuleChoice`) ; ligne `PawnIoModulesRowProvider` | #2 | `src/PCPerfSuite.Core/Hardware/Cpu/PawnIoModules.cs` | livrée ; reprise par #4 |
| Inventaire des écrans `DisplayTopology.Read()` → `DisplayTopologySnapshot` : `DisplayMonitor` (HMONITOR, bornes et zone de travail en pixels physiques, DPI et échelle, principal, nom du GPU) et un `DisplayTarget` par écran physique, plusieurs si dupliqués (nom convivial, chemin du moniteur, EDID fabricant en code PNP et produit, sortie `DisplayOutputKind`, connecteur, LUID et chemin de l'adaptateur, fréquence). Filtre des fenêtres du premier plan `IsIgnoredForegroundWindow` (PCPerfSuite, barre des tâches, bureau, menu Démarrer, recherche, Alt+Tab) et fenêtre → écran `MonitorFromWindow` / `FindMonitor`, pour #9. Identité enregistrée `DisplayIdentity` (chemin, EDID, nom, empreinte du numéro de série ; jamais `\\.\DISPLAYn`) et `DisplayIdentityResolver` ; numéros de série WMI `MonitorSerials` (empreinte seulement) ; noms `DisplayNames` ; ligne « Écrans » `DisplaysRowProvider`. Win32 seulement, aucun WinRT (D10) | #3 | `src/PCPerfSuite.Core/Hardware/Displays/` | livrée ; pour #9, #17, #22 |
| `SessionJournal` (format et règles : voir « Journal de session » plus haut) ; qualification d'incident `IncidentClassifier.Qualify` (même démarrage de Windows = interrompu ; sinon événements en direct avant le premier redémarrage, événements d'après coup entre ce redémarrage et le suivant) et `ClassifyAll` (incidents des 30 derniers jours) ; lecture du journal Système `SystemEventReader` (Kernel-Power 41, WER 1001, 6008, démarrages et arrêts propres, WHEA 17/18/19/47, Display 4101, disk 7/51/153, Kernel-Processor-Power 37 ; champs techniques seulement, jamais le texte des messages) ; lignes « Journal de session » et « Journaux Windows » | #4 | `src/PCPerfSuite.Core/Safety/SessionJournal.cs`, `src/PCPerfSuite.Core/Safety/Events/` | livrée ; pour #8 à #21 |
| Étape `StartupRecovery` : logique pure `StartupRecovery` (ordre fixe des étapes, gestionnaires isolés, journal Système lu 2 s au plus, opérations closes avec leur qualification, compactage), appelée par `App.OnStartup` après le mutex d'instance et avant la fenêtre ; gestionnaires dans `StartupRecoveryHandlers` | #4 | `src/PCPerfSuite.Core/Safety/StartupRecovery.cs`, `src/PCPerfSuite.App/App.xaml.cs`, `src/PCPerfSuite.App/StartupRecoveryHandlers.cs` | livrée ; aucun gestionnaire inscrit |
| Lecteur CfgMgr32 (graine du service des périphériques de #18) | #4 | `src/PCPerfSuite.Core/Devices/` | prévue (pas encore fait par #4) |
| Bail de cadence `HardwareMonitorService.RequestCadence(demandeur, intervalle, groupes)` → IDisposable, logique pure `CadenceLeases` : le plus rapide l'emporte sur la cadence imposée, l'automatique et le plafond GPU, sans les modifier ; groupe sous bail relu en mode éco ; tick minimal 100 ms (`MinTickInterval`) ; `IsCadenceLeased(groupe)` | #4 | `src/PCPerfSuite.Core/Hardware/HardwareMonitorService.Cadence.cs`, `CadenceLeases.cs` | livrée ; pour #10, #11 |
| Bridage CPU dans le relevé `HardwareSnapshot.CpuThrottle` (`CpuThrottleReading` : thermique, PROCHOT, puissance, courant, TjMax et décalage TCC, fréquences, valeurs AMD, source et cause) : MSR Intel (IntelMSR, 0x64F tenté), PM table AMD par version (`PmTableLayouts`), compteurs Windows toujours ; balayage par cœur seulement sous bail ; ligne « Raisons de bridage CPU » ; dernier relevé `HardwareMonitorService.LastSnapshot` | #4 | `src/PCPerfSuite.Core/Hardware/Cpu/Throttle/` | livrée ; pour #10, #11, #12 |
| Bridage GPU dans le relevé, verrou des appels au pilote dans `GpuControlService`, liens PCIe, contrôles de configuration, carte « Bridage » de Monitoring | #4 | voir le plan de #4 | prévue (pas encore fait par #4) |
| `CpuTopology` (CPU sets : cœur, fils SMT, L3/CCD, classe d'efficacité ; logique pure) | #5 | `src/PCPerfSuite.Core/Hardware/Cpu/` | prévue |
| `OfficialInstaller` paramétré par source | #7 | `src/PCPerfSuite.Core/Installations/` | prévue |
| Dossier sécurisé `%ProgramData%\PCPerfSuite` (ACL restreinte, jonctions refusées) | #7 | `src/PCPerfSuite.Core/Installations/` | prévue |
| API appliquer/capturer des onglets Processeur, GPU et Ventilateurs, orchestrateur des groupes | #8 | `src/PCPerfSuite.Core/Profiles/` | prévue |
| Bail de réglage `TuningLease` (un seul pilote automatique des réglages CPU/GPU/ventilateurs à la fois) | #8 | `src/PCPerfSuite.Core/Profiles/` | prévue |
| `ApplicationMatch` (chemin complet normalisé, éditeur facultatif) et lecteur du premier plan | #9 | `src/PCPerfSuite.Core/Processes/` | prévue |
| Modes secondaires de l'exe (liste fermée, lus avant le mutex d'instance unique ; `--demarrage-windows` inchangé) | #10 | `src/PCPerfSuite.App/App.xaml.cs` | prévue |
| Moteur de charge et noyaux CPU vérifiés | #10 | à préciser par #10 | prévue |
| Noyaux GPU vérifiés | #11 | à préciser par #11 | prévue |
| `DiagnosticReport` (JSON v1, rendus HTML et texte) | #12 | à préciser par #12 | prévue |
| Sessions NVAPI, ADLX et IGCL partagées, à compteur de références | #14 | `src/PCPerfSuite.Core/Hardware/Gpu/` (`NvApiSession`, `AdlxSession`, `IgclSession`) | prévue |
| Chien de garde `Watchdog` (mode `--watchdog`) et essai à retour automatique `TrialGuard` | #15, phase 1 (ou #17 s'il passe avant, mêmes noms et emplacement) | `src/PCPerfSuite.Core/Safety/` | prévue |
| Service des périphériques | #18 | `src/PCPerfSuite.Core/Devices/` | prévue |
| Socle d'éclairage | #23 | `src/PCPerfSuite.Core/Hardware/Lighting/` | prévue |
