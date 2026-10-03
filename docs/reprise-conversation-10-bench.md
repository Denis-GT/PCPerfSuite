# Reprise de la conversation #10 (Lot D · Bench CPU / RAM / disque, F1 partie 1)

Note écrite le 02/10/2026 à l'arrêt de la conversation, pour reprendre depuis un autre poste. À supprimer quand le lot
est terminé. Branche : `feature/lot-d-bench` (créée depuis lot-c = 37e1819, poussée sur origin). Protocole de la feuille
de route : un commit par étape `feat(F1): …`, build + tests verts avant l'étape suivante, push après chaque commit,
jamais de fusion, `/review-max` avant de finir, décisions ajoutées à `docs/decisions.md` à la fin.

## Décisions prises avec Denis (02/10/2026, AskUserQuestion)

- Périmètre : CPU + RAM + disque ici ; GPU et test combiné « alimentation » en #11.
- Worker : mode secondaire du même exe (`PCPerfSuite.exe --bench-worker <tube>`), dicté par D2 ; pas de second binaire.
- Scores : unités physiques **et** points (1000 = machine de référence ; références provisoires relevées sur le
  i5-13500T, à rebaser sur le 14600K, « expérimental »).
- Disque : écriture sur le disque système autorisée (fichier unique dans `%ProgramData%\PCPerfSuite\Bench`) ;
  **volume au choix de l'utilisateur**, tout type local (SSD, NVMe, HDD raccourci, USB « amovible » ; réseau exclu) ;
  **taille du fichier réglable par +/−** (1 Go par défaut, pas de 256 Mo, de 256 Mo à 8 Go) ; volume écrit plafonné à
  4 × la taille du fichier.
- Batterie : bench autorisé, résultat « non représentatif » ; arrêt de sécurité sous 30 %.
- Calibration : i5-14600K / RTX 5070 Ti / TUF B760 (Denis), un portable Ryzen, et le PC de travail (i5-13500T, hybride
  6P+8E, Windows 11 22631).

## Écarts entre le prompt et le code (constatés à l'exploration)

- `LogicalProcessor` (CpuTopology) ne portait pas l'id de CPU set : **ajouté** (`CpuSetId`, DWORD à l'offset 8,
  paramètre optionnel ; les tests existants construisent par `ParseCpuSets`, le helper `CpuSetBuffers.Entry` écrit
  `256 + index` à l'offset 8).
- Aucun P/Invoke existant de `SetThreadSelectedCpuSets`, `PowerGetEffectiveOverlayScheme`, `IOCTL_STORAGE_QUERY_PROPERTY`,
  Job Object ni tube nommé : tout est créé dans `src/PCPerfSuite.Core/Benchmark/`.
- Aucun module `Hardware/LaptopFans/` ne lit un mode constructeur (Silence/Turbo) : le résultat dira « mode constructeur
  non lu sur cette marque ».
- Liens PCIe de #4 pas livrés : champ nullable `PcieLink` à prévoir dans le résultat (« N/D : relevé à venir (#4) »).
- `BatteryReader`, `SystemMemoryReader`, `ThreadPinning`, `ProgramDataFolder.TryEnsure(root, segments)` sont `internal`
  (utilisables depuis `Core/Benchmark/`, même assemblage).
- `MainViewModel` ne publie ni `_hardware` ni `_tuningLease` : le ViewModel du bench les reçoit au constructeur (comme
  `FanCurvesViewModel`). Le bail de réglage est aussi accessible par `Tuning.Lease`.
- `App.xaml.cs` : pas de `Main`, `ShutdownMode="OnExplicitShutdown"` (un mode sans fenêtre doit appeler `Shutdown(code)`),
  `--demarrage-windows` lu par `StartupTask.LaunchArgument` à la ligne ~86, `AppDataPaths.Current` figé à la première
  lecture (CrashLog, SessionJournal) : le mode secondaire doit être lu **avant** le mutex et avant tout cela.

## Fait

### Commit 1 (poussé) : `feat(F1): protocole du tube, noyaux CPU verifies et statistiques`

`src/PCPerfSuite.Core/Benchmark/` :
- `BenchVersion.cs` : `BenchVersion.Bench = 1`, `Protocol = 1`, `Requester = "bench"` ; `BenchTestKind` + `BenchTestKinds`
  (clés `cpu-mono`, `cpu-multi`, `ram-debit`, `ram-latence`, `disque`, titres).
- `Protocol/BenchMessage.cs` (enveloppe JSON : `v`, `type`, `pid`, `bench`, `jobId`, `job`, `progress`, `result`,
  `error`, `notes`, `[JsonExtensionData]`), types `bonjour`, `demarrer`, `progression`, `resultat`, `erreur`,
  `battement`, `arreter` ; `BenchProgress`.
- `Protocol/BenchJobRequest.cs` : `BenchJobRequest` (Id, Kind, Cpu/Ram/Disk), `LogicalProcessorTarget` (Group, Index,
  CpuSetId), `CpuJobParameters` (WarmupSeconds 5, PassSeconds 2, Passes 3, SustainedSeconds 180, Threads, ThreadCount,
  Kernels, MaxFloatStretchSeconds 20, DutyLoadMs/DutyRestMs), `RamJobParameters`, `DiskJobParameters`.
- `Protocol/BenchJobResult.cs` : `BenchMeasurement` (Key, Label, Unit, HigherIsBetter, Values, Median, Cv, IsUnstable ;
  `From(...)`), `BenchJobResult` (Succeeded, Error, ChecksumMismatch, Measurements, Notes, IsComparable, DurationSeconds).
- `Protocol/BenchMessageCodec.cs` : une ligne JSON par message, lecture tolérante, autre version refusée.
- `Protocol/LivenessWatch.cs` : veille de vivacité pure (`Note`, `IsExpired`, `Silence`, `MonotonicNow`).
- `BenchStatistics.cs` (médiane, CV d'échantillon, seuil 3 % `UnstableCvThreshold`, moyenne géométrique) ;
  `BenchScore.cs` (points = 1000 × moyenne géométrique des ratios, latence inversée) + `BenchReferences` (valeurs
  provisoires par test, **à calibrer** sur le 13500T puis le 14600K).
- `Kernels/` : `SeededRandom` (xoshiro256**), `AlignedBuffer` (NativeMemory.AlignedAlloc, `Touch`, `Address`),
  `SattoloPermutation` (cycle unique), `ICpuKernel` + `KernelPass` (`RunFor`, `Calibrate`, `KernelPassOutcome`),
  `IntegerKernel` (introsort maison + FNV-1a, 65 536 uint), `FloatKernel` (128×128 Vector256+FMA, repli Vector<T> non
  comparable), `BranchKernel`, `CpuKernelCatalog` (clés `entier`, `flottant`, `branches`, graine `DefaultSeed`).

Tests (verts, 43) : `BenchMessageCodecTests`, `LivenessWatchTests`, `BenchStatisticsTests` (+ points, références, clés),
`SattoloPermutationTests`, `CpuKernelsTests`.

### Commit 2 (poussé) : `feat(F1): worker de charge, modes secondaires et bench CPU`

- `Hardware/Cpu/CpuTopology.cs` : `LogicalProcessor.CpuSetId` lu à l'offset 8.
- `Benchmark/Cpu/ThreadPlacement.cs` : `PinCurrentThread(LogicalProcessorTarget)` → `ThreadPlacementResult(Mechanism,
  Verified)` : `SetThreadSelectedCpuSets` puis `SetThreadGroupAffinity`, vérification `GetCurrentProcessorNumberEx`.
- `Benchmark/Cpu/WorkerProcessSetup.cs` : `Apply()` = EcoQoS désactivé (`SetProcessInformation(ProcessPowerThrottling,
  ControlMask = EXECUTION_SPEED, StateMask = 0)`) + priorité High.
- `Benchmark/Cpu/CpuBenchRunner.cs` : `Run(request, progress, cancel)` : équipe de threads `CpuLoadTeam` (barrière,
  épinglage, étalonnage des sommes, phases Calibrate / Warmup / Measure / Exit), préchauffe, passes « rafale »
  (`<noyau>.rafale`), charge continue (noyaux alternés, tronçons ≤ 10 s, cycles charge/repos facultatifs) jusqu'à
  `SustainedSeconds`, passes « soutenu » (`<noyau>.soutenu`), notes (threads, épinglage, jeu d'instructions, étalons,
  erreurs de calcul), `IsComparable`.
- `Benchmark/Worker/JobObject.cs` : `TryCreateKillOnClose`, `TryAssign(process)`, Dispose = kill.
- `Benchmark/Worker/BenchLineChannel.cs` : lignes sur un flux duplex, envoi sous verrou, `IsBroken`.
- `Benchmark/Worker/BenchJobDispatcher.cs` : `Run(request, progress, cancel)` par type (CPU seulement pour l'instant ;
  RAM et disque à brancher quand leurs runners existent).
- `Benchmark/Worker/BenchWorkerHost.cs` : `Run(pipeName, log, applyProcessSetup)` (client de tube, CurrentUserOnly) et
  `Serve(stream, setupNotes, log)` (bonjour, thread lecteur, un test à la fois, `arreter`, battement toutes les 500 ms,
  sortie `ExitAppSilent = 3` après 2 s sans nouvelles de l'app, `ExitPipeUnavailable = 2`).
- `Benchmark/Worker/BenchWorkerSession.cs` : côté app : `AcceptAsync(server, process, job, expectedPid, timeout, cancel)`
  (vérifie pid et version du bench), `RunJobAsync(request, cancel)` (un test à la fois ; annulation = `arreter` puis kill
  après 3 s), `ProgressReported`, `Kill()`, battement toutes les 500 ms, worker muet 5 s → tué, `HeartbeatEnabled`
  (tests), `WorkerNotes`, Dispose (ferme le tube, attend 1,5 s, ferme le Job Object).
- `Benchmark/Worker/BenchWorkerLauncher.cs` : `FindExecutable` (refuse l'hôte dotnet), `StartAsync(log, cancel)` :
  tube `PCPerfSuite.Bench.<guid>` (Asynchronous | CurrentUserOnly), Job Object, `Process.Start(exe, --bench-worker <tube>)`
  par chemin complet, `AcceptAsync` (15 s).
- `src/PCPerfSuite.App/SecondaryModes.cs` : `Parse(args)` → `SecondaryMode(Kind Normal | BenchWorker | Refused,
  LaunchedByWindows, PipeName, Problem)` ; liste fermée (`--demarrage-windows`, `--bench-worker <nom>` avec nom
  `^[A-Za-z0-9._-]{1,128}$`) ; inconnu → `Refused`, code de sortie 2.

- `App.xaml.cs` : `SecondaryModes.Parse(e.Args)` en tête d'`OnStartup`, avant le mutex : `Refused` → `Shutdown(2)` ;
  `BenchWorker` → `RunBenchWorker(pipe)` (thread dédié qui exécute `BenchWorkerHost.Run`, messages et plantage dans
  `CrashLog`, puis `Shutdown(code)` par le Dispatcher ; ni mutex, ni reprise, ni fenêtre) ; `launchedByWindows` lu dans
  `mode.LaunchedByWindows`. `BenchWorkerHost.ExitCrashed = 1`.
- Défaut trouvé par les tests et corrigé : `BenchWorkerSession.AcceptAsync` créait `BenchLineChannel` (StreamWriter en
  AutoFlush) sur le tube serveur **avant** la connexion → « Pipe hasn't been connected yet ». Le canal est créé après
  `WaitForConnectionAsync` ; en échec avant, le serveur est fermé directement (`CloseQuietly`).
- Tests (verts) : `SecondaryModesTests` (App.Tests : inconnu refusé code 2, `--demarrage-windows` inchangé et insensible
  à la casse, tube valide, noms invalides et trop longs, sans nom, répété, worker + Windows incompatibles),
  `CpuTopologyTests` (CpuSetId = 256 + index, 0 par défaut), `CpuBenchRunnerTests` (mono court → 3 mesures `.rafale`,
  notes ; multi 2 threads avec soutenu 0,3 s → `.soutenu` ; annulation avant et pendant → « arrêté » ; noyau inconnu ;
  distributeur), `ThreadPlacementTests` (épinglage vérifié sur (0,0), mécanisme cpu-set si la topologie donne l'id,
  processeur hors de portée sans plantage), `BenchWorkerSessionTests` (vrai tube nommé, worker dans un thread :
  bonjour + test + sortie `ExitOk` à la fermeture du tube ; sans battement → `ExitAppSilent` ; annulation → résultat
  partiel « arrêté » sans tuer ; second test refusé ; mauvais pid refusé ; attente expirée → `TimeoutException`).

### Commit 3 (poussé) : `feat(F1): bench RAM, debit et latence`

- `Benchmark/Memory/MemorySlices.cs` (pur : tranches contiguës alignées 4 Ko, une par thread).
- `Benchmark/Kernels/MemoryBandwidthKernel.cs` : tampon unique aligné page ; écriture = motif par page (graine ⊕ page)
  en `StoreAlignedNonTemporal` + sfence ; lecture = `LoadAlignedVector256` **additionnée** par voie (pas un XOR : un
  motif répété un nombre pair de fois s'annulerait, vu au premier test) ; copie = première moitié → seconde (lus + écrits
  comptés) ; repli `Vector<T>` non comparable ; `Snapshot` pour les tests.
- `Benchmark/Kernels/MemoryLatencyKernel.cs` : lignes de 64 o, nombre de lignes ramené à une puissance de deux (indice lu
  toujours masqué : une corruption ne sort pas du tampon), chaîne de Sattolo écrite dans les lignes, `Chase(start,
  steps)` déroulé par 4 → ns/pas, `VerifyPermutationChecksum` (XOR des indices = XOR de 0..n−1).
- `Benchmark/Memory/MemoryBenchSizing.cs` (pur) : `Compute(l3, libre)` → `MemoryBenchSizes` (débit = max(8 × L3,
  128 Mo) plafonné à 25 % de la RAM libre, aligné 2 Mo ; latence = 512 Mo dans [256 Mo, 1 Go], ≤ 25 % libre, puissance de
  deux ; sous le plancher → `Unavailable` HardwareOrDriver avec la raison) ; `ReadCurrent(topology)` (plus grand L3 des
  groupes de cache, RAM libre par `SystemMemoryReader`).
- `Benchmark/Memory/MemoryBenchRunner.cs` : débit = équipe `MemoryLoadTeam` (barrière ; Fill, Calibrate, Measure), par
  passe écriture → lecture (somme vérifiée → `ChecksumMismatch`) → copie, balayages entiers jusqu'à `PassSeconds`,
  mesures `ecriture`, `lecture`, `copie` en **Go/s décimaux** (10⁹) ; latence = préchauffe non comptée (1 chaîne),
  passes par tronçons de 1 M pas (annulation, avancement), mesure `latence` en ns (HigherIsBetter faux), passes qui
  doivent rendre le même indice final + permutation intacte, sinon `ChecksumMismatch` ; notes (tampon-mo, threads,
  jeu-instructions, erreurs-lecture, pages 4 Ko, verification). Branché dans `BenchJobDispatcher`.
- Mesure de vraisemblance sur le 13500T (RAM libre 4,3 Go, L3 16 Mo → tampon 128 Mo) : lecture ≈ 15–16 Go/s, écriture
  ≈ 12–13, copie ≈ 15–17 (1, 4 ou 8 threads : la RAM sature dès 1 thread sur ce PC), latence ≈ 131 ns (première passe
  151 → d'où la préchauffe). Références `BenchReferences` RAM encore à calibrer (étape 6).
- Tests (verts, 18) : `MemoryBenchTests.cs` (tranches, dimensionnement et bornes, noyaux : somme attendue, copie
  recopiée, graine, cycle unique masqué et vérifiable, runner : débit, latence, refus, annulation, distributeur).

### Commit 4 (poussé) : `feat(F1): bench disque, volumes et fichier de test`

- `Benchmark/Disk/DiskBenchPlan.cs` (pur) : `DiskPhase` (profil, opération, bloc, file, aléatoire, durée, `MaxBytes`,
  `MeasurementKey` = `<profil>.lecture|.ecriture`, `ReportsIops` ≤ 64 Ko), `DiskBenchPlan.Create(taille, secteur,
  phaseSeconds, hdd, budget)` : seq1m-q8, seq1m-q1, alea4k-q32, alea4k-q1, lecture puis écriture ; HDD = files de 1 ;
  taille alignée au Mo (≥ 8 Mo), blocs ≥ secteur ; budget par défaut 4 × (préremplissage 1 ×, séq 1 × par phase, aléa
  0,5 ×), un budget plus court réduit les écritures au prorata, jamais sous le préremplissage ; `SliceSeconds = 1` ;
  `LooksLikeCacheExhaustion(tranches)` (dernier tiers < moitié du premier → cache SLC).
- `Benchmark/Disk/VolumeDevice.cs` : `Read("X:")` → `VolumeDeviceInfo` (secteurs logique/physique, numéro de disque,
  pénalité de recherche) par `IOCTL_STORAGE_QUERY_PROPERTY` (AccessAlignment, SeekPenalty) et
  `IOCTL_STORAGE_GET_DEVICE_NUMBER` sur `\.\X:` ouvert sans droit d'accès (pas d'administrateur requis) ; parseurs purs ;
  `SectorBytes` = physique, sinon logique, sinon 4 096 ; `NormalizeLetter`.
- `Benchmark/Disk/BenchVolume.cs` : `BenchVolume` (lettre, nom, format, type, tailles, système, `Device`, `Disk`
  `PhysicalDiskInfo?`, BitLocker, `Unavailable`), `IsRotational` = pénalité de recherche sinon média WMI, `Describe()` ;
  `BenchVolumeRules.Judge` (pur : fixe ou amovible, prêt, NTFS/ReFS/exFAT ; réseau, optique, RAM, FAT32 écartés avec la
  raison), `SupportsAcl` ; `BenchVolumeReader.Read()`/`ReadAsync` (DriveInfo + IOCTL + MSFT_PhysicalDisk +
  `Win32_EncryptableVolume` best-effort), système d'abord.
- `Hardware/DiskHealthService.cs` : `PhysicalDiskInfo` (DeviceId, FriendlyName, BusType, MediaType, secteurs ;
  `IsRotational`, `Describe()`), `PhysicalDiskLabels` (codes MSFT_PhysicalDisk → libellés),
  `ReadPhysicalDiskInfo(deviceNumber)` statique + `ReadPhysicalDiskInfoAsync`. `ProgramDataFolder.BenchFolderName = "Bench"`.
- `Benchmark/Disk/DiskTestFile.cs` : `test-disque.bin` ; système → `ProgramDataFolder.TryEnsure("Bench")` ; autre volume
  NTFS/ReFS → `TryEnsure(@"X:\PCPerfSuite.Bench", [])` (ACL administrateurs) ; exFAT → dossier simple, lien refusé ;
  `RequiredFreeBytes` = 1,1 × taille + 512 Mo ; `Prepare(volume, taille)` → `DiskTestFilePlacement` (chemin, notes,
  `Unavailable`) avec suppression d'un fichier restant (`TryDeleteStale`, jamais un lien) ; `TryDeleteOnVolume` pour la
  reprise ; `ClampFileBytes` (pas de 256 Mo, [256 Mo, 8 Go]).
- `Benchmark/Disk/DiskBenchRunner.cs` : `File.OpenHandle(CreateNew, Asynchronous | 0x20000000, preallocationSize)`,
  `RandomAccess.ReadAsync/WriteAsync`, tampons `AlignedBuffer` 4 Ko exposés en `Memory<byte>` (`AlignedBuffer.AsMemory`,
  `MemoryManager` natif), pool de 16 blocs aléatoires de 1 Mo en rotation, préremplissage séquentiel Q8 mesuré
  (`preremplissage`), puis les phases : `QueueDepth` E/S en vol (`Task.WhenAny`), tranches de 1 s → mesures en **Mo/s
  décimaux** (médiane/CV sur les tranches) + `.iops` pour le 4 Ko ; budget respecté par phase ; annulation → « arrêté » ;
  **suppression en `finally` du seul fichier créé par le runner** (un fichier déjà là est refusé et laissé : défaut
  trouvé par le test) ; notes (fichier-mo, secteur-o, budget-ecrit-mo, ecrit-mo, disque-a-plateaux, e-s, donnees,
  cache-slc, fichier-supprime, unite). Branché dans `BenchJobDispatcher`.
- Tests (verts, 37) : `DiskBenchTests.cs` (plan SSD/HDD, alignements, budget réduit, refus, cache SLC ; fichier de test :
  espace, dossier, pas de taille, volume écarté/plein, fichier restant ; règles des volumes, description, inventaire de
  ce PC ; décodage IOCTL, lettre normalisée, volume système de ce PC ; libellés WMI ; runner sur un vrai fichier de 16 Mo
  dans %TEMP% : 13 mesures, fichier supprimé, HDD 7 mesures, fichier déjà là refusé et laissé, annulation, refus,
  distributeur).

### Commit 5 (poussé) : `feat(F1): protocole commun, securite et reprise du bench`

`src/PCPerfSuite.Core/Benchmark/Session/` :
- `BenchPreconditions.cs` : `BackgroundLoadWindow` (pur : fenêtre 10 s, calme < 5 % en moyenne, `Describe`),
  `BenchPreconditions.Evaluate(inputs)` → `BenchPreconditionReport` (par test : `Unavailable?` ; sur batterie autorisé
  mais `IsRepresentative` faux ; batterie < 30 % ou worker non lançable → rien ne part ; raisons propres RAM/disque).
- `PowerModeReader.cs` : `PowerGetEffectiveOverlayScheme` (libellés Équilibré / Meilleure efficacité / Meilleures
  performances / Performances élevées) + plan actif par `IPowerPlanValues`.
- `BenchThermalPolicy.cs` : `Resolve(platform, topology, tjMax)` → `BenchThermalLimits` (Intel = TjMax lu, sinon 98 ;
  AMD Zen 4/5 = 98 ; X3D (L3 mixtes ou « X3D ») = 93 ; inconnu = 98 ; 10 s tenues, perte 10 s) ; `CreateGuard()` =
  `ThermalGuard` de #2.
- `BenchSafetyMonitor.cs` : `BenchSafetySample.From(snapshot, at)` (paquet sinon cœur max, ventilateur `FanCategory.Cpu`,
  batterie %), `Note(sample, underLoad)` → thermique / ventilateur CPU à 0 pendant 10 s sous charge / batterie < 30 % sur
  batterie / perte ; `NoteNoReading(now)` ; `BenchStopReason` + `Label`.
- `IdleReturn.cs` : ±3 °C de la base, 60 s max, 10 s fixes sans température.
- `SensorRecording.cs` : dédoublonnage par `CapturedAtUtc`, séries 1 Hz (cpu-temp, cpu-puissance, cpu-frequence,
  cpu-charge, gpu-temp, gpu-puissance, ventilateur-cpu, batterie), `ThrottleTally` (#4), `RecordingCadence` (relevés,
  intervalle moyen, trou max, relevés sans le groupe CPU : l'affamement par le worker High se mesure là).
- `BenchContextReader.cs` : `BenchContext` (CPU, topologie, RAM, GPU, machine, Windows, app, runtime, admin, limites
  CPU/GPU, ventilation (texte de l'app), mode constructeur **null + raison**, `PcieLink` **null + raison « à venir (#4) »**,
  mode d'alimentation, problèmes) ; `AppVersion()`.
- `ProcessPriorityScope.cs` : priorité de l'app relevée (High) le temps d'un test, rendue ensuite.
- `BenchSession.cs` : `IBenchWorker` (implémentée par `BenchWorkerSession`), `BenchTestPlan`, `BenchSessionPlan`,
  `BenchSessionPorts` (StartWorker, Lease, Journal, Time, SubscribeSnapshots, RequestCadence, RaisePriority,
  LastSnapshot), `RunAsync` : bail `TryAcquire("bench", "le bench", "mesure en cours")` toute la session (refus →
  `LeaseRefusal`), bail de cadence, abonnement capteurs, worker (relancé s'il meurt), température de départ ; par test :
  `Journal.Begin("bench", <clé>, valeurs)` (non durable → test non commencé), priorité relevée, veille de sécurité
  toutes les 500 ms (relevés horodatés à leur **capture**, perte à l'horloge de la session), annulation du test puis
  `Kill` si le worker ne rend pas la main, `Complete`/`Fail`, retour au repos avant le suivant ; `BenchTestOutcome`,
  `BenchSessionOutcome`.
- `BenchRecoveryHandler.cs` : `IStartupRecoveryHandler` (étape `Bench`, composant `bench`) : « bench interrompu : <test> »
  (Interrupted, CleanShutdown) ou « arrêt brutal pendant le bench (<qualification>) : <test> » ; pour `disque`, supprime
  `test-disque.bin` du volume des valeurs (`volume` = lettre sans deux-points, `systeme` oui/non) via
  `DiskTestFile.TryDeleteOnVolume` ; jamais de chemin dans la note. Inscrit dans `StartupRecoveryHandlers.cs`.

Tests (verts) : `BenchSessionLogicTests.cs` (fenêtre de charge, préconditions, politique thermique, moniteur de sécurité,
retour au repos, enregistrement, mode d'alimentation, contexte, priorité, gestionnaire de reprise) et
`BenchSessionTests.cs` (faux worker, faux capteurs, horloge manuelle : deux tests et leurs lignes au journal, bail refusé,
arrêt thermique → ligne échouée et suivants sautés, annulation, journal non écrit, worker non lancé, retour au repos).

## Reste à faire (plan approuvé par Denis, dans l'ordre)

6. **Résultats et page** : `Results/BenchSessionResult.cs` (JSON v1, `[JsonExtensionData]` partout : contexte,
   BenchVersion, runtime, par test unités + points + CV + instable + séries + bridage + préconditions + `IsComparable`),
   `Results/BenchResultStore.cs` (un fichier `bench\<horodatage>.json` sous `AppDataPaths.BenchFolder`, lecture tolérante),
   `AppDataPaths.BenchFolder => InRoot("bench")`, `BenchSettings` + `AppSettings.Bench` (tests cochés, lettre du
   volume, `DiskFileSizeMb` 1024, `SustainedEnabled` true), `BenchRowProvider` (ligne « Bench » du diagnostic),
   `ViewModels/BenchDiagnosticViewModel.cs` (page `IPageLifecycle` + `IDisposable`, sections Bench / Diagnostic
   (ComingSoon #12) / Technicien (ComingSoon #13), modèle `AppSettingsViewModel.Sections` + `PillSelector`),
   `ViewModels/BenchViewModel.cs` (tests cochables avec raison, volume en combo, taille +/−, durée estimée,
   confirmation « Non » par défaut avant Démarrer (D6), Arrêter toujours visible, phase et %, courbes
   `Sparkline`/`SampleHistory` (température, puissance, fréquence CPU), cartes de résultat, historique à la première
   ouverture ; `IBackgroundSensorConsumer` pendant un test ; jamais de lancement automatique),
   `Views/BenchDiagnosticView.xaml(.cs)`, `MainWindow.xaml` (ligne de la vue, modèle `ProfileGroupsView`),
   `MainViewModel.cs` (construction avec `_hardware`, `_monitoring`, `_cpuControl`, `_gpuControl`, `_fans`,
   `_tuningLease`, `startupRecovery` ; `[PageKeys.BenchDiagnostic] = …` ; `_compatibilityRows.Add(new
   BenchRowProvider(...))` avant la ligne ~183 ; `UpdateEcoMode` ; `Dispose` juste après `_autoSwitch.Stop`),
   `ComingSoonPages.cs` (retirer la ligne `bench-diagnostic`). Calibrer `BenchReferences` sur le 13500T.
7. **Documentation** : `docs/decisions.md` (briques « Modes secondaires » livrée et « Moteur de charge et noyaux CPU
   vérifiés » à `Core/Benchmark/` ; `bench` inscrit au `StartupRecovery` ; décisions de cette conversation en ajout, une
   ligne chacune), `docs/navigation.md` (ligne Bench → livrée, expérimental), `README.md` (section « Bench et
   diagnostic », « Organisation de l'app », dossiers `bench\` et `%ProgramData%\PCPerfSuite\Bench`). Aucune dépendance
   NuGet nouvelle.
8. `/review-max` sur le travail de la conversation, puis `fix(F1): …`.

## À vérifier sur une vraie machine (prompt, section 9)

Mono épinglé sur un cœur P ; multi sur tous les processeurs logiques ; écart rafale/soutenu cohérent avec PL1/PL2 ;
3 passes à CV < 3 % au repos ; RAM du même ordre qu'un outil connu ; NVMe proche de CrystalDiskMark, fichier supprimé ;
Arrêter et fermeture brutale de l'app → worker arrêté (Job Object) ; app tuée pendant le test disque → au relancement,
fichier supprimé et test noté « interrompu » ; cadence du relevé pendant le test (affamement par le worker High) visible
dans le résultat ; `--inconnu` → sortie immédiate code 2 ; `--demarrage-windows` inchangé.
