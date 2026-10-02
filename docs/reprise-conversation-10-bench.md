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

### Commit 2 (en cours, poussé tel quel : compile, **non testé**, pas encore branché dans l'app)

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

**Reste à faire pour clore ce commit** :
1. `App.xaml.cs` : en tête d'`OnStartup`, juste après `base.OnStartup(e)` et avant le mutex :
   `SecondaryMode mode = SecondaryModes.Parse(e.Args);` → `Refused` : `Shutdown(SecondaryModes.RefusedExitCode); return;`
   → `BenchWorker` : thread qui exécute `BenchWorkerHost.Run(mode.PipeName!, message => CrashLog.RecordMessage(message,
   "worker de bench", false))` puis `Dispatcher.InvokeAsync(() => Shutdown(code))` ; brancher aussi
   `AppDomain.UnhandledException` → `CrashLog.Record` dans ce mode ; `return` sans mutex ni reprise ni fenêtre.
   Remplacer la ligne `bool launchedByWindows = e.Args.Contains(...)` par `mode.LaunchedByWindows`.
2. Tests à écrire : `tests/PCPerfSuite.App.Tests/SecondaryModesTests.cs` (inconnu refusé, `--demarrage-windows`
   inchangé, tube valide, nom invalide, répétition refusée, worker + Windows incompatibles) ;
   `tests/PCPerfSuite.Core.Tests/CpuTopologyTests.cs` : un cas « CpuSetId = 256 + index » ;
   `CpuBenchRunnerTests` (mono : Warmup 0,02 s, PassSeconds 0,02, Passes 2, Sustained 0 → 3 mesures `.rafale`, Succeeded,
   sans erreur de calcul ; multi 2 threads non épinglés avec Sustained 0,3 → mesures `.soutenu` ; annulation → Error
   « arrêté ») ; `BenchWorkerSessionTests` (tube nommé en mémoire : `NamedPipeServerStream` + thread
   `BenchWorkerHost.Run(name, applyProcessSetup: false)` + `AcceptAsync(server, null, null, Environment.ProcessId, …)` ;
   un test cpu-mono court rend un résultat ; `HeartbeatEnabled = false` → le worker sort avec `ExitAppSilent` en ~3 s) ;
   `ThreadPlacementTests` (thread dédié épinglé sur (0,0) → mécanisme non nul, `CurrentProcessor()` = (0,0)).
3. `dotnet build -c Release` et `dotnet test` verts, puis commit `feat(F1): worker de charge, modes secondaires et bench CPU`
   et push.

## Reste à faire (plan approuvé par Denis, dans l'ordre)

3. **Bench RAM** (`Benchmark/Memory/`, `Benchmark/Kernels/`) : `MemoryBandwidthKernel` (lecture
   `Avx.LoadAlignedVector256` sommée, écriture `Avx.StoreAlignedNonTemporal`, copie ; multi-thread par tranches alignées
   4 Ko ; repli `Vector<T>` ; copie comptée lus + écrits), `MemoryLatencyKernel` (lignes de 64 o, `SattoloPermutation`
   sur les indices de lignes, pointer chasing, ns par pas, « pages de 4 Ko »), `MemoryBenchRunner.Run(request, progress,
   cancel)` : 3 passes débit (`lecture`, `ecriture`, `copie` en Go/s) et 3 passes latence (`latence` en ns) ; tailles
   fournies par l'app (`RamJobParameters.BandwidthBytes` = max(8 × L3, 128 Mo) plafonné à 25 % de la RAM libre ;
   `LatencyBytes` 512 Mo borné [256 Mo, 1 Go] et 25 % libre ; RAM libre lue par `SystemMemoryReader` interne).
   Brancher dans `BenchJobDispatcher`. Tests sur la logique pure (tailles, bornes).
4. **Bench disque** (`Benchmark/Disk/`) : `DiskBenchPlan` (pur : phases façon CrystalDiskMark séq 1 Mo Q8/Q1, aléa 4 Ko
   Q32/Q1, lecture puis écriture, 5 s chacune ; alignement sur `SectorBytes` ; budget écrit 4 × taille : préremplissage
   1 ×, séq ≤ 1 × par phase, aléa ≤ 0,5 × ; HDD raccourci = Q1 seulement ; CV sur tranches de 1 s, une passe par profil),
   `DiskTestFile` (dossier `%ProgramData%\PCPerfSuite\Bench` par `ProgramDataFolder.TryEnsure("Bench")` sur le volume
   système, sinon `<X>:\PCPerfSuite.Bench\` par l'overload interne `TryEnsure(root, segments)` ; `CreateNew`, nom fixe
   `test-disque.bin`, espace libre exigé = taille × 1,1 + 512 Mo, `DeleteStale()`), `DiskBenchRunner`
   (`File.OpenHandle(FileOptions.Asynchronous | (FileOptions)0x20000000)`, `RandomAccess.ReadAsync/WriteAsync`, tampons
   alignés page, préremplissage aléatoire intégral avant lecture, pas de `SetFileValidData`, Mo/s et IOPS, clés
   `seq1m-q8.lecture`… + `.iops` pour le 4 Ko, suppression en `finally`), `PhysicalSectorSize`
   (`IOCTL_STORAGE_QUERY_PROPERTY` StorageAccessAlignmentProperty sur `\\.\X:`, repli 4096 ; `IOCTL_STORAGE_GET_DEVICE_NUMBER`),
   `BenchVolumeReader` + `BenchVolume` (`DriveInfo` Fixed/Removable, NTFS/ReFS/exFAT, libre/total, système, amovible,
   BitLocker par `Win32_EncryptableVolume.ProtectionStatus` best-effort, BusType/MediaType), ajout à
   `Hardware/DiskHealthService.cs` de `ReadPhysicalDiskInfoAsync(deviceNumber)` → `PhysicalDiskInfo(DeviceId,
   FriendlyName, BusType, MediaType, PhysicalSectorSize, LogicalSectorSize)` + libellés (réutilisable par #19), constante
   `ProgramDataFolder.BenchFolderName = "Bench"`. Tests `DiskBenchPlanTests`.
5. **Protocole commun, sécurité, reprise** (`Benchmark/Session/`, `Benchmark/`) : `BenchPreconditions` (pur : secteur /
   batterie %, charge de fond < 5 % sur 10 s `BackgroundLoadWindow`, raisons d'indisponibilité par test, règles 1-3),
   `PowerModeReader` (`PowerGetEffectiveOverlayScheme` → libellé ; plan actif par `PowerPlanValues.Instance.ActiveScheme()`
   + `FriendlyName`), `BenchThermalPolicy` (pur : Intel = TjMax de `CpuThrottle.TjMaxC` tenu 10 s ; AMD Zen 4/5 (famille
   0x19 modèle ≥ 0x60, 0x1A) = 98 °C ; X3D (`HasMixedL3Sizes` ou nom « X3D ») = 93 °C ; inconnu = 98 °C ; `ThermalGuard`
   de #2 avec `lossDelay` 10 s), `BenchSafetyMonitor` (pur : thermique, ventilateur `FanCategory.Cpu` à 0 tr/min 10 s
   sous charge, batterie < 30 %, perte de relevé → `StopReason`), `IdleReturn` (±3 °C de la base, 60 s max),
   `SensorRecording` (dédoublonnage par `CapturedAtUtc`, `GroupsRead`, séries 1 Hz, bridage compté, cadence obtenue :
   ticks et trou max), `BenchContextReader` (CPU `CpuPlatform` + topologie, RAM, GPU, `MachineInfo`, Windows, app,
   runtime, limites CPU `CpuControlService.ReadPowerLimits`, limite GPU `GpuControlService.GetSnapshot()?.PowerLimitPercent`,
   ventilation (texte fourni par l'app), mode constructeur null + raison, `PcieLink` null), `BenchSession` (orchestrateur
   à ports : bail `TuningLease.TryAcquire("bench", "le bench", "mesure en cours")` tenu toute la session, une
   `SessionJournal.Current.Begin("bench", "<cpu-mono|…>", {volume, taille-mo})` par test, priorité de l'app relevée à
   High pendant un test (parade au worker High), bail de cadence `HardwareMonitorService.RequestCadence("bench", 250 ms,
   [Cpu, CpuLoad, Motherboard, Battery])`, retour au repos entre tests), `BenchRecoveryHandler` (`IStartupRecoveryHandler`,
   `RecoveryStage.Bench`, composant `bench` ; note « bench interrompu » pour Interrupted/CleanShutdown, « arrêt brutal
   pendant le bench (…) » pour PowerLoss/BlueScreen/ForcedShutdown/UnexpectedShutdown/Unknown ; supprime
   `test-disque.bin` du volume noté dans les valeurs ; jamais de chemin dans la note), inscription dans
   `src/PCPerfSuite.App/StartupRecoveryHandlers.cs`. Tests sur chaque classe pure (+ handler avec `TempDirectory`).
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
