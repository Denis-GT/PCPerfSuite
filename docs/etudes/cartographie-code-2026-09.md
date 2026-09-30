# Cartographie du code — feuille de route PCPerfSuite (28/09/2026)

Produite par 4 agents en lecture seule sur develop @ 517d3a1. Les numéros de ligne datent de ce commit : ils bougeront, fiez-vous d’abord aux noms de classes et de méthodes.


## Mesure et diagnostic (relevé des capteurs, cadence, catalogue de métriques, Monitoring, diagnostic « Compatibilité de ce PC ») — couvre F1, F2, F3, F5, F13, F18, plus la partie mesure de F4, F9 et F19

### Architecture

Un seul service matériel, HardwareMonitorService (src/PCPerfSuite.Core/Hardware/HardwareMonitorService.cs, 1280 lignes), créé une fois dans MainViewModel.cs:16. Il enveloppe LibreHardwareMonitorLib 0.9.6 : Computer ouvert en tâche de fond (106-128, avec IsPsuEnabled à la ligne 117, et IsReady à la ligne 100). S'y ajoutent des lectures hors LHM : la charge CPU par PDH « % Processor Utility » (PdhCounterSampler, ligne 45), la batterie par IOCTL (BatteryReader), les FPS RTSS du processus au premier plan et les ventilateurs des portables en WMI, en lecture seule.
Une seule boucle lit les capteurs : SamplingLoop (SamplingLoop.cs), sur un thread dédié en priorité AboveNormal (ligne 35), à échéances absolues. Un tick qui déborde fait sauter les suivants. Seul MonitoringViewModel l'instancie (MonitoringViewModel.cs:683-684), et c'est le seul appelant de GetSnapshot(epoch, tick) (HardwareMonitorService.cs:134-160).
Les capteurs forment 9 groupes (SensorGroup : CpuLoad, Cpu, Gpu, Memory, Motherboard, Storage, Network, Fps, Battery ; SensorReadSchedule.cs:4-20). Chaque groupe est relu tous les N ticks. La cadence de base vient du réglage MonitoringRefreshMs : 1000 ms par défaut, bornée entre 100 et 60000 ms (RefreshRates.cs). En automatique, un groupe coûteux est espacé pour ne pas dépasser 5 % du temps en lecture (AutoReadBudget, ligne 36). Plafonds : 5 s en général (ligne 39) et 750 ms pour le GPU (SensorReadSchedule.cs:86-90). Un CPU Intel coûte environ 30 ms, il est donc relu toutes les ~600 ms. L'utilisateur peut imposer une cadence par groupe (SetManualInterval, ligne 361), enregistrée dans settings.json sous SensorGroupIntervalsMs.
Chaque tick produit un HardwareSnapshot immuable (HardwareModels.cs:303-340) avec CPU, GPU, mémoire, carte mère, ventilateurs, disques, réseau, batterie, PsuPowerWatts, les temps de lecture, GroupsRead et GroupsEverRead. GroupsEverRead sert à distinguer « -- » (pas encore lu) de « N/D » (ce PC ne fournit pas la valeur).
Diffusion : le relevé est posté au thread d'interface. Là, Apply (MonitoringViewModel.cs:945-977) alimente les tuiles et l'historique (SampleHistory de 90 points par métrique), puis lève SnapshotUpdated et MetricsUpdated (lignes 565-568). Chaque abonné est appelé séparément sous try/catch, les erreurs vont au CrashLog (InvokeEachSafely, 982-990). Si l'interface n'a pas encore appliqué le relevé précédent, le tick est sauté (_applyPending, 756-760).
Mode éco, fenêtre cachée (809-865) : seuls les groupes déclarés par les IBackgroundSensorConsumer sont relus. Ce sont Overlay, FanCurves et CpuControl, en liste codée en dur dans MainViewModel.cs:152-154. Mémoire, disques et réseau en pause sortent alors vides du relevé (HardwareMonitorService.cs:248-259).
Abonnés actuels : Processes, FanCurves (courbes et chien de garde), GpuControl (affichage et « perf cap » 1 fois/s, NVIDIA seulement), CpuControl (sécurité thermique 98 °C pendant 15 s, CpuControlService.cs:17-21 et 104-128), Overlay, Compatibility.
Les métriques sont décrites de façon déclarative dans MetricCatalog.All (MetricCatalog.cs:225-284), plus deux catalogues dynamiques : MonitoringSensorCatalog pour les sondes, tensions, disques et ventilateurs, PowerMetricCatalog pour la conso totale et la batterie. Chaque métrique a un Id stable, un ReadGroup et un UnavailableHint.
Diagnostic : CompatibilityViewModel (693 lignes) construit des CompatibilityRow(Title, Status, Detail, IsSupported, IsPersonal) à partir de MachineInfo, des ViewModels et du dernier MetricSample. Il liste aussi les métriques N/D avec leur raison. Le rapport ne sort qu'en texte vers le presse-papiers, avec le compte Windows masqué (CopyReport 620-692).
Réglages : AppSettingsStore, JSON atomique dans %LOCALAPPDATA%\PCPerfSuite\settings.json. À utiliser via Update() pour toute lecture-modification-écriture (AppSettingsStore.cs:409-418).
Garde-fous : toute lecture matérielle est best-effort (null ou false, jamais d'exception). Le dernier relevé est resservi au plus 3 ticks en cas d'exception (lignes 62-66). MachineInfo.SoftwareFanControlRefused refuse toute écriture ventilateur hors PC de bureau avéré. Les ventilateurs sont rendus au BIOS à la fermeture, avec le mutex ISA.

### Conventions

- Code et identifiants en anglais (HardwareMonitorService, GetSnapshot), commentaires XML, messages et interface en français. Commits en français au présent, 3e personne, souvent sans accents (« Ajoute… », « Corrige… », « Rend… »).
- Best-effort (règle 2 du CLAUDE.md) : toute méthode Core qui touche au matériel, à WMI, PDH ou IOCTL attrape Exception et renvoie null ou false. Exemples : BatteryReader.Read (BatteryReader.cs:46-60), SystemMemoryReader.Read, MemoryModuleReader, DiskModelReader, MachineInfo.Read. La raison d'un échec est gardée dans une propriété (UnavailableReason, LastError, ModulesUnavailableReason) pour être affichée.
- Valeur absente : MetricReading.Missing affiche « -- » (groupe pas encore lu), MetricReading.Unavailable affiche « N/D » (MetricCatalog.cs:33-37). Le choix se fait dans MetricCatalog.Absent (339-342) d'après HardwareSnapshot.GroupsEverRead. Une nouvelle métrique passe par MetricCatalog.Numeric ou Rate (303-367) avec un Id stable (il est persisté, ne jamais le renommer), un ReadGroup et un hint.
- Rédaction des hints (règle 3) : dire si la cause est le matériel ou le pilote, la marque non prise en charge, ou l'absence d'administrateur ou de PawnIO (BuildLowLevelSensorHint, MetricCatalog.cs:160-167). Ils finissent souvent par « Ce n'est pas un dysfonctionnement de PCPerfSuite. » et renvoient à « Paramètres › Compatibilité de ce PC ».
- Toute nouvelle source matérielle ajoute une ligne CompatibilityRow dans CompatibilityViewModel.BuildRows (106-219), avec les trois états distingués : absent du matériel, marque non gérée, sans admin. Une donnée personnelle se marque IsPersonal (masquée dans le rapport copié, lignes 633-636).
- Support non vérifié sur une vraie machine : marqué « expérimental » (ILaptopFanProvider.IsVerified, CpuMaxWattsInfo.IsExperimental, bandeau GPU/CPU du commit 7ed24d9).
- Abonnés au relevé : handlers sur le thread d'interface, travail O(1). Un traitement lourd part sur le pool, sinon il gonfle « Interface et abonnés (Apply) » et fait sauter des ticks. Un abonné qui doit tourner fenêtre cachée implémente IBackgroundSensorConsumer.AddRequiredGroups (IBackgroundSensorConsumer.cs:9-13), s'appuie sur BackgroundSensorNeeds pour la logique testable, et s'ajoute au tableau de MainViewModel.UpdateEcoMode (152-154).
- Ne jamais appeler HardwareMonitorService.GetSnapshot depuis un deuxième consommateur : l'échéancier (epoch/tick), le cache et les moyennes de coût sont partagés. Il faut s'abonner à MonitoringViewModel.SnapshotUpdated ou MetricsUpdated.
- Persistance : AppSettingsStore.Update(mutate) pour toute lecture-modification-écriture. Clés en chaînes plutôt qu'en valeurs d'enum (FanCategoryInfo.Key, SensorGroupIntervalsMs[group.ToString()]). Profils tolérants : null veut dire « ne pas toucher », une entrée inconnue est ignorée et signalée (CpuProfile.cs, FanProfileMatcher). Un historique volumineux (bench, usage) va dans un fichier à part dans %LOCALAPPDATA%\PCPerfSuite\ plutôt que dans settings.json.
- Écritures matérielles : relire ce que le matériel a réellement retenu (CPU PL1/PL2, GPU). Rien n'est appliqué au lancement sans la case « Appliquer au démarrage », et tout est rendu d'origine en quittant. Garde thermique sur le modèle de CpuControlService (98 °C pendant 15 s). Contrôleur embarqué des portables en lecture seule (règle 5 ; MachineInfo.SoftwareFanControlRefused, MachineInfo.cs:41).
- Tests xUnit ([Fact]) sans faux objets : de vrais snapshots et le vrai catalogue (tests/PCPerfSuite.App.Tests/TestData.cs:8-9). La logique de décision est extraite dans des classes statiques pures, testables sans matériel (CpuMaxWattsResolver, FanProfileMatcher, GpuFanPairing, EmptyHeaderDetector, BackgroundSensorNeeds, SensorReadSchedule). Côté Core, ces classes restent internal grâce à InternalsVisibleTo Core.Tests (PCPerfSuite.Core.csproj). Les noms de tests mêlent phrase française en snake_case (SensorReadScheduleTests) et forme anglaise Sujet_Cas_Attendu (BackgroundSensorNeedsTests).
- Nouvel onglet de navigation : une entrée NavEntry dans MainViewModel.NavItems (99-110), une vue dans MainWindow.xaml (108-127) affichée selon CurrentPage.Title, et un Dispose protégé par DisposeSafely dans MainViewModel.Dispose (191-215), en respectant l'ordre (monitoring arrêté avant ventilateurs, GPU et CPU).

### F1

**Déjà dans le code**

Aucun benchmark : pas de charge générée, pas de score, pas de résultat enregistré. Ce qui existe en partie : (1) le bouton « Remettre à zéro les stats » du Monitoring, prévu « avant un benchmark » (MonitoringViewModel.ResetMetricStats, MonitoringViewModel.cs:921-935), avec min, moyenne et max par métrique depuis la remise à zéro (Utils/RunningStats.cs) ; (2) FPS moyen, 1 % et 0,1 % low calculés sur les 1024 derniers temps de frame de RTSS (src/PCPerfSuite.Core/Overlay/RtssFrameStatsReader.cs, TryReadForeground ligne 68), réutilisables pour un bench « en jeu » ; (3) le test de santé des disques DiskHealthService.CheckAsync (santé, usure, heures, erreurs E/S : un état, pas une mesure de performance) ; (4) tous les capteurs à surveiller pendant une charge (températures, puissances CPU/GPU/PSU/batterie, horloge max CPU, horloges GPU, ventilateurs).

**À réutiliser**

- src/PCPerfSuite.App/ViewModels/MonitoringViewModel.cs:565-568 — événements SnapshotUpdated et MetricsUpdated, à écouter depuis un enregistreur de bench
- src/PCPerfSuite.Core/Hardware/HardwareModels.cs:303-340 — HardwareSnapshot immuable (CapturedAtUtc, GroupsRead), à stocker tel quel dans l'enregistrement
- src/PCPerfSuite.App/Metrics/MetricCatalog.cs:225-284 — MetricCatalog.All : Id et Read(sample) pour extraire des séries nommées
- src/PCPerfSuite.App/Utils/RunningStats.cs:10-43 — min, moyenne et max cumulés
- src/PCPerfSuite.Core/Hardware/Cpu/CpuPlatformDetector.cs:55-101 — nom, famille, modèle, IsHybrid du CPU (clé pour comparer aux scores de référence)
- src/PCPerfSuite.Core/Processes/ProcessService.cs:159-164 — GetActiveProcessorCount(ALL_PROCESSOR_GROUPS) pour dimensionner les threads du bench CPU
- src/PCPerfSuite.Core/Hardware/Cpu/CpuControlService.cs:104-128 — modèle de coupure thermique (98 °C tenus 15 s) à reprendre pour arrêter un bench
- src/PCPerfSuite.Core/Overlay/RtssFrameStatsReader.cs:7-13 — RtssFrameStats (Fps, AverageFps, 1 % low, 0,1 % low)
- src/PCPerfSuite.Core/Hardware/SamplingLoop.cs:17-125 — boucle à échéances absolues, réutilisable pour cadencer les phases du bench
- src/PCPerfSuite.App/Controls/Sparkline.cs — courbe avec repère, pour afficher température, fréquence et puissance pendant le test
- src/PCPerfSuite.Core/PowerSettings/AppSettingsStore.cs:409-418 — Update() pour les réglages du bench (sélection des composants, durée)

**Fichiers à toucher**

- nouveau src/PCPerfSuite.Core/Benchmark/ (IBenchmark, CpuBenchmark, MemoryBenchmark, DiskBenchmark, GpuBenchmark, PowerStressBenchmark, BenchRunner, BenchResult)
- nouveau src/PCPerfSuite.Core/Benchmark/SensorRecording.cs (enregistrement des snapshots pendant le test, horodatés et dédoublonnés)
- src/PCPerfSuite.Core/Hardware/HardwareMonitorService.cs (API de demande de cadence temporaire par demandeur, sans toucher au réglage utilisateur)
- src/PCPerfSuite.Core/Hardware/SensorReadSchedule.cs (cadence demandée en plus de la manuelle et de l'automatique)
- nouveau src/PCPerfSuite.App/ViewModels/BenchmarkViewModel.cs + src/PCPerfSuite.App/Views/BenchmarkView.xaml(.cs)
- src/PCPerfSuite.App/ViewModels/MainViewModel.cs (construction, NavItems, UpdateEcoMode, Dispose)
- src/PCPerfSuite.App/MainWindow.xaml (vue affichée par titre)
- src/PCPerfSuite.Core/PowerSettings/AppSettingsStore.cs (BenchSettings ; résultats dans un fichier séparé)
- src/PCPerfSuite.App/ViewModels/CompatibilityViewModel.cs (ligne « Benchmarks disponibles sur ce PC »)
- tests/PCPerfSuite.Core.Tests/Benchmark*Tests.cs

**Manques**

Tout le moteur : (a) charges CPU mono et multi-cœur (entier, flottant, éventuellement AVX), à durée fixe ou à quantité de travail fixe, avec score ; (b) RAM : bande passante et latence (parcours de buffer, pointer-chasing) ; (c) disque : séquentiel et 4K aléatoire en lecture/écriture sur un fichier temporaire en FILE_FLAG_NO_BUFFERING, avec contrôle de l'espace libre ; (d) GPU : aucune API graphique ou de calcul dans le projet (dépendances du Core : LibreHardwareMonitorLib, TraceEvent, NvAPIWrapper, System.Management). Il faut ajouter une dépendance D3D11/12 (Vortice.Windows, Silk.NET) ou piloter un outil externe ; (e) « power » : charge combinée CPU + GPU en enregistrant CPU Package W, GPU W, PsuPowerWatts ou débit batterie et les tensions +12 V ; (f) enregistreur à cadence élevée pendant le bench ; (g) historique des résultats et comparaison avant/après ; (h) arrêt : bouton d'annulation, coupure thermique, arrêt si la fenêtre se ferme ; (i) fréquences par cœur sous charge, absentes de CpuSnapshot (seul MaxClockMhz, HardwareMonitorService.cs:448-453) ; (j) contexte du résultat : secteur ou batterie, plan d'alimentation, limites CPU/GPU actives.

**Pièges**

1) GetSnapshot ne doit avoir qu'un appelant (échéancier partagé, HardwareMonitorService.cs:170-172 et 222-227) : l'enregistreur s'abonne, il ne lit pas lui-même. 2) Les abonnés tournent sur le thread d'interface, un traitement lourd fait sauter des ticks (MonitoringViewModel.cs:756-760) : copier la référence du snapshot et analyser ailleurs. 3) Snapshot resservi jusqu'à 3 fois quand une lecture lève (HardwareMonitorService.cs:62-66 et 150-158) : dédoublonner par CapturedAtUtc. 4) Un groupe non relu garde ses anciennes valeurs, il faut tester snapshot.GroupsRead avant de compter un point (comme MonitoringViewModel.cs:962). 5) Pour accélérer les relevés pendant un bench, SetManualInterval ne convient pas : c'est le réglage de l'utilisateur, enregistré par SensorGroupCadenceViewModel.ApplyTo (MonitoringViewModel.cs:493-504). Il faut une surcouche « demande temporaire ». Le GPU reste plafonné à 750 ms en automatique et un CPU Intel est relu toutes les ~600 ms. 6) Le bench tourne dans le même processus que le thread de relevé (AboveNormal) et l'interface : lancer les workers en priorité Normal ou BelowNormal, et savoir que la charge de l'app est incluse. 7) Mode éco : pendant un bench, forcer la lecture complète via un IBackgroundSensorConsumer qui déclare tous les groupes. 8) HardwareSnapshot ne décrit qu'un GPU, le préféré (lignes 265-281) : sur un portable hybride, le iGPU n'est pas suivi. 9) Écrire sur le SSD système use le disque et remplit la partition : limiter la taille, supprimer en finally. 10) Fichiers déjà gros : MonitoringViewModel.cs contient 1024 lignes et 7 classes, HardwareMonitorService.cs 1280 lignes. Mettre le bench dans ses propres fichiers. 11) Règle 3 : un bench indisponible sur ce PC (GPU sans D3D, pas de droit d'écriture disque) doit le dire, et apparaître dans le diagnostic.

### F2

**Déjà dans le code**

Aucune IA, aucun appel à un LLM : le seul HttpClient est celui de l'installeur (src/PCPerfSuite.Core/Installations/OfficialInstaller.cs:60). Aucun moteur de règles « normal / anormal ». Les signaux utiles existent en partie. Températures : CPU package et cœur max (ReadCpu, HardwareMonitorService.cs:425-473), GPU cœur, hot spot et mémoire (549-581), VRM et système (837-881), disques, barrettes RAM. Puissances : CPU package, où seuls les noms « total » sont retenus (TotalPackagePowerNames, ligne 481) ; GPU (lignes 574-575) ; PsuPowerWatts, seulement avec une alimentation connectée (Corsair HXi/RMi, lignes 319-323) ; débit batterie. Tensions : Vcore CPU (457-460) et toutes les tensions de la carte mère sous leur nom brut (866-869 ; métriques dynamiques mb.volt:{nom}, MonitoringSensorCatalog.cs:24-29), dont +12V, +5V et +3.3V quand la puce Super I/O les expose. Bridage GPU : GpuPerformanceLimit Power/Temperature/Voltage/NoLoad (GpuControlModels.cs:89-103), NVIDIA seulement (NvApiGpuBackend.cs:190-205), null chez ADLX (AdlxGpuBackend.cs:316) et IGCL (IgclGpuBackend.cs:438). Il est lu une fois par seconde par GpuControlViewModel.RefreshActiveLimit (GpuControlViewModel.cs:638-664), hors de HardwareSnapshot. Bridage CPU : rien de lu, seule existe la coupure de sécurité à 98 °C quand les limites ont été relevées. Refroidissement : ventilateurs rangés par catégorie CPU, GPU, pompe, boîtier, autres, non identifiés (Fans/FanCategory.cs:7-23 ; FanIdentification), comptage FanInventory.Of (FanInventory.cs:31), connecteurs vides (EmptyHeaderDetector), RPM et %, ventilateurs de portable en lecture seule. Limites : CPU PL1/PL2/PPT et maximum (CpuControlService.ReadPowerLimits, CpuMaxWattsInfo) ; limite de puissance GPU en % actuelle, par défaut, min et max (GpuControlSnapshot, GpuControlModels.cs:21-35).

**À réutiliser**

- src/PCPerfSuite.Core/Hardware/HardwareModels.cs:61-88 — CpuSnapshot et GpuSnapshot (températures, puissances, horloges)
- src/PCPerfSuite.Core/Hardware/HardwareModels.cs:177-195 — MotherboardSnapshot.Voltages et OtherTemperatures (rails 12V/5V/3.3V selon la carte)
- src/PCPerfSuite.Core/Hardware/GpuControlService.cs:118 — GetActiveLimit() (perf cap, NVIDIA)
- src/PCPerfSuite.Core/Hardware/Fans/FanInventory.cs:31 — FanInventory.Of : inventaire du refroidissement
- src/PCPerfSuite.Core/Hardware/Fans/FanCategory.cs:7-23 — Cpu, Gpu, Pump, Case (présence d'un AIO par exemple)
- src/PCPerfSuite.Core/Hardware/Cpu/CpuControlService.cs:66 — ReadPowerLimits() (PL1/PL2 réellement appliquées)
- src/PCPerfSuite.Core/Hardware/Cpu/IntelPowerLimitBackend.cs:246-251 — TryReadMsr via le module PawnIO IntelMSR : piste pour lire IA32_THERM_STATUS (0x19C), IA32_PACKAGE_THERM_STATUS (0x1B1) et MSR_CORE_PERF_LIMIT_REASONS (0x64F), à vérifier que le module autorise ces MSR
- src/PCPerfSuite.App/Metrics/MetricCatalog.cs:49-99 — MetricDefinition.UnavailableHint, pour expliquer au moteur ou au LLM pourquoi une donnée manque
- src/PCPerfSuite.Core/Hardware/HardwareMonitorService.cs:589-610 — DescribeMemorySensors : modèle de dump brut des capteurs, à généraliser au CPU et à la carte mère pour valider les noms des tensions et des cœurs
- src/PCPerfSuite.App/ViewModels/CompatibilityViewModel.cs:28 — CompatibilityRow : format de ligne de constat

**Fichiers à toucher**

- nouveau src/PCPerfSuite.Core/Diagnostics/ (règles pures : ThermalVerdict, PowerDeliveryVerdict, CoolingAssessment, ComponentScoreComparer, DiagnosticFinding)
- nouveau src/PCPerfSuite.Core/Diagnostics/ReferenceScores (base de scores de référence par modèle CPU/GPU, embarquée en JSON)
- src/PCPerfSuite.Core/Hardware/HardwareModels.cs (bridage CPU/GPU dans le snapshot, fréquences par cœur)
- src/PCPerfSuite.Core/Hardware/HardwareMonitorService.cs (ReadCpu : Distance to TjMax / TjMax, horloges par cœur ; ReadGpu : perf cap)
- src/PCPerfSuite.Core/Hardware/Cpu/IntelPowerLimitBackend.cs ou nouveau CpuThrottleReader (MSR de bridage Intel ; AMD à étudier)
- nouveau src/PCPerfSuite.Core/Diagnostics/EventLogReader (Kernel-Power 41, WHEA-Logger, Display 4101 TDR, BugCheck 1001)
- nouveau src/PCPerfSuite.Core/Diagnostics/LlmReportWriter (facultatif, rédaction seulement)
- src/PCPerfSuite.App/ViewModels/BenchmarkViewModel.cs (affichage des constats)
- tests/PCPerfSuite.Core.Tests/Diagnostics*Tests.cs

**Manques**

(1) Bridage CPU non mesuré. Les sondes « Distance to TjMax » d'Intel sont exclues exprès (HardwareMonitorService.cs:434-444) et TjMax n'est connu nulle part. Aucun registre de raisons de bridage (thermique, PL1/PL2, PROCHOT, courant). AMD : rien. (2) Bridage GPU AMD/Intel N/D, et même chez NVIDIA il n'est pas dans HardwareSnapshot (seulement dans l'onglet GPU, sur le thread d'interface). (3) Fréquences par cœur et fréquence effective absentes. (4) « Manque de puissance de l'alimentation » presque jamais mesurable directement. Aucun wattmètre hors alimentations connectées. +12V dépend de la puce Super I/O ; sur une carte absente de la table LHM, les tensions arrivent en « Voltage #n » brut, peut-être sans mise à l'échelle, à vérifier sur machine. Il manque le journal d'événements Windows (Kernel-Power 41 = coupure brutale, WHEA, TDR), aucun code ne le lit, et le lien PCIe du GPU (génération, largeur). (5) Pas de référence de scores « normaux » : ni données ni source dans le dépôt. Démarrer par des références relatives (TDP et boost annoncés, ligne de base du même PC, écart entre cœurs) et une petite base embarquée. (6) Température ambiante inconnue : la demander au technicien ou l'estimer au repos. (7) Refroidissement « suffisant ? » : croiser la température sous charge, la puissance dissipée (W par °C au-dessus du repos), le % des ventilateurs à pleine charge, le type de refroidissement (pompe détectée ou non) et la catégorie de CPU. (8) Couche IA : moteur de règles déterministe et testable pour le verdict. Un LLM seulement pour la rédaction et l'explication, facultatif, avec consentement, fonctionnement hors ligne en repli, gestion de clé (rien n'existe pour stocker un secret).

**Pièges**

Règle 3 : chaque verdict doit dire son niveau de preuve (« mesuré », « déduit », « impossible à mesurer sur ce PC ») et pourquoi. Un « perf cap Power » NVIDIA est la limite du GPU lui-même, pas une alimentation trop faible : ne pas confondre. Une tension +12V lue sur une carte inconnue peut être fausse, à marquer « non fiable » quand le nom du capteur est générique. Le LLM ne doit jamais inventer une valeur : lui fournir les mesures structurées et les hints N/D. Données envoyées en ligne depuis le PC d'un client : consentement, nom de compte masqué (modèle IsPersonal). Les appels NVAPI de GpuControlService ne sont pas protégés par un verrou (GpuControlService.cs) : ne pas les appeler depuis un thread de bench, passer par le thread d'interface ou ajouter un verrou. Règle 6 : les règles de diagnostic non validées sur de vraies machines sont marquées « expérimental ».

### F3

**Déjà dans le code**

La base la plus proche est « Paramètres › Compatibilité de ce PC » (src/PCPerfSuite.App/ViewModels/CompatibilityViewModel.cs). BuildRows (106-219) produit une vingtaine de lignes : machine et châssis, droits administrateur, PawnIO, compte Windows, démarrage auto, réglages, dernière erreur, CPU, limite de puissance CPU, GPU, contrôle GPU, ADLX désactivé, lecture, identification, pilotage et profils des ventilateurs, mémoire, disques, batterie, %CPU par processus, trace ETW, sondes carte mère, noms des disques, RTSS. S'y ajoutent la liste des métriques N/D avec leur raison (94-101) et CopyReport (620-692), rapport texte copié dans le presse-papiers : version, Windows, lignes [OK]/[--], ventilateurs détaillés, mémoire détaillée, capteurs mémoire bruts. Autres éléments pour un rapport : DiskHealthService.CheckAsync (santé, usure, heures, erreurs), déclenché disque par disque (DiskItemViewModel.TestAsync, MonitoringViewModel.cs:85-114) ; santé et cycles de la batterie (BatterySnapshot.HealthPercent, HardwareModels.cs:284) ; fiche des barrettes ; MachineInfo (fabricant, modèle, châssis, GPU vus par Windows). C'est un rapport de CAPACITÉS (« ce que l'app peut lire »), pas un verdict « normal / anormal / très bien ».

**À réutiliser**

- src/PCPerfSuite.App/ViewModels/CompatibilityViewModel.cs:28 — CompatibilityRow (modèle de ligne, IsPersonal)
- src/PCPerfSuite.App/ViewModels/CompatibilityViewModel.cs:344-420 — MemoryRow, DiskRow, BatteryRow : statiques et presque purs, à déplacer en Core
- src/PCPerfSuite.App/ViewModels/CompatibilityViewModel.cs:620-692 — CopyReport : structure du rapport texte et masquage des données personnelles
- src/PCPerfSuite.Core/Hardware/DiskHealthService.cs:34-117 — CheckAsync (HealthStatus, Wear, PowerOnHours, erreurs E/S)
- src/PCPerfSuite.Core/Hardware/HardwareModels.cs:246-291 — BatterySnapshot (HealthPercent, CycleCount, DesignMWh)
- src/PCPerfSuite.Core/Hardware/Memory/MemoryModuleReader.cs:34-118 — barrettes et emplacements
- src/PCPerfSuite.Core/Environment/MachineInfo.cs:23-58 — identité de la machine (namespace PCPerfSuite.Core.SystemInfo)
- src/PCPerfSuite.App/Utils/CrashLog.cs — journal plafonné, modèle pour un journal de session technicien

**Fichiers à toucher**

- nouveau src/PCPerfSuite.Core/Diagnostics/TechnicianReport.cs (sections, constats, verdict à 4 états : très bien / normal / anormal / non évalué, avec niveau de preuve)
- nouveau src/PCPerfSuite.Core/Diagnostics/ReportExporter (HTML autonome ou PDF, et JSON pour l'archivage)
- src/PCPerfSuite.App/ViewModels/CompatibilityViewModel.cs (déplacer les constructeurs de lignes en Core, les réutiliser)
- nouveau src/PCPerfSuite.App/ViewModels/TechnicianModeViewModel.cs + vue (assistant : identification, santé, bench, verdict, export)
- src/PCPerfSuite.App/ViewModels/MainViewModel.cs, MainWindow.xaml (entrée de navigation ou mode de lancement)
- src/PCPerfSuite.App/PCPerfSuite.App.csproj (publication self-contained, un seul fichier)
- src/PCPerfSuite.App/App.xaml.cs (argument --technicien, dossier de sortie)

**Manques**

Verdicts à 3 niveaux et seuils par composant. Test de santé des disques automatique sur tous les disques. Informations d'identification absentes du code : version du BIOS, carte mère (Win32_BaseBoard), numéro de série, versions des pilotes GPU, chipset et stockage. Aucun code ne lit DriverVersion, BIOSVersion ou SerialNumber, sauf une structure IGCL. Canal RAM (simple/double, déductible de DeviceLocator et BankLabel), XMP/EXPO actif ou non. Journal d'événements (crashs, WHEA). Export vers un fichier : aucun SaveFileDialog dans l'app, seulement le presse-papiers. Horodatage et identifiant du rapport. Mode « session technicien » : parcours guidé, durée maîtrisée, tout rendu d'origine en fin de session.

**Pièges**

Le rapport est construit sur le thread d'interface à partir de ViewModels (_fans.NoFansMessage, _gpu.UnavailableMessage, _processes.IoTraceState) : difficile à tester, aucun test n'existe. Extraire un modèle Core avant de l'enrichir. Déploiement chez un client : l'app n'est pas self-contained (PCPerfSuite.App.csproj ligne 14, SelfContained=false) et exige le runtime .NET 8 Desktop. Elle impose l'administrateur (app.manifest ligne 13). Sans PawnIO, températures et tensions CPU sont N/D : le mode technicien doit détecter PawnIO, proposer l'installation et savoir la retirer. Traces laissées sur le PC client : settings.json, erreurs.log, diagnostic-ventilateurs.log et la tâche planifiée dans %LOCALAPPDATA%\PCPerfSuite. Le mode technicien doit prévoir un dossier de sortie (clé USB) et un nettoyage. Masquer le compte et le numéro de série dans le rapport partagé (IsPersonal).

### F5

**Déjà dans le code**

Réglage : un seul interrupteur « Désactiver la mise en veille des cœurs CPU (core parking) » dans Optimisation Windows (WindowsPerformanceSettingsService.cs:241-255). Il écrit CPMINCORES (PowerSubGroups.ProcessorMinCoreParkingState, PowerPlanService.cs:181) à 100 ou 5 sur le plan actif. Visuel par cœur : rien. CpuSnapshot n'agrège que MaxCoreLoadPercent (capteur LHM « CPU Core Max », HardwareMonitorService.cs:466), MaxCoreTempC et MaxClockMhz (maximum de toutes les horloges, 448-453). LHM expose pourtant, pendant la même Update, les capteurs par cœur (horloge « Core #n », température « CPU Core #n », charge « CPU Core #n Thread #m »), qui ne sont pas extraits. L'infobulle du groupe CPU annonce « charge par cœur » (MonitoringViewModel.cs:436) mais rien ne l'affiche. La topologie hybride est lue (GetSystemCpuSetInformation, EfficiencyClass à l'octet 18, CpuPlatformDetector.cs:112-148), mais ne sert qu'à rendre IsHybrid.

**À réutiliser**

- src/PCPerfSuite.Core/Hardware/PdhCounterSampler.cs:12-83 — lecture PDH par nom anglais, à étendre aux instances multiples (PdhGetFormattedCounterArrayW) pour \Processor Information(*)\% Processor Utility et \Processor Information(*)\Parking Status
- src/PCPerfSuite.Core/Hardware/Cpu/CpuPlatformDetector.cs:115-148 — parcours de SYSTEM_CPU_SET_INFORMATION : EfficiencyClass (octet 18) ; AllFlags (octet 19, bit 0 = Parked, selon la doc Win32) donne l'état parqué par processeur logique
- src/PCPerfSuite.Core/Hardware/HardwareMonitorService.cs:425-473 — ReadCpu, où extraire les horloges et températures par cœur de LHM
- src/PCPerfSuite.Core/Hardware/SensorReadSchedule.cs:4-20 — SensorGroup (un groupe « CpuCores » bon marché, relu au rythme de CpuLoad)
- src/PCPerfSuite.Core/PowerSettings/PowerPlanService.cs:175-185 — PowerSubGroups (ajouter CPMAXCORES, concurrence et seuils de parking) et GetValueIndexAsync
- src/PCPerfSuite.App/Controls/MeterBar.cs — barre de charge 0-100 animée
- src/PCPerfSuite.App/Controls/AdaptiveGrid.cs — grille qui s'adapte à la largeur, pour une tuile par cœur
- src/PCPerfSuite.App/Controls/Sparkline.cs + Utils/SampleHistory.cs — mini-courbe par cœur

**Fichiers à toucher**

- src/PCPerfSuite.Core/Hardware/PdhCounterSampler.cs (mode tableau)
- src/PCPerfSuite.Core/Hardware/HardwareModels.cs (CpuCoreSnapshot : index logique, cœur physique, classe P/E, charge %, horloge, température, parqué)
- src/PCPerfSuite.Core/Hardware/HardwareMonitorService.cs (lecture et assemblage)
- src/PCPerfSuite.Core/Hardware/SensorReadSchedule.cs (groupe éventuel)
- src/PCPerfSuite.Core/Hardware/Cpu/CpuPlatformDetector.cs (exposer la topologie par processeur logique, pas seulement IsHybrid)
- src/PCPerfSuite.App/ViewModels/MonitoringViewModel.cs (SensorGroupCadenceViewModel, switch lignes 433-445)
- nouveau src/PCPerfSuite.App/ViewModels/CpuCoresViewModel.cs + vue (onglet Processeur ou Monitoring)
- src/PCPerfSuite.Core/PowerSettings/PowerPlanService.cs et WindowsPerformanceSettingsService.cs (réglages de parking plus fins)
- src/PCPerfSuite.App/ViewModels/CompatibilityViewModel.cs (ligne « Charge par cœur / parking »)

**Manques**

Charge par processeur logique cohérente avec le Gestionnaire des tâches, état parqué par cœur, association processeur logique ↔ cœur physique ↔ classe P/E ↔ capteur LHM, historique par cœur, vue graphique, réglages du parking au-delà du « min 100 % / 5 % » (maximum de cœurs, concurrence, par plan, secteur et batterie) avec relecture.

**Pièges**

Les capteurs de charge par thread de LHM mesurent le temps processeur brut, pas l'utilité pondérée par la fréquence du Gestionnaire des tâches (PdhCounterSampler.cs:5-11 : 1-6 % contre 30-60 %). Utiliser PDH par instance, plafonner à 100 comme à la ligne 178. Instances PDH « groupe,index » (0,0 … 1,3 au-delà de 64 processeurs logiques), à séparer des instances « _Total » et « 0,_Total ». Le compteur « Parking Status » peut manquer (ARM, certaines éditions) : N/D avec sa raison. La numérotation des cœurs LHM (physique, noms P-Core/E-Core sur hybride, à vérifier dans un dump brut) ne correspond pas directement aux processeurs logiques SMT. Le réglage du parking ne vaut que pour le plan actif et saute au changement de plan (texte des lignes 246-247). Ajouter un SensorGroup change l'ordre des lignes de « Cadence des capteurs » (ordre de l'enum) ; la persistance, par nom, ne casse pas. Dessiner 32 cœurs × Sparkline à chaque tick a un coût d'interface, qui compte dans « Interface et abonnés ».

### F13

**Déjà dans le code**

Lecture seulement, rien sur l'allocation. Utilisation : SystemMemoryReader (GlobalMemoryStatusEx : total, disponible, utilisée, charge, commit et fichier d'échange ; Memory/SystemMemoryReader.cs:20-49), fusionnée avec LHM (MergeMemory et CompleteMemory, HardwareMonitorService.cs:675-797). Fiche matérielle : MemoryModuleReader, lu une fois en WMI Win32_PhysicalMemory (emplacement, banque, capacité, vitesse, type DDR, format, fabricant, référence) et nombre d'emplacements (Win32_PhysicalMemoryArray) (Memory/MemoryModuleReader.cs:34-118). Température par barrette via LHM (704-715). Carte Mémoire du Monitoring (MemoryInfoViewModel, MonitoringViewModel.cs:226-272). Mémoire par processus (PrivateWorkingSetBytes, CommittedBytes) dans l'onglet Processus (ProcessModels.cs:80-87).

**À réutiliser**

- src/PCPerfSuite.Core/Hardware/Memory/SystemMemoryReader.cs:20-49 — lecture Windows sans privilège
- src/PCPerfSuite.Core/Hardware/Memory/MemoryModuleReader.cs:69-95 — ReadModule (garder aussi Speed nominale, voir pièges)
- src/PCPerfSuite.Core/Hardware/HardwareModels.cs:111-167 — MemoryModuleInfo et MemorySnapshot
- src/PCPerfSuite.App/ViewModels/MonitoringViewModel.cs:226-272 — carte Mémoire, mise à jour en place
- src/PCPerfSuite.Core/Hardware/PdhCounterSampler.cs — compteurs \Memory\Standby Cache*, Modified Page List Bytes, Free & Zero Page List Bytes
- src/PCPerfSuite.Core/Processes/ProcessService.cs:218 — GetSnapshot (mémoire par processus) pour une vue « qui occupe la RAM »

**Fichiers à toucher**

- src/PCPerfSuite.Core/Hardware/Memory/ (nouveau MemoryListReader et MemoryListPurger ; PageFileSettings)
- src/PCPerfSuite.Core/Hardware/Memory/MemoryModuleReader.cs (garder Speed et ConfiguredClockSpeed séparés ; canal)
- src/PCPerfSuite.Core/Hardware/HardwareModels.cs
- src/PCPerfSuite.App/ViewModels/MonitoringViewModel.cs ou nouveau MemoryViewModel
- src/PCPerfSuite.App/ViewModels/CompatibilityViewModel.cs (MemoryRow)

**Manques**

Le sens est à préciser avec Denis. Pistes : (a) une vue « allocation » à la RAMMap : en cours d'utilisation, modifiée, en attente (cache), libre, compressée ; (b) vider la liste d'attente, comme ISLC (NtSetSystemInformation SystemMemoryListInformation, privilège SeProfileSingleProcessPrivilege, donc administrateur) ; (c) configurer le fichier d'échange (Win32_PageFileSetting) ; (d) mémoire partagée du GPU intégré (UMA) : se règle presque toujours dans le BIOS, et l'app doit le dire plutôt que proposer un réglage sans effet ; (e) limiter la RAM par processus relève de F14 (Job Objects). Utiles aussi au diagnostic : double canal et XMP/EXPO actif ou non.

**Pièges**

MemoryModuleReader.cs:76 fusionne ConfiguredClockSpeed et Speed. Pour savoir si l'XMP est actif, il faut les deux, et Speed n'est pas toujours la vitesse XMP (souvent JEDEC). La fusion Windows > LHM > WMI est délicate (commentaires 717-797) : ne pas en changer l'ordre. En mode éco, le groupe Memory est sauté et MemorySnapshot sort vide (HardwareMonitorService.cs:248 et 333). Une purge de la RAM est une écriture système : bouton explicite, jamais automatique, avec explication (pas de gain de FPS garanti).

### F18

**Déjà dans le code**

Rien sur la classification d'usage ni sur la bascule automatique. Sources disponibles : à chaque tick, charge CPU et GPU, RAM, disques, réseau et FPS RTSS du processus au premier plan (RtssFrameStatsReader.TryReadForeground, qui lit le PID via GetForegroundWindow, RtssFrameStatsReader.cs:68 et 215-225 ; RtssFrameStats ne porte pas le nom du processus) ; secteur ou batterie (BatterySnapshot.PowerOnline). Infrastructure d'arrière-plan : IBackgroundSensorConsumer, mode éco (MonitoringViewModel.cs:796-865), zone de notification et tâche planifiée au démarrage. La liste des processus coûte cher et ne tourne que si l'onglet est affiché (ProcessesViewModel.IsActive, ProcessesViewModel.cs:868-886). Profils par domaine, déjà persistés : AppSettings.FanProfiles (FanProfile), GpuControlSettings.OverclockProfiles (GpuOverclockProfile, GpuControlModels.cs:107), CpuControlSettings.Profiles (CpuProfile.cs). Leurs méthodes d'application sont privées : CpuControlViewModel.cs:573, FanCurvesViewModel.cs:996, GpuControlViewModel.cs:556.

**À réutiliser**

- src/PCPerfSuite.App/ViewModels/IBackgroundSensorConsumer.cs:9-53 — déclarer les groupes à relire fenêtre cachée (et BackgroundSensorNeeds, testable)
- src/PCPerfSuite.App/ViewModels/MainViewModel.cs:152-154 — UpdateEcoMode, liste des consommateurs d'arrière-plan
- src/PCPerfSuite.App/ViewModels/MonitoringViewModel.cs:565-568 — SnapshotUpdated et MetricsUpdated (reçus aussi en mode éco, lignes 855-856)
- src/PCPerfSuite.Core/Overlay/RtssFrameStatsReader.cs:68 — FPS du premier plan, signe fiable qu'une appli 3D tourne
- src/PCPerfSuite.Core/Hardware/HardwareModels.cs:246-291 — BatterySnapshot.PowerOnline (bascule secteur / batterie)
- src/PCPerfSuite.Core/Hardware/Cpu/CpuProfile.cs, src/PCPerfSuite.Core/Hardware/Fans/FanProfile.cs:12, src/PCPerfSuite.Core/Hardware/GpuControlModels.cs:107 — modèles de profil à regrouper
- src/PCPerfSuite.Core/Hardware/Fans/FanProfile.cs:41-77 — FanProfileMatcher.Match et Sanitize (profil tolérant)
- src/PCPerfSuite.Core/PowerSettings/AppSettingsStore.cs:409-418 — Update()

**Fichiers à toucher**

- nouveau src/PCPerfSuite.Core/Usage/ (UsageSample, UsageRecorder agrégé par tranches de 1 à 5 min, UsageClassifier pur et testable, ProfileSwitchPolicy avec hystérésis et délai de maintien, ForegroundAppReader léger)
- nouveau fichier d'historique %LOCALAPPDATA%\PCPerfSuite\usage.json, hors settings.json
- nouveau src/PCPerfSuite.App/ViewModels/UsageProfilesViewModel.cs + vue (3 profils bureautique, gaming léger, gaming lourd, éditables à la main)
- src/PCPerfSuite.App/ViewModels/MainViewModel.cs (construction, ajout à UpdateEcoMode, Dispose)
- src/PCPerfSuite.App/ViewModels/CpuControlViewModel.cs, FanCurvesViewModel.cs, GpuControlViewModel.cs (méthode publique « appliquer le profil nommé X » avec compte rendu)
- src/PCPerfSuite.Core/PowerSettings/AppSettingsStore.cs (UsageProfilesSettings)
- src/PCPerfSuite.App/ViewModels/CompatibilityViewModel.cs (ligne « Bascule automatique »)
- tests/PCPerfSuite.Core.Tests/UsageClassifierTests.cs

**Manques**

Enregistreur d'usage sur plusieurs jours, léger. Identifiant de l'appli au premier plan (GetForegroundWindow, puis PID, puis nom de l'exécutable) indépendant de ProcessService. Règles de classification : FPS RTSS présents, charge GPU soutenue, charge CPU, plein écran. Générateur des 3 profils : courbes de ventilation déduites des températures observées, niveau d'OC tiré de F4. Moteur de bascule avec hystérésis, délai minimal et reprise en main manuelle. Interface d'édition des 3 profils. API publique d'application des profils par domaine.

**Pièges**

Sans implémenter IBackgroundSensorConsumer et sans ajout au tableau de MainViewModel.cs:153-154, l'analyse ne reçoit fenêtre cachée que des groupes déclarés par d'autres. Les groupes en pause gardent des valeurs figées ou vides : toujours tester GroupsRead. Chaque bascule est une écriture matérielle : règle 5 (jamais le contrôleur embarqué des portables), CPU seulement si RiskAccepted, renonciation Intel pour l'OC GPU, garde thermique, conflit avec un réglage manuel en cours. L'OC GPU passe par un Debouncer dans GpuControlViewModel. Ne pas empiler l'historique dans settings.json (réécrit en entier à chaque Save). Les noms d'exécutables sont des données personnelles, à ne pas mettre dans le rapport partagé. Anti-oscillation : un jeu en menu ou en pause ne doit pas faire basculer toutes les 10 s.

### F4

**Déjà dans le code**

Côté mesure seulement : l'OC GPU existe (NVAPI, ADLX, IGCL), avec relecture de ce que le pilote retient, et les limites de puissance CPU aussi (IntelPowerLimitBackend, AmdSmuBackend). Aucune détection de stabilité : pas de lecture des TDR, WHEA ou crashs, pas de recherche automatique des limites, pas de test de stabilité.

**À réutiliser**

- src/PCPerfSuite.Core/Hardware/GpuControlService.cs:114-139 — GetOverclock, TrySetClockOffsets, RestoreOverclockDefaults
- src/PCPerfSuite.Core/Hardware/GpuControlService.cs:118 — GetActiveLimit (perf cap NVIDIA pendant la montée)
- src/PCPerfSuite.Core/Hardware/Cpu/CpuControlService.cs:68-128 — TrySetPowerLimits et coupure thermique
- src/PCPerfSuite.Core/Overlay/RtssFrameStatsReader.cs — temps de frame, pour repérer les saccades

**Fichiers à toucher**

- nouveau src/PCPerfSuite.Core/Diagnostics/EventLogReader (TDR Display 4101, WHEA-Logger, Kernel-Power 41), commun avec F2
- src/PCPerfSuite.Core/Benchmark/ (réutilisé comme charge de validation)

**Manques**

Charge GPU interne (voir F1, aucune API graphique). Détection d'instabilité : TDR, artefacts (calcul vérifié sur GPU), erreurs WHEA CPU. Mémoire de la dernière étape tentée, pour reprendre après un plantage ou un redémarrage.

**Pièges**

Un plantage pendant la recherche emporte l'app : il faut noter l'étape sur disque avant chaque palier, sur le modèle d'AdlxProbeGuard (sentinelle). Les écritures ne survivent pas toujours à un redémarrage : c'est voulu et sert de filet.

### F9

**Déjà dans le code**

Côté relevé seulement : HardwareSnapshot ne décrit qu'un GPU, le préféré (PreferredGpuVendor et PreferredGpuName, HardwareMonitorService.cs:69-78 et 265-281). MachineInfo.VideoControllers liste tous les GPU vus par Windows (MachineInfo.cs:44 et 121-141). L'infobulle GPU explique qu'un GPU dédié en veille ne renvoie rien (MetricCatalog.cs:136-138). Rien ne dit quel GPU est actif ou éteint.

**À réutiliser**

- src/PCPerfSuite.Core/Environment/MachineInfo.cs:121-141 — ReadVideoControllers
- src/PCPerfSuite.Core/Hardware/HardwareMonitorService.cs:524-545 — IsPreferredGpu et GpuNameMatches

**Fichiers à toucher**

- src/PCPerfSuite.Core/Hardware/HardwareModels.cs (liste de GPU, ou état « en veille » du GPU dédié)
- src/PCPerfSuite.Core/Hardware/HardwareMonitorService.cs

**Manques**

État d'alimentation du GPU dédié (D0/D3), liste des applis qui le réveillent, relevé des deux GPU en même temps.

**Pièges**

Interroger le GPU dédié (NVAPI ou LHM) peut le réveiller et ruiner l'économie visée : lecture à espacer, à mesurer sur machine.

### F19

**Déjà dans le code**

Trois familles de profils séparées : FanProfile (AppSettings.FanProfiles), GpuOverclockProfile (GpuControlSettings.OverclockProfiles) et CpuProfile (CpuControlSettings.Profiles). Chacune se gère dans son onglet, avec ApplyProfile privé (CpuControlViewModel.cs:573, FanCurvesViewModel.cs:996, GpuControlViewModel.cs:556). Pas de profil qui regroupe les trois domaines.

**À réutiliser**

- src/PCPerfSuite.Core/Hardware/Cpu/CpuProfile.cs — null veut dire « ne pas toucher »
- src/PCPerfSuite.Core/Hardware/Fans/FanProfile.cs:41-77 — rapprochement tolérant et compte rendu
- src/PCPerfSuite.App/ViewModels/CompatibilityViewModel.cs:502-507 — FanProfilesRow (compte rendu du dernier profil appliqué)

**Fichiers à toucher**

- src/PCPerfSuite.Core/PowerSettings/AppSettingsStore.cs (ProfileGroup : références par nom aux trois profils)
- src/PCPerfSuite.App/ViewModels/CpuControlViewModel.cs, FanCurvesViewModel.cs, GpuControlViewModel.cs (API publique d'application)

**Manques**

Modèle de groupe, API d'application commune qui renvoie ce qui a été appliqué ou ignoré et pourquoi, interface. C'est le socle de F18.

**Pièges**

Renommer ou supprimer un profil référencé par un groupe : il faut gérer la référence cassée et le signaler. L'ordre d'application compte (ventilateurs avant OC).

### Remarques

- Cadences effectives aujourd'hui : charge CPU PDH à chaque tick (1 s par défaut, 100 ms minimum) ; CPU complet par LHM toutes les ~600 ms sur un Intel (≈30 ms par lecture, 5 % de budget) ; GPU au plus toutes les 750 ms en automatique ; groupes coûteux jusqu'à 5 s ; perf cap GPU 1 fois par seconde, hors snapshot. Un bench ou un diagnostic fin demandera une API de cadence temporaire par demandeur dans HardwareMonitorService et SensorReadSchedule, qui ne touche pas au réglage utilisateur.
- Pour s'abonner sans dégrader le Monitoring : écouter MonitoringViewModel.SnapshotUpdated ou MetricsUpdated, ne faire sur le thread d'interface qu'une copie de référence (le snapshot est immuable), dédoublonner par CapturedAtUtc, filtrer par GroupsRead, analyser sur le pool. Fenêtre cachée, implémenter IBackgroundSensorConsumer et s'ajouter à MainViewModel.UpdateEcoMode. Surveiller l'effet dans « Temps de lecture des capteurs › Interface et abonnés (Apply) » et dans le compteur de ticks sautés.
- Capteurs réellement lus : CPU (charge totale PDH, charge du cœur max, température package et cœur max, puissance package, horloge max, Vcore) ; GPU préféré (charge, températures cœur, hot spot et mémoire, horloges cœur et mémoire, puissance, VRAM, premier ventilateur) ; mémoire (Windows, LHM, WMI, température par barrette) ; carte mère (système, VRM, autres sondes, TOUTES les tensions sous leur nom brut) ; disques (activité, débits, température, vie restante, lettres) ; réseau physique ; batterie (IOCTL) ; alimentation connectée (total W) ; FPS RTSS. Non lus : valeurs par cœur (charge, horloge, température, parking), bridage CPU, bridage GPU dans le snapshot, second GPU, lien PCIe, journal d'événements, version BIOS et pilotes, timings RAM (présents sur les barrettes LHM mais non extraits, commentaire HardwareMonitorService.cs:285-292).
- Proposition de découpage en conversations pour le parent. (A) Socle mesure : valeurs par cœur, PDH en tableau, topologie et parking, bridage CPU par MSR et TjMax, perf cap GPU dans le snapshot, API de cadence temporaire, dump brut des capteurs CPU et carte mère dans le diagnostic. Débloque F5, F1, F2 et F18 ; Opus, effort élevé : le cœur du relevé est délicat. (B) Bench CPU, RAM et disque avec enregistreur et résultats (F1 sans GPU). (C) Bench GPU et « power », qui exige une nouvelle dépendance D3D : à trancher avant. (D) Moteur de diagnostic déterministe, journal d'événements et rapport technicien exportable (F2 et F3), en commençant par extraire les lignes de CompatibilityViewModel en Core ; LLM facultatif ensuite. (E) Groupes de profils avec API publique d'application (F19), puis analyse d'usage et bascule (F18). (F) Visuel par cœur et réglages du parking (F5, après A).
- README.md:324 contredit les lignes 55 et 119-123 : il dit que le contrôle GPU ne fonctionne qu'avec NVIDIA, alors que le tableau annonce AMD (ADLX) et Intel (IGCL). La documentation est périmée, à corriger avec F6.
- MachineInfo est dans src/PCPerfSuite.Core/Environment/MachineInfo.cs, mais son namespace est PCPerfSuite.Core.SystemInfo (ligne 4), et non PCPerfSuite.Core.Environment.
- Fichiers déjà très gros, où ne rien ajouter directement : ProcessesViewModel.cs (2218 lignes), FanCurvesViewModel.cs (1512), HardwareMonitorService.cs (1280), MonitoringViewModel.cs (1024 lignes, 7 classes), CompatibilityViewModel.cs (693). Pour les nouveaux relevés, extraire des lecteurs dédiés (sur le modèle de Memory/SystemMemoryReader.cs et Storage/DiskModelReader.cs) plutôt que de grossir ReadCpu et ReadGpu.
- Aucune API graphique ni de calcul GPU dans les dépendances (LibreHardwareMonitorLib 0.9.6, TraceEvent 3.2.6, NvAPIWrapper.Net, System.Management ; CommunityToolkit.Mvvm côté App). Un bench GPU interne impose d'en ajouter une (Vortice.Windows ou Silk.NET), à décider tôt.
- Aucune base de « scores normaux » dans le dépôt. Pour F2 et F3, prévoir d'abord des verdicts relatifs (spécifications du CPU : TDP et boost, ligne de base au repos, cohérence entre cœurs, bridage mesuré), puis une base de référence embarquée et versionnée.
- Aucun test ne couvre CompatibilityViewModel ni MonitoringViewModel ; les tests existants portent sur la logique pure (SensorReadScheduleTests, BackgroundSensorNeedsTests, CpuMaxWattsResolverTests…). Écrire le moteur de diagnostic en fonctions pures sur des snapshots construits dans les tests, comme TestData.Hardware.


## Tuning CPU / GPU / ventilateurs et profils (Core/Hardware/Cpu, Core/Hardware/Gpu + GpuControlService, Core/Hardware/Fans + FanControlModels, Core/PowerSettings, ViewModels CpuControl/GpuControl/FanCurves/Optimization, tests associés)

### Architecture

Trois services Core, instanciés une seule fois dans MainViewModel (src/PCPerfSuite.App/ViewModels/MainViewModel.cs:16-24) et partagés : HardwareMonitorService (capteurs LibreHardwareMonitor + écriture des ventilateurs de carte mère), GpuControlService (IFanController + OC GPU) et CpuControlService (limites de puissance). Les ViewModels sont construits aux lignes 82-91 et s'abonnent tous à MonitoringViewModel.SnapshotUpdated (MonitoringViewModel.cs:565), levé sur le thread UI : aucun second sondage du matériel.
CPU : CpuControlService (Core/Hardware/Cpu/CpuControlService.cs:43-64) choisit au démarrage un backend ICpuTuningBackend (CpuTuningModels.cs:51-72) : IntelPowerLimitBackend (MSR 0x610 PL1/PL2 via le module IntelMSR de PawnIO), AmdSmuBackend (RSMU PPT sur bureau, MP1 STAPM/slow/fast sur APU, via le module RyzenSMU), ou UnsupportedCpuBackend, un Null Object qui porte la raison (Snapdragon, non-x64, PawnIO absent, famille inconnue). À part, CpuPowerTuningService (CpuPowerTuningService.cs:62-219) règle sans pilote, par powrprof, le mode boost, les états min/max, la fréquence max et l'EPP du plan actif (valeurs secteur et batterie).
GPU : GpuControlService.TryInitialize (GpuControlService.cs:53-87) garde le premier backend IGpuTuningBackend qui répond, dans l'ordre NVAPI, ADLX, IGCL. Un seul GPU est piloté (le dédié de préférence).
Ventilateurs : FanCurvesViewModel régule chaque ventilateur à chaque relevé (FanControlItemViewModel.Apply, FanCurvesViewModel.cs:344-446) avec FanCurveRegulator (hystérésis) et FanSpeedRamp (FanControlModels.cs:206-332). L'écriture passe par IFanController : HardwareMonitorService.TrySetFanPercent pour la carte mère, GpuControlService pour les ventilateurs « gpu:N ».
Réglages : un seul fichier JSON, %LOCALAPPDATA%\PCPerfSuite\settings.json (AppSettingsStore.cs:341-429). Il est protégé par un verrou et écrit de façon atomique (fichier temporaire puis Move). Update() fait lecture-modification-écriture sous verrou. Chaque ViewModel possède sa sous-arborescence (Cpu, Gpu, FanCurves/FanProfiles) et la réécrit depuis sa copie en mémoire après un Load(). Les enums sont sérialisées en nombres (pas de JsonStringEnumConverter).
Profils existants, trois modèles indépendants sans identifiant, uniques par nom (insensible à la casse), chacun dans son onglet :
- CpuProfile (CpuProfile.cs:13-35) : dictionnaire Id→{Ac, Battery} + SustainedWatts/BurstWatts ;
- GpuOverclockProfile (GpuControlModels.cs:107-131) : décalages cœur et mémoire non nullables, puissance %, température, tension + unité ;
- FanProfile (FanProfile.cs:12-23) : liste de FanCurveConfig + noms des ventilateurs.
Aucun groupe de profils, aucune bascule automatique, aucune notion de « profil actif ».
Garde-fous :
- rien n'est appliqué au lancement par défaut ; « Appliquer au démarrage » réapplique ET conserve à la fermeture ;
- on ne restaure à la fermeture que ce qui a été touché ;
- valeurs bornées dans les backends et relues après écriture côté CPU ;
- sécurité thermique CPU (98 °C pendant 15 s) ;
- protections des ventilateurs : GPU 88 °C, CPU 95 °C, pompe ≥ 30 %, CPU ≥ 20 %, chien de garde 15 s, température absente pendant 3 relevés ;
- refus d'écrire dans les ventilateurs d'un portable ou d'un châssis inconnu ;
- ré-armement après veille pour le CPU et les ventilateurs, mais PAS pour le GPU.
Il n'existe aucun moteur de bench/stress, aucun historique d'usage, aucune intégration IA/LLM et aucun code d'affichage (fréquence d'écran).

### Conventions

- Code, commentaires XML et textes d'interface en français. Les commentaires expliquent le POURQUOI (souvent avec la référence au rapport de revue « M2 », « U4 »…).
- Toute méthode matérielle est best-effort : elle renvoie bool/null et ne lève jamais (try/catch autour de chaque appel natif, ex. NvApiGpuBackend.cs:120-138, PawnIoModule.TryExecute PawnIoDriver.cs:366-399). Un refus du pilote est une information à afficher, pas une exception.
- Le backend est le dernier rempart : il borne lui-même toute valeur écrite (Math.Clamp sur la plage du pilote ou du processeur), quelle que soit l'UI appelante (IntelPowerLimitBackend.cs:164-171, AdlxGpuBackend.cs:335-339, IgclGpuBackend.cs:56-67).
- Relecture après écriture et message sur la valeur réellement retenue (« Le processeur a retenu X W au lieu de Y ») : IntelPowerLimitBackend.cs:217-229, AmdSmuBackend.cs:176-187, CpuPowerSettingViewModel.WriteNow CpuControlViewModel.cs:167-188, GpuControlViewModel.ShowAppliedOffsets 493-505.
- Disponibilité modélisée explicitement : CpuCapability(CanRead, CanWrite, Reason) (CpuTuningModels.cs:8-13), drapeaux « …Supported » dans GpuOverclockSnapshot (GpuControlModels.cs:51-87), Null Object UnsupportedCpuBackend qui porte la raison. Une fonction absente affiche un message propre au PC qui distingue : pas administrateur / pilote / marque non gérée (GpuControlViewModel.UnavailableMessage 88-119, FanCurvesViewModel.NoFansMessage 857-880, GpuControlViewModel.DescribeUnsupported 356-390).
- Tout ce qui n'a pas été vérifié sur une vraie machine est marqué expérimental : IsExperimentalBackend + ExperimentalNotice (CpuControlViewModel.cs:253-257, GpuControlViewModel.cs:147-151), CpuMaxWattsInfo.IsExperimental / ExperimentalNotice (CpuMaxWatts.cs:32-38).
- Sécurité d'application : rien n'est posé au lancement sans opt-in. « Appliquer au démarrage » vaut aussi conservation à la fermeture (CpuControlService.KeepLimitsOnExit, GpuControlService.KeepOverclockOnExit). Seul ce que l'app a touché est restauré (_touched, _overclockTouched, _powerLimitTouched, _fanTouched). Avertissement à accepter (CpuControlSettings.RiskAccepted) ou renonciation Intel (IntelOverclockWaiverAccepted) avant toute écriture.
- Écritures issues d'un curseur : Debouncer de 250 ms par clé (App/Utils/Debouncer.cs), Flush() dans Dispose. Drapeau _suppressApply quand on repositionne plusieurs propriétés d'un coup (profil, reset).
- Persistance : AppSettingsStore.Load() puis modification de SA sous-section puis Save() (ou Update() pour un vrai lecture-modification-écriture). Pour les nouveaux champs, des chaînes plutôt que des enums (ProcessesSettings, AppSettingsStore.cs:110-114). Les anciens champs sont gardés pour la migration (GpuControlSettings.VoltageBoostPercent, FanMode/FanPoints). Les profils sont tolérants : une entrée inconnue sur ce PC est ignorée et le nombre ignoré est annoncé (CpuControlViewModel.ApplyProfile 572-606, FanProfileMatcher.Match/Sanitize FanProfile.cs:49-160).
- Profils : même nom = remplacement, suppression avec MessageBox de confirmation, résumé calculé dans les termes de CETTE machine (BuildSummary CpuControlViewModel.cs:648-667, BuildProfileSummary FanCurvesViewModel.cs:1141-1156).
- Logique de décision pure séparée de l'accès matériel pour être testée sans pilote (CpuMaxWattsResolver « les backends lisent, cette classe tranche » CpuMaxWatts.cs:73-80, FanProfileMatcher, FanSpeedRamp, BackgroundSensorNeeds). Tests xUnit dans tests/PCPerfSuite.Core.Tests (InternalsVisibleTo sur Core) et tests/PCPerfSuite.App.Tests (UseWPF). Noms de tests en anglais Pascal_Case (CpuMaxWattsResolverTests) ou en phrase française (FanProfileTests).
- Tout ce qui doit continuer fenêtre cachée implémente IBackgroundSensorConsumer (IBackgroundSensorConsumer.cs:9-13) et s'ajoute à la liste de MainViewModel.UpdateEcoMode (MainViewModel.cs:152-154). En mode éco (MonitoringViewModel.IsBackgroundMode) on ne met plus à jour l'affichage, mais la sécurité continue (CpuControlViewModel.OnSnapshotUpdated 719-734).
- Chaque fonction matérielle nouvelle ajoute une ligne CompatibilityRow à CompatibilityViewModel.BuildRows (CompatibilityViewModel.cs:158-219), qui sert aussi de rapport de bug, avec les valeurs brutes lues quand c'est utile (CpuPowerLimitRow 286-310).
- Les modules propres à une marque vivent dans des dossiers séparés et sont choisis par détection à l'exécution (CpuPlatformDetector, AmdCodeName, MachineInfo.Current). Jamais de commande envoyée « à l'aveugle » à une famille non répertoriée : on annonce N/D ou lecture seule (AmdSmuBackend.cs:87-95, 350-361).
- Accès matériel partagé protégé par les mutex système utilisés par les autres outils : Global\Access_PCI (AmdSmuBackend.cs:44, PciGuard 404-444), Global\Access_ISABUS.HTP.Method (HardwareMonitorService.cs:1109, RunWithIsaBus 1133-1164).
- Fermeture : ordre imposé et chaque étape isolée par DisposeSafely (MainViewModel.cs:191-221) : relevé arrêté → ventilateurs rendus au BIOS → VM GPU/CPU → services GPU/CPU → matériel.
- Contrôleur embarqué des portables : lecture seule, garde-fou au plus près de l'écriture (HardwareMonitorService.LaptopControlRefused 1177, MachineInfo.SoftwareFanControlRefused = châssis ≠ Desktop, MachineInfo.cs:41).

### F4

**Déjà dans le code**

Aucun scan, aucun bench, aucune recherche de limites, aucun profil généré. Il n'existe que le réglage manuel et des profils enregistrés à la main.

GPU (GpuControlService.cs:94-139, backends Core/Hardware/Gpu) :
- NVIDIA : décalages cœur/mémoire sur P0 par Pstates20 (NvApiGpuBackend.cs:215-257), plages lues sur le pilote ou repli -500/+1000 et -1000/+2000 (29-32) ; limite de puissance % (116-139) ; limite de température (260-294) ; surtension % Pascal seulement (297-311) ; raison du bridage GetActiveLimit (190-211).
- AMD : fréquence max GFX et VRAM présentées en décalage (AdlxGpuBackend.cs:258-339), tension mV absolue ou en décalage (304-347), puissance % (227-250), reset d'usine (350-369) ; pas de limite de température (341), pas de raison du bridage (316).
- Intel : décalage cœur, vitesse ou décalage VRAM, décalage de tension, puissance, température (IgclGpuBackend.cs:207-242, 374-454), renonciation obligatoire (91-107).

CPU : seules écritures, PL1/PL2 Intel (MSR 0x610, IntelPowerLimitBackend.cs:186-235), PPT bureau AMD (RSMU 0x53/0x56, AmdSmuBackend.cs:208-227) et STAPM/slow/fast APU (MP1 0x14/0x16/0x15, 233-243), plus les réglages du plan d'alimentation.

Modèles de sortie prêts à l'emploi : GpuOverclockProfile et CpuProfile. Le README (README.md:247-248) conseille à l'utilisateur de « monter par paliers de 15 à 25 MHz et tester entre chaque » : c'est la procédure que l'IA devrait automatiser.

**À réutiliser**

- src/PCPerfSuite.Core/Hardware/GpuControlService.cs:114-139 — GetOverclock (plages du pilote) / TrySetClockOffsets / TrySetVoltage / TrySetTemperatureLimit / RestoreOverclockDefaults : les primitives de chaque palier
- src/PCPerfSuite.Core/Hardware/GpuControlService.cs:118 — GetActiveLimit : bridage par puissance, température ou tension pendant le stress (NVIDIA seulement)
- src/PCPerfSuite.Core/Hardware/GpuControlModels.cs:107-131 — GpuOverclockProfile : sortie des profils Safe / Classic / Agressive
- src/PCPerfSuite.Core/Hardware/Cpu/CpuProfile.cs:13-35 — CpuProfile : sortie côté CPU (PL et plan d'alimentation)
- src/PCPerfSuite.Core/Hardware/Cpu/CpuControlService.cs:68-128 — TrySetPowerLimits + NoteTemperature / EmergencyRestored : sécurité thermique à garder active pendant le test
- src/PCPerfSuite.Core/Hardware/Gpu/AdlxProbeGuard.cs:35-123 — modèle de témoin disque pour détecter un plantage pendant une tentative (à dupliquer pour « palier OC en cours »)
- src/PCPerfSuite.Core/Hardware/Cpu/CpuMaxWatts.cs:80-223 — CpuMaxWattsResolver : bornes max CPU (×1,5 BIOS, 400 W) et modèle de logique pure testable
- src/PCPerfSuite.App/ViewModels/GpuControlViewModel.cs:555-592 — ApplyProfile (bornage aux plages, unité de tension compatible)
- src/PCPerfSuite.App/Utils/RunningStats.cs — min/moy/max d'une série pendant un stress

**Fichiers à toucher**

- NOUVEAU src/PCPerfSuite.Core/Tuning/ (ex. OcSearch.cs : paliers, marges et génération des 3 profils, logique pure ; OcTestSentinel.cs : témoin de plantage ; StabilityProbe.cs : lecture du journal d'événements TDR/WHEA)
- NOUVEAU src/PCPerfSuite.App/ViewModels/AutoOcViewModel.cs (+ vue), pour ne pas grossir GpuControlViewModel (705 lignes)
- src/PCPerfSuite.App/ViewModels/GpuControlViewModel.cs — méthode publique pour importer des profils générés (Profiles + Persist) au lieu d'écrire settings.json directement
- src/PCPerfSuite.App/ViewModels/CpuControlViewModel.cs — idem pour CpuProfile
- src/PCPerfSuite.App/ViewModels/CompatibilityViewModel.cs — ligne « OC automatique » (dernier résultat, palier instable, raison)
- src/PCPerfSuite.App/ViewModels/MainViewModel.cs — enregistrement du VM et ordre de fermeture
- tests/PCPerfSuite.Core.Tests/OcSearchTests.cs

**Manques**

- Moteur de charge / bench GPU et CPU pour valider chaque palier : dépend de F1, rien n'existe.
- Détection d'instabilité : événements TDR du pilote d'affichage (journal Système), erreurs WHEA-Logger, plantage du processus de test, chute de score ou d'horloge. Rien n'est lu dans le journal d'événements aujourd'hui.
- Témoin « palier en cours » survivant à un plantage ou à un redémarrage : au relancement, retour d'usine et palier marqué instable.
- Sécurité thermique GPU : il n'existe que la protection du ventilateur à 88 °C, pas de retour d'usine de l'OC.
- Stratégie de marges : Safe = dernier palier stable moins N paliers, Aggressive = palier stable au test court, etc.
- Mémoire GPU testée séparément du cœur.
- Identité de la carte dans le profil : aujourd'hui seule la marque est comparée (GpuControlViewModel.cs:268).
- CPU : aucun OC réel possible. Ratio : MSR 0x1AD en lecture seule dans le module IntelMSR (IntelPowerLimitBackend.cs:16-17). Pas d'undervolt Intel (MSR 0x150, autorisation dans le module PawnIO non vérifiée, souvent verrouillé par « undervolt protection »). Pas de Curve Optimizer / PBO / boost override AMD : commandes SMU absentes, à répertorier par nom de code et à marquer expérimental. Pas de fenêtres de temps tau, ni TDC/EDC, ni ICCMax (0x601 volontairement jamais écrit, 25-28).
- Aucune « IA » : pas d'intégration LLM ; tout est à créer, ou bien en heuristiques déterministes.

**Pièges**

- Ne jamais lancer un test avec « Appliquer au démarrage » coché : ApplyOverclockAtStartup réapplique au lancement suivant les dernières valeurs persistées (GpuControlViewModel.cs:270, 328-353). Un palier instable serait donc réappliqué après le plantage même qu'il a provoqué. Il faut forcer KeepOverclockOnExit=false pendant tout le test et ne pas passer par Persist().
- GpuControlViewModel.ApplyClockOffsetsNow et ApplyProfile appellent Persist() : piloter l'OC automatique via le VM écrit chaque palier dans settings.json. Il faut passer directement par GpuControlService.
- Les Try* GPU renvoient true sans vérifier que la valeur a été retenue (NVAPI 247-248, ADLX TrySetInt). Seul GetOverclock() relu fait foi, à comparer à chaque palier.
- NVAPI : ReadDelta retombe sur des plages de repli si le pilote renvoie min=max (NvApiGpuBackend.cs:355-370). Ce ne sont pas les vraies limites de la carte.
- AMD : les écritures ADLX sont hors du témoin AdlxProbeGuard (portée limitée à l'initialisation, AdlxProbeGuard.cs:26-30). Une violation d'accès pendant la boucle tuerait le processus.
- Intel : aucune écriture sans renonciation (_waiverSet, IgclGpuBackend.cs:375-454).
- GPU de portable : NVAPI refuse souvent la limite de puissance (GpuControlViewModel.cs:368-370).
- CPU de portable : relever PL1/PL2 déclenche la sécurité à 98 °C / 15 s (CpuControlService.cs:17-21), qui rend les limites d'origine. La recherche doit traiter EmergencyRestored comme un échec du palier.
- Règle 6 (CLAUDE.md) : tout ceci est « expérimental » tant que ce n'est pas validé sur de vraies machines de chaque marque.
- Règle 5 : ne jamais toucher aux modes de performance constructeur des portables (EC/WMI).

### F5

**Déjà dans le code**

Un simple interrupteur binaire dans « Optimisation Windows » : le tweak « core-parking » (WindowsPerformanceSettingsService.cs:241-255).
- Il écrit CPMINCORES (GUID 0cc5b647-c1df-4637-891a-dec35c318583, PowerPlanService.cs:180-181) à 100 % sur secteur ET sur batterie, via powercfg /setacvalueindex + /setdcvalueindex + /S (PowerPlanService.cs:99-104).
- La valeur d'origine AC/DC est mémorisée avant la première écriture dans AppSettings.OriginalPowerValues, clés « sub/setting/ac|dc » (WindowsPerformanceSettingsService.cs:23-57, AppSettingsStore.cs:66). Décocher la restaure.
- L'état est lu par powercfg /query (les deux dernières valeurs hexadécimales, PowerPlanService.cs:113-119) : Enabled si 100, sinon Disabled ou Unknown.
- Rien d'autre : pas de pourcentage réglable, pas de CPMAXCORES, pas de réglage par classe de cœurs (P/E), pas d'état parqué par cœur, pas de charge par cœur. CpuSnapshot (HardwareModels.cs:61-71) n'expose que LoadPercent et MaxCoreLoadPercent. PdhCounterSampler ne lit qu'un compteur _Total sans joker (PdhCounterSampler.cs:14-15, 22-53).

**À réutiliser**

- src/PCPerfSuite.Core/Hardware/Cpu/CpuPowerTuningService.cs:62-219 — catalogue CpuPowerSetting + TryRead/TryWrite par powrprof (voit aussi les réglages masqués) : ajouter les réglages de parking ici les rend automatiquement affichables, enregistrables dans CpuProfile et relus
- src/PCPerfSuite.App/ViewModels/CpuControlViewModel.cs:18-194 — CpuPowerSettingViewModel (curseur AC/DC, debounce, relecture, Capture/ApplyFromProfile)
- src/PCPerfSuite.Core/Hardware/Cpu/CpuPlatformDetector.cs:115-148 — parcours de GetSystemCpuSetInformation (EfficiencyClass à l'octet 18) : à réutiliser pour étiqueter chaque cœur logique P ou E
- src/PCPerfSuite.Core/Hardware/PdhCounterSampler.cs:12-83 — base PDH (PdhAddEnglishCounterW, indépendant de la langue) à étendre avec PdhGetFormattedCounterArrayW pour « \Processor Information(*)\% Processor Utility » et « \Processor Information(*)\Parking Status »
- src/PCPerfSuite.Core/PowerSettings/WindowsPerformanceSettingsService.cs:23-57 — mémorisation de la valeur d'origine AC/DC (modèle de restauration)
- src/PCPerfSuite.App/Controls/MeterBar.cs, Sparkline.cs, AdaptiveGrid.cs — contrôles existants pour une grille de jauges par cœur
- src/PCPerfSuite.App/ViewModels/IBackgroundSensorConsumer.cs — ne rien relire fenêtre cachée (le visuel n'en a pas besoin)

**Fichiers à toucher**

- src/PCPerfSuite.Core/Hardware/Cpu/CpuPowerTuningService.cs — réglages « core-parking-min » (CPMINCORES), « core-parking-max » (CPMAXCORES ea062031-0e34-4ff1-9b6d-eb1059334028) et, sur processeur hybride, la variante de la classe performante (0cc5b647-…-8584) ; GUID à confirmer avec powercfg /qh
- NOUVEAU src/PCPerfSuite.Core/Hardware/Cpu/CoreActivityReader.cs (PDH par instance : charge et état parqué par cœur logique + classe d'efficacité)
- NOUVEAU src/PCPerfSuite.App/ViewModels/CoreParkingViewModel.cs (+ section de vue dans CpuControlView.xaml ou vue dédiée), plutôt que de grossir CpuControlViewModel (761 lignes)
- src/PCPerfSuite.Core/PowerSettings/WindowsPerformanceSettingsService.cs:241-255 — faire converger le tweak binaire avec le nouveau réglage (ou le retirer / le rediriger)
- src/PCPerfSuite.App/ViewModels/CompatibilityViewModel.cs — ligne « Core parking » : réglage exposé ou non par ce Windows, compteur Parking Status disponible ou non
- tests/PCPerfSuite.Core.Tests/ — analyse des instances PDH « 0,3 » → (groupe, cœur) et regroupement P/E

**Manques**

- Réglage en % (min et max de cœurs non parqués), par classe de cœurs sur les processeurs hybrides.
- Visuel temps réel par cœur logique : charge, fréquence, état parqué, étiquette P/E.
- Lecture de l'état de parking : compteur PDH « Parking Status » par instance, à vérifier sur Windows 10/11 FR.
- Restauration de la valeur d'origine quand le réglage passe par CpuPowerTuningService : TryWrite ne mémorise rien.

**Pièges**

- Deux mécanismes pour un même réglage :
  - le tweak passe par powercfg.exe avec mémoire d'origine (WindowsPerformanceSettingsService) ;
  - CpuPowerTuningService passe par powrprof sans mémoire d'origine.
  Les exposer tous les deux rendra l'interrupteur incohérent (il n'est « activé » qu'à exactement 100).
- Risque probable à vérifier : CpuPowerTuningService.cs:32-34 note que les réglages masqués n'apparaissent pas dans la sortie de powercfg, et CPMINCORES est masqué par défaut. Le GetState du tweak (powercfg /query, PowerPlanService.cs:113-119) peut donc renvoyer Unknown sur une installation standard.
- Réglage limité au plan actif : changer de plan (dont « Performances ultimes », qui en crée un nouveau) le remet par défaut (description du tweak, ligne 246).
- TryWrite réapplique tout le plan (PowerSetActiveScheme, CpuPowerTuningService.cs:209), l'écriture la plus lourde de l'app : garder le Debouncer.
- AMD Ryzen double CCD : le parking est piloté par le pilote chipset AMD (préférence de CCD), qui peut écraser le réglage. À expliquer, pas à supposer.
- Snapdragon : réglage parfois absent, alors N/D avec la raison.
- Charge par cœur : reproduire le Gestionnaire des tâches (% Processor Utility, plafonné à 100) et non % Processor Time (voir HardwareMonitorService.cs:177-178 et ProcessService.cs:475-481).

### F6

**Déjà dans le code**

Interprétation la plus probable : cartes graphiques. L'OC GPU AMD et Intel est DÉJÀ implémenté de bout en bout :
- AdlxGpuBackend (485 lignes) avec les tables de fonctions natives AdlxNative.cs (331 lignes) ;
- IgclGpuBackend (529 lignes) avec IgclNative.cs (264 lignes).
Les deux sont branchés dans GpuControlService.cs:58-63 et affichés dans le même onglet que NVIDIA. Ils restent marqués expérimentaux (GpuControlViewModel.cs:144-151 : seul NVAPI a été testé sur une vraie machine). AMD est protégé à l'initialisation par AdlxProbeGuard (2 non-retours consécutifs → ADLX n'est plus tenté, message à GpuControlViewModel.cs:101-107 et ligne de diagnostic CompatibilityViewModel.cs:174-182).
Ce qui reste N/D par limite d'API : limite de température AMD (AdlxGpuBackend.cs:341), raison du bridage AMD/Intel (316 / IgclGpuBackend.cs:438), ventilateur AMD uniquement en vitesse fixe (courbe du pilote aplatie, 371-405).
Si « cartes » veut dire cartes mères, donc OC CPU sur plateformes AMD et Intel : voir F4. Seules les limites de puissance existent, pas les ratios, la tension ou le Curve Optimizer.

**À réutiliser**

- src/PCPerfSuite.Core/Hardware/Gpu/IGpuTuningBackend.cs:11-51 — contrat commun (ne lève jamais, drapeaux Supported)
- src/PCPerfSuite.Core/Hardware/Gpu/AdlxGpuBackend.cs:59-106, 110-151 — initialisation et choix du GPU dédié
- src/PCPerfSuite.Core/Hardware/Gpu/IgclGpuBackend.cs:207-261 — résolution V1/V2 et unités
- src/PCPerfSuite.Core/Hardware/Gpu/AdlxProbeGuard.cs — témoin de plantage
- src/PCPerfSuite.App/ViewModels/GpuControlViewModel.cs:356-390 — DescribeUnsupported (N/D expliqué par marque)
- src/PCPerfSuite.App/ViewModels/CompatibilityViewModel.cs:168-182 — lignes Contrôle GPU

**Fichiers à toucher**

- src/PCPerfSuite.Core/Hardware/Gpu/AdlxGpuBackend.cs / AdlxNative.cs — corrections après tests réels ; éventuellement IADLXGPUPresetTuning / IADLXGPUAutoTuning (presets et auto-undervolt Adrenalin, utiles à F4)
- src/PCPerfSuite.Core/Hardware/Gpu/IgclGpuBackend.cs / IgclNative.cs — corrections après tests réels
- src/PCPerfSuite.App/ViewModels/GpuControlViewModel.cs:147 — lever le drapeau expérimental par marque une fois vérifiée (règle 6)
- src/PCPerfSuite.App/ViewModels/CompatibilityViewModel.cs:168-170 — détailler par capacité (UnsupportedNotes, expérimental, version du pilote), aujourd'hui juste « Disponible via … »
- README.md:112-154

**Manques**

- Validation sur de vraies Radeon RDNA 1 à 4 et Arc A/B : impossible sans le matériel. Il faut une campagne de test avec le rapport de Compatibilité.
- Relecture et comparaison après écriture, qui ne fait que s'afficher aujourd'hui.
- Détail des capacités GPU dans le diagnostic.
- Gestion de plusieurs GPU : une seule carte pilotée, première trouvée. NVAPI prend FirstOrDefault (NvApiGpuBackend.cs:47), sans choix possible.
- Aucune raison du bridage chez AMD/Intel.

**Pièges**

- Les index des tables de fonctions ADLX sont recopiés des en-têtes du SDK (AdlxProbeGuard.cs:8-12) : un pilote Adrenalin qui réordonne provoque une AccessViolation non rattrapable. Les écritures sont hors du témoin.
- IGCL utilise une valeur d'unité magique « 3 » pour les volts (IgclGpuBackend.cs:307, 318) : à confirmer sur le matériel.
- La tension AMD est absolue (RDNA 1-3) ou en décalage (RDNA 4), détectée par range.Min <= 0 (AdlxGpuBackend.cs:313) : heuristique à valider.
- SettingsMatchCurrentGpu ne compare que la marque (GpuControlViewModel.cs:268) : deux cartes AMD différentes partagent le même OC réappliqué.
- Sans administrateur, les pilotes refusent tout (UnavailableMessage 92-94).
- F6 ne peut pas être « terminée » par du code seul : il faut des machines de test.

### F16

**Déjà dans le code**

Rien. Aucun appel EnumDisplaySettings, ChangeDisplaySettingsEx, DisplayConfig ou résolution personnalisée NVAPI/ADLX/IGCL dans src (grep vide). La seule référence aux écrans est SystemParameters (MainWindow.xaml.cs:101-120, OverlayWindow.xaml.cs:55-56). RefreshRates.cs concerne la cadence de rafraîchissement du monitoring, pas l'écran.

**À réutiliser**

- src/PCPerfSuite.Core/Hardware/GpuControlService.cs:53-87 — modèle de choix de backend par marque (NVAPI → ADLX → IGCL), réutilisable pour un IDisplayModeBackend
- src/PCPerfSuite.Core/Hardware/Gpu/NvApiGpuBackend.cs — NvAPIWrapper.Net déjà référencé (PCPerfSuite.Core.csproj), qui expose l'API d'affichage NVIDIA (résolutions personnalisées : à vérifier dans la version 0.8.1.101)
- src/PCPerfSuite.Core/Hardware/Gpu/AdlxNative.cs — mécanique d'appel ADLX (services d'affichage ADLX à ajouter)
- src/PCPerfSuite.Core/Hardware/Gpu/AdlxProbeGuard.cs — témoin disque : si l'écran reste noir et que l'utilisateur redémarre, ne pas réappliquer le mode testé
- src/PCPerfSuite.Core/Environment/MachineInfo.cs:44 — VideoControllers ; IsLaptop (dalle interne)

**Fichiers à toucher**

- NOUVEAU src/PCPerfSuite.Core/Hardware/Display/ (DisplayModeService.cs : énumération des écrans et modes Win32 ; IDisplayOverclockBackend + NvApiDisplayBackend / AdlxDisplayBackend : résolution personnalisée ; DisplayModeModels.cs)
- NOUVEAU src/PCPerfSuite.App/ViewModels/DisplayViewModel.cs + Views/DisplayView.xaml (entrée de navigation dans MainViewModel.cs:99-110)
- src/PCPerfSuite.Core/PowerSettings/AppSettingsStore.cs — sous-section DisplaySettings (mode validé par écran, chaînes)
- src/PCPerfSuite.App/ViewModels/CompatibilityViewModel.cs — ligne « Fréquence d'écran » (modes proposés, OC possible ou non par marque)
- src/PCPerfSuite.App/ViewModels/MainViewModel.cs — construction et Dispose

**Manques**

Tout est à faire :
1) Lister les écrans et leurs modes (EnumDisplayDevices / EnumDisplaySettingsEx). Signaler un écran réglé sous sa fréquence maximale native (utile pour F3 : « écran 165 Hz réglé à 60 Hz »).
2) Appliquer un mode listé (ChangeDisplaySettingsEx avec CDS_TEST puis application).
3) Vrai OC : essai d'une résolution personnalisée via NVAPI (essai temporaire), ADLX (résolution personnalisée) ou IGCL. Confirmation obligatoire avec retour automatique après environ 15 s sans réponse, comme Windows.
4) Recherche par paliers (+1 à +5 Hz) avec validation visuelle par l'utilisateur ; il n'existe pas de détection logicielle fiable des images sautées.

**Pièges**

- Risque d'écran noir : ne jamais persister un mode avant confirmation. Retour automatique garanti par minuteur, et par témoin disque au redémarrage.
- Dalles de portable souvent verrouillées (et MUX / Optimus : l'écran interne est branché sur l'iGPU, donc c'est l'API Intel/AMD de l'iGPU qui compte, pas celle du GPU dédié).
- Limite de bande passante du câble ou du port (HDMI 2.0 / DP 1.2), G-Sync / FreeSync perdus en mode personnalisé : la cause doit être expliquée (règle 3).
- Pas d'API publique ADLX/IGCL garantie pour les résolutions personnalisées sur toutes les générations : détection à l'exécution et N/D expliqué.
- Marquer expérimental (règle 6).
- Aucun lien avec GpuControlService aujourd'hui : un seul GPU y est retenu, alors que l'écran peut être sur un autre adaptateur.

### F18

**Déjà dans le code**

Aucune analyse d'usage, aucune bascule automatique, aucune notion de profil actif.
Éléments exploitables :
- préréglages de courbe Silencieux / Équilibré / Perf (FanCurveMath, FanControlModels.cs:172-197) ;
- détection du jeu au premier plan via RTSS (HardwareMonitorService.cs:185 → HardwareSnapshot.Game, HardwareModels.cs:339 ; RtssFrameStatsReader.TryReadForeground 68, GetForegroundProcessId 215-221), mais seulement si RTSS tourne ;
- charges CPU/GPU dans chaque relevé (HardwareSnapshot.Cpu.LoadPercent, Gpu.LoadPercent) ;
- énumération des fenêtres de processus dans ProcessService (ProcessService.cs:698) ;
- mode éco et IBackgroundSensorConsumer pour tourner fenêtre cachée ;
- CpuPlatform.HasBattery et BatterySnapshot.PowerOnline pour secteur / batterie.
Les trois profils existants (CPU, GPU, ventilation) sont réglables à la main mais séparés (voir F19).

**À réutiliser**

- src/PCPerfSuite.App/ViewModels/MonitoringViewModel.cs:565 — SnapshotUpdated (thread UI) : source des signaux d'usage
- src/PCPerfSuite.Core/Overlay/RtssFrameStatsReader.cs:68-221 — jeu au premier plan (PID) ; GetForegroundProcessId à extraire en utilitaire public si besoin sans RTSS
- src/PCPerfSuite.App/ViewModels/IBackgroundSensorConsumer.cs:9-53 + MainViewModel.cs:152-154 — continuer à basculer fenêtre cachée (ajouter le consommateur à la liste)
- src/PCPerfSuite.Core/Hardware/FanControlModels.cs:172-197 — préréglages de courbe par usage
- src/PCPerfSuite.Core/Hardware/FanControlModels.cs:206-255 — FanCurveRegulator : modèle d'hystérésis à transposer en délai de maintien / anti-oscillation entre profils
- src/PCPerfSuite.Core/Hardware/Cpu/CpuControlService.cs:19-21, 104-128 — modèle « condition tenue N secondes avant d'agir » (EmergencyDelay)
- src/PCPerfSuite.App/Utils/RunningStats.cs, SampleHistory.cs — agrégats pour l'analyse d'usage
- src/PCPerfSuite.Core/PowerSettings/AppSettingsStore.cs:409-418 — Update() pour persister l'historique ou les profils générés
- tout le socle F19 (groupes de profils) : une bascule automatique = appliquer un groupe

**Fichiers à toucher**

- NOUVEAU src/PCPerfSuite.Core/Profiles/UsageClassifier.cs (logique pure : relevés → bureautique / gaming léger / gaming intensif, avec délai de maintien) + tests
- NOUVEAU src/PCPerfSuite.Core/Profiles/UsageHistory.cs (agrégats persistés, pas de relevés bruts)
- NOUVEAU src/PCPerfSuite.App/ViewModels/AutoProfileSwitcher.cs (abonné à SnapshotUpdated, IBackgroundSensorConsumer, applique les groupes via l'orchestrateur de F19)
- src/PCPerfSuite.App/ViewModels/MainViewModel.cs:80-117, 152-154, 191-215 — construction, mode éco, ordre de Dispose (le switcher doit s'arrêter AVANT les VM GPU/CPU/ventilateurs)
- src/PCPerfSuite.Core/PowerSettings/AppSettingsStore.cs — AutoSwitchSettings (activé, délais, association usage → groupe, en chaînes)
- src/PCPerfSuite.App/ViewModels/CompatibilityViewModel.cs — ligne « Bascule automatique » (dernier changement, raison, parties non appliquées)
- tests/PCPerfSuite.Core.Tests/UsageClassifierTests.cs

**Manques**

- Collecte d'un historique d'usage.
- Classification en 3 usages, idéalement en fonction pure et testable.
- Génération des 3 groupes (ventilation + OC CPU/GPU) :
  - bureautique : courbe Silencieux, PL / EPP économie, GPU d'origine ;
  - gaming léger : Équilibré + OC Safe (F4) ;
  - gaming intensif : Perf + OC Classic.
- Moteur de bascule avec délai de maintien, pause sur réglage manuel, verrouillage après une sécurité thermique.
- Interface d'édition manuelle des 3 groupes (dépend de F19).
- Indicateur « profil actif ».

**Pièges**

- Verrouiller la bascule après CpuControlService.EmergencyRestored (CpuControlService.cs:35, 121-127), sinon elle reposerait immédiatement une limite relevée, en boucle.
- Chaque bascule écrit settings.json : les ApplyProfile des VM appellent Persist, et Fans.ApplyProfile persiste FanCurves (FanCurvesViewModel.cs:1035-1039). L'état « de démarrage » devient celui de la dernière bascule, parce que « Appliquer au démarrage » réapplique la dernière valeur persistée (CpuControlViewModel.cs:402-409, GpuControlViewModel.cs:328-353). Il faut décider explicitement de ce qui est réappliqué au lancement.
- Les réglages du plan d'alimentation écrits par CpuPowerSettingViewModel sont PERMANENTS : jamais restaurés à la fermeture. Une bascule laisse donc le PC sur le dernier profil de plan quand l'app se ferme.
- Chaque écriture de plan fait un PowerSetActiveScheme (coûteux) : délai de maintien obligatoire.
- Portable : aucun ventilateur de carte mère pilotable (règle 5, FanCurvesViewModel.cs:1212-1214), et les modes constructeur sont interdits en écriture. La partie « ventilation » d'un groupe se réduit au cooler GPU piloté par le pilote graphique. À dire dans l'UI.
- Accord de risque CPU (RiskAccepted) et renonciation Intel : une bascule automatique ne doit jamais les contourner ; elle signale la partie non appliquée.
- Détection du jeu : RTSS optionnel, donc repli sur la charge GPU ou le processus au premier plan.
- Le switcher ne doit pas tourner pendant un test OC (F4).

### F19

**Déjà dans le code**

Trois familles de profils indépendantes, chacune enregistrée, appliquée et supprimée dans son onglet. Aucune notion de groupe, aucun identifiant stable, pas de référence croisée.
- CPU : CpuProfile {Name, PowerSettings: Dictionary<string, CpuProfilePowerValue{Ac,Battery}>, SustainedWatts?, BurstWatts?} (CpuProfile.cs:13-35), stocké dans AppSettings.Cpu.Profiles (AppSettingsStore.cs:221). Enregistrement CpuControlViewModel.cs:537-564, application 572-606 : réglages du plan puis watts seulement si IsPowerLimitAvailable && RiskAccepted (626-644). Persistance 669-674.
- GPU : GpuOverclockProfile {Name, CoreClockOffsetMhz, MemoryClockOffsetMhz (non nullables), PowerLimitPercent?, TemperatureLimitC?, VoltageValue?/VoltageUnit? + ancien VoltageBoostPercent} (GpuControlModels.cs:107-131), stocké dans AppSettings.Gpu.OverclockProfiles (AppSettingsStore.cs:184). Enregistrement GpuControlViewModel.cs:527-553, application 555-592 (refus si !CanOverclock, tension seulement si même unité).
- Ventilation : FanProfile {Name, Fans: List<FanCurveConfig>, FanNames} (FanProfile.cs:12-23), stocké dans AppSettings.FanProfiles (AppSettingsStore.cs:43). Enregistrement FanCurvesViewModel.cs:960-987, application 995-1062 (Match + Sanitize, ventilateurs absents annoncés, un seul Persist), renommage 1084-1113.
« Appliquer au démarrage » : CPU → settings.Cpu.ApplyAtStartup (dernières valeurs en watts, pas un profil) ; GPU → settings.Gpu.ApplyOverclockAtStartup (dernières valeurs courantes, si même marque) ; ventilateurs → toujours réappliqués (le mode persisté dans FanCurves reprend au premier relevé) et rendus au BIOS à la fermeture.

**À réutiliser**

- src/PCPerfSuite.Core/Hardware/Cpu/CpuProfile.cs:13-35 — CpuProfile (tolérant, null = ne pas toucher)
- src/PCPerfSuite.Core/Hardware/GpuControlModels.cs:107-131 — GpuOverclockProfile
- src/PCPerfSuite.Core/Hardware/Fans/FanProfile.cs:12-160 — FanProfile + FanProfileMatcher.Match/Sanitize (modèle d'application tolérante et testée)
- src/PCPerfSuite.App/ViewModels/CpuControlViewModel.cs:572-644 — logique ApplyProfile / ApplyProfileWatts à extraire en méthode publique renvoyant un rapport
- src/PCPerfSuite.App/ViewModels/GpuControlViewModel.cs:555-592 — idem
- src/PCPerfSuite.App/ViewModels/FanCurvesViewModel.cs:995-1062 — idem (LastProfileReport déjà exposé au diagnostic, 839)
- src/PCPerfSuite.App/ViewModels/CpuControlViewModel.cs:648-667 et FanCurvesViewModel.cs:1141-1164 — résumés dans les termes de la machine
- src/PCPerfSuite.App/ViewModels/FanCurvesViewModel.cs:676-717 — FanProfileViewModel avec renommage sur place (à imiter pour les groupes)
- src/PCPerfSuite.Core/PowerSettings/AppSettingsStore.cs:409-418 — Update()

**Fichiers à toucher**

- NOUVEAU src/PCPerfSuite.Core/Profiles/ProfileGroup.cs ({Id, Name, Usage en chaîne, Cpu: CpuProfile?, Gpu: GpuOverclockProfile?, Fans: FanProfile?, Origine « manuel »/« généré »}) : copies intégrées plutôt que références par nom
- NOUVEAU src/PCPerfSuite.App/ViewModels/ProfileGroupsViewModel.cs + Views/ProfileGroupsView.xaml (entrée de navigation MainViewModel.cs:99-110)
- src/PCPerfSuite.App/ViewModels/CpuControlViewModel.cs — ajouter public string ApplyProfileModel(CpuProfile) et public CpuProfile CaptureCurrent(), puis faire appeler ces méthodes par la RelayCommand existante
- src/PCPerfSuite.App/ViewModels/GpuControlViewModel.cs — idem (GpuOverclockProfile)
- src/PCPerfSuite.App/ViewModels/FanCurvesViewModel.cs — idem (FanProfile) ; fichier déjà à 1512 lignes, donc extraction minimale
- src/PCPerfSuite.Core/PowerSettings/AppSettingsStore.cs:10-67 — AppSettings.ProfileGroups (+ éventuellement ActiveProfileGroupId)
- src/PCPerfSuite.App/ViewModels/CompatibilityViewModel.cs:502-507 — ligne « Groupes de profils » sur le modèle de FanProfilesRow
- tests/PCPerfSuite.Core.Tests/ProfileGroupTests.cs (sérialisation tolérante, parties absentes)

**Manques**

- Modèle de groupe, stockage et interface (liste, enregistrer « l'état actuel » comme groupe, appliquer, renommer, supprimer).
- API publique d'application sur les 3 VM, avec rapport consolidé des parties non appliquées et de leurs raisons.
- Ordre d'application sûr :
  - montée en performance : ventilateurs d'abord, puis CPU/GPU ;
  - descente : CPU/GPU d'abord, puis ventilateurs.
- Indicateur de groupe actif.
- Import des profils générés par F4 / F18.
- Décision sur « Appliquer au démarrage » au niveau du groupe.

**Pièges**

- Propriété des sous-arborescences : chaque VM réécrit sa section depuis sa copie en mémoire. FanCurvesViewModel.Persist écrit settings.FanProfiles = _settings.FanProfiles (FanCurvesViewModel.cs:1486-1496) ; CPU et GPU réécrivent leurs listes de Profiles. Écrire des profils directement dans settings.json depuis un autre service serait écrasé au prochain enregistrement : tout doit passer par les VM.
- Références par nom fragiles : les profils de ventilation se renomment, ceux du CPU et du GPU sont remplacés à nom égal. D'où des copies intégrées, ou un Id stable ajouté aux trois classes (compatible System.Text.Json, propriété absente = valeur par défaut).
- GpuOverclockProfile a des décalages non nullables : « ne pas toucher au GPU » = Gpu null dans le groupe, pas un profil à 0.
- Les ApplyProfile actuels écrivent le statut de leur propre onglet et DeleteProfile ouvre une MessageBox dans le VM. Il faut garder ces interactions hors du chemin automatique.
- Enums sérialisées en nombres dans les FanCurveConfig intégrées (FanControlMode, FanTempSource) : ne pas insérer de valeur au milieu d'une énumération.
- Les ventilateurs ne sont connus qu'après le premier relevé (_hasSnapshot, FanCurvesViewModel.cs:785) : appliquer un groupe au lancement avant ce relevé ne réglerait aucun ventilateur.
- Portables et accord de risque / renonciation : mêmes limites qu'en F18.

### F2

**Déjà dans le code**

Hors zone principale, mais plusieurs signaux de diagnostic existent déjà dans ma zone :
- raison du bridage GPU (puissance / température / tension / pas de charge), NVIDIA seulement (NvApiGpuBackend.cs:190-211, affichage GpuControlViewModel.cs:638-664) ;
- limites CPU en place contre limites d'usine (CpuPowerLimitSnapshot, CpuFirmwareDefaultsStore) et plafond (CpuMaxWattsInfo) ;
- température, puissance et fréquence CPU/GPU ;
- identification et catégorie des ventilateurs (CPU, pompe, GPU, boîtier), connecteurs vides (EmptyHeaderDetector), puissance d'alimentation Corsair (HardwareSnapshot.PsuPowerWatts).

**À réutiliser**

- src/PCPerfSuite.Core/Hardware/GpuControlService.cs:118 — GetActiveLimit
- src/PCPerfSuite.Core/Hardware/Cpu/CpuTuningModels.cs:23-41 — CpuPowerLimitSnapshot (limite actuelle et d'origine)
- src/PCPerfSuite.Core/Hardware/Fans/FanInventory.cs, FanIdentification.cs, EmptyHeaderDetector.cs — inventaire du refroidissement (pompe présente ? ventilateur CPU à 0 tr/min ?)
- src/PCPerfSuite.App/ViewModels/FanCurvesViewModel.cs:793-839 — compteurs exposés au diagnostic

**Fichiers à toucher**

- (zone bench/IA) consommer ces API ; aucune modification nécessaire dans la zone tuning, sauf exposer la raison du bridage dans le relevé si le rapport doit l'avoir en arrière-plan

**Manques**

- La raison du bridage n'est lue que par le VM GPU, fenêtre visible, une fois par seconde (GpuControlViewModel.cs:612, 638-649). Il faudrait un accès pendant un bench.
- Aucune raison de bridage CPU (PROCHOT, limite de puissance atteinte) n'est lue : ni les bits de throttle MSR, ni les limites SMU.

**Pièges**

Chez AMD/Intel, GetActiveLimit renvoie null, donc N/D : le diagnostic « mauvais refroidissement / alimentation insuffisante » doit s'appuyer sur température + fréquence + puissance, pas sur ce drapeau.

### F17

**Déjà dans le code**

Hors zone. L'overlay fenêtre se positionne sur l'écran principal uniquement : OverlayWindow.Reposition utilise SystemParameters.PrimaryScreenWidth/Height (src/PCPerfSuite.App/Views/OverlayWindow.xaml.cs:55-56). OverlayAppearanceSettings n'a pas de champ écran (AppSettingsStore.cs:279-327).

**À réutiliser**

- src/PCPerfSuite.App/Views/OverlayWindow.xaml.cs:49-70 — Reposition
- src/PCPerfSuite.Core/PowerSettings/AppSettingsStore.cs:279-327 — OverlayAppearanceSettings (ajouter un identifiant d'écran en chaîne)

**Fichiers à toucher**

- src/PCPerfSuite.App/Views/OverlayWindow.xaml.cs
- src/PCPerfSuite.App/ViewModels/OverlayAppearanceViewModel.cs
- src/PCPerfSuite.Core/PowerSettings/AppSettingsStore.cs

**Manques**

- Énumération des écrans (identique à F16, point 1 : module d'écrans partageable).
- Choix persisté par identifiant stable (nom de périphérique), avec repli sur l'écran principal si l'écran a disparu.

**Pièges**

- Coordonnées DIP / DPI par écran : un écran secondaire avec une autre mise à l'échelle.
- Le module d'énumération des écrans de F16 devrait être commun à F17.

### Remarques

- Garde-fous absents côté GPU, alors qu'ils existent côté CPU et ventilateurs : pas de sécurité thermique qui annule l'OC GPU (seul le ventilateur passe à 100 % à 88 °C, FanCurvesViewModel.cs:272, 409-414), pas de ré-armement après veille (aucun PowerModeChanged dans GpuControlViewModel), et GpuControlViewModel n'est pas un IBackgroundSensorConsumer (MainViewModel.cs:154). À combler AVANT F4 et F18, qui automatisent des écritures GPU.
- Dette : CpuControlViewModel.OnPowerModeChanged (CpuControlViewModel.cs:352-366) ne fait rien au réveil si « Appliquer au démarrage » est décoché. Le firmware a rétabli ses limites, mais l'onglet affiche encore la valeur d'avant la veille (aucune relecture).
- Les réglages Windows du plan d'alimentation (CpuPowerTuningService) et les tweaks de l'onglet Optimisation sont des changements PERMANENTS de Windows : ils ne sont jamais rendus à la fermeture, contrairement aux watts CPU, à l'OC GPU et aux ventilateurs. À dire clairement dès qu'un profil ou une bascule automatique les touche.
- Les fichiers déjà gros sont à ne pas faire grossir : FanCurvesViewModel.cs (1512 lignes), HardwareMonitorService.cs (1280), CpuControlViewModel.cs (761), GpuControlViewModel.cs (705), CompatibilityViewModel.cs (693), ProcessesViewModel.cs (2218). Les nouvelles fonctions vont dans de nouveaux fichiers (Core/Profiles, Core/Tuning, Core/Hardware/Display, nouveaux ViewModels) ; on n'extrait des VM existants que des méthodes publiques d'application et de capture.
- Aucune intégration IA/LLM, aucun moteur de bench ni de stress, aucun historique d'usage dans le dépôt. Le seul client HTTP est OfficialInstaller (Installations/OfficialInstaller.cs:60). F4 et F18 dépendent donc du moteur de bench de F1 (autre zone) et d'une décision d'architecture sur « l'IA » : heuristiques locales testables ou appel à un modèle.
- Ambiguïté F6 : l'OC GPU AMD Radeon (ADLX) et Intel Arc (IGCL) est déjà codé en entier. Ce qui manque surtout, c'est la VALIDATION sur de vraies cartes pour lever le drapeau « expérimental » (règle 6). Claude ne peut pas le faire seul : il faut un plan de test et des correctifs guidés par les rapports de Compatibilité. Si « cartes » veut dire « cartes mères » (OC CPU AMD / Intel), cela rejoint F4 : ni ratio, ni tension, ni Curve Optimizer aujourd'hui.
- Ordre de travail suggéré pour ta zone :
1) Socle profils : F19 + extraction d'une API publique « appliquer / capturer » sur les 3 VM + garde-fous GPU manquants (sécurité thermique, veille).
2) F18 bâti sur F19.
3) F5, indépendant (CpuPowerTuningService + lecteur PDH par cœur + visuel).
4) F16, module d'écrans neuf et expérimental, à mutualiser avec F17.
5) F4 en dernier, après le bench de F1 et le socle F19 (les 3 profils OC sont des GpuOverclockProfile / CpuProfile).
6) F6 en campagne de validation matérielle.
- Pour toute bascule ou application automatique : ne jamais contourner CpuControlSettings.RiskAccepted ni la renonciation Intel (IntelOverclockWaiverAccepted). Toujours annoncer la partie non appliquée et sa raison, comme le font déjà ApplyProfile CPU et ventilateurs.
- settings.json sérialise les enums en nombres (pas de JsonStringEnumConverter, commentaire AppSettingsStore.cs:110-114). Les nouveaux modèles (usage, groupe, écran choisi) doivent utiliser des chaînes pour rester lisibles à la main et robustes aux évolutions.


## Gestionnaires système : processus (actions et limites), disques, pilotes et outils à télécharger, effets visuels et animations Windows, RAM, périphériques

### Architecture

La solution compte deux projets : Core (net8.0-windows, LibreHardwareMonitorLib 0.9.6, TraceEvent 3.2.6, NvAPIWrapper, System.Management) et App (WPF, CommunityToolkit.Mvvm 8.2.2). L'app est manifestée requireAdministrator (app.manifest:13). MainViewModel (src/PCPerfSuite.App/ViewModels/MainViewModel.cs:80-117) construit tous les ViewModels dans son constructeur et publie la navigation en NavEntry (lignes 99-110). MainWindow.xaml (lignes 108-127) affiche chaque vue selon le titre de la page (CurrentPage.Title, converti par StringEqualsToVisibility). Paramètres a ses sous-onglets dans AppSettingsViewModel.Sections (AppSettingsViewModel.cs:32-38), avec un panneau par clé dans AppSettingsView.xaml.
Processus : ProcessService (Core/Processes/ProcessService.cs, 1118 l.) garde des handles PROCESS_QUERY_LIMITED_INFORMATION, fait un instantané Toolhelp, calcule le %CPU calibré sur PDH et un anneau de 3 relevés. ProcessIoTracer (session ETW noyau nommée, admin, active seulement quand l'onglet est affiché) donne le disque et le réseau par PID. Une seule action sur un processus : Terminate (lignes 554-617), avec revérification de l'identité PID + heure de démarrage et le garde ProcessTerminationGuard. ProcessesViewModel.cs (2218 l.) contient 5 classes, et son relevé ne tourne que si l'onglet est visible (IsActive 863-886, MainViewModel.UpdateAttention 163-173).
Stockage et disques : DiskHealthService (WMI root\Microsoft\Windows\Storage, MSFT_PhysicalDisk et MSFT_StorageReliabilityCounter), DiskVolumeReader et DiskModelReader (Win32_*), HardwareMonitorService.ReadDisk (LHM). L'affichage est dans Monitoring › Disques (DiskItemViewModel, MonitoringViewModel.cs:17-135). L'onglet Stockage est une treemap (DiskSpaceScanner, FolderTree), la suppression passe par la Corbeille sous DeletionGuard. Nettoyage : KnownCaches et CacheCleanerService.
Réglages Windows : WindowsPerformanceSettingsService.GetTweaks() renvoie des PerformanceTweak (Id, Category, GetState, Apply(bool), RequiresElevation/Restart/IsRisky/IsReadOnly). OptimizationViewModel les enveloppe dans des TweakItemViewModel, et la vue les regroupe par Category. Les écritures passent par RegistryHelper (DWORD uniquement) et par PowerPlanService (powercfg, parsing indépendant de la langue). Les valeurs d'origine sont mémorisées dans AppSettings.OriginalPowerValues.
Installations : OfficialInstaller télécharge en HTTPS vers des hôtes autorisés, contrôle chaque redirection à la main, borne la taille à 100 Mo, relève l'intégrité du dossier à High, verrouille le fichier en lecture seule, vérifie l'en-tête MZ, la signature Authenticode (WinVerifyTrust) et l'éditeur attendu, puis lance l'installeur (runas si l'app n'est pas élevée). Seul PawnIO l'utilise. RTSS n'ouvre que sa page officielle (ExternalLink, HTTPS seulement). InstallationsViewModel et ExternalSoftwareViewModel servent l'onglet Paramètres › Installations et le clignotement du bouton Paramètres.
Réglages de l'app : AppSettingsStore, un fichier JSON unique dans %LOCALAPPDATA%\PCPerfSuite\settings.json, écriture atomique sous verrou, avec Update() pour lire-modifier-écrire. Chaque onglet possède son bloc, stocké en chaînes plutôt qu'en enums.
Diagnostic : CompatibilityViewModel.BuildRows (106-221) produit des CompatibilityRow. CopyReport (621-692) en fait un rapport texte qui masque les données personnelles (IsPersonal).
Garde-fous transverses : best-effort (résultats typés et jamais d'exception, sauf RegistryHelper.WriteDword, qui lève volontairement), confirmations MessageBox avec « Non » par défaut, jamais de Process.Start direct d'un exécutable depuis l'app élevée (on passe par explorer.exe), et SessionUser.IsOtherProfile pour signaler que HKCU ou %TEMP% désignent un autre profil.

### Conventions

- Namespaces : les fichiers de src/PCPerfSuite.Core/Environment/ sont dans le namespace PCPerfSuite.Core.SystemInfo (MachineInfo, ElevationHelper, SessionUser, StartupTask). Ne jamais créer un namespace PCPerfSuite.Core.Environment : il masquerait System.Environment (voir ProcessService.cs:161-162).
- Best-effort (règle 2) : une méthode Core qui touche au matériel ou au système renvoie un résultat typé avec sa raison (TerminateResult/TerminateFailure dans ProcessModels.cs:175-202, InstallOutcome dans OfficialInstaller.cs:22, SignatureCheck dans AuthenticodeVerifier.cs:9, RecycleResult, DiskHealthReport.ErrorMessage) ou null, et ne lève jamais. Le Message technique reste brut ; c'est le ViewModel qui écrit la phrase pour l'utilisateur (ProcessModels.cs:194-197).
- Exception assumée : RegistryHelper.WriteDword lève sur une écriture demandée explicitement (RegistryHelper.cs:39-63), et ReadDword lève InvalidOperationException sur un accès refusé (8-31). TweakItemViewModel les attrape et affiche ErrorMessage avec « État inconnu ».
- Gardes : classe statique avec GetRefusalReason(x) qui renvoie une phrase prête à afficher, ou null si l'action est permise. En cas d'exception, le garde refuse (fail-safe). Modèles : ProcessTerminationGuard.cs:26-70 et DeletionGuard.cs:46-59. Ne jamais identifier par le seul nom de fichier (ProcessService.cs:874-879).
- Actions destructrices : revérifier l'identité PID + StartTimeUtc sur un handle ouvert pour l'occasion avec les droits minimaux (ProcessService.cs:554-617). Le handle de relevé reste en lecture seule (722-726). Confirmation MessageBox détaillée avec MessageBoxResult.No par défaut (ProcessesViewModel.cs:2026-2036, StorageViewModel.cs:362-372), puis compte rendu dans StatusText.
- App élevée : ne jamais faire Process.Start d'un exécutable ou d'un fichier utilisateur, passer par explorer.exe (ProcessesViewModel.cs:2089-2094, StorageViewModel.cs:236-240). Les URL passent par ExternalLink.TryOpen (HTTPS seulement), les téléchargements exécutables par OfficialInstaller.
- Hors thread UI : Task.Run pour WMI, powercfg et les relevés. Les services sérialisent leur état sous un verrou (_gate). Rien de lent dans un constructeur de ViewModel : StorageViewModel.cs:112-118 charge ses lecteurs en asynchrone parce que MainViewModel est construit avant la fenêtre.
- Coût : un relevé ne tourne que si son onglet est visible (ProcessesViewModel.IsActive). En mode éco, seuls les IBackgroundSensorConsumer continuent (MainViewModel.cs:152-154).
- Affichage (règle 3) : « -- » = pas encore lu, « N/D » = ce PC ne le fournit pas (exemple : ProcessRowViewModel.NetworkDisplay, ProcessesViewModel.cs:353-357). Une colonne dont la donnée manque sur toute la machine se masque (IsAvailable), et une capacité vue une fois reste acquise (ProcessService.cs:263-267).
- Réglages : un bloc par onglet dans AppSettings (AppSettingsStore.cs:10-67). Valeurs en chaînes plutôt qu'en enums (AppSettingsStore.cs:108-114). Utiliser AppSettingsStore.Update pour lire-modifier-écrire (405-418), ou relire avant d'écrire (ProcessesViewModel.Persist 2172-2187). Ajouter une colonne à des réglages déjà enregistrés passe par un drapeau « déjà proposé » (NetworkColumnOffered, ProcessesViewModel.cs:907-911).
- Écriture système réversible : mémoriser la valeur d'origine avant la première écriture et la restaurer exactement (WindowsPerformanceSettingsService.cs:15-57, AppSettings.OriginalPowerValues, PreUltimatePerformanceSchemeGuid).
- Nouveau réglage Windows : une entrée PerformanceTweak. Sa Category crée automatiquement une carte dans l'onglet Optimisation (OptimizationView.xaml:7-12). Poser RequiresElevation=false pour ce qui n'écrit que dans HKCU, et RequiresRestart ou IsRisky selon le cas.
- Diagnostic (règle 4) : chaque nouvelle fonction ajoute une CompatibilityRow dans CompatibilityViewModel.BuildRows (106-221). Marquer IsPersonal=true pour toute donnée personnelle, masquée dans le rapport copié.
- Nouvelle page : NavEntry dans MainViewModel.NavItems (99-110), vue dans MainWindow.xaml avec ConverterParameter = le titre exact (108-127), Dispose dans MainViewModel.Dispose (191-215). Nouveau sous-onglet de Paramètres : AppSettingsSection dans AppSettingsViewModel.Sections (32-38) et panneau dans AppSettingsView.xaml, affiché par la clé.
- Interop : les structures sont recopiées du SDK Windows avec un commentaire sur l'alignement 64 bits (ProcessService.cs:933-938, ShellFileOperations.cs:93-97). Uniquement des API documentées, avec le lien learn.microsoft.com en commentaire (ProcessService.cs:9-33). Les compteurs PDH sont ajoutés par leur nom anglais (PdhCounterSampler).
- Commentaires en français qui expliquent le POURQUOI et les alternatives écartées. Interface en français, au tutoiement (« Relance PCPerfSuite en administrateur… »).
- Tests : xUnit 2.9.3, noms de méthodes en français avec des underscores (OfficialInstallerTests). Aucun accès réseau, rien n'est lancé. Tests « ne lève jamais » sur les détections. Les classes internal de Core sont testables (InternalsVisibleTo PCPerfSuite.Core.Tests dans Core.csproj).
- Règle 6 : ce qui n'a pas été vérifié sur une vraie machine est marqué « expérimental » dans l'interface.

### F14

**Déjà dans le code**

Partiel : mesure seulement, aucune limite. Une seule action modifie un processus : « Terminer » (ProcessService.Terminate, src/PCPerfSuite.Core/Processes/ProcessService.cs:554-617), déclenchée depuis le menu contextuel, le bouton « Terminer » de la barre d'outils, Suppr, la sélection multiple ou une ligne d'agrégat (ProcessesViewModel.cs:1887-2083 ; ProcessesView.xaml:107-108 et 420-432 ; ProcessesView.xaml.cs:79-85). Les autres entrées du menu ne modifient rien : ouvrir l'emplacement, Propriétés, copier nom/chemin/PID (ProcessesViewModel.cs:633-649, 2085-2130). AUCUNE priorité, affinité, CPU sets, EcoQoS ni Job Object : la recherche de SetPriorityClass|ProcessorAffinity|JobObject|SetProcessInformation ne trouve rien. Mesures par processus déjà disponibles : %CPU, working set privé, WS, commit, E/S, disque et réseau via ETW (ProcessModels.cs:37-107 ; ProcessIoTracer.cs). Le seul bridage existant est global : le tweak « power-throttling » (WindowsPerformanceSettingsService.cs:162-178).

**À réutiliser**

- src/PCPerfSuite.Core/Processes/ProcessService.cs:554-617 — Terminate : modèle d'action (OpenProcess dédié aux droits minimaux, revérification de l'heure de démarrage, codes Win32 traduits en résultat typé)
- src/PCPerfSuite.Core/Processes/ProcessService.cs:718-727 — EnsureHandle : le handle de relevé PROCESS_QUERY_LIMITED_INFORMATION suffit aussi pour lire GetPriorityClass, GetProcessAffinityMask et IsProcessInJob
- src/PCPerfSuite.Core/Processes/ProcessModels.cs:27-30 — ProcessIdentity (PID + StartTimeUtc, IsComplete) ; 175-202 TerminateFailure/TerminateResult comme modèle de résultat
- src/PCPerfSuite.Core/Processes/ProcessTerminationGuard.cs:11-103 — garde à décliner en garde de limitation (soi-même, PID 0/4, IsCritical, noms protégés, svchost)
- src/PCPerfSuite.App/ViewModels/ProcessesViewModel.cs:1988-1995 — ExpandAggregate : un agrégat est un arbre de processus, donc une cible naturelle pour un Job Object (les enfants lancés ensuite y entrent aussi)
- src/PCPerfSuite.App/ViewModels/ProcessesViewModel.cs:1914-1982 — TerminateManyAsync : modèle de traitement par lot (garde, confirmation, compteurs de résultat, StatusText)
- src/PCPerfSuite.App/ViewModels/ProcessesViewModel.cs:116-169 — ProcessColumnsViewModel (colonnes nommées, DefaultVisible ligne 143) et 905-913 (migration d'une nouvelle colonne dans des réglages déjà enregistrés)
- src/PCPerfSuite.App/ViewModels/ProcessesViewModel.cs:423-447 et 535-544 — ProcessRowViewModel.Apply / ToInfo (à étendre pour transporter priorité et limites jusqu'au garde)
- src/PCPerfSuite.App/Views/ProcessesView.xaml:412-434 — menu contextuel dont la cible est figée à l'ouverture (ProcessesView.xaml.cs:36-47) ; 449-519 panneau de détail pour afficher les limites actives
- src/PCPerfSuite.Core/PowerSettings/AppSettingsStore.cs:115-140 — ProcessesSettings, à étendre avec les règles persistantes
- src/PCPerfSuite.App/ViewModels/CompatibilityViewModel.cs:449-472 — ProcessIoTraceRow comme modèle de ligne de diagnostic

**Fichiers à toucher**

- src/PCPerfSuite.Core/Processes/ProcessLimitService.cs (nouveau : priorité, affinité/CPU sets, EcoQoS, Job Object nommé pour les plafonds CPU/mémoire/réseau sortant, et disque en expérimental)
- src/PCPerfSuite.Core/Processes/ProcessLimitGuard.cs (nouveau)
- src/PCPerfSuite.Core/Processes/ProcessLimitModels.cs (nouveau : règle, résultat, capacités)
- src/PCPerfSuite.Core/Processes/ProcessModels.cs (PriorityClass, IsInJob et masque d'affinité dans ProcessInfo)
- src/PCPerfSuite.Core/Processes/ProcessService.cs (lecture de ces champs dans GetSnapshotCore)
- src/PCPerfSuite.Core/PowerSettings/AppSettingsStore.cs (liste de règles par chemin d'exécutable + éditeur)
- src/PCPerfSuite.App/ViewModels/ProcessesViewModel.cs (à découper d'abord : ProcessRowViewModel, ProcessColumnsViewModel)
- src/PCPerfSuite.App/ViewModels/ProcessLimitsViewModel.cs (nouveau)
- src/PCPerfSuite.App/Views/ProcessesView.xaml
- src/PCPerfSuite.App/ViewModels/MainViewModel.cs (surveillance en arrière-plan pour réappliquer les règles)
- src/PCPerfSuite.App/ViewModels/CompatibilityViewModel.cs
- tests/PCPerfSuite.Core.Tests/ProcessLimitGuardTests.cs (nouveau)

**Manques**

Presque tout. Service d'écriture : SetPriorityClass (sans Temps réel), SetProcessAffinityMask ou SetProcessDefaultCpuSets, SetProcessInformation(ProcessPowerThrottling) pour EcoQoS. Job Object : plafond CPU dur JOBOBJECT_CPU_RATE_CONTROL_INFORMATION, plafond mémoire JOB_OBJECT_LIMIT_PROCESS_MEMORY/JOB_MEMORY (ou SetProcessWorkingSetSizeEx HARDWS pour un plafond plus doux), débit réseau sortant JOBOBJECT_NET_RATE_CONTROL_INFORMATION, débit disque SetIoRateControlInformationJobObject (à valider). Lecture de l'état courant dans le relevé. Règles persistantes par exécutable, réappliquées aux nouveaux lancements par une surveillance indépendante de l'onglet. UI : sous-menu « Priorité », « Affinité… », « Limiter… », colonne « Limite/Priorité », limites visibles dans le panneau de détail, liste des règles. Ligne de diagnostic (version de Windows requise, admin).

**Pièges**

ProcessesViewModel.cs fait déjà 2218 lignes et 5 classes (ProcessColumnViewModel 41-113, ProcessColumnsViewModel 116-169, ProcessRowViewModel 175-650, ProcessRowCollection 658-672, ProcessesViewModel 681-2218) : sortir les classes avant d'y ajouter quoi que ce soit.
Le relevé s'arrête quand l'onglet est caché (IsActive 863-886, MainViewModel.cs:172) : une règle persistante a besoin de sa propre surveillance, légère et compatible avec le mode éco (IBackgroundSensorConsumer).
Job Object : un processus ne quitte jamais un job, et le job survit à PCPerfSuite tant que ses processus vivent. Les limites resteraient donc actives après la fermeture de l'app. Ne jamais poser JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE, remettre les limites à zéro au Dispose et à la désactivation, nommer le job pour le retrouver après un plantage. Les navigateurs et les services sont souvent déjà dans un job (jobs imbriqués depuis Windows 8, parfois refusés).
Un plafond mémoire dur fait échouer les allocations, donc planter l'application : l'avertir clairement.
Le réseau ne se limite qu'en sortie ; l'entrant exige un pilote WFP.
Le plafond disque est à marquer « expérimental » (règle 6).
La priorité Temps réel peut figer la machine : l'interdire.
Pour les processus protégés, prévoir AccessDenied avec un message.
Identifier une règle par le chemin complet (et l'éditeur), jamais par le nom (ProcessService.cs:874-879).
Ne pas donner de droits d'écriture au handle de relevé (commentaire ProcessService.cs:565-566).
L'action sur un agrégat doit viser tous les membres (ExpandAggregate), exactement comme Terminate.

### F7

**Déjà dans le code**

Aucun gestionnaire de pilotes, et l'exclusion est explicite : commentaire InstallationsViewModel.cs:345-346 (« Pas de pilote graphique ici… ») et texte de pied de l'onglet Installations (AppSettingsView.xaml:153). L'infrastructure de téléchargement sécurisé existe mais ne sert qu'à PawnIO, le seul pilote que l'app installe : PawnIoDriver.SetupSource (Core/Hardware/Cpu/PawnIoDriver.cs:40-41, URL github.com/namazso/PawnIO.Setup/releases/latest/download/PawnIO_setup.exe, arguments « -install -silent », éditeur « namazso »), recherche de mise à jour (45-66), code 183 « déjà installé » (49). Détection du GPU : MachineInfo.VideoControllers, noms seulement (Environment/MachineInfo.cs:43-46, 121-141). GpuControlViewModel.UnavailableMessage conseille « Installe le dernier pilote du constructeur » (GpuControlViewModel.cs:110-111). Seul IGCL expose une version de pilote (IgclNative.cs:92, DriverVersion). Aucun inventaire des pilotes installés : pas de Win32_PnPSignedDriver.

**À réutiliser**

- src/PCPerfSuite.Core/Installations/OfficialInstaller.cs:62-117 — DownloadAndRunAsync : pipeline complet, jamais d'exception
- src/PCPerfSuite.Core/Installations/OfficialInstaller.cs:121-165 — DownloadAsync : redirections suivies une par une (AllowAutoRedirect=false, 355), HTTPS et hôte autorisé revérifiés à chaque étape (RequireAllowed 335-351, jokers « *.domaine »), MaxRedirects=5
- src/PCPerfSuite.Core/Installations/OfficialInstaller.cs:167-199 — SaveAsync : taille bornée et progression en % (IProgress<string>)
- src/PCPerfSuite.Core/Installations/OfficialInstaller.cs:375-391 — RestrictToElevatedProcesses (icacls /setintegritylevel High, contre le DLL planting)
- src/PCPerfSuite.Core/Installations/OfficialInstaller.cs:202-219 — Verify : en-tête MZ, puis AuthenticodeVerifier.Check sur le handle verrouillé, puis comparaison exacte de l'éditeur
- src/PCPerfSuite.Core/Installations/OfficialInstaller.cs:223-275 — LaunchAsync : runas si l'app n'est pas élevée, annulation UAC (1223), codes 0/3010, délai d'installation ; DescribeExitCode 279-294
- src/PCPerfSuite.Core/Installations/OfficialInstaller.cs:302-332 — TryGetLatestReleaseVersionAsync / ReleaseVersionFromLocation : dernière version GitHub sans quota d'API
- src/PCPerfSuite.Core/Installations/AuthenticodeVerifier.cs:36-59 et 103-116 — WinVerifyTrust (sans révocation) ; l'éditeur lu est l'O= du certificat, à défaut le CN=
- src/PCPerfSuite.App/ViewModels/InstallationsViewModel.cs:31-102 — ExternalSoftwareViewModel (état, bouton principal/secondaire, Message, IsRequired) ; 105-258 PawnIoItemViewModel comme exemple de flux « vérifier → installer → relire »
- src/PCPerfSuite.Core/Overlay/RtssInstallation.cs:38-110 — détection d'installation par les clés Uninstall (HKLM/HKCU, WOW6432Node)
- src/PCPerfSuite.Core/Environment/MachineInfo.cs:121-141 — ReadVideoControllers, à enrichir (PNPDeviceID VEN_10DE/1002/8086, DriverVersion, DriverDate)
- tests/PCPerfSuite.Core.Tests/OfficialInstallerTests.cs:58-67 — test type de conformité d'une source (HTTPS, hôte autorisé, éditeur, nom de fichier)

**Fichiers à toucher**

- src/PCPerfSuite.Core/Drivers/DriverInventory.cs (nouveau : Win32_PnPSignedDriver joint à Win32_PnPEntity)
- src/PCPerfSuite.Core/Drivers/WindowsUpdateDriverSearch.cs (nouveau : API COM Windows Update Agent, recherche Type='Driver')
- src/PCPerfSuite.Core/Drivers/DriverSources.cs (nouveau : sources constructeur quand il existe un lien direct vérifiable)
- src/PCPerfSuite.Core/Installations/OfficialInstaller.cs (taille maximale et délais par source, MSI/CAB en plus de MZ, en-têtes optionnels)
- src/PCPerfSuite.Core/Installations/OfficialInstaller.cs:12-17 (OfficialInstallerSource : nouveaux champs optionnels)
- src/PCPerfSuite.App/ViewModels/DriversViewModel.cs (nouveau)
- src/PCPerfSuite.App/Views/DriversView.xaml (nouveau)
- src/PCPerfSuite.App/ViewModels/MainViewModel.cs et src/PCPerfSuite.App/MainWindow.xaml (nouvelle page)
- src/PCPerfSuite.App/ViewModels/InstallationsViewModel.cs:345-346 et src/PCPerfSuite.App/Views/AppSettingsView.xaml:153 (textes qui excluent les pilotes)
- src/PCPerfSuite.App/ViewModels/CompatibilityViewModel.cs
- tests/PCPerfSuite.Core.Tests/OfficialInstallerTests.cs

**Manques**

Inventaire (périphérique, classe, fournisseur, version, date, signé, INF). Dernière version disponible, par Windows Update (seule source documentée qui couvre toutes les marques) ou par constructeur. Téléchargement de gros paquets. Installation silencieuse avec les arguments propres à chaque constructeur. Point de restauration système avant l'installation. Historique et retour arrière (désinstaller ou restaurer un pilote). États « à jour », « mise à jour disponible », « source inconnue » (règle 3). Ligne de diagnostic.

**Pièges**

OfficialInstaller borne tout : MaxBytes = 100 Mo (OfficialInstaller.cs:42), DownloadTimeout 5 min (52), InstallTimeout 10 min (53). Un pilote GPU pèse plusieurs centaines de Mo : rendre ces limites paramétrables PAR SOURCE sans les relever globalement, sous peine d'affaiblir PawnIO.
Verify refuse tout fichier qui ne commence pas par « MZ » (206-209) : .msi, .cab, .zip et .inf sont rejetés.
Le HttpClient est unique, avec pour seul en-tête le User-Agent « PCPerfSuite/1.0 » (353-359). Certains CDN de constructeurs exigent d'autres en-têtes : à vérifier sur les vraies URL.
Les API « dernière version » de NVIDIA et d'AMD ne sont pas documentées publiquement, donc fragiles. Repli obligatoire : ExternalLink vers la page officielle avec l'explication.
L'éditeur est comparé exactement (214) : relever les vraies chaînes O= sur de vrais installeurs.
Sur un portable, le pilote GPU OEM est souvent requis (Optimus, MUX) : détecter avec MachineInfo.IsLaptop et avertir.
Lancée en admin, l'app installe sans invite UAC (LaunchAsync 232-234) : confirmation explicite obligatoire.
Ne jamais désinstaller un pilote en cours d'utilisation (choix déjà fait pour PawnIO, InstallationsViewModel.cs:231-233).
Un pilote « à mettre à jour » ne doit pas faire clignoter Paramètres : IsRequired et HasMissing, InstallationsViewModel.cs:54-58 et 408-415.
Win32_PnPSignedDriver est lent : Task.Run et cache.

### F10

**Déjà dans le code**

Partiel. Paramètres › Installations liste deux logiciels (InstallationsViewModel.cs:348-419, AppSettingsView.xaml:85-156). PawnIO se télécharge directement depuis GitHub puis se lance après vérification. RTSS n'a PAS de lien direct : le bouton ouvre la page Guru3D, et l'app explique pourquoi (RtssInstallation.cs:12-16 et 24 ; InstallationsViewModel.cs:290-297, « pages à jeton, nom de fichier différent à chaque version »). ExternalLink.TryOpen n'ouvre que des adresses HTTPS (ExternalLink.cs:14-33). Il n'existe ni catalogue d'outils ni mode « télécharger sans lancer » : le dossier temporaire est toujours effacé dans le finally (OfficialInstaller.cs:113-116).

**À réutiliser**

- src/PCPerfSuite.Core/Installations/OfficialInstaller.cs:12-17 — OfficialInstallerSource (Url, AllowedHosts, FileName, Arguments, ExpectedPublisher)
- src/PCPerfSuite.Core/Installations/OfficialInstaller.cs:38-40 — GitHubHosts
- src/PCPerfSuite.Core/Installations/OfficialInstaller.cs:121-199 — DownloadAsync/SaveAsync (internal : réutilisables pour un mode téléchargement seul)
- src/PCPerfSuite.Core/Installations/OfficialInstaller.cs:302-332 — résolution de la dernière version GitHub, pour les assets dont le nom contient la version
- src/PCPerfSuite.Core/Installations/ExternalLink.cs:14-33 — repli vers la page officielle
- src/PCPerfSuite.App/ViewModels/InstallationsViewModel.cs:31-102 — ExternalSoftwareViewModel (IsRequired=false pour un outil facultatif)
- src/PCPerfSuite.App/Views/AppSettingsView.xaml:89-156 — gabarit de ligne (badge d'état, boutons, Purpose, StatusDetail, Message)
- src/PCPerfSuite.App/ViewModels/AppSettingsViewModel.cs:32-38 — Sections (ajouter « Outils »)
- src/PCPerfSuite.Core/Overlay/RtssInstallation.cs:38-110 — détection d'installation par le registre
- tests/PCPerfSuite.Core.Tests/OfficialInstallerTests.cs:14-56 — tests sans réseau des garde-fous

**Fichiers à toucher**

- src/PCPerfSuite.Core/Installations/ToolCatalog.cs (nouveau : liste statique d'outils avec nom, rôle, source, résolveur de version, détection, mode télécharger/installer)
- src/PCPerfSuite.Core/Installations/OfficialInstaller.cs (DownloadVerifiedAsync vers un dossier choisi, sans lancement ; prise en charge .msi/.zip)
- src/PCPerfSuite.App/ViewModels/ToolsViewModel.cs (nouveau, ou à côté d'InstallationsViewModel)
- src/PCPerfSuite.App/ViewModels/AppSettingsViewModel.cs
- src/PCPerfSuite.App/Views/AppSettingsView.xaml
- tests/PCPerfSuite.Core.Tests/OfficialInstallerTests.cs (Theory sur tout le catalogue : HTTPS, hôte autorisé, éditeur non vide, nom sans chemin)

**Manques**

Le catalogue lui-même (outils de bench, de diagnostic et de stress, par exemple). Mode « télécharger seulement » qui garde le fichier dans Téléchargements, ou « télécharger puis installer ». Résolution de la dernière version quand le nom de fichier est versionné. Détection « déjà installé ». État par outil : lien direct / page officielle seulement, avec la raison.

**Pièges**

Beaucoup d'éditeurs n'ont pas de lien direct stable (le cas RTSS est documenté). Ne pas extraire les liens de pages à jeton : afficher la raison et renvoyer vers la page officielle (règle 3).
releases/latest/download/<nom> ne fonctionne que si le nom de l'asset ne contient pas la version.
Chaque entrée doit fixer ses hôtes autorisés et l'éditeur Authenticode attendu, relevés sur le vrai fichier. Un outil non signé (fréquent chez les gratuits) échoue à Verify : décider s'il est seulement téléchargé, avec un avertissement, mais JAMAIS exécuté.
Téléchargements ou %USERPROFILE% désignent un autre profil quand SessionUser.IsOtherProfile (SessionUser.cs:47-59).
Ouvrir un outil téléchargé depuis l'app élevée lui transmet le jeton admin : passer par explorer.exe /select (ProcessesViewModel.cs:2089-2094).
MaxBytes de 100 Mo.
IsRequired=false, sinon le bouton Paramètres clignote en permanence.

### F11

**Déjà dans le code**

Partiel, en lecture et réparti sur trois onglets.
(1) Monitoring › Disques : DiskItemViewModel (MonitoringViewModel.cs:17-135 ; MonitoringView.xaml:340-419) affiche nom, % occupé, activité, vie restante, volumes « Nom (C:) » et le bouton « Tester l'état ». Ce bouton appelle DiskHealthService (Core/Hardware/DiskHealthService.cs:30-117), qui lit MSFT_PhysicalDisk.HealthStatus, puis dans MSFT_StorageReliabilityCounter la température, l'usure, les heures sous tension et les erreurs de lecture et d'écriture.
(2) Capteurs LHM via HardwareMonitorService.ReadDisk (HardwareMonitorService.cs:897-945) : occupation, activité, débits, température, vie restante (capteur « Life »). Appoints WMI : DiskVolumeReader (Hardware/Storage/DiskVolumeReader.cs, lettres et noms par disque physique, cache de 30 s) et DiskModelReader (Win32_DiskDrive.Model).
(3) Stockage : treemap de l'occupation (DiskSpaceScanner, FolderTree, Controls/TreemapControl.cs) et tuiles de lecteurs DriveOption (StorageViewModel.cs:16-88 : type, taille, occupé). Menu : ouvrir, rescanner, propriétés, Corbeille sous DeletionGuard (Storage/DeletionGuard.cs).
Nettoyage vide les caches et la corbeille. Diagnostic : DiskRow et DiskNamesRow (CompatibilityViewModel.cs:316-399).
Aucun SMART brut, et rien sur les partitions, systèmes de fichiers, BitLocker, TRIM, défragmentation ou chkdsk (aucun MSFT_Partition, MSFT_Volume ni Optimize).

**À réutiliser**

- src/PCPerfSuite.Core/Hardware/DiskHealthService.cs:142-211 — FindBestMatch / TryParsePhysicalDriveIndex (appariement LHM ↔ WMI par numéro de disque) ; 131 EscapeWmiString
- src/PCPerfSuite.Core/Hardware/DiskHealthService.cs:88-117 — lecture des compteurs de fiabilité, dégradée en douceur
- src/PCPerfSuite.Core/Hardware/Storage/DiskVolumeReader.cs:81-136 — jointure Win32_LogicalDisk / Win32_LogicalDiskToPartition
- src/PCPerfSuite.Core/Hardware/HardwareModels.cs:199-236 — DiskVolume / DiskSnapshot
- src/PCPerfSuite.App/ViewModels/StorageViewModel.cs:37-87 — DriveOption.TryFromDrive (volume qui disparaît, étiquette illisible)
- src/PCPerfSuite.App/ViewModels/MonitoringViewModel.cs:85-135 — TestAsync / BuildSummary (formulation de l'état de santé)
- src/PCPerfSuite.Core/Storage/ShellFileOperations.cs:75-91 — fenêtre Propriétés native
- src/PCPerfSuite.Core/Storage/DeletionGuard.cs — modèle de garde à décliner pour les volumes (système, EFI, Recovery)
- src/PCPerfSuite.App/Converters/DiskHealthStatusToBrushConverter.cs

**Fichiers à toucher**

- src/PCPerfSuite.Core/Hardware/Storage/DiskLayoutReader.cs (nouveau : MSFT_Disk, MSFT_Partition, MSFT_Volume ; GPT/MBR, BusType, MediaType SSD/HDD, partitions, système de fichiers, espace libre, BitLocker via Win32_EncryptableVolume)
- src/PCPerfSuite.Core/Hardware/Storage/DiskMaintenanceService.cs (nouveau : Optimize, retrim ou défragmentation ; analyse en lecture seule)
- src/PCPerfSuite.App/ViewModels/DiskManagerViewModel.cs (nouveau)
- src/PCPerfSuite.App/Views/StorageView.xaml (onglet « Disques ») ou nouvelle vue avec MainViewModel.cs et MainWindow.xaml
- src/PCPerfSuite.App/ViewModels/CompatibilityViewModel.cs

**Manques**

Arborescence disque → partitions → volumes. Type réel (NVMe, SATA SSD, HDD, USB) et bus. Style de partition et système de fichiers. Espace par volume. Chiffrement. État du TRIM. Optimisation (retrim sur SSD, défragmentation sur HDD) et analyse d'erreurs. Attributs SMART bruts pour le rapport technicien. Opérations de partition (lettre, formatage, suppression, redimensionnement) : aucune.

**Pièges**

L'app tourne en admin : formater ou supprimer une partition est irréversible. Au minimum un garde (disque système ou de démarrage, partitions EFI et Recovery, volumes BitLocker) et une double confirmation. Recommandation : exclure ces opérations en v1 et renvoyer vers diskmgmt.msc.
MSFT_StorageReliabilityCounter est refusé sans admin et derrière certains ponts USB (DiskHealthService.cs:24-29, 88-90) : afficher N/D avec la raison.
L'appariement par numéro ne marche pas sous Storage Spaces (commentaire 133-141).
DiskVolumeReader est internal et son premier appel est synchrone (54-57) : ne jamais l'appeler depuis le thread UI.
MonitoringViewModel.cs fait 1024 lignes et contient déjà DiskItemViewModel et BatteryInfoViewModel : ne pas y loger le gestionnaire.
Une défragmentation de HDD est longue : elle doit être annulable et hors thread UI.
Le namespace root\Microsoft\Windows\Storage peut manquer sur un Windows endommagé : message adapté.

### F12

**Déjà dans le code**

Rien. Le seul code SetupAPI est BatteryReader.cs : SetupDiGetClassDevs, SetupDiEnumDeviceInterfaces, SetupDiGetDeviceInterfaceDetail et DeviceIoControl, restreints à la classe batterie (lignes 153-238, P/Invoke 327-357). Côté WMI, MachineInfo lit Win32_VideoController.Name et Win32_Battery.PNPDeviceID (MachineInfo.cs:121-141, 184-214). Pas de Win32_PnPEntity, de Win32_PnPSignedDriver ni de cfgmgr32.

**À réutiliser**

- src/PCPerfSuite.Core/Hardware/BatteryReader.cs:153-238 et 327-357 — P/Invoke SetupAPI déjà écrits et best-effort
- src/PCPerfSuite.Core/Environment/MachineInfo.cs:60-86 et 121-141 — requêtes WMI best-effort (using ManagementObject, catch → liste vide)
- src/PCPerfSuite.Core/Hardware/DiskHealthService.cs:131 — EscapeWmiString
- src/PCPerfSuite.App/ViewModels/CompatibilityViewModel.cs:28 — CompatibilityRow (ligne « périphériques en erreur »)
- src/PCPerfSuite.App/ViewModels/StorageViewModel.cs:112-136 — chargement asynchrone hors du constructeur

**Fichiers à toucher**

- src/PCPerfSuite.Core/Devices/DeviceInventory.cs (nouveau : Win32_PnPEntity avec Name, PNPClass, Manufacturer, Status, ConfigManagerErrorCode, Present, DeviceID ; joint à Win32_PnPSignedDriver pour DriverVersion, DriverDate, DriverProviderName, IsSigned, InfName)
- src/PCPerfSuite.Core/Devices/DeviceProblemCodes.cs (nouveau : codes CM_PROB_* expliqués en français)
- src/PCPerfSuite.App/ViewModels/DevicesViewModel.cs (nouveau)
- src/PCPerfSuite.App/Views/DevicesView.xaml (nouveau)
- src/PCPerfSuite.App/ViewModels/MainViewModel.cs et src/PCPerfSuite.App/MainWindow.xaml
- src/PCPerfSuite.App/ViewModels/CompatibilityViewModel.cs

**Manques**

Liste par classe. Périphériques en erreur ou sans pilote (code 28…), cachés ou déconnectés. Version et date du pilote (commun avec F7). Recherche. Ouverture de devmgmt.msc. Actions éventuelles : activer/désactiver, rechercher les modifications, désinstaller.

**Pièges**

Désactiver un périphérique est une écriture risquée : contrôleur de stockage système, USB du clavier et de la souris, ou GPU d'un portable à MUX, avec un écran noir possible (lien avec F9). V1 en lecture seule, puis actions limitées à une liste blanche de classes, avec confirmation.
Win32_PnPSignedDriver prend plusieurs secondes : Task.Run et cache, jamais dans le constructeur (MainViewModel construit tout avant la fenêtre).
Identifier le fabricant par VEN_/DEV_ et non par le nom (règle 1).
Chaque code d'erreur doit dire sa cause (règle 3).

### F13

**Déjà dans le code**

Lecture seulement. SystemMemoryReader (Core/Hardware/Memory/SystemMemoryReader.cs:15-73, GlobalMemoryStatusEx) : mémoire totale visible, disponible, utilisée, charge, et virtuelle (commit) totale et utilisée. MemoryModuleReader (Win32_PhysicalMemory et PhysicalMemoryArray) : barrettes, emplacements, type, vitesse. Le tout alimente MemorySnapshot (HardwareModels.cs:111-167), les métriques ram.* (MetricCatalog.cs:251) et le rapport (CompatibilityViewModel.cs:652-680). Par processus : working set privé, WS et commit (ProcessService.cs:729-761), colonne Mémoire. Rien sur la liste d'attente, les pages modifiées, la mémoire compressée, le cache ou le fichier d'échange, et aucune limite.

**À réutiliser**

- src/PCPerfSuite.Core/Hardware/PdhCounterSampler.cs:12-60 — compteurs PDH par nom anglais (\Memory\Standby Cache Normal Priority Bytes, Modified Page List Bytes, Free & Zero Page List Bytes, Cache Bytes, Pool Paged/Nonpaged Bytes)
- src/PCPerfSuite.Core/Hardware/Memory/SystemMemoryReader.cs:20-49 — modèle de lecture best-effort
- src/PCPerfSuite.Core/Processes/ProcessService.cs:729-761 — TryReadMemory (bascule EX2 → EX)
- src/PCPerfSuite.Core/PowerSettings/PerformanceTweak.cs:10-36 — réglage avec RequiresRestart, pour le fichier d'échange
- src/PCPerfSuite.App/ViewModels/ProcessesViewModel.cs:313-329 — intensité mémoire rapportée à la RAM totale

**Fichiers à toucher**

- src/PCPerfSuite.Core/Hardware/Memory/MemoryCompositionReader.cs (nouveau : GetPerformanceInfo + PDH)
- src/PCPerfSuite.Core/PowerSettings/PageFileService.cs (nouveau, si le sens retenu est le fichier d'échange : Win32_PageFileSetting, Win32_ComputerSystem.AutomaticManagedPagefile)
- src/PCPerfSuite.App/ViewModels/MemoryViewModel.cs + vue (ou carte dans Monitoring)
- src/PCPerfSuite.Core/Processes/ProcessLimitService.cs (si le sens retenu est le plafond par processus, voir F14)

**Manques**

Le sens est à trancher avec Denis :
(a) répartition de la RAM façon Moniteur de ressources (utilisée, modifiée, en attente, libre, compressée) ;
(b) réglage du fichier d'échange ;
(c) plafond de RAM par processus, qui recoupe F14 ;
(d) mémoire partagée de l'iGPU, fixée par le BIOS et rarement modifiable depuis Windows.
Aucun des quatre n'existe.

**Pièges**

Purger la liste d'attente demande NtSetSystemInformation, non documentée : c'est contraire au principe « API documentées uniquement » (ProcessService.cs:14-19), et le bénéfice est discutable.
Modifier le fichier d'échange exige admin et redémarrage ; un fichier trop petit entraîne des plantages et empêche les dumps.
ullTotalPhys n'est pas la capacité installée (SystemMemoryReader.cs:27-29) : afficher aussi la part réservée au matériel.
PdhCounterSampler ne lit qu'une instance à la fois, ce qui suffit pour \Memory.

### F15

**Déjà dans le code**

Une seule bascule, « Effets visuels : privilégier les performances » (WindowsPerformanceSettingsService.cs:142-160, catégorie « Interface »). Activée, elle écrit HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects\VisualFXSetting = 2 ; désactivée, elle écrit 0 (« Laisser Windows choisir »), et non la valeur d'origine. Aucun appel à SystemParametersInfo, et rien sur UserPreferencesMask, MinAnimate, TaskbarAnimations ou EnableTransparency (recherche négative). L'app elle-même respecte SystemParameters.ClientAreaAnimation (Controls/RevealPanel.cs:77).

**À réutiliser**

- src/PCPerfSuite.Core/PowerSettings/PerformanceTweak.cs:10-36 — modèle de réglage (GetState/Apply, RequiresElevation=false pour HKCU, RequiresRestart)
- src/PCPerfSuite.App/ViewModels/OptimizationViewModel.cs:8-91 — TweakItemViewModel (application immédiate à la bascule, retour arrière en cas d'erreur, ErrorMessage)
- src/PCPerfSuite.App/Views/OptimizationView.xaml:7-12 — regroupement automatique par Category (Category = « Animations » crée sa carte)
- src/PCPerfSuite.Core/PowerSettings/WindowsPerformanceSettingsService.cs:45-57 — RememberOriginalValue et AppSettings.OriginalPowerValues (AppSettingsStore.cs:63-66) pour restaurer exactement
- src/PCPerfSuite.Core/PowerSettings/RegistryHelper.cs:17-63 — lecture/écriture DWORD (EnableTransparency, TaskbarAnimations)
- src/PCPerfSuite.Core/Environment/SessionUser.cs:47-59 — IsOtherProfile / OtherProfileMessage

**Fichiers à toucher**

- src/PCPerfSuite.Core/PowerSettings/WindowsAnimationTweaks.cs (nouveau : P/Invoke SystemParametersInfo SPI_GET/SETCLIENTAREAANIMATION, SPI_SETANIMATION (ANIMATIONINFO), SPI_SETMENUANIMATION, SPI_SETCOMBOBOXANIMATION, SPI_SETLISTBOXSMOOTHSCROLLING, SPI_SETTOOLTIPANIMATION, SPI_SETSELECTIONFADE, SPI_SETDROPSHADOW, SPI_SETCURSORSHADOW, SPI_SETDRAGFULLWINDOWS, avec SPIF_UPDATEINIFILE|SPIF_SENDCHANGE)
- src/PCPerfSuite.Core/PowerSettings/WindowsPerformanceSettingsService.cs (concaténer ces réglages, corriger la restauration de visual-effects-performance)
- src/PCPerfSuite.Core/PowerSettings/AppSettingsStore.cs (valeurs d'origine des réglages HKCU)
- src/PCPerfSuite.App/ViewModels/OptimizationViewModel.cs (préréglages « Tout couper » / « Rétablir l'origine »)
- src/PCPerfSuite.App/Views/OptimizationView.xaml
- src/PCPerfSuite.App/ViewModels/CompatibilityViewModel.cs

**Manques**

Réglage individuel de chaque animation et effet : ouverture/réduction des fenêtres, menus, info-bulles, défilement fluide, ombres, contenu pendant le glissement, animations de la barre des tâches, transparence, Aero Peek. Lecture de l'état réel via SPI_GET*. Application immédiate (WM_SETTINGCHANGE). Restauration à l'identique. Préréglages.

**Pièges**

VisualFXSetting seul ne coupe probablement rien avant une nouvelle ouverture de session : à vérifier sur une vraie machine. Le réglage n'est pas marqué RequiresRestart, et « désactiver » n'y restaure pas l'état d'origine.
HKCU et SystemParametersInfo agissent sur le profil du compte qui exécute l'app : avec SessionUser.IsOtherProfile (app élevée sous un autre administrateur), on règle le mauvais profil. Le défaut touche DÉJÀ game-mode et visual-effects-performance (RequiresElevation=false).
RegistryHelper n'écrit que des DWORD (48-63) et UserPreferencesMask est binaire : passer par SPI plutôt que par le registre.
TaskbarAnimations demande de redémarrer l'Explorateur.
GetTweaks est une longue liste littérale (298 lignes dans le fichier) : placer les animations dans un fichier dédié.
TweakItemViewModel applique à chaque bascule (OnIsOnChanged 54-58) : un préréglage doit éviter N écritures et N diffusions successives.
Les animations internes de l'app (RevealPanel) se couperont aussi.

### F5

**Déjà dans le code**

Partiel : le réglage existe, le visuel non. Tweak « core-parking » (WindowsPerformanceSettingsService.cs:241-255) : CPMINCORES (PowerSubGroups.ProcessorMinCoreParkingState 0cc5b647-c1df-4637-891a-dec35c318583, PowerPlanService.cs:180-181) passe à 100 % sur le plan actif, secteur et batterie, avec mémorisation de l'origine (ApplyPowerValue 23-43). Pas de vue par cœur : CpuSnapshot n'expose que LoadPercent et MaxCoreLoadPercent (HardwareModels.cs:61-71 ; HardwareMonitorService.cs:466).

**À réutiliser**

- src/PCPerfSuite.Core/PowerSettings/PowerPlanService.cs:95-119 — Set/GetValueIndices(Async) par GUID de sous-réglage
- src/PCPerfSuite.Core/PowerSettings/WindowsPerformanceSettingsService.cs:23-57 — ApplyPowerValue / RememberOriginalValue
- src/PCPerfSuite.Core/Hardware/PdhCounterSampler.cs — base pour \Processor Information(*)\Parking Status et % Processor Utility par cœur

**Fichiers à toucher**

- src/PCPerfSuite.Core/PowerSettings/PowerPlanService.cs (GUID CPMAXCORES, seuils)
- src/PCPerfSuite.Core/Hardware/PdhCounterSampler.cs (variante multi-instances)
- src/PCPerfSuite.App/ViewModels/CpuControlViewModel.cs ou vue dédiée

**Manques**

CPMAXCORES et les seuils de parking, l'état de parking par cœur, la charge par cœur, et le visuel (grille de cœurs P/E).

**Pièges**

Le réglage ne vaut que pour le plan actif : il est perdu au changement de plan (dit dans la description, ligne 246). PdhCounterSampler n'appelle que PdhGetFormattedCounterValue (une instance) : il faut PdhGetFormattedCounterArrayW pour « (*) ». Sur les CPU hybrides, P-cores et E-cores se comportent différemment. Le plan « Performances ultimes » crée un nouveau plan qui repart des valeurs par défaut.

### F3

**Déjà dans le code**

Base de rapport réutilisable : CompatibilityViewModel.CopyReport (CompatibilityViewModel.cs:621-692) produit un rapport texte (lignes [OK]/[--], métriques N/D, ventilateurs, mémoire, capteurs bruts) qui masque les données personnelles (IsPersonal, ligne 28 et 635-636). DiskHealthService fournit santé, usure, heures et erreurs, directement utilisables comme verdict « normal / pas normal » pour les disques.

**À réutiliser**

- src/PCPerfSuite.App/ViewModels/CompatibilityViewModel.cs:621-692 — CopyReport
- src/PCPerfSuite.Core/Hardware/DiskHealthService.cs:13-22 — DiskHealthReport
- src/PCPerfSuite.App/ViewModels/MonitoringViewModel.cs:116-134 — BuildSummary (formulation de la santé disque)

**Fichiers à toucher**

- src/PCPerfSuite.App/ViewModels/CompatibilityViewModel.cs (ou nouveau service de rapport dans Core)

**Manques**

Du côté de cette zone : périphériques en erreur (F12), pilotes obsolètes (F7), SMART et partitions (F11) à ajouter au rapport technicien.

**Pièges**

Le constructeur de CompatibilityViewModel prend déjà 7 dépendances (57-59) : ajouter les nouveaux gestionnaires par une interface de fournisseur de lignes plutôt que par un paramètre de plus.

### F9

**Déjà dans le code**

Rien dans cette zone. Seul lien : désactiver le GPU dédié dans le gestionnaire de périphériques (F12).

**À réutiliser**

- src/PCPerfSuite.Core/Environment/MachineInfo.cs:32-46 — IsLaptop, VideoControllers

**Fichiers à toucher**



**Manques**

Voir la zone GPU et portables.

**Pièges**

Désactiver le dGPU d'un portable à MUX ou dont l'écran interne y est câblé donne un écran noir. Ne jamais le proposer par le chemin « désactiver le périphérique » sans détection fiable.

### F18

**Déjà dans le code**

ProcessService sait détecter les applications au premier plan (ProcessKind.Application, fenêtre visible, ProcessService.cs:880-893), mais seulement pendant que l'onglet Processus est affiché.

**À réutiliser**

- src/PCPerfSuite.Core/Processes/ProcessService.cs:647-714 — EnumerateProcesses / CollectWindowTitles

**Fichiers à toucher**

- src/PCPerfSuite.App/ViewModels/MainViewModel.cs (surveillance commune avec F14)

**Manques**

Une surveillance légère et permanente des processus, que F14 (règles persistantes) et F18 (détection de jeu) pourraient partager.

**Pièges**

Un relevé complet de ProcessService coûte plusieurs dizaines de millisecondes (ReadDurationHint) et ouvre un handle par processus : trop cher pour tourner en permanence ou en mode éco. Il faut une énumération Toolhelp seule ou une trace ETW Process.

### Remarques

- Aucun test unitaire ne couvre ProcessTerminationGuard, DeletionGuard, les PerformanceTweak ou le stockage (tests/ contient 22 fichiers, aucun sur ces classes). Tout nouveau garde (limites de processus, opérations disque, désactivation de périphérique) doit arriver avec ses tests.
- CLAUDE.md désigne src/PCPerfSuite.Core/Environment/MachineInfo.cs, mais le namespace réel est PCPerfSuite.Core.SystemInfo, comme pour les 4 fichiers de ce dossier. Le commentaire ProcessService.cs:161-162 parle d'un namespace PCPerfSuite.Core.Environment qui n'existe pas. Ce commentaire est obsolète mais sans conséquence.
- Petite dette : double <summary> sur CompatibilityRow (CompatibilityViewModel.cs:23-24) ; `_ = pid;` et paramètre inutilisé dans ProcessService.ResolveImmutableData (765-790).
- Défaut latent déjà présent : les réglages HKCU (game-mode, visual-effects-performance) ignorent SessionUser.IsOtherProfile. Le Nettoyage, lui, le signale (CleanupViewModel.cs:132-134). À corriger en même temps que F15.
- Hors zone mais observé : pour F17, OverlayWindow.Reposition se cale sur SystemParameters.PrimaryScreenWidth/Height (OverlayWindow.xaml.cs:55-56), donc sur l'écran principal uniquement. Aucune énumération des écrans dans le code (EnumDisplayMonitors et EnumDisplaySettings absents), ce qui concerne aussi F16.
- Découpage suggéré en conversations : (1) F14, précédé du découpage de ProcessesViewModel.cs, et de la surveillance de processus partagée avec F18 ; (2) F7 + F10 ensemble, car ils étendent tous deux OfficialInstaller (taille et délais par source, MSI, téléchargement seul, catalogue) et touchent le même test OfficialInstallerTests ; (3) F11 + F12 + F13 en lecture seule d'abord (inventaire WMI : MSFT_Disk/Partition/Volume, Win32_PnPEntity/PnPSignedDriver, PDH mémoire), avec lignes de diagnostic ; les écritures (formatage, désactivation) à part, plus tard ou jamais ; (4) F15 dans l'onglet Optimisation, avec la correction IsOtherProfile. F13 demande d'abord de trancher le sens avec Denis.
- Sécurité : F7 et F10 exécutent des fichiers téléchargés avec les droits administrateur. La chaîne actuelle (HTTPS, hôtes autorisés à chaque redirection, intégrité High, verrou en lecture seule, Authenticode, éditeur exact) est solide ; ne jamais l'assouplir globalement, mais la paramétrer par source. Tout outil ou pilote non signé : téléchargement seul, jamais exécution.
- Le bouton Paramètres clignote tant qu'un logiciel IsRequired manque (MainViewModel.cs:74, InstallationsViewModel.cs:408-415). Les catalogues d'outils et de pilotes doivent rester en IsRequired=false pour ne pas le rendre permanent.
- Une page de gestionnaire qui interroge WMI ne doit rien lire dans son constructeur : MainViewModel construit tous les ViewModels avant d'afficher la fenêtre (StorageViewModel.cs:112-118 montre le bon modèle).


## Coquille de l'app (navigation, pages, cycle de vie, mode éco), overlay fenêtre (position/écran/DPI), modules par marque de portable (LaptopFans, MachineInfo), conventions, dépendances, tests, workflow git

### Architecture

Composition manuelle, sans conteneur d'injection : MainWindow (MainWindow.xaml.cs:14) crée MainViewModel, qui crée dans son constructeur (MainViewModel.cs:80-117) les services partagés (HardwareMonitorService _hardware l.16, GpuControlService _gpuControl l.20, CpuControlService _cpuControl l.24, InstallationsViewModel l.34) et tous les ViewModels de page, qui les reçoivent par constructeur.
Le pivot des données est MonitoringViewModel : une SamplingLoop sur un thread dédié (l.683) lit HardwareMonitorService.GetSnapshot, puis passe au thread d'interface et diffuse SnapshotUpdated et MetricsUpdated (l.565-568) à chaque abonné, un par un (InvokeEachSafely l.982-990). Les pages GPU, Ventilateurs, Processeur, Overlay et Compatibilité ne relisent pas le matériel : elles s'abonnent.
Navigation : NavItems (MainViewModel.cs:99-110), 9 NavEntry(Title, glyphe Segoe Fluent, ViewModel), plus un bouton Paramètres hors liste (AppSettingsNav l.92, MainWindow.xaml:67-70). Toutes les vues sont créées une seule fois au démarrage dans une même Grid (MainWindow.xaml:107-128). Chaque vue devient visible par comparaison de CHAÎNES : CurrentPage.Title + StringEqualsToVisibility + ConverterParameter=titre exact. Une vue masquée est seulement repliée (Collapsed), ses liaisons continuent de se mettre à jour.
Cycle de vie : MainWindow.UpdateWindowShown (l.149) pose IsWindowShown. MainViewModel.UpdateAttention (l.163-173) arrête le relevé coûteux de Processus (_processes.IsActive = page choisie ET fenêtre visible, ProcessesViewModel.cs:868-886) et pose AppSettings.IsPageShown. UpdateEcoMode (l.152-154) appelle MonitoringViewModel.SetBackgroundMode (l.809-829) avec la liste fixe {_overlay, _fans, _cpu}, qui implémentent IBackgroundSensorConsumer (IBackgroundSensorConsumer.cs:9-13). En mode éco, seuls les groupes de capteurs demandés par ces trois-là sont relus (HardwareMonitorService.SetBackgroundGroups l.403), et ApplyBackgroundTick (l.849-865) ne touche plus l'interface. Un abonné qui ne fait qu'afficher doit sortir tout seul si _monitoring.IsBackgroundMode (exemple : OverlayViewModel.cs:261).
Fermeture : App.xaml:5 en ShutdownMode=OnExplicitShutdown ; la croix range l'app dans la zone de notification. La seule sortie est MainWindow.OnClosed (l.78-93), qui appelle MainViewModel.Dispose (l.191-215) : chaque étape passe par DisposeSafely, dans un ordre imposé (relevé, puis ventilateurs, GPU, CPU, overlay, puis services).
Réglages : un seul fichier %LOCALAPPDATA%\PCPerfSuite\settings.json via AppSettingsStore (AppSettingsStore.cs:341-429) : verrou, écriture atomique, Update() pour lire-modifier-écrire (l.409-418), LastError affiché dans le diagnostic. Une sous-classe par zone dans AppSettings (l.10-67).
Garde-fous : manifeste requireAdministrator + PerMonitorV2 (app.manifest). Méthodes Core « best-effort » (null/false, jamais d'exception). MachineInfo.SoftwareFanControlRefused (MachineInfo.cs:41) : seul un PC de bureau avéré autorise l'écriture. Instance unique par mutex (App.xaml.cs:15-57). CrashLog pour les exceptions de l'interface, des threads de fond et des tâches (App.xaml.cs:59-76). Diagnostic « Compatibilité de ce PC » : CompatibilityViewModel.BuildRows (l.106-219), qui sert aussi de rapport de bug (CopyReport l.621-692).
Overlay fenêtre : OverlayViewModel crée lui-même OverlayWindow (SyncWindow l.367-384). Reposition (OverlayWindow.xaml.cs:51-75) ne connaît que l'écran PRINCIPAL (SystemParameters.PrimaryScreenWidth/Height l.55-56, en DIP, écran entier et non zone de travail). Aucun suivi d'un changement d'écran ou de DPI.
Portables : un module par marque derrière ILaptopFanProvider, choisi par sous-chaîne de MachineInfo.Current.Manufacturer (LaptopFanService.CreateProvider l.136-146), en lecture seule via WMI root\WMI (WmiMethods).
Dépendances : Core = LibreHardwareMonitorLib 0.9.6, Microsoft.Diagnostics.Tracing.TraceEvent 3.2.6, NvAPIWrapper.Net 0.8.1.101, System.Management 10.0.2, AllowUnsafeBlocks (ADLX/IGCL) ; App = CommunityToolkit.Mvvm 8.2.2 ; les deux en net8.0-windows (pas de TFM WinRT, donc aucune API Windows.* utilisable aujourd'hui). Tests xUnit 2.9.3 (Core.Tests avec InternalsVisibleTo, App.Tests avec UseWPF).

### Conventions

- Français partout : code (identifiants anglais, commentaires et doc XML en français), textes d'interface, messages de commit. Commentaires qui expliquent le POURQUOI (souvent avec l'incident vécu), jamais le quoi.
- Commits : sujet à la 3e personne du présent, avec préfixe de zone facultatif (« Overlay : … », « GPU/CPU : … », « Corrige … », « Ajoute … », « Rend … »), sans accents dans les commits récents, corps qui explique la cause, trailer Co-Authored-By. Branches feature/<kebab-fr> et fix/<kebab-fr>, fusionnées dans develop (merge local « Merge branch 'x' into develop » ou PR GitHub #17, #21 à #24), puis master mis à jour depuis develop (master = develop~1 aujourd'hui). Les 10 derniers commits ont été faits directement sur develop (cherry-pick) : écart à la règle « une branche par fonctionnalité ». Relecture indépendante avant fusion (commit 9c6f636 « Corrige les defauts releves par la relecture independante »).
- Ajouter une page de premier niveau : (1) champ/propriété du ViewModel dans MainViewModel (l.26-52), construit dans le constructeur avec les services partagés injectés ; (2) NavEntry dans NavItems (l.99-110) avec Glyph(0xE…) ; (3) une ligne <views:XxxView DataContext="{Binding Xxx}" Visibility="{Binding DataContext.CurrentPage.Title, … ConverterParameter='Titre exact'}"/> dans MainWindow.xaml:107-128 (titre dupliqué : le renommer casse la page sans erreur) ; (4) si la page relève quelque chose de coûteux, une propriété IsActive posée dans UpdateAttention (l.163-173) comme _processes, et le chargement fait à la première activation plutôt que dans le constructeur (OptimizationViewModel lance LoadCommand dans son constructeur, l.110, à ne pas imiter pour une page lourde) ; (5) si elle doit tourner fenêtre cachée, implémenter IBackgroundSensorConsumer et l'ajouter au tableau d'UpdateEcoMode (l.153-154) ; sinon sortir tout de suite dans son abonné si _monitoring.IsBackgroundMode ; (6) Dispose via DisposeSafely dans MainViewModel.Dispose, à la bonne place dans l'ordre ; (7) une ligne dans CompatibilityViewModel.BuildRows ; (8) une section du README.
- Sous-onglet des Paramètres : une AppSettingsSection(key, titre) dans AppSettingsViewModel.Sections (l.31-37), puis un panneau StackPanel dont la Visibility compare SelectedSection.Key dans AppSettingsView.xaml (modèle l.89-90 et 160-161). Sélecteur en pastilles : Style PillSelector/PillItem (Theme.xaml:1358/1319), réutilisable pour des sous-onglets dans une page.
- Squelette d'une vue : ScrollViewer Padding={StaticResource PagePadding} > StackPanel MaxWidth={StaticResource PageMaxWidth} > controls:PageHeader Title/Subtitle, puis des Border Style=Card. Fonction indisponible : Border CalloutWarn + TextBox SelectableBody lié à un message adapté au PC (GpuControlView.xaml:10-15). Bandeau « expérimental » : CalloutWarn lié à IsExperimentalBackend/ExperimentalNotice (GpuControlView.xaml:21-25). Badges : Badge/BadgeWarn/BadgeAccent/BadgeSuccess.
- Texte sélectionnable en lecture seule : TextBox Style SelectableCaption/SelectableBody. Une propriété sans setter se lie en Mode=OneWay, sinon WPF lève une exception (commit db14dc7, AppSettingsView.xaml:85-88).
- Valeurs absentes : MetricReading.Missing (« -- », MetricCatalog.cs:33) et MetricReading.Unavailable (« N/D », l.37) avec MetricDefinition.UnavailableHint (l.93). Fonction entière absente : propriété message dédiée (GpuControlViewModel.UnavailableMessage l.88-119, FanCurvesViewModel.NoFansMessage l.857), qui distingue « sans administrateur », « pilote muet » et « marque ou carte non prise en charge ».
- Matériel : détecter à l'exécution. Méthodes Core best-effort (try/catch vers null/false) ; toute liste de marques prises en charge a une source unique (LaptopFanService.SupportedVendors l.132, relue par MetricCatalog.cs:142 et CompatibilityViewModel.cs:492) ; propriété IsVerified pour marquer l'expérimental (ILaptopFanProvider.cs:34-36) ; jamais d'écriture dans le contrôleur embarqué d'un portable ; MachineInfo.SoftwareFanControlRefused pour toute écriture de ventilateur.
- MVVM : CommunityToolkit.Mvvm, [ObservableProperty] sur des champs en camelCase et [RelayCommand] ; partial void OnXxxChanged pour enregistrer ou appliquer. Pour poser une valeur sans déclencher l'écriture : drapeau _suppressApply ou _restoring (OptimizationViewModel.cs:47-52, AppSettingsViewModel.cs:64-65), ou affecter le champ plutôt que la propriété dans le constructeur (AppSettingsViewModel.cs:93-97).
- Travail matériel ou système hors du thread d'interface : await Task.Run(...) dans le ViewModel (AppSettingsViewModel.cs:113, OptimizationViewModel.cs:31, ProcessesViewModel.cs:959). Abonnés à un événement statique (SystemEvents.PowerModeChanged, CpuControlViewModel.cs:349/759) : toujours se désabonner dans Dispose.
- Persistance : une sous-classe de réglages par zone dans AppSettingsStore.cs. Les enums partent en NOMBRES (pas de JsonStringEnumConverter, avertissement l.108-114) : ne jamais insérer une valeur au milieu d'un enum persisté (OverlayAnchor par exemple), ou stocker une chaîne comme ProcessesSettings. Pour lire-modifier-écrire, utiliser AppSettingsStore.Update() (l.409-418). Un réglage jamais touché reste null pour suivre le défaut du catalogue (OverlayViewModel.StoredOrder l.420-421).
- Interop Win32 : classes internal static ou internal sealed dans src/PCPerfSuite.App/Interop/ (P/Invoke user32 local à chaque classe, try/catch muet avec un commentaire qui dit la conséquence). Côté Core : P/Invoke directement dans la classe de service (BatteryReader.cs:153-338 pour SetupAPI, MachineInfo.cs:250-252).
- Espaces de noms : les fichiers de src/PCPerfSuite.Core/Environment/ utilisent l'espace de noms PCPerfSuite.Core.SystemInfo (MachineInfo.cs:4), pas …Environment, pour éviter le conflit avec System.Environment.
- Tests : xUnit ; noms mêlant français et anglais (Une_courbe_est_plate_avant_son_premier_point, Overlay_Disabled_NeedsNothing). La logique testable est sortie en classes publiques statiques et pures (BackgroundSensorNeeds, OverlayComposer, OverlayLineOrder, FanCurveMath). App.Tests n'a pas d'InternalsVisibleTo, donc les classes Interop internal ne sont pas testables. Core.Tests voit l'internal (Core.csproj InternalsVisibleTo).
- Styles : Theme.xaml fait déjà 1513 lignes ; pour un nouveau domaine, créer un ResourceDictionary (comme Styles/Menus.xaml et Styles/Overlay.xaml) et l'ajouter à App.xaml:16-20. Les convertisseurs se déclarent dans App.xaml:22-37. Couleurs pour les contrôles dessinés en code : Styles/ThemeColors.cs.

### F17

**Déjà dans le code**

Canal fenêtre complet, mais uniquement sur l'écran principal. OverlayWindow (Views/OverlayWindow.xaml:1-32) : fenêtre sans bordure, AllowsTransparency, Topmost, ShowActivated=False, SizeToContent=WidthAndHeight, WindowStartupLocation=Manual. ClickThroughWindow.Apply (Interop/ClickThroughWindow.cs:29-46) pose WS_EX_TRANSPARENT, WS_EX_TOOLWINDOW et WS_EX_NOACTIVATE. TopmostKeeper (Interop/TopmostKeeper.cs:94-134) garde la fenêtre devant grâce à un hook EVENT_SYSTEM_FOREGROUND et un second passage à 250 ms. Placement : OverlayWindow.Reposition (OverlayWindow.xaml.cs:51-75) calcule Left/Top en DIP à partir de SystemParameters.PrimaryScreenWidth/Height (l.55-56), de l'ancrage 3x3 (OverlayAnchor, Core/Overlay/OverlayAnchor.cs:5-16) et des marges MarginX/MarginY (0 à 600, OverlayAppearanceViewModel.cs:143-144 et 179-180). Il vise l'écran entier et non la zone de travail, et suppose que l'écran principal commence en (0,0). Reposition est appelé seulement sur SizeChanged (l.26), Loaded (l.38) et OverlayViewModel.LayoutChanged (l.131, déclenché par OnAppearanceLayoutChanged l.248-253, c'est-à-dire ancrage, marges, police, taille, espacements, fond). Rien n'écoute un changement d'écran (branchement, résolution, écran principal) ni de DPI, alors que l'app est PerMonitorV2 (app.manifest). Réglages persistés : OverlayAppearanceSettings.Anchor/MarginX/MarginY (AppSettingsStore.cs:318-323). Interface : bloc « Canal fenêtre » (OverlayView.xaml:358-426), libellé « Position sur l'écran principal » (l.374). Le canal RTSS se positionne dans RTSS lui-même : non concerné.

**À réutiliser**

- src/PCPerfSuite.App/Views/OverlayWindow.xaml.cs:51 — Reposition : à faire travailler sur les bornes de l'écran choisi au lieu de PrimaryScreenWidth/Height
- src/PCPerfSuite.App/ViewModels/OverlayAppearanceViewModel.cs:300-309 — OnAnchorChanged → Changed() : même chemin (enregistrement + LayoutChanged) pour un SelectedMonitor
- src/PCPerfSuite.App/ViewModels/OverlayAppearanceViewModel.cs:233-257 — WriteTo : y ajouter l'identifiant d'écran
- src/PCPerfSuite.Core/PowerSettings/AppSettingsStore.cs:279-327 — OverlayAppearanceSettings : nouveau champ string? MonitorId (null = écran principal, compatible avec les anciens fichiers)
- src/PCPerfSuite.App/Interop/TopmostKeeper.cs:82-84 — P/Invoke SetWindowPos déjà écrit : placer la fenêtre en pixels physiques
- src/PCPerfSuite.App/Interop/TrayIcon.cs:291-295 — DeviceToDip (CompositionTarget.TransformFromDevice) : conversion pixels vers DIP
- src/PCPerfSuite.App/ViewModels/CpuControlViewModel.cs:349/759 — modèle d'abonnement/désabonnement à Microsoft.Win32.SystemEvents (ici pour DisplaySettingsChanged)
- src/PCPerfSuite.App/MainWindow.xaml.cs:99-128 — RestoreWindowBounds : même souci de retomber sur un écran encore branché
- Theme.xaml:1261 GlassComboBox — liste déroulante des écrans

**Fichiers à toucher**

- src/PCPerfSuite.App/Views/OverlayWindow.xaml.cs
- src/PCPerfSuite.App/ViewModels/OverlayAppearanceViewModel.cs
- src/PCPerfSuite.App/Views/OverlayView.xaml (l.358-426, libellé l.374)
- src/PCPerfSuite.Core/PowerSettings/AppSettingsStore.cs (OverlayAppearanceSettings)
- nouveau src/PCPerfSuite.App/Interop/DisplayMonitors.cs (EnumDisplayMonitors + GetMonitorInfoW/MONITORINFOEX + GetDpiForMonitor + QueryDisplayConfig/DisplayConfigGetDeviceInfo pour le nom convivial et un chemin stable)
- nouveau src/PCPerfSuite.Core/Overlay/OverlayPlacement.cs (calcul pur ancrage + marges + bornes de l'écran + taille, testable)
- tests/PCPerfSuite.Core.Tests/OverlayPlacementTests.cs
- src/PCPerfSuite.App/ViewModels/CompatibilityViewModel.cs (ligne « Écrans / écran de l'overlay »)
- README.md (section Overlay, l.83-100)

**Manques**

Lister les écrans (nom convivial EDID, résolution, échelle, principal ou non). Identifiant stable à enregistrer : \\.\DISPLAYn change d'un démarrage à l'autre, le monitorDevicePath de DisplayConfig est plus fiable. Repli sur l'écran principal, avec message, quand l'écran enregistré est débranché. Placement multi-DPI correct. Replacement sur DisplaySettingsChanged et DpiChanged. Liste relue à l'ouverture de l'onglet. Option « zone de travail ou écran entier » éventuelle. Ligne de diagnostic. Tests du calcul de position.

**Pièges**

PerMonitorV2 + WPF : affecter Left/Top en DIP pour envoyer une fenêtre sur un écran d'échelle différente donne une position fausse (la conversion DIP vers pixels se fait avec le DPI de l'écran où la fenêtre SE TROUVE encore). Il faut placer en pixels physiques via SetWindowPos (SWP_NOSIZE|SWP_NOZORDER|SWP_NOACTIVATE) après SourceInitialized, puis recalculer à WM_DPICHANGED. ActualWidth est en DIP : le multiplier par l'échelle de l'écran cible pour ancrer à droite ou en bas. Coordonnées négatives d'un écran à gauche ou au-dessus du principal : Reposition suppose aujourd'hui une origine en (0,0). SystemEvents.DisplaySettingsChanged est un événement statique : se désabonner dans OverlayWindow.OnClosed (l.41-47), sinon la fenêtre fuit, car OverlayViewModel la recrée à chaque activation (SyncWindow l.382). Couplage ViewModel vers vue : OverlayViewModel fait « new OverlayWindow » (l.382, using Views l.9). OverlayViewModel.Persist (l.395-416) fait Load() puis Save() au lieu d'Update() : risque de réglage perdu en cas d'écriture concurrente. OverlayAnchor est persisté en nombre : ne pas insérer de valeur au milieu. Fichiers déjà gros : OverlayView.xaml 448 lignes, OverlayViewModel.cs 431, OverlayAppearanceViewModel.cs 310 ; mettre le code écran dans un fichier Interop dédié plutôt que dans ces trois-là.

### F9

**Déjà dans le code**

Rien pour basculer entre GPU dédié et intégré. Briques voisines : MachineInfo.VideoControllers (MachineInfo.cs:44, Win32_VideoController lu UNE fois, via un Lazy l.56-58, par ReadVideoControllers l.121-141) ; ChassisKind et IsLaptop (l.11-16, 32). GpuControlService.TryInitialize (GpuControlService.cs:53-87) essaie NVAPI, puis ADLX, puis IGCL, garde la première carte pilotable (la carte dédiée sur un PC hybride), et ne s'exécute qu'une fois (_initialized, l.55-56). HardwareMonitorService.PreferredGpuVendor/PreferredGpuName (l.72/78) fixent le GPU affiché ; HardwareSnapshot n'expose qu'UN seul Gpu (HardwareModels.cs:306). La logique « dédié ou intégré » par nom est dans GpuControlViewModel.DedicatedGpu (l.123-137). MetricCatalog.cs:137 sait qu'un GPU dédié de portable en veille ne renvoie rien. OverlayViewModel reconstruit ses lignes quand le GPU dédié se réveille (l.67-69). Le %GPU par processus a été volontairement écarté (ProcessService.cs:26-27). Les modules WMI par marque existent pour les ventilateurs (lecture seule), avec AsusFanProvider qui ouvre déjà AsusAtkWmi_WMNB (AsusFanProvider.cs:40).

**À réutiliser**

- src/PCPerfSuite.Core/Hardware/LaptopFans/ILaptopFanProvider.cs:30-43 — modèle d'interface par marque (Vendor, IsVerified, TryDetect appelé une fois, Read qui peut lever)
- src/PCPerfSuite.Core/Hardware/LaptopFans/LaptopFanService.cs:48-60, 81-112, 132, 136-146 — enum Support (NotLaptop, UnsupportedVendor, InterfaceMissing, Active), choix par Manufacturer, SupportedVendors comme source unique, DetectionError
- src/PCPerfSuite.Core/Hardware/LaptopFans/WmiMethods.cs:8-61 — FirstInstance, CreateInstance, Invoke avec conversion de type CIM (à déplacer dans un espace de noms neutre si partagé)
- src/PCPerfSuite.Core/Hardware/LaptopFans/AsusFanProvider.cs:67-71 — appel DSTS (lecture d'un Device_ID ATK), même mécanisme pour lire le mode GPU
- src/PCPerfSuite.Core/Environment/MachineInfo.cs:25-46 — Manufacturer, Model, Chassis, VideoControllers
- src/PCPerfSuite.Core/Hardware/BatteryReader.cs:153-338 — P/Invoke SetupAPI déjà écrit (énumération de périphériques), base pour l'état ou la désactivation du GPU dédié (partagé avec F12)
- src/PCPerfSuite.Core/PowerSettings/RegistryHelper.cs:17/48 — lecture/écriture de registre (DWORD seulement ; UserGpuPreferences demande des chaînes REG_SZ)
- src/PCPerfSuite.Core/Environment/SessionUser.cs:49-54 — IsOtherProfile : HKCU de l'app élevée peut ne pas être celui de l'utilisateur
- src/PCPerfSuite.Core/Hardware/HardwareMonitorService.cs:403 — SetBackgroundGroups : suspendre le groupe Gpu
- src/PCPerfSuite.App/ViewModels/CompatibilityViewModel.cs:474-498 — FanReadingRow comme modèle de ligne de diagnostic par marque

**Fichiers à toucher**

- nouveau dossier src/PCPerfSuite.Core/Hardware/LaptopGpu/ (IGpuModeProvider, GpuModeService, un fichier par marque : Asus, Lenovo, Msi, Hp… choisis d'après MachineInfo.Current)
- nouveau src/PCPerfSuite.Core/Hardware/Gpu/GpuPreferenceService.cs (préférence GPU par application de Windows, toutes marques)
- src/PCPerfSuite.Core/Hardware/GpuControlService.cs (permettre de redétecter la carte après une bascule)
- src/PCPerfSuite.Core/Hardware/HardwareMonitorService.cs (ne plus réveiller le GPU dédié en mode économie ; redétecter le matériel)
- src/PCPerfSuite.Core/Environment/MachineInfo.cs (VideoControllers figé par le Lazy)
- src/PCPerfSuite.App/ViewModels/MainViewModel.cs + MainWindow.xaml (nouvelle page, ou une carte dans GPU)
- nouveau src/PCPerfSuite.App/ViewModels/GpuSwitchViewModel.cs + Views/GpuSwitchView.xaml
- src/PCPerfSuite.App/ViewModels/CompatibilityViewModel.cs
- src/PCPerfSuite.Core/PowerSettings/AppSettingsStore.cs
- README.md
- CLAUDE.md (si Denis décide d'une exception à la règle 5)

**Manques**

Tout : détecter qu'un PC est hybride (iGPU + dGPU, et MUX éventuel) ; lire le mode courant (Hybride / Éco, dGPU coupé / dGPU seul par MUX) ; savoir si le dGPU dort ; dire quelles applications le gardent éveillé ; basculer ; gérer le redémarrage demandé par un changement de MUX ; message adapté quand ce n'est pas possible (pas un portable, un seul GPU, marque non prise en charge, interface absente, sans administrateur) ; ligne de diagnostic ; statut expérimental par marque.

**Pièges**

CONFLIT AVEC LA RÈGLE 5 DE CLAUDE.md, À TRANCHER PAR DENIS. « Éteindre le gros GPU » sur un portable passe, chez les constructeurs, par une écriture dans le firmware ou l'EC : ASUS ATK DEVS sur les identifiants dGPU/MUX d'asus-wmi (0x00090020, 0x00090016, repris du pilote Linux et de G-Helper, à vérifier), Lenovo GameZone WMI, HP hpqBIntM, MSI Center. Or la règle dit « Contrôleur embarqué des portables : lecture seule… (ventilateurs, alimentation) ». Voies sans écriture firmware : (a) préférence GPU par application de Windows (HKCU\Software\Microsoft\DirectX\UserGpuPreferences, valeur « GpuPreference=1; » pour l'économie d'énergie), qui laisse le pilote éteindre le dGPU quand plus rien ne l'utilise ; (b) désactiver le périphérique par SetupAPI (DICS_DISABLE), dangereux en mode MUX « dGPU seul » (écran noir) : ne le proposer que si l'affichage passe par l'iGPU. PCPerfSuite peut garder le dGPU éveillé à lui seul : le groupe Gpu relu à chaque tick (LibreHardwareMonitor, NVAPI) empêche le dGPU d'un Optimus de s'endormir ; il faut suspendre sa lecture en mode économie, sinon la bascule ne sert à rien (le groupe SensorGroup.Gpu couvre tous les GPU à la fois). Un GPU qui disparaît ou réapparaît à chaud n'est pas géré : GpuControlService ne redétecte jamais (l.55-56), le Computer de LibreHardwareMonitor est ouvert une seule fois (l.120-125) et MachineInfo.VideoControllers est figé (Lazy). Risque de handles NVAPI périmés, à tester. MachineInfo a un constructeur privé et un Current statique : le choix du module n'est pas testable en l'état (LaptopFanService non plus) ; prévoir un constructeur internal ou une fabrique. HKCU d'une app élevée sous un autre compte (SessionUser.IsOtherProfile). Changer un MUX demande un redémarrage : le dire et ne jamais le déclencher soi-même. Fichiers trop gros à ne pas grossir : HardwareMonitorService.cs (1280 lignes), GpuControlViewModel.cs (705).

### F8

**Déjà dans le code**

Rien : aucune occurrence de RGB, OpenRGB, Aura, iCUE, Mystic Light, Polychrome ou LampArray dans src/ (vérifié par recherche). LibreHardwareMonitorLib 0.9.6 ne gère pas l'éclairage. Les ventilateurs sont pilotés en vitesse seulement (FanCurvesViewModel, HardwareMonitorService.TrySetFanPercent l.1079). ColorPicker existe (Controls/ColorPicker.xaml(.cs), 249 lignes) avec une palette et des pastilles (OverlayPalette, OverlayColorSlotViewModel dans OverlayAppearanceViewModel.cs:34-103).

**À réutiliser**

- src/PCPerfSuite.App/Controls/ColorPicker.xaml.cs:17-19 — sélecteur de couleur (ColorHex)
- src/PCPerfSuite.App/Overlay/OverlayPalette.cs + ViewModels/OverlayAppearanceViewModel.cs:12-103 — pastilles de couleur et case réinitialisable (modèle ViewModel)
- src/PCPerfSuite.Core/Hardware/LaptopFans/ILaptopFanProvider.cs:30-43 et LaptopFanService.cs:81-146 — modèle de module détecté à l'exécution, IsVerified, Support/DetectionError
- src/PCPerfSuite.Core/Installations/OfficialInstaller.cs:12-17 et 36-40 — si OpenRGB doit être installé depuis sa source officielle (vérification de signature)
- src/PCPerfSuite.App/ViewModels/InstallationsViewModel.cs:31-52 — ExternalSoftwareViewModel pour déclarer OpenRGB comme logiciel externe
- src/PCPerfSuite.App/Controls/AdaptiveGrid.cs:11 — grille de cartes d'appareils

**Fichiers à toucher**

- nouveau dossier src/PCPerfSuite.Core/Hardware/Rgb/ (IRgbProvider, RgbService, un fichier par source)
- nouveau src/PCPerfSuite.App/ViewModels/RgbViewModel.cs + Views/RgbView.xaml
- src/PCPerfSuite.App/ViewModels/MainViewModel.cs + MainWindow.xaml (nouvelle page « Éclairage »)
- src/PCPerfSuite.Core/PowerSettings/AppSettingsStore.cs (RgbSettings)
- src/PCPerfSuite.App/ViewModels/CompatibilityViewModel.cs
- src/PCPerfSuite.Core/PCPerfSuite.Core.csproj et src/PCPerfSuite.App/PCPerfSuite.App.csproj (TFM ou paquet si LampArray / OpenRGB.NET)
- README.md

**Manques**

Tout. Recommandation, sans supposer de fabricant : (1) Windows Dynamic Lighting / HID LampArray (Windows.Devices.Lights.LampArray, standard multi-marques) ; (2) client du SDK OpenRGB (protocole TCP local, port 6742, OpenRGB lancé en mode serveur) pour la carte mère, la RAM, les hubs et les ventilateurs ARGB ; (3) SDK constructeurs (iCUE, Aura, Mystic Light, Chroma) plus tard, modules séparés. Il faut aussi : liste des appareils détectés avec leur source, couleur ou effet par zone, raison d'absence par appareil, et restitution de l'état d'origine à la fermeture.

**Pièges**

LampArray est une API WinRT : il faut passer App/Core (et les tests) en net8.0-windows10.0.xxxxx. Le contrôle en arrière-plan de Dynamic Lighting semble demander le réglage « contrôle d'éclairage en arrière-plan » de Windows, voire une identité de package : à vérifier avant de s'engager. OpenRGB est sous GPL-2.0 : ne pas intégrer son code, seulement parler à son serveur (licence du paquet client OpenRGB.NET à vérifier). NE JAMAIS écrire soi-même sur le SMBus pour la RAM ou la carte mère comme le fait OpenRGB : des écritures à une mauvaise adresse ont déjà corrompu des SPD de barrettes, et LibreHardwareMonitor utilise le même bus (mutex SMBus, voir le commentaire HardwareMonitorService.cs:87-92 sur Afterburner et Armoury Crate). Rétroéclairage clavier des portables : écriture WMI/EC, donc règle 5 en jeu, à trancher par Denis. Ne pas démarrer un sondage RGB en mode éco sans l'inscrire comme consommateur. Theme.xaml (1513 lignes) : styles propres dans un nouveau ResourceDictionary.

### F10

**Déjà dans le code**

Paramètres › Installations (AppSettingsViewModel.Sections l.34, AppSettingsView.xaml:89-157). InstallationsViewModel (ViewModels/InstallationsViewModel.cs:348-355) liste PawnIO et RTSS (ExternalSoftwareViewModel l.31-52 : Name, Purpose, MissingReason, PrimaryCommand, SecondaryCommand). PawnIO est téléchargé directement (PawnIoDriver.SetupUrl = https://github.com/namazso/PawnIO.Setup/releases/latest/download/PawnIO_setup.exe, PawnIoDriver.cs:35) par OfficialInstaller : HTTPS, liste d'hôtes autorisés, taille bornée, Authenticode et éditeur attendu (OfficialInstaller.cs:12-17, 25-36). RTSS n'a pas de lien direct stable : ouverture de la page officielle (README.md:295-297). ExternalLink.TryOpen n'accepte que HTTPS (ExternalLink.cs:14-33). Le bouton Paramètres clignote quand un logiciel manque (MainViewModel.cs:74, Attention.IsBlinking).

**À réutiliser**

- src/PCPerfSuite.Core/Installations/OfficialInstaller.cs:12-17 — OfficialInstallerSource(Url, AllowedHosts, FileName, Arguments, ExpectedPublisher)
- src/PCPerfSuite.Core/Installations/OfficialInstaller.cs:40 — GitHubHosts
- src/PCPerfSuite.Core/Installations/ExternalLink.cs:14 — TryOpen (HTTPS seulement, ne lève jamais)
- src/PCPerfSuite.App/ViewModels/InstallationsViewModel.cs:31 — ExternalSoftwareViewModel (ligne d'outil avec état et boutons)
- src/PCPerfSuite.App/Views/AppSettingsView.xaml:100-156 — modèle de ligne outil + badge d'état
- src/PCPerfSuite.App/ViewModels/AppSettingsViewModel.cs:31-37 — ajouter une section « Outils » si la liste grandit

**Fichiers à toucher**

- src/PCPerfSuite.App/ViewModels/InstallationsViewModel.cs ou nouveau ViewModels/ToolLinksViewModel.cs
- src/PCPerfSuite.App/ViewModels/AppSettingsViewModel.cs (section)
- src/PCPerfSuite.App/Views/AppSettingsView.xaml
- nouveau src/PCPerfSuite.Core/Installations/ToolCatalog.cs (source unique des outils)
- tests/PCPerfSuite.Core.Tests/OfficialInstallerTests.cs

**Manques**

Un catalogue d'outils (OCCT, HWiNFO, CPU-Z, GPU-Z, Cinebench, CrystalDiskMark, DDU, OpenRGB…) avec pour chacun un lien direct, l'hôte autorisé, l'éditeur attendu et une ligne « à quoi ça sert ».

**Pièges**

La demande « lien qui télécharge direct, pas de redirection » se heurte au constat déjà écrit dans le README (l.295-297) : beaucoup d'éditeurs (Guru3D, TechPowerUp, CPUID) n'ont pas de lien stable, ou passent par des pages à jeton ou des miroirs. Seuls les outils sur GitHub releases/latest/download/<fichier fixe> sont fiables. Tout téléchargement lancé par l'app élevée doit passer par OfficialInstaller (signature + éditeur), sinon on exécute en administrateur un binaire non vérifié. Les liens morts doivent se voir (vérification à la demande, message clair), jamais un échec silencieux.

### F18

**Déjà dans le code**

Pas de profils automatiques. Briques : mode éco et IBackgroundSensorConsumer (seuls _overlay, _fans et _cpu reçoivent des capteurs fenêtre cachée, MainViewModel.cs:153-154) ; détection du jeu au premier plan par RTSS (HardwareSnapshot.Game, HardwareModels.cs:339 ; RtssFrameStatsReader.GetForegroundProcessId l.215-221) ; hook EVENT_SYSTEM_FOREGROUND déjà écrit dans TopmostKeeper (l.107). Profils existants par zone : FanProfile (Core/Hardware/Fans/FanProfile.cs:12), CpuProfile (Core/Hardware/Cpu/CpuProfile.cs:13), GpuOverclockProfile (Core/Hardware/GpuControlModels.cs:107).

**À réutiliser**

- src/PCPerfSuite.App/ViewModels/IBackgroundSensorConsumer.cs:9-52 — déclarer les groupes de capteurs nécessaires en arrière-plan (+ BackgroundSensorNeeds testable)
- src/PCPerfSuite.App/ViewModels/MainViewModel.cs:152-154 — UpdateEcoMode : ajouter le nouveau consommateur
- src/PCPerfSuite.Core/Overlay/RtssFrameStatsReader.cs:215-221 — PID du premier plan
- src/PCPerfSuite.App/Interop/TopmostKeeper.cs:72-134 — modèle de hook du premier plan (délégué gardé en champ)
- src/PCPerfSuite.App/ViewModels/MonitoringViewModel.cs:565-568 — SnapshotUpdated/MetricsUpdated comme source d'analyse d'usage

**Fichiers à toucher**

- src/PCPerfSuite.App/ViewModels/MainViewModel.cs
- nouveau src/PCPerfSuite.Core/Profiles/ (analyse d'usage, règles de bascule)
- src/PCPerfSuite.Core/PowerSettings/AppSettingsStore.cs

**Manques**

Analyse d'usage, génération des 3 profils, bascule automatique avec hystérésis, réglage manuel des 3 profils, journal des bascules.

**Pièges**

La bascule doit tourner fenêtre cachée : sans inscription dans UpdateEcoMode, le mode éco suspend les groupes CpuLoad et Gpu et l'analyse ne voit plus rien. ProcessesViewModel ne relève QUE quand son onglet est affiché (IsActive, ProcessesViewModel.cs:868-886) : ne pas s'appuyer dessus pour savoir quelle application tourne. Les ApplyProfile de chaque zone sont private [RelayCommand] liés à l'interface (voir F19).

### F19

**Déjà dans le code**

Des profils par zone, isolés les uns des autres : ventilateurs (FanCurvesViewModel SaveProfile l.961, ApplyProfile l.996, rapport LastProfileReport relu par CompatibilityViewModel.FanProfilesRow l.502-507), CPU (CpuControlViewModel SaveProfile l.538, ApplyProfile l.573, ApplyProfileWatts l.626), GPU (GpuControlViewModel SaveProfile l.528, ApplyProfile l.556). Persistés dans AppSettings.FanProfiles (AppSettingsStore.cs:43), Cpu.Profiles (l.221) et Gpu.OverclockProfiles (l.184).

**À réutiliser**

- src/PCPerfSuite.Core/Hardware/Fans/FanProfile.cs:12
- src/PCPerfSuite.Core/Hardware/Cpu/CpuProfile.cs:13
- src/PCPerfSuite.Core/Hardware/GpuControlModels.cs:107 — GpuOverclockProfile
- src/PCPerfSuite.App/ViewModels/FanCurvesViewModel.cs:996 / CpuControlViewModel.cs:573 / GpuControlViewModel.cs:556 — logique d'application à rendre appelable (public, avec un rapport de ce qui a été appliqué ou refusé)

**Fichiers à toucher**

- src/PCPerfSuite.App/ViewModels/FanCurvesViewModel.cs, CpuControlViewModel.cs, GpuControlViewModel.cs (exposer ApplyProfileByName)
- nouveau src/PCPerfSuite.App/ViewModels/ProfileGroupsViewModel.cs + Views/ProfileGroupsView.xaml
- src/PCPerfSuite.Core/PowerSettings/AppSettingsStore.cs (ProfileGroups : références par NOM aux profils existants)
- src/PCPerfSuite.App/ViewModels/MainViewModel.cs + MainWindow.xaml

**Manques**

Modèle « groupe », application atomique avec rapport par zone, gestion d'un profil membre supprimé ou renommé.

**Pièges**

Les trois ApplyProfile sont private, prennent le ViewModel de profil de l'interface et écrivent un statut propre à leur page : il faut les factoriser sans dupliquer la logique de sécurité (sécurité thermique, plancher CPU, refus sur un portable). FanCurvesViewModel.cs (1512 lignes), CpuControlViewModel.cs (761) et GpuControlViewModel.cs (705) sont déjà trop gros : sortir la logique d'application dans des services Core plutôt que d'y ajouter du code.

### F16

**Déjà dans le code**

Rien : aucun appel à EnumDisplaySettings, ChangeDisplaySettings ou QueryDisplayConfig dans src/.

**À réutiliser**

- futur src/PCPerfSuite.App/Interop/DisplayMonitors.cs de F17 — même énumération d'écrans (QueryDisplayConfig, nom convivial, chemin stable)

**Fichiers à toucher**

- Interop/Core d'affichage partagé avec F17
- nouvelle page ou section « Écran »

**Manques**

Liste des modes par écran, application, et retour automatique au mode d'avant sans confirmation dans un délai.

**Pièges**

Couplage avec F17 : faire UN module d'énumération des écrans pour les deux fonctionnalités. Un mode non supporté peut laisser un écran noir : retour automatique obligatoire si l'utilisateur ne confirme pas.

### F12

**Déjà dans le code**

Aucun gestionnaire de périphériques. P/Invoke SetupAPI déjà présent dans BatteryReader (Core/Hardware/BatteryReader.cs:153-210 et 327-338 : SetupDiGetClassDevs, SetupDiEnumDeviceInterfaces, SetupDiGetDeviceInterfaceDetail, SetupDiDestroyDeviceInfoList).

**À réutiliser**

- src/PCPerfSuite.Core/Hardware/BatteryReader.cs:327-338 — déclarations SetupAPI

**Fichiers à toucher**

- nouveau src/PCPerfSuite.Core/Devices/ (service commun à F12, F7 et F9)

**Manques**

Tout.

**Pièges**

F9 (désactiver le dGPU) et F7 (pilotes) utiliseront les mêmes appels SetupAPI/CfgMgr32 : un seul service Devices. Désactiver un périphérique critique (contrôleur de stockage, GPU qui porte l'affichage) doit être refusé, comme ProcessTerminationGuard le fait pour les processus critiques (Core/Processes/ProcessTerminationGuard.cs).

### F6

**Déjà dans le code**

Backends ADLX (AMD) et IGCL (Intel) présents (Core/Hardware/Gpu/AdlxGpuBackend.cs 485 lignes, IgclGpuBackend.cs 529). Bandeau expérimental codé par marque : GpuControlViewModel.IsExperimentalBackend => Vendor is Amd or Intel (l.147), ExperimentalNotice (l.149-151), affiché dans GpuControlView.xaml:21-25. Garde-fou si la détection ADLX a planté l'app (AdlxProbeGuard, CompatibilityViewModel.cs:174-182).

**À réutiliser**

- src/PCPerfSuite.Core/Hardware/LaptopFans/ILaptopFanProvider.cs:34-36 — IsVerified à reprendre sur IGpuTuningBackend (Core/Hardware/Gpu/IGpuTuningBackend.cs:11) au lieu d'un test sur la marque dans le ViewModel

**Fichiers à toucher**

- src/PCPerfSuite.Core/Hardware/Gpu/IGpuTuningBackend.cs
- src/PCPerfSuite.App/ViewModels/GpuControlViewModel.cs:147-151
- README.md:324

**Manques**

Vérification sur de vraies cartes AMD et Intel, puis passage à « vérifié » backend par backend.

**Pièges**

README.md:324 est périmé : il dit « Le contrôle GPU ne fonctionne qu'avec NVIDIA pour l'instant » alors que la l.55 et l.119-123 décrivent AMD et Intel. Une AccessViolation dans ADLX ou IGCL ne peut pas être rattrapée par .NET (GpuControlViewModel.cs:144-146).

### F5

**Déjà dans le code**

Réglage Windows « Désactiver la mise en veille des cœurs CPU (core parking) » dans Optimisation Windows (Core/PowerSettings/WindowsPerformanceSettingsService.cs:244-245). Aucune donnée par cœur dans le relevé : CpuSnapshot ne donne que LoadPercent et MaxCoreLoadPercent (Core/Hardware/HardwareModels.cs:61-71). Le libellé du groupe Cpu promet pourtant une « charge par cœur » (MonitoringViewModel.cs:436).

**À réutiliser**

- src/PCPerfSuite.App/Controls/MeterBar.cs:16 — barre par cœur
- src/PCPerfSuite.App/Controls/AdaptiveGrid.cs:11 — grille de cœurs
- src/PCPerfSuite.App/Controls/Sparkline.cs:23 — historique par cœur

**Fichiers à toucher**

- src/PCPerfSuite.Core/Hardware/HardwareModels.cs (CpuSnapshot)
- src/PCPerfSuite.Core/Hardware/HardwareMonitorService.cs (ReadCpu l.425)
- page Processeur

**Manques**

Charge et état « parké » par cœur, visuel.

**Pièges**

Le visuel par cœur ne doit se mettre à jour que page affichée (même logique que ProcessesViewModel.IsActive). HardwareMonitorService.cs fait déjà 1280 lignes.

### Remarques

- Fichiers déjà trop gros pour y ajouter du code (lignes) : ProcessesViewModel.cs 2218 ; Theme.xaml 1513 ; FanCurvesViewModel.cs 1512 ; HardwareMonitorService.cs 1280 ; ProcessService.cs 1118 ; MonitoringViewModel.cs 1024 (plusieurs classes dans le fichier : SensorGroupCadenceViewModel l.390, MonitoringViewModel l.552…) ; CpuControlViewModel.cs 761 ; GpuControlViewModel.cs 705 ; CompatibilityViewModel.cs 693 (grossit à chaque fonctionnalité : chaque ligne est une méthode privée, et son constructeur prend déjà 7 dépendances, l.57-59) ; ProcessesView.xaml 524 ; MonitoringView.xaml 481 ; OverlayView.xaml 448. Petits mais touchés par chaque nouvelle page : MainViewModel.cs (222) et MainWindow.xaml (131).
- La navigation ne tiendra pas 10 pages de plus : 9 entrées aujourd'hui, environ 40 px chacune (NavListItem Padding 12,9, Theme.xaml:533-572), fenêtre MinHeight=600 (MainWindow.xaml:6). Au-delà d'une dizaine d'entrées (estimation), la ListBox défile, sans groupes ni en-têtes. Il faut décider d'une structure avant d'ajouter les pages : sections dans la barre latérale (Surveiller / Régler / Diagnostiquer / Outils), ou regrouper dans des pages existantes avec des sous-onglets PillSelector (core parking dans Processeur, animations dans Optimisation Windows, gestionnaire de disques dans Stockage, limites par processus dans le détail de Processus, écran de l'overlay dans Overlay).
- La visibilité d'une page passe par une comparaison de chaînes (CurrentPage.Title == ConverterParameter, MainWindow.xaml:108-127) : renommer un titre casse la page en silence. À terme : une clé stable dans NavEntry, ou un DataTemplate par type de ViewModel dans un conteneur qui garde les vues vivantes.
- Seules Processus (IsActive) et Paramètres (IsPageShown) savent si leur page est affichée. Une vue repliée garde ses liaisons actives : les pages à relevé en direct (futur Bench, RGB, GPU hybride) devraient partager une interface commune du genre « IPageLifecycle { bool IsPageShown { set; } } », posée pour chaque NavEntry dans UpdateAttention (MainViewModel.cs:163-173), au lieu d'ajouter un _isXxxPageSelected par page.
- ComingSoonView.xaml(.cs) est du code mort : aucune vue ne l'utilise. Seul ComingSoonViewModel sert, pour l'onglet Thèmes (AppSettingsViewModel.cs:77-85), avec un gabarit recopié dans AppSettingsView.xaml:234-258. Pour montrer des pages « bientôt disponible » dans la navigation, il faudrait brancher ComingSoonView dans MainWindow.xaml.
- Toutes les vues et tous les ViewModels sont créés au démarrage (MainViewModel.cs:80-117, MainWindow.xaml:108-127). Une page lourde (Bench, Pilotes, Périphériques, OC IA) doit charger ses données à la première ouverture, sinon le démarrage ralentit, y compris au lancement par la tâche de démarrage vers la zone de notification.
- Dette sur la persistance : plusieurs endroits font Load() puis Save() au lieu d'Update(), avec risque de perdre un réglage écrit en parallèle : OverlayViewModel.Persist (l.395-416), AppSettingsViewModel.OnMinimizeToTrayOnCloseChanged (l.192-200), le setter MonitoringViewModel.RefreshMs (l.591-600), MonitoringViewModel.OnMyMetricsSelectionChanged (l.691-693), MainWindow.ShowTrayHintOnce (l.227-239).
- Doc XML en double dans CompatibilityViewModel.cs:23-24 (deux <summary> de suite au-dessus de CompatibilityRow). README.md:3-5 présente l'app comme construite pour la config de Denis, ce qui s'accorde mal avec une diffusion publique ; README.md:324 est périmé (contrôle GPU « NVIDIA seulement »).
- Testabilité : MachineInfo (constructeur privé, Current statique via Lazy, MachineInfo.cs:48 et 56-58) rend non testables le choix d'un module par marque et toute logique « portable ou pas ». Le décodage AsusFanProvider.DecodeLevel (internal static, AsusFanProvider.cs:75) est testable (InternalsVisibleTo) mais n'a aucun test. Aucun test ne couvre les modules portables, MachineInfo ni le placement de l'overlay.
- Règle 5 de CLAUDE.md à clarifier avec Denis avant F9 et F8 (rétroéclairage clavier des portables), et sans doute F4 et F18 s'ils touchent aux modes de performance constructeur des portables : ces bascules sont des écritures WMI/ACPI dans le firmware du portable. La règle cite « ventilateurs, alimentation » ; un mode GPU relève de l'alimentation.
- Passer en TFM WinRT (net8.0-windows10.0.xxxxx), nécessaire pour LampArray (F8) et utile ailleurs (Windows.Devices.Display, par exemple), touche les deux .csproj et les deux projets de tests : à faire dans une tâche à part, avant F8.
- Workflow : master n'a qu'un commit de retard sur develop (git rev-list master..develop = 1), et les derniers développements ont été commités directement sur develop puis cherry-pickés. Pour la feuille de route : une branche feature/<kebab-fr> par fonctionnalité, relecture indépendante, fusion dans develop.
