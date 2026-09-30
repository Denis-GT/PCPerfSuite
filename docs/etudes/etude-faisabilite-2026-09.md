# Étude de faisabilité — feuille de route PCPerfSuite (28/09/2026)

Produite par 5 agents de recherche (sources primaires : docs Microsoft, SDK constructeurs, dépôts des outils de référence). Aucun binaire téléchargé ni exécuté. Les incertitudes sont signalées telles quelles : tout ce qui n’a pas été vérifié sur une vraie machine reste « expérimental ».

Identifiants : F1 bench, F2 diagnostic IA, F3 mode technicien, F4 OC auto, F5 core parking, F6 OC GPU AMD/Intel, F7 pilotes, F8 RGB, F9 GPU portable, F10 liens directs, F11 disques, F12 périphériques, F13 RAM, F14 limites par processus, F15 animations, F16 OC écran, F17 écran de l’overlay, F18 profils auto, F19 groupes de profils.


## F1-F3 : bench intégré sélectionnable, diagnostic automatique (verdict déterministe et rédaction IA optionnelle) et mode technicien, dans PCPerfSuite (WPF .NET 8, toutes marques)

### F1 — faisable, taille XL

**Approche recommandée**

ARCHITECTURE. Nouveau module Core `Benchmark/` et un exe worker séparé `PCPerfSuite.BenchWorker` (même solution, lancé par l'app, dialogue par pipe nommé en JSON) qui ne fait que générer la charge. L'app continue d'échantillonner les capteurs pendant le test (HardwareMonitorService existant, 4 à 10 Hz pendant un test). Pourquoi un worker séparé : l'interface reste fluide, un plantage du test ne fait pas tomber l'app, et le bench a ses propres réglages d'exécution (`[MethodImpl(MethodImplOptions.AggressiveOptimization)]` sur les noyaux ou `TieredPGO=false` dans son runtimeconfig, aucune allocation dans les boucles). Le runtime .NET est embarqué (self-contained) et chaque score porte un `BenchVersion` : un score ne se compare qu'à un score de la même version.

PROTOCOLE COMMUN. (1) Contrôles avant le test : PC sur secteur (BatteryReader existant, PowerOnline ; sur batterie, on refuse ou on marque le résultat « non représentatif ») ; charge CPU de fond < 5 % pendant 10 s (PDH) ; mode d'alimentation Windows (PowerGetEffectiveOverlayScheme) et plan actif (PowerPlanService) relevés dans le rapport. (2) Le worker sort d'EcoQoS : SetProcessInformation(ProcessPowerThrottling, ControlMask=PROCESS_POWER_THROTTLING_EXECUTION_SPEED, StateMask=0), priorité High (jamais Realtime). (3) Préchauffe de 5 à 10 s non comptée, puis 3 passes. On garde la médiane et le coefficient de variation ; au-delà de 3 % (seuil à calibrer), la mesure est marquée « instable, activité en arrière-plan ». (4) Retour au repos entre deux tests : on attend que la température revienne à ±3 °C de la base, 60 s au plus. (5) Arrêts de sécurité : CPU à TjMax pendant 10 s (sauf Zen 4/5, voir F2), GPU hot spot à sa limite, ventilateur CPU à 0 tr/min sous charge, batterie < 30 %. Un bouton « Arrêter » global reste toujours visible.

CPU. Noyaux C# déterministes à graine fixe : un noyau entier (Deflate/Brotli via System.IO.Compression sur un tampon pseudo-aléatoire, ou tri et hachage sans instructions dédiées SHA/AES pour ne pas avantager une puce), un noyau flottant (produit matriciel Vector256 + Fma, repli Vector<T>) et un noyau mixte à branchements. Mono-thread : thread épinglé sur un cœur de la plus haute EfficiencyClass (GetSystemCpuSetInformation, puis SetThreadSelectedCpuSets). Multi-thread : un thread par processeur logique. Deux chiffres : « rafale » (20 premières secondes) et « soutenu » (après 3 min, au-delà du Tau Intel de 28 ou 56 s). L'écart entre les deux est déjà un diagnostic.

RAM. Tampons hors tas GC (NativeMemory.AlignedAlloc, alignés sur 64 octets), d'une taille d'au moins 8 fois le L3 (attention aux 96 Mo des X3D) et d'au plus 25 % de la RAM libre. Toutes les pages sont touchées avant le chronométrage. Débit en lecture, écriture et copie multi-thread (Avx.LoadAlignedVector256 ; Avx.StoreAlignedNonTemporal pour l'écriture). Latence : pointer chasing sur une permutation aléatoire à cycle unique de 256 Mo à 1 Go. Le résultat s'affiche en ns « pages de 4 Ko » : les grandes pages demandent SeLockMemoryPrivilege, qui n'est pas accordé par défaut et exige une déconnexion, donc elles sont écartées. Ce chiffre se compare d'un PC à l'autre, pas avec AIDA64.

DISQUE. Un fichier de test dédié sur le volume visé, jamais dans Documents ou Bureau (l'accès contrôlé aux dossiers bloquerait les écritures) ; par exemple %ProgramData%\\PCPerfSuite\\Bench ou la racine d'un volume secondaire. Ouverture par File.OpenHandle(..., FileOptions.Asynchronous | (FileOptions)0x20000000). 0x20000000 est FILE_FLAG_NO_BUFFERING, que .NET accepte « par valeur » sous Windows. Les E/S passent par RandomAccess.ReadAsync/WriteAsync. Offsets et tailles sont des multiples de la taille de secteur physique (IOCTL_STORAGE_QUERY_PROPERTY, champ BytesPerPhysicalSector) et les tampons sont alignés sur la page. On écrit tout le fichier avec des données aléatoires AVANT les lectures : sinon NTFS renvoie des zéros au-delà de la valid data length sans toucher au disque. SetFileValidData est écarté (privilège SE_MANAGE_VOLUME et risque d'exposer des données). Profils du même type que ceux de CrystalDiskMark : séquentiel 1 Mo en file de 8 et de 1, aléatoire 4 Ko en file de 32 et de 1 (la file = N opérations en vol), 5 à 10 s chacun. Volume écrit plafonné (1 à 4 Go ; à trancher). On prévient qu'un test long sur SSD sort du cache SLC, et on raccourcit les tests sur HDD. En parallèle, on relève le BusType et le MediaType (MSFT_PhysicalDisk, déjà utilisé par DiskHealthService) et le lien PCIe du NVMe (DEVPKEY_PciDevice_CurrentLinkSpeed/CurrentLinkWidth comparés à MaxLinkSpeed/MaxLinkWidth, via SetupAPI ou CM_Get_DevNode_Property).

GPU. ComputeSharp 3.x (MIT, cible net8.0, shaders de calcul DX12). La carte est choisie explicitement : la dédiée, par LUID, via GraphicsDevice.QueryDevices. On refuse un device dont IsHardwareAccelerated vaut false : sans cela, ComputeSharp bascule tout seul sur WARP, c'est-à-dire sur le CPU. Deux noyaux : FMA FP32 (débit de calcul) et copie de grands buffers (bande passante VRAM). Les dispatches restent courts (< 100 ms) pour ne jamais approcher le délai TDR de Windows (2 s). Charge soutenue de 3 à 5 min en boucles de 30 s, avec une « stabilité » = pire boucle / meilleure boucle, même principe et même seuil de 97 % que les stress tests 3DMark. Le rendu 3D (Vortice.Direct3D11, MIT, compatible net8.0) n'arrive qu'en V2, si le calcul ne suffit pas à reproduire un profil de jeu.

« ALIMENTATION ». Ce n'est pas une mesure du bloc d'alimentation, c'est un test combiné CPU + GPU soutenu de 3 à 5 min (worker CPU et noyau GPU en même temps). Pendant ce test, on relève les signaux de F2 : arrêt brutal, power brake, bridage, tension. Il faut le consentement explicite du technicien : un bloc faible peut éteindre le PC et faire perdre le travail non enregistré. Un fichier-marqueur « test combiné en cours » (horodaté) est écrit avant le test et relu au démarrage suivant.

**Alternatives écartées**

Embarquer des outils tiers (Prime95, y-cruncher, FurMark, OCCT, AIDA64, Cinebench, 3DMark) : licences propriétaires ou interdiction de redistribution, et pas d'accès aux données brutes. DiskSpd (Microsoft, MIT) est légalement embarquable, mais c'est un exe externe de plus à signer et à piloter. Il est plus propre de réimplémenter ses profils en C# et de garder DiskSpd seulement pour valider nos chiffres en développement. WinSAT (winsat formal, Win32_WinSAT) : Microsoft prévient que la classe peut disparaître après Windows 8.1, les scores sont plafonnés et peu discriminants. Noyaux natifs C/C++ en P/Invoke : un peu plus stables d'une version de .NET à l'autre, mais une chaîne de build native en plus ; l'embarquement du runtime et le numéro de version du bench suffisent. Grandes pages pour la latence RAM : privilège non accordé par défaut. SetFileValidData pour préparer le fichier disque : privilège et fuite de données. Rendu 3D Vortice dès la V1 : gros coût pour un gain faible en diagnostic. Bench « en jeu » à partir des temps de frame RTSS (RtssFrameStatsReader existe) : dépend d'un jeu et de RTSS, pas automatisable. On peut le garder comme option manuelle.

**Couverture matérielle**

CPU et RAM : tous les x64 Intel et AMD, sans pilote ni droits administrateur pour la charge elle-même. ARM64 (Snapdragon X) : .NET tourne, mais sans AVX (chemin Vector128/AdvSimd) et avec PawnIO indisponible (x64 seulement). Les scores ne se comparent pas à ceux des x64 et doivent être marqués comme tels. Hybrides Intel (12e génération et suivantes) : l'épinglage par EfficiencyClass est nécessaire, sinon le mono-thread peut tomber sur un cœur E. GPU : DX12 avec Shader Model 6.0 exigé par ComputeSharp. En pratique NVIDIA Maxwell et plus, AMD GCN et RDNA, Intel Gen9 (HD/UHD 500 et plus) et Arc avec des pilotes récents. Les iGPU anciens ou aux pilotes figés peuvent échouer à la compilation SM6 (non vérifié, à tester) ; ils doivent s'afficher en N/D avec la raison, pas planter. Qualcomm Adreno : DX12 présent, comportement ComputeSharp non vérifié. Portables Optimus ou hybrides : la sélection par LUID permet de charger la carte dédiée ; sur batterie, les GPU sont bridés par conception (raison AC_BATT chez NVIDIA). Disque : NTFS et ReFS locaux, SATA, NVMe, USB et SD (lents mais mesurables) ; volumes réseau exclus. BitLocker coûte un peu de débit et doit être signalé dans le rapport. Le lien PCIe du NVMe dépend de la présence des propriétés DEVPKEY_PciDevice sur ce pilote (à vérifier au cas par cas). Windows 10 et 11 x64. L'app actuelle démarre en requireAdministrator ; les benchs de débit eux-mêmes n'ont pas besoin de l'administrateur.

**Risques**

Matériel : un stress de 5 min sur un portable encrassé ou un bloc faible peut provoquer une coupure (OCP) avec perte de données, d'où les arrêts de sécurité, le consentement et le marqueur. Les charges FMA pures peuvent consommer plus qu'un jeu (profil de « power virus »), il faut plafonner leur durée. Usure SSD minime mais réelle : plafonner le volume écrit. Justesse des mesures : turbo et Tau (rafale et soutenu à séparer) ; GPU Boost qui descend par paliers à mesure que la carte chauffe ; cache SLC ; tiered JIT et Dynamic PGO de .NET (noyaux en AggressiveOptimization, préchauffe) ; mode constructeur des portables (Silence/Performance, réglé dans le contrôleur embarqué et non lisible partout) qui change tout et doit être noté quand la WMI de la marque le donne ; WARP silencieux chez ComputeSharp ; TDR si un dispatch dure plus de 2 s. Antivirus et EDR : écrire des Go de données aléatoires à haute entropie ressemble à un rançongiciel (accès contrôlé aux dossiers, EDR d'entreprise), d'où un fichier unique hors dossiers protégés ; un exe worker non signé est bloqué par Smart App Control. Licences : ComputeSharp, Vortice et DiskSpd sont sous MIT, rien de propriétaire embarqué. Incertain : les seuils (CV 3 %, ±3 °C, durées) sont des propositions à calibrer sur de vraies machines.

**Règles du projet (CLAUDE.md)**

Aucun conflit d'écriture : le bench charge le matériel et lit les capteurs, il n'écrit ni dans les MSR, ni dans la SMU, ni dans le contrôleur embarqué. Règle 5 : pendant un stress sur portable, l'app ne doit en aucun cas « aider » le refroidissement en écrivant dans le contrôleur embarqué. Elle lit seulement les ventilateurs via les providers WMI existants. Sur un PC de bureau, les courbes de ventilation de l'app restent actives et doivent figurer dans le rapport. Règles 1 à 4 : chaque test se déclare indisponible avec sa raison (« GPU sans DX12/SM6 », « seul un rendu logiciel WARP est disponible », « volume réseau », « PC sur batterie », « PawnIO absent : bench mesuré mais sans fréquences ni températures CPU »), et chaque capacité apparaît dans CompatibilityViewModel. Règle 6 : les seuils et échelles non vérifiés sur de vraies machines portent la mention « expérimental ».

**Questions pour Denis**

- Quel périmètre pour la V1 : CPU + RAM + disque d'abord, GPU et test combiné ensuite, ou tout d'un coup ?
- Acceptes-tu deux nouvelles briques, ComputeSharp (MIT) et un exe worker séparé (à signer lui aussi) ?
- Les scores s'affichent-ils en unités physiques (Go/s, GFLOPS, Mo/s, ns), en points (échelle propre à l'app), ou les deux ?
- Pour le bench disque, peut-on écrire sur le disque système, et combien au maximum (1, 4 ou 8 Go) ? Teste-t-on aussi les HDD et les clés USB ?
- Pour le test combiné « alimentation », acceptes-tu qu'il soit opt-in avec avertissement de coupure possible ? Sinon on le retire du parcours par défaut.
- Sur batterie, refuse-t-on les benchs ou les autorise-t-on avec la mention « non représentatif » ?
- De quelles machines disposes-tu pour calibrer (Intel et AMD, bureau et portable, NVIDIA, Radeon, Arc) ? Sans elles, tout reste « expérimental ».

**Sources**

- https://www.nuget.org/packages/ComputeSharp
- https://github.com/Sergio0694/ComputeSharp
- https://github.com/Sergio0694/ComputeSharp/wiki/3.-Getting-started-%F0%9F%93%96
- https://www.nuget.org/packages/Vortice.Direct3D11
- https://learn.microsoft.com/en-us/windows-hardware/drivers/display/timeout-detection-and-recovery
- https://learn.microsoft.com/en-us/windows/win32/fileio/file-buffering
- https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-setfilevaliddata
- https://github.com/dotnet/runtime/issues/27408
- https://github.com/dotnet/runtime/issues/89750
- https://github.com/Microsoft/diskspd/blob/master/LICENSE
- https://github.com/hiyohiyo/CrystalDiskMark/blob/master/DiskBench.cpp
- https://learn.microsoft.com/en-us/windows/win32/memory/large-page-support
- https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-setprocessinformation
- https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-getsystemcpusetinformation
- https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-system_cpu_set_information
- https://learn.microsoft.com/en-us/dotnet/core/runtime-config/compilation
- https://benchmarks.ul.com/news/check-your-pcs-stability-with-new-3dmark-stress-tests
- https://support.benchmarks.ul.com/support/solutions/articles/44002134931-stress-test-result-screen
- https://github.com/MicrosoftDocs/Virtualization-Documentation/blob/main/hyperv-samples/benarm-powershell/DDA/survey-dda.ps1
- https://www.hexus.net/tech/news/cpu/143278-intel-details-big-changes-comet-lake-s-power-limits-tau-values/
- https://learn.microsoft.com/en-us/windows/win32/winsat/win32-winsat

### F2 — partiel, taille L

**Approche recommandée**

PRINCIPE. Un moteur de règles DÉTERMINISTE (module Core `Diagnostics/`) transforme les séries horodatées du test en constats (`Finding` : id, composant, verdict Très bien / Normal / À surveiller / Anormal / Non mesurable, cause probable, niveau de confiance, preuves chiffrées, action conseillée, et la raison quand ce n'est pas mesurable). L'IA ne tranche rien : elle rédige, voir plus bas.

(c) SIGNAUX, échantillonnés pendant les tests.
- CPU Intel, par le module IntelMSR de PawnIO déjà chargé par IntelPowerLimitBackend. Sa liste blanche de lecture est vérifiée dans IntelMSR.p : 0x1B1 IA32_PACKAGE_THERM_STATUS (bit 0 = au seuil thermique en ce moment, bit 2 = PROCHOT/FORCEPR, bit 10 = limitation de puissance) ; 0x19C IA32_THERM_STATUS par cœur (épingler le thread lecteur sur chaque cœur ; bits 0, 2, 10, 12 = limite de courant, 14 = limite venue d'un autre domaine) ; 0x1A2 (TjMax) ; 0xCE (ratio de base) ; 0x1AD (ratios turbo, donc fréquence attendue selon le nombre de cœurs actifs ; sémantique sur hybrides à vérifier) ; 0x613 MSR_PKG_PERF_STATUS (temps bridé par RAPL, implémentation incertaine sur les CPU grand public) ; 0x610 et 0x614 déjà lus (PL1, PL2, TDP) ; APERF et MPERF (fréquence effective). NON lisibles : 0x64F MSR_PERF_LIMIT_REASONS et 0x6B0/0x6B1, absents de la liste blanche, donc pas de « raisons » fines façon ThrottleStop ou XTU. Les bits « log » sont collants et ne peuvent pas être remis à zéro (écriture refusée par le module) : n'utiliser que les bits d'état échantillonnés.
- CPU AMD, par le module RyzenSMU déjà utilisé par AmdSmuBackend. La PM table donne les valeurs et limites PPT, TDC, EDC et THM. Ses offsets ne sont pas documentés par AMD et changent avec la version de table (get_pm_table_version) : ne prendre en charge que les versions cartographiées (ryzen_monitor, ZenStates), N/D ailleurs. Le statut HTC/PROCHOT existe peut-être par lecture SMN (ioctl_read_smn d'AMDFamily17), mais ses adresses ne sont pas publiques : à investiguer, non retenu en V1. Zen 4 et 5 : atteindre 95 °C (89 °C sur X3D) en pleine charge est un fonctionnement normal voulu par AMD, et le verdict doit le savoir.
- Tous les CPU, sans pilote ni droits administrateur : compteurs PDH « Processor Information\\% Processor Performance » (fréquence effective), « % Performance Limit » et « Performance Limit Flags » (flags non documentés, donc simple indice), et l'événement Kernel-Processor-Power 37 (« vitesse limitée par le firmware »). C'est le repli pour AMD hors tables connues, pour Snapdragon et sans PawnIO.
- GPU NVIDIA : perf cap NVAPI (déjà dans NvApiGpuBackend.GetActiveLimit) plus NvAPI_GPU_GetPerfDecreaseInfo. L'enum PerformanceDecreaseReason de NvAPIWrapper donne ThermalProtection, PowerControl, AC_BATT et InsufficientPower (= connecteur d'alimentation manquant). NVML nvmlDeviceGetCurrentClocksEventReasons (dont HwPowerBrakeSlowdown = frein de puissance externe, par exemple déclenché par l'alimentation) reste en best-effort : NVML n'est qu'en « limited support » sur les GeForce.
- GPU Intel Arc : IGCL ctlFrequencyGetState().throttleReasons (AVE_PWR_CAP, BURST_PWR_CAP, CURRENT_LIMIT, THERMAL_LIMIT, PSU_ALERT, SW_RANGE, HW_RANGE) et ctlPowerTelemetryGet (gpuPowerLimited, gpuTemperatureLimited, gpuCurrentLimited, gpuVoltageLimited). À brancher dans IgclGpuBackend.GetActiveLimit, qui renvoie null aujourd'hui.
- GPU AMD Radeon : ADLX n'expose AUCUNE raison de bridage (IADLXGPUMetrics : usage, fréquences, températures cœur et hot spot, puissance, TBP, ventilateur, VRAM, tension, admission ; plus la mémoire dans IPerformanceMonitoring2). On ne peut qu'inférer : fréquence qui chute avec un hot spot proche du maximum, ou TBP proche de la limite de réglage.
- Refroidissement : inventaire existant (FanInventory, EmptyHeaderDetector, pompe) avec % et tr/min pendant le test.
- Journaux Windows (EventLogReader et XPath, 30 derniers jours plus la fenêtre du test) : Kernel-Power 41 (BugcheckCode 0 et PowerButtonTimestamp 0 = coupure brutale ; Microsoft cite une alimentation sous-dimensionnée ou défaillante parmi les causes), BugCheck 1001, WHEA-Logger 17/18/19/47 (erreur matérielle corrigée ou fatale : OC instable, lien PCIe…), Display 4101 (TDR), Kernel-Processor-Power 37, disque 7/51/153.
- Contrôles de configuration, sans bench : lien PCIe négocié comparé au maximum pour le GPU et le NVMe (DEVPKEY_PciDevice_* ; à lire SOUS charge, car le lien d'un GPU descend au repos) ; écran d'un PC de bureau branché sur l'iGPU au lieu de la carte dédiée (IDXGIAdapter::EnumOutputs par adaptateur) ; RAM : bande passante mesurée comparée au théorique (MT/s × 8 octets × canaux ; en dessous de 60 % du double canal, on conclut « compatible avec un seul canal ») ; ConfiguredClockSpeed comparé à Speed (vitesse JEDEC SMBIOS ; un XMP/EXPO non activé n'est PAS détectable de façon fiable, seulement par heuristique sur la référence des barrettes) ; microcode Intel 13e et 14e génération bureau inférieur à 0x12F (registre HKLM\\HARDWARE\\DESCRIPTION\\System\\CentralProcessor\\0, valeur « Update Revision ») ; portable sur secteur mais dont la batterie se décharge sous charge (BatteryReader : PowerOnline et Discharging = chargeur insuffisant).

RÈGLES D'ATTRIBUTION (à calibrer, « expérimental » tant qu'elles ne sont pas vérifiées).
- Refroidissement limitant : bit thermique ou TjMax atteint alors que la puissance CPU reste sous PL1 (Intel) ou sous la limite PPT (AMD) ; perf cap Thermal côté GPU. Précisions : ventilateurs à 100 % = refroidissement saturé ou encrassé ; ventilateurs bas = courbe ou réglage BIOS ; ventilateur CPU à 0 tr/min = critique. Chiffres donnés : temps avant bridage, pente de montée, résistance thermique apparente (hausse au-dessus du repos / W).
- Limité par la puissance réglée : PL1, PPT ou perf cap Power atteints avec une température inférieure à TjMax − 10 °C. Le refroidissement est suffisant ; c'est normal si ce sont les limites d'usine.
- Suspicion d'alimentation, JAMAIS une certitude : arrêt brutal pendant le test combiné (marqueur plus Kernel-Power 41 code 0), HwPowerBrake NVML, PSU_ALERT IGCL, InsufficientPower NVAPI, ou chute du +12 V lu sur la puce Super I/O. Ce dernier signal ne compte qu'en relatif (repos puis charge, même capteur) et seulement si la valeur au repos est plausible, entre 11,2 et 12,6 V (tolérance ATX 3.0 : +5 % / −7 %). Sur portable : batterie qui se décharge sur secteur. Ce qu'on ne peut PAS dire : la puissance réelle délivrée, l'état des condensateurs, les pics transitoires de l'ordre de la ms (invisibles à 10 Hz), ni distinguer un bloc d'alimentation d'un VRM de carte mère ou d'un câble. Exception : les blocs numériques (Corsair HXi/RMi, NZXT E) exposent tension et courant en USB HID. Protocole connu par liquidctl (GPL) et le pilote Linux corsair-psu : c'est une extension possible, mais en réimplémentation propre à cause de la licence.
- Le refroidissement suffit-il ? On peut dire « suffisant pour tenir les limites d'usine » ou « insuffisant : le processeur se bride avant sa limite de puissance ». On ne peut PAS identifier le ventirad, la pâte thermique, le sens du flux d'air, ni le nombre de ventilateurs branchés sur un hub.

(b) SCORES NORMAUX, sans aucun scraping, du plus fiable au moins fiable. (1) Attendus internes calculés sur la machine elle-même : fréquence tenue comparée aux ratios turbo MSR ou au boost annoncé par le pilote GPU (NvAPI_GPU_GetAllClockFrequencies, plages ADLX et IGCL) ; puissance tenue comparée à PL1, PPT ou la limite GPU ; température comparée à TjMax ; débit disque comparé au plafond de l'interface négociée (SATA ≈ 0,55 Go/s, PCIe 3.0 x4 ≈ 3,5, 4.0 x4 ≈ 7, 5.0 x4 ≈ 14) ; RAM comparée au théorique ; écart rafale/soutenu ; stabilité à 97 %. C'est le cœur du verdict V1, sans base de données. (2) Référence « même configuration » : un technicien enregistre une machine saine comme étalon, et les suivantes s'y comparent. Idéal pour un revendeur qui monte des configurations répétées. (3) Table de référence livrée avec l'app, mesurée par Denis et ses partenaires (modèle exact, mode d'alimentation, version du bench), marquée expérimentale. (4) Télémétrie opt-in plus tard : percentiles par modèle, affichés seulement au-delà d'environ 30 échantillons ; il faut alors un backend et une conformité RGPD. L'estimation à partir des specs (cœurs × fréquence × IPC par microarchitecture) sert seulement de garde-fou grossier (±20 %, incertain), jamais de verdict.

(d) PARTIE IA. Le verdict chiffré reste déterministe : reproductible, testable en unitaire, explicable, identique hors ligne, sans chiffre inventé, sans coût ni réseau. Le LLM est une couche optionnelle de rédaction. En entrée, le JSON des constats et rien d'autre. En sortie, un schéma contraint (structured outputs ; en C# `OutputConfig = new OutputConfig { Format = new JsonOutputFormat { Schema = ... } }`) : résumé client, explication pour le technicien, actions, et pour chaque phrase l'id du constat cité. L'app vérifie que chaque id existe et que chaque nombre cité figure dans l'entrée ; sinon elle rejette la réponse et revient au gabarit. SDK : NuGet « Anthropic » (officiel, GA, .NET Standard 2.0 et plus). Modèle claude-opus-5 par défaut, gestion de stop_reason « refusal » avec repli sur le gabarit. Coût estimé entre 0,05 et 0,20 $ par rapport (environ 8 k tokens en entrée, 2 à 4 k en sortie avec la réflexion ; à mesurer) ; claude-sonnet-5 (2 $ / 10 $ par million de tokens) si Denis veut baisser le coût. Clé API : jamais embarquée dans l'exe, où elle serait extractible. Trois modes. (a) Hors ligne par défaut : rapport rédigé par gabarits français déterministes, toujours disponible. (b) Clé de l'utilisateur, chiffrée par DPAPI (ProtectedData, CurrentUser), ou saisie à chaque session en mode clé USB. (c) Proxy sur un serveur de Denis ou de l'entreprise : clé côté serveur, authentification des techniciens, quotas. Les Commercial Terms le permettent (« to power products and services Customer makes available to its own customers and end users »). La clause « pas d'intermédiation, chaque utilisateur avec sa propre clé » vise l'intégration de Claude Code, pas l'API Messages. Interdit : faire se connecter l'utilisateur avec son abonnement Claude.ai. Confidentialité : on envoie uniquement le matériel et les mesures ; ni nom du PC ou de l'utilisateur, ni numéros de série, ni adresses MAC ou IP, ni chemins, ni noms de processus. Consentement à chaque envoi. L'API Anthropic conserve les entrées et sorties jusqu'à 30 jours et n'entraîne pas ses modèles sur les données clientes (Commercial Terms). Les chaînes lues sur le PC (noms de disques et de GPU) sont des données et jamais des instructions : les encadrer et les tronquer contre l'injection de prompt.

**Alternatives écartées**

Verdict rendu par le LLM : non reproductible, chiffres potentiellement inventés, impossible à tester, dépend du réseau et d'un coût, responsabilité floue. LLM local (Phi Silica ou Windows AI) : réservé aux PC Copilot+ avec NPU, pas universel. Clé API de Denis embarquée dans l'app : extractible, abus et facture illimités. Connexion par abonnement Claude.ai : interdite aux développeurs tiers. Scraping ou reprise des scores PassMark, 3DMark ou Geekbench : juridiquement exclu, et pas comparable de toute façon (autres charges). Lecture de MSR_PERF_LIMIT_REASONS (0x64F) : refusée par le module PawnIO (une PR upstream serait possible). Effacer les bits log de 0x19C/0x1B1 pour compter les événements : c'est une écriture, refusée par le module et inutile. Tension +12 V Super I/O prise en valeur absolue : trop souvent fausse (diviseur non connu de LibreHardwareMonitor ou BIOS). Deviner le modèle de ventirad : aucune source logicielle.

**Couverture matérielle**

Intel bureau et portable avec PawnIO et administrateur : bits thermiques, PROCHOT, limitation de puissance et de courant, TjMax, PL1/PL2, fréquence effective. Bonne couverture, sans raisons fines (0x64F). Intel sans PawnIO : PDH, événement 37 et températures absentes, verdict thermique CPU « non mesurable (pilote PawnIO absent) ». AMD Ryzen bureau et APU pris en charge par AmdSmuBackend avec une version de PM table cartographiée : PPT, TDC, EDC, THM valeur et limite. Autres familles ou versions : N/D, repli sur PDH. Threadripper et EPYC non ciblés. Snapdragon : PDH et événement 37 seulement. NVIDIA : perf cap (Kepler et plus via NVAPI) et PerfDecreaseInfo ; NVML power brake non garanti sur GeForce (limited support, à tester). Intel Arc A/B : raisons complètes par IGCL, dont PSU_ALERT (qu'Arc le remonte réellement n'est pas vérifié). iGPU Intel UHD/Iris : IGCL peut-être partiel (non vérifié). AMD Radeon : aucune raison, inférence seulement ; iGPU Radeon : encore moins. Ventilateurs : cartes mères connues de LibreHardwareMonitor ; portables en lecture seule via ASUS (vérifié), Lenovo, HP, MSI, Acer (expérimentaux) ; autres marques : « non pris en charge ». Journaux Windows : partout (le journal Système est lisible sans administrateur, à vérifier). DEVPKEY PCIe : Windows 10 et 11, selon le pilote. Blocs d'alimentation numériques : très rares et hors V1.

**Risques**

Faux positifs graves : un Ryzen 7000/9000 à 95 °C ou un portable fin qui se bride sont normaux par conception, donc règles par famille et par châssis. Accuser à tort une alimentation, un revendeur ou un fabricant : toujours écrire « indices compatibles avec… » avec un niveau de confiance, jamais « défaillant ». Offsets de PM table faux, donc valeurs absurdes : ne gérer que les versions connues et contrôler la plausibilité. Les modules PawnIO effectivement embarqués par LibreHardwareMonitor 0.9.6 peuvent ne pas avoir exactement la liste blanche de la branche main vérifiée ici (à contrôler). Lire les MSR de tous les cœurs à 10 Hz coûte un peu de CPU et fausse légèrement le bench : lire à 2 Hz pendant la mesure. Journaux Windows : ils peuvent contenir des noms d'applications, à filtrer avant rapport ou envoi IA. IA : injection de prompt par des chaînes matérielles, hallucination de chiffres (validation), refus du modèle (repli), coût, disponibilité réseau, RGPD si on envoie des données identifiantes. Incertains : sémantique des Performance Limit Flags, 0x613 sur les CPU grand public, ratios turbo des hybrides, NVML sur GeForce, PSU_ALERT sur Arc.

**Règles du projet (CLAUDE.md)**

Aucun, tant que tout reste en lecture : MSR, SMU (PM table), SMN, NVAPI, IGCL, ADLX, WMI. Ne jamais tenter d'écrire 0x19C ou 0x1B1 pour effacer les logs (le module le refuse, et ce serait une écriture MSR hors besoin). Règle 5 : sur portable, seulement la lecture WMI existante du contrôleur embarqué, aucun ventilateur forcé « pour aider » pendant un test. Règle 3 : chaque constat « Non mesurable » dit s'il s'agit d'une limite du matériel ou du pilote (ADLX sans raisons de bridage), d'une marque ou d'un modèle non pris en charge (PM table inconnue, portable Dell), ou d'un manque de droits (sans administrateur ou sans PawnIO). Règle 4 : ajouter à CompatibilityViewModel des lignes « Raisons de bridage CPU », « Raisons de bridage GPU », « Journaux Windows », « Liens PCIe ». Règle 6 : chaque règle d'attribution reste « expérimental » tant qu'elle n'est pas validée sur une vraie machine de la famille.

**Questions pour Denis**

- Base de référence : qui la construit ? Toi et tes machines, des partenaires (un revendeur qui mesure ses configurations), une télémétrie opt-in (donc un serveur, un hébergement et une conformité RGPD), ou seulement des attendus internes en V1 ?
- IA : hors ligne seulement en V1, clé fournie par l'utilisateur, ou proxy que tu paies ou que paie une entreprise ? Quel budget par rapport ? Opus 5 (qualité) ou Sonnet 5 (moins cher) ?
- Acceptes-tu la règle « l'IA rédige, elle ne décide jamais du verdict » ?
- Faut-il deux tons de rapport (client et technicien) ou un seul ?
- Acceptes-tu des verdicts formulés prudemment (« indices compatibles avec une alimentation insuffisante », niveau de confiance) plutôt que des affirmations ?
- Doit-on proposer une PR à PawnIO.Modules pour autoriser la lecture de 0x64F/0x6B0/0x6B1 (raisons de bridage Intel fines), sachant que la version embarquée suit LibreHardwareMonitor ?
- Les blocs d'alimentation numériques (Corsair HXi/RMi) valent-ils une extension dédiée plus tard ?

**Sources**

- https://github.com/namazso/PawnIO.Modules/blob/main/IntelMSR.p
- https://github.com/namazso/PawnIO.Modules/blob/main/RyzenSMU.p
- https://github.com/namazso/PawnIO.Modules/blob/main/AMDFamily17.p
- https://github.com/torvalds/linux/blob/master/arch/x86/include/asm/msr-index.h
- https://docs.kernel.org/admin-guide/thermal/intel_thermal_throttle.html
- https://cdrdv2-public.intel.com/825748/253669-sdm-vol-3b.pdf
- https://github.com/hattedsquirrel/ryzen_monitor
- https://docs.nvidia.com/deploy/nvml-api/latest/api/group__nvmlClocksEventReasons.html
- https://docs.nvidia.com/deploy/nvml-api/latest/api/group__nvmlDeviceQueries.html
- https://github.com/NVIDIA/nvapi/blob/main/nvapi.h
- https://github.com/falahati/NvAPIWrapper/blob/master/NvAPIWrapper/Native/GPU/PerformanceDecreaseReason.cs
- https://github.com/intel/drivers.gpu.control-library/blob/master/include/igcl_api.h
- https://github.com/GPUOpen-LibrariesAndSDKs/ADLX/blob/main/SDK/Include/IPerformanceMonitoring.h
- https://learn.microsoft.com/en-us/troubleshoot/windows-client/performance/event-id-41-restart
- https://learn.microsoft.com/en-us/troubleshoot/windows-server/setup-upgrade-and-drivers/event-id-37-windows-kernel-processor-power
- https://learn.microsoft.com/en-us/windows-hardware/drivers/display/timeout-detection-and-recovery
- https://learn.microsoft.com/en-us/answers/questions/3844975/whea-logger-event-id-17-a-corrected-hardware-error
- https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.eventing.reader.eventlogreader
- https://learn.microsoft.com/en-us/windows/win32/api/dxgi/nf-dxgi-idxgiadapter-enumoutputs
- https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-physicalmemory
- https://edc.intel.com/content/www/us/en/design/ipla/software-development-platforms/client/platforms/alder-lake-desktop/atx-version-3-0-multi-rail-desktop-platform-power-supply-design-guide/2.0/dc-voltage-regulation-required/
- https://www.hwinfo.com/forum/threads/incorrect-12v-voltage-reading-on-asus-b450-f-gaming-motherboard.6221/
- https://github.com/liquidctl/liquidctl/blob/main/docs/corsair-hxi-rmi-psu-guide.md
- https://www.kernel.org/doc/Documentation/hwmon/corsair-psu.rst
- https://www.intel.com/content/www/us/en/support/articles/000102331/processors.html
- https://www.pcgamer.com/amd-views-ryzen-5000-cpu-temperatures-up-to-95c-as-typical-and-by-design/
- https://learn.microsoft.com/en-us/windows/win32/winsat/win32-winsat
- https://github.com/anthropics/anthropic-sdk-csharp
- https://www.anthropic.com/legal/commercial-terms
- https://privacy.claude.com/en/articles/7996866-how-long-do-you-store-my-organization-s-data
- https://code.claude.com/docs/en/legal-and-compliance
- https://platform.claude.com/docs/en/about-claude/pricing

### F3 — faisable, taille L

**Approche recommandée**

PARCOURS. Un parcours « Diagnostic complet » guidé, en trois durées. Express, environ 5 min : inventaire, contrôles de configuration, journaux Windows, SMART, courts tests CPU, RAM et disque système. Standard, environ 15 min, recommandé comme cible : Express plus CPU soutenu 3 min, GPU 5 min avec stabilité, test combiné opt-in de 3 à 5 min. Complet, 30 à 60 min, pour la stabilité longue. Ordre de grandeur de référence : UL considère qu'environ 10 min de charge soutenue suffisent en général à révéler un problème de refroidissement (stress test 3DMark : 20 boucles, environ 10 min, seuil de 97 %).

RAPPORT. Un modèle JSON versionné (`DiagnosticReport` v1, System.Text.Json) est la source de vérité. Trois rendus à partir de lui : (1) HTML autonome (CSS et graphiques SVG en ligne, aucun fichier externe ; s'ouvre partout, s'archive, s'envoie par mail) ; (2) PDF produit à partir du même HTML par WebView2, CoreWebView2.PrintToPdfAsync (runtime présent dans Windows 11 et sur la grande majorité des Windows 10 ; en son absence, on propose « ouvrir le HTML et imprimer en PDF ») ; (3) le texte « Copier le rapport » existant. Contenu : identité de la machine (fabricant, modèle, CPU, GPU, RAM, disques, BIOS et date, versions de pilotes) ; synthèse en trois couleurs (Très bien / Normal / Anormal, plus « Non mesuré, et pourquoi ») ; un bloc par composant (score, attendu interne, courbes température, fréquence et puissance, constats avec preuves et action conseillée) ; événements Windows ; conditions du test (secteur, mode d'alimentation, durée, version du bench, PawnIO présent ou non) ; mention « diagnostic indicatif ». Numéros de série exclus par défaut, avec une option pour le technicien (utile au SAV).

EXÉCUTION DEPUIS UNE CLÉ USB. Publication self-contained win-x64 : un dossier copiable tel quel, en single-file si souhaité (WPF l'accepte avec IncludeNativeLibrariesForSelfExtract). Un fichier `portable.flag` à côté de l'exe fait écrire réglages et rapports sur la clé et non dans %LOCALAPPDATA%. L'installeur PawnIO est copié sur la clé et vérifié par sa signature (OfficialInstaller et AuthenticodeVerifier existants). Il est proposé au lancement ; sans lui, le rapport dit « thermique CPU et ventilateurs carte mère non mesurés ». En fin de diagnostic, on propose de le désinstaller ; la ligne de commande de désinstallation reste à confirmer, voir les questions. Signature de code obligatoire pour une diffusion publique : Smart App Control (Windows 11) bloque un exe non signé dès que la prédiction cloud est incertaine, et la réputation SmartScreen se construit sur une identité de signature stable.

SANS DROITS ADMINISTRATEUR. L'exe actuel est en requireAdministrator : il ne démarre pas sans élévation (l'invite UAC demande un compte administrateur). Recommandation : pas de mode dégradé en V1, un technicien a les droits ou demande l'identifiant. Si le besoin se confirme : un lanceur asInvoker séparé, limité aux benchs de débit, aux API GPU constructeur, aux journaux Système et à l'inventaire WMI, qui marque « thermique CPU, SMART détaillé, ventilateurs carte mère : non mesurés (app lancée sans administrateur) ». Cela touche le manifeste et découpe l'app en deux processus : c'est une décision d'architecture.

**Alternatives écartées**

QuestPDF : licence communautaire limitée aux entités de moins de 1 M$ de chiffre d'affaires et interdite aux sociétés cotées et au secteur public, donc licence payante probable pour une diffusion par une entreprise. PDFsharp/MigraDoc (MIT) reste une alternative sans WebView2, mais il faut une seconde mise en page à maintenir. PDF seul : moins pratique que le HTML pour les graphiques et le copier-coller. Installeur MSI pour le mode technicien : laisse plus de traces sur le PC client et va à l'encontre de l'usage clé USB. WebView2 en version fixe embarquée sur la clé : plus de 250 Mo et des ACL à poser sous Windows 10. Mode sans administrateur complet en V1 : oblige à refondre le manifeste et à séparer un helper élevé, pour un bénéfice faible chez un technicien.

**Couverture matérielle**

Windows 10 et 11 x64, tous fabricants. ARM64 : build séparé, sans PawnIO (x64 seulement), donc rapport sans thermique CPU bas niveau. WebView2 absent sur quelques Windows 10 : HTML seulement. Sans PawnIO (installation refusée ou bloquée par un EDR) : pas de températures, fréquences ni puissance CPU, ni de ventilateurs carte mère ; restent GPU, SMART (administrateur), benchs de débit, journaux et configuration. Portables : ventilateurs en lecture seule pour ASUS (vérifié), Lenovo, HP, MSI, Acer (expérimentaux) ; les autres marques sont annoncées « non prises en charge ». Alimentation : jamais mesurée, indices seulement. Durée : le Standard de 15 min vaut pour un PC ; sur HDD ou avec plusieurs disques, compter plus.

**Risques**

Responsabilité : un technicien qui s'appuie sur « Anormal » pour refuser ou accepter un SAV. Il faut des formulations prudentes, la version du bench, les conditions du test et la mention « indicatif ». Traces sur le PC client : pilote PawnIO installé, fichier de bench (à supprimer à coup sûr, même après un plantage). EDR d'entreprise et antivirus tiers : exe venu d'une clé USB, élévation, chargement de pilote, trace ETW noyau et écritures massives peuvent déclencher une alerte ; la signature réduit le risque sans l'éliminer. Signature : Azure Artifact Signing accepte les organisations de l'UE, mais les particuliers seulement aux États-Unis et au Canada (selon les prérequis, à revérifier), donc un particulier en France doit passer par un certificat OV classique ou publier sous une entité. RGPD : un rapport qui contient numéro de série, nom d'utilisateur ou de PC devient une donnée personnelle chez le revendeur. Licences : QuestPDF à éviter. Incertains : option de désinstallation silencieuse de PawnIO_setup.exe, lecture du journal Système sans administrateur, taux réel de présence de WebView2 sur les Windows 10 des clients.

**Règles du projet (CLAUDE.md)**

Aucun. Le mode technicien n'ajoute aucune écriture matérielle. Règles 3 et 4 : le rapport reprend les motifs d'absence de CompatibilityViewModel (limite du matériel, marque non prise en charge, sans administrateur ou sans PawnIO) et doit en être une extension, pas une copie divergente. Même source de données, avec « Copier le rapport » qui devient un rendu du même modèle. Règle 6 : chaque verdict qui repose sur une règle non vérifiée affiche « expérimental » dans le rapport. Règle 5 : pendant le parcours sur portable, aucune écriture dans le contrôleur embarqué, même si le test chauffe.

**Questions pour Denis**

- La cible est-elle un vrai partenariat (LDLC ou autre revendeur) ou un persona ? Cela change la signature, la licence, le support et le besoin d'un logo ou d'un en-tête personnalisable.
- Sous quelle entité signer l'app (toi en particulier, ta société, XEFI) ? Un particulier en France n'est pas éligible à Azure Artifact Signing en public trust.
- Acceptes-tu d'installer le pilote PawnIO sur le PC d'un client depuis la clé, et de le désinstaller en fin de diagnostic ? (La commande de désinstallation de l'installeur reste à vérifier.)
- Faut-il un mode sans administrateur, qui impose de découper l'app en deux processus, ou le mode technicien exige-t-il l'administrateur ?
- Le PDF est-il obligatoire, ou le HTML autonome suffit-il en V1 ?
- Le rapport doit-il contenir les numéros de série (utile au SAV, mais donnée personnelle) ? Par défaut non ?
- Quelle durée cible : Standard de 15 min comme proposé, ou plus court (5 min) pour un comptoir de magasin ?

**Sources**

- https://benchmarks.ul.com/news/check-your-pcs-stability-with-new-3dmark-stress-tests
- https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution
- https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2.printtopdfasync
- https://learn.microsoft.com/en-us/microsoft-edge/webview2/how-to/print
- https://www.questpdf.com/license/community.html
- https://github.com/empira/PDFsharp/blob/master/LICENSE
- https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview
- https://learn.microsoft.com/en-us/windows/apps/develop/smart-app-control/code-signing-for-smart-app-control
- https://support.microsoft.com/en-us/windows/security/threat-malware-protection/smart-app-control-frequently-asked-questions
- https://learn.microsoft.com/en-us/azure/artifact-signing/quickstart
- https://learn.microsoft.com/en-us/azure/artifact-signing/faq
- https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/pull/1857
- https://github.com/vlinkevych/megamini-g1-toolkit/issues/1
- https://github.com/namazso/PawnIO.Setup
- https://learn.microsoft.com/en-us/troubleshoot/windows-client/performance/event-id-41-restart

### Remarques transverses

- Découpage conseillé en étapes, chacune livrable seule. (1) Socle de signaux en lecture seule : échantillonneur de bridage CPU Intel (MSR autorisés) et AMD (PM table), raisons de bridage GPU (NVAPI PerfDecreaseInfo, IGCL throttleReasons à brancher dans IgclGpuBackend.GetActiveLimit), journaux Windows, liens PCIe, contrôles de configuration, et nouvelles lignes de CompatibilityViewModel. Taille M, utile dès l'onglet Monitoring, sans bench. (2) Worker de charge et bench CPU/RAM (L). (3) Bench disque (M). (4) Bench GPU ComputeSharp et test combiné (M à L). (5) Moteur de règles, modèle de rapport JSON, rendu HTML puis PDF (L). (6) Mode technicien : parcours guidé, exécution depuis une clé USB, PawnIO sur la clé, signature (M). (7) Couche IA optionnelle : SDK Anthropic, schéma, validation, confidentialité (S à M).
- Limite dure vérifiée dans le code source du module IntelMSR de PawnIO (branche main) : MSR_PERF_LIMIT_REASONS (0x64F) et les registres GFX/RING (0x6B0/0x6B1) ne sont pas lisibles, et aucun registre thermique n'est inscriptible. Le diagnostic Intel reposera sur 0x19C/0x1B1 (bits d'état), 0x610/0x614/0x613 et les fréquences. Les modules réellement embarqués viennent de LibreHardwareMonitor 0.9.6 : vérifier que leur liste blanche est identique.
- Nouvelles dépendances proposées : ComputeSharp (MIT, net8.0), Microsoft.Web.WebView2 (Microsoft), Anthropic (SDK C# officiel, GA ; licence non vérifiée ici). PDFsharp/MigraDoc (MIT) en option. QuestPDF à écarter pour raison de licence. Aucun outil tiers propriétaire embarqué, et aucun score ni nom de benchmark commercial (3DMark, PassMark, Geekbench) utilisé comme référence. S'inspirer de leur méthode (97 % de stabilité) est permis.
- Honnêteté produit : le cœur du diagnostic est un système expert déterministe. Seule la rédaction peut être « IA ». Présenter le verdict comme décidé par l'IA serait trompeur et plus fragile.
- C'est la première fonction de l'app qui chauffe volontairement le matériel. Il faut donc un bouton d'arrêt global, un watchdog côté app (le worker est tué si l'app ne reçoit plus de capteurs), un consentement explicite pour le test combiné, et aucun lancement automatique (pas de bench au démarrage ni planifié).
- Réutilisables tels quels : HardwareMonitorService (capteurs), BatteryReader (secteur et décharge), DiskHealthService (SMART et MSFT_PhysicalDisk), FanInventory et EmptyHeaderDetector, NvApiGpuBackend.GetActiveLimit, IntelPowerLimitBackend et AmdSmuBackend (PL1/PL2, PPT via PM table), MachineInfo (portable ou bureau, cartes graphiques), OfficialInstaller et AuthenticodeVerifier (PawnIO sur la clé), CompatibilityViewModel (base du rapport).
- La signature de code conditionne tout le reste pour une diffusion publique : Smart App Control, SmartScreen, EDR d'entreprise, et aussi l'exe worker. À trancher avant de publier le mode technicien.
- Incertitudes signalées, non tranchées ici : MSR 0x613 sur CPU grand public ; ratios turbo 0x1AD sur hybrides ; NVML sur GeForce ; PSU_ALERT réellement remonté par Arc ; sémantique des Performance Limit Flags PDH ; SM 6.0 sur iGPU anciens ; journal Système lisible sans administrateur ; commande de désinstallation de PawnIO ; tous les seuils numériques (CV, ΔT, 60 % de bande passante, ±20 %) ; coût réel par rapport IA. Tous doivent être mesurés sur de vraies machines avant de quitter le statut « expérimental ».


## F4 / F5 / F6 : overclocking automatique « par IA » (GPU et CPU, 3 profils), core parking avec visuel par cœur, finalisation de l'OC GPU AMD Radeon et Intel Arc

### F4 — partiel, taille XL

**Approche recommandée**

Recommandation : faire un moteur de recherche déterministe et borné, pas un LLM qui choisit des valeurs. « IA » = recherche par paliers, règles et marges fixes ; un LLM éventuel sert seulement à rédiger l'explication du rapport. En v1 : GPU sur les 3 marques et CPU AMD (Curve Optimizer négatif). Pas d'OC CPU Intel.

1) GPU (priorité 1), en passant par les backends existants (IGpuTuningBackend) :
- NVIDIA : l'OC Scanner n'est pas dans le NVAPI public (la doc publique ne documente que NvAPI_GPU_GetPstates20, même pas Set). Algorithme maison sur les deltas P0 de SetPstates20, déjà utilisés par NvApiGpuBackend.
- AMD : IADLXGPUAutoTuning existe (IGPUTuningServices : IsSupportedAutoTuning = emplacement 6, GetAutoTuning = 12 ; méthodes IsSupported/IsCurrent/Start pour UndervoltGPU, OverclockGPU et OverclockVRAM, asynchrones avec IADLXGPUAutoTuningCompleteListener). Mais il est opaque (aucune valeur rendue) et exclusif du réglage manuel. À proposer seulement comme bouton séparé ; les 3 profils se calculent avec l'algorithme maison via ManualGraphicsTuning2 et VRAMTuning2.
- Intel Arc : IGCL n'a aucune fonction d'auto-OC. Algorithme maison via ctlOverclock*.
- Charge de test maison, dans un processus enfant (PCPerfSuite.exe --stress-gpu), pour qu'un TDR ne tue pas l'interface. Compute D3D12 en C# avec ComputeSharp (MIT), ou D3D11 via Vortice.Windows (MIT) :
  (a) noyau FMA/entier déterministe, comparé au résultat de référence calculé d'abord aux fréquences d'origine ;
  (b) motifs VRAM écriture/relecture et mesure de bande passante ;
  (c) cycles charge/repos de 1 s pour les transitions de boost.
- Critères d'échec :
  - erreur de calcul ;
  - DXGI_ERROR_DEVICE_REMOVED ;
  - journal Système : Display 4101 (TDR), WHEA-Logger 17/18/19, nvlddmkm/amdkmdag ;
  - au démarrage suivant : Kernel-Power 41, EventLog 6008, BugCheck 1001 ;
  - fréquence qui s'effondre ;
  - pour la VRAM : baisse du débit. En GDDR6/6X, l'EDC retransmet les données en erreur, donc on cherche le pic de bande passante, pas l'absence d'artefacts.
- Recherche : cœur par pas de 15 MHz chez NVIDIA (sinon le pas de la plage ADLX/IGCL), 90 s par palier, dichotomie au premier échec. VRAM par pas de 50 MHz jusqu'au pic de débit. Au moins 60 s entre deux échecs GPU : 5 TDR en 1 minute donnent un écran bleu 0x117.

2) CPU AMD, par le module RyzenSMU embarqué (ioctl_send_smu_command, commandes 0x00 à 0xFF) :
- Curve Optimizer négatif par cœur. Commandes relevées dans ZenStates-Core :
  - Zen 3 : RSMU SetDldoPsmMargin 0x0A, SetAll 0x0B, Get 0x7C ; MP1 0x35/0x36 ;
  - Zen 4 et Zen 5 : RSMU 0x06/0x07, Get 0xD5 ; MP1 0x35/0x36 ;
  - APU, d'après RyzenAdj : Renoir/Lucienne/Cezanne MP1 0x55 (tous cœurs) / 0x54 (par cœur) ; Rembrandt à Strix MP1 0x4C/0x4B ; Dragon/Fire Range PSMU 0x07/0x06.
- Masque de cœur (Zen 3+) : (ccd<<28)|((core%8)<<20). Les cœurs désactivés se lisent dans les fusibles (0x5D218+…, 0x30081800+…) via AMDFamily17.ioctl_read_smn, qui est embarqué et sans liste blanche.
- Relecture systématique par GetDldoPsmMargin, comme TrySetPowerLimits.
- Réglage volatil : perdu au redémarrage, et à réappliquer au réveil d'après les guides PBO2 Tuner. C'est le meilleur filet de sécurité.
- Test par cœur : thread épinglé (SetThreadSelectedCpuSets), noyau AVX2/FMA System.Runtime.Intrinsics à résultat vérifié, alterné avec des phases légères (c'est là que la CO casse), plus surveillance WHEA 19.

3) Définition des 3 profils (L = dernier palier réussi pendant la recherche commune) :
- SÛR :
  - GPU cœur = min(L−60 MHz ; 50 % du gain) ; VRAM = pic − 2 pas ; tension et limite de puissance d'origine ;
  - CPU : CO = L+5 (vers 0), PPT d'origine ;
  - validation 60 min GPU (20 compute vérifié, 20 charge chaude, 10 cycles charge/repos, 10 repos→charge) ; CPU 10 min par cœur + 30 min tous cœurs ;
  - zéro erreur, zéro TDR, zéro WHEA même corrigée, T < limite − 5 °C ;
  - libellé « aucun plantage constaté + marge large », jamais « garanti ».
- CLASSIQUE : GPU L−30 MHz, VRAM pic − 1 pas, limite de puissance max autorisée (bureau seulement) ; CO = L+3 ; validation 20 à 30 min.
- AGRESSIF : GPU L−15 MHz, VRAM au pic, limites de puissance et de température au max ; CO = L+1, PPT relevé tant que T < 90 °C ; validation 10 min ; mention « stabilité non garantie ».
- Durées réalistes :
  - GPU : 35 à 60 min de recherche + environ 1h30 de validations. Référence : l'auto-tuning de la NVIDIA App annonce 10 à 20 min.
  - CPU AMD : 3 à 5 min par palier × environ 5 paliers × N cœurs, soit 2 à 3 h pour 8 cœurs et 4 à 6 h pour 16. Référence : l'auto-CO par cœur de Ryzen Master prend 1h45 à 2 h sur un 5950X selon des utilisateurs. Prévoir un mode nuit.

4) Retour arrière :
- Journal en écriture directe (FileOptions.WriteThrough + Flush(true)) avant chaque palier : composant, valeurs, heure, EnCours → Terminé.
- Au lancement, si un palier est resté EnCours :
  (1) tout remettre d'origine AVANT le reste (ADLX ResetToFactory, ctlOverclockResetToDefault, deltas NVAPI à 0), car ADLX et IGCL peuvent persister dans le pilote ;
  (2) lire le journal d'événements depuis l'heure notée pour qualifier l'incident ;
  (3) marquer le palier échoué ;
  (4) proposer de reprendre, via la StartupTask existante avec --reprendre-oc et jamais avec la valeur fautive.
- Chien de garde : même exe lancé avec --watchdog, recevant un battement du processus de test ; sans battement pendant plus de 10 s, retour d'origine.
- Garde-fou thermique (EmergencyTempC) actif avec des seuils plus bas pendant le test.
- « Appliquer au démarrage » : attendre 90 s. Si la session précédente s'est terminée par un arrêt inattendu moins de 30 min après application, ne pas réappliquer, descendre d'un profil et prévenir.
- « Redémarrage = retour d'origine » : vrai pour le SMU AMD, les MSR Intel et NVAPI (pour NVAPI, l'état après un TDR reste à confirmer). Pas garanti pour ADLX et IGCL : c'est le journal qui couvre ces deux cas.

**Alternatives écartées**

- OC Scanner NVIDIA : intégré au pilote depuis la série 456 et exposé par NVAPI aux éditeurs partenaires (MSI, EVGA), mais absent du SDK public. L'accès sous NDA n'est pas garanti.
- IADLXGPUAutoTuning pour les 3 profils : un seul résultat opaque, exclusif du manuel. Gardé comme bouton optionnel.
- Ratios turbo Intel (MSR 0x1AD) : lecture seule dans IntelMSR, même en version courante.
- Undervolt / OC Intel par l'OC mailbox 0x150 : format non documenté par Intel (rétro-ingénierie : ThrottleStop, intel-undervolt), protection UVP active par défaut en 12e gen et plus, 0x150 intercepté par VBS/HVCI, écriture absente du module embarqué.
- P-states AMD (MSR_PSTATE, inscriptible via AMDFamily17) : OC fixe tous cœurs qui supprime le boost ; moins bon et plus risqué que la CO.
- Boost override / PBO scalar (Zen 4 RSMU 0x70 / 0x5B) : commandes connues, mais effet et verrous BIOS non vérifiés. À voir en v2.
- Embarquer Prime95, y-cruncher, OCCT ou CoreCycler : licences (CoreCycler est en CC BY-NC-SA 4.0) et redistribution. Les proposer plutôt en liens (F10).
- Lier ZenStates-Core : GPL-3.0. Ne reprendre que les faits (numéros de commandes, adresses).
- LLM qui décide des valeurs : non reproductible, non bornable.

**Couverture matérielle**

- GPU NVIDIA bureau Kepler→RTX 50 (P-States 2.0) : oui. GPU NVIDIA portable : décalages souvent acceptés dans une plage réduite, limite de puissance refusée (Dynamic Boost), ventilateurs gérés par l'EC.
- AMD Radeon post-Navi (ManualGraphicsTuning2) : oui si ADLX le déclare. RDNA4 fonctionne en décalage, RDNA1 à 3 en valeurs absolues. Pré-Navi (Polaris/Vega, Tuning1) : non couvert par le backend actuel. iGPU Radeon : rarement.
- Intel Arc dédiées A et B : oui, après la renonciation de garantie. iGPU Intel : non.
- CPU AMD bureau : Curve Optimizer à partir de Zen 3 (Vermeer, dont le 5800X3D), puis Zen 4 et Zen 5. Zen 2 (Matisse) n'a pas de CO malgré les identifiants hérités dans ZenStates.
- APU AMD : CO par MP1 annoncée par RyzenAdj de Renoir à Strix Halo, non vérifiée ; un firmware OEM peut l'ignorer.
- CPU Intel : aucun OC, seulement les limites de puissance, et elles demandent un module plus récent (voir les remarques transverses).
- Snapdragon : rien, firmware verrouillé.
- Administrateur et PawnIO requis. Le fonctionnement du SMU AMD sous VBS reste à vérifier : VBS est actif sur le PC de Denis (Win32_DeviceGuard.VirtualizationBasedSecurityStatus = 2).

**Risques**

- Plantages provoqués volontairement : travail non enregistré perdu, corruption de fichiers possible (rare) lors d'un arrêt brutal. Consentement explicite obligatoire et pas de test pendant une mise à jour Windows.
- Dégradation : sur les Core 13e/14e gen de bureau, le « Vmin shift » vient de tensions et températures élevées (corrigé par les microcodes 0x125, 0x129, 0x12B puis 0x12F) → OC positif proscrit. Côté AMD, la CO négative baisse la tension (risque faible) mais un PPT relevé chauffe davantage.
- Garantie : renonciation Intel obligatoire pour les Arc ; l'OC Ryzen est hors spécification.
- Outils concurrents qui écrasent les réglages : Afterburner, Adrenalin, Ryzen Master, auto-tuning de la NVIDIA App, Armoury Crate.
- Mauvais mappage cœur → index SMU sur les CPU à cœurs désactivés (5600X, 7600, 9600X) : CO appliquée au mauvais cœur si les fusibles ne sont pas lus.
- Une validation « sûre » ne couvre pas toutes les charges : compilation de shaders DX12, AVX-512.
- Antivirus : moins exposé qu'avec WinRing0 (PawnIO est signé), mais un processus de stress combiné à des écritures SMU peut déclencher des heuristiques.
- TDR répétés → écran bleu 0x117.

**Règles du projet (CLAUDE.md)**

- Règle 5 (EC des portables en lecture seule) : pendant le stress, l'app ne peut pas forcer les ventilateurs d'un portable. Sur portable donc : pas de relèvement des limites de puissance ou de température, arrêt thermique plus bas, et côté CPU seulement la CO négative. Aucune écriture EC n'est nécessaire.
- Règle 2 : le test fait planter la machine exprès. Aucune méthode Core ne doit lever pour autant (DeviceRemoved attrapé dans le processus enfant), et un journal de reprise est indispensable.
- Règle 6 : chaque couple marque × génération reste « expérimental » tant qu'il n'est pas vérifié ; la matrice est large.
- Règle 4 : ajouter au diagnostic l'état VBS, le microcode (registre HKLM\HARDWARE\DESCRIPTION\System\CentralProcessor\0 « Update Revision », lisible), les ioctl des modules chargés, la prise en charge CO / ADLX AutoTuning et le dernier incident OC.
- La conception actuelle de PawnIoDriver (« modules pris dans LHM, rien de plus à livrer ») empêche toute écriture MSR Intel.

**Questions pour Denis**

- Périmètre v1 : GPU (NVIDIA, AMD, Intel) plus Curve Optimizer AMD seulement, sans OC CPU Intel ?
- Qu'attends-tu de l'« IA » : un moteur de recherche déterministe et borné (recommandé), avec ou sans explication par LLM ? Si LLM, en ligne (coût, confidentialité) ou pas du tout ?
- Acceptes-tu que la recherche agressive provoque volontairement des écrans bleus ou des redémarrages (avec consentement et sauvegarde conseillée) ? En mode technicien (F3), l'OC doit-il même être proposé ?
- Durées acceptables (GPU environ 1h30 à 2h, CPU AMD 2 à 6 h) ? Faut-il un mode nuit ?
- Portables : OC GPU autorisé en mode restreint, ou interdit ?
- Embarquer les modules officiels PawnIO.Modules ≥ 0.2.4 (LGPL-2.1, signés) au lieu de ceux de LHM ?
- Relever les limites de puissance dans les profils classique et agressif, sachant que c'est lié au diagnostic d'alimentation de F2 ?
- Noyaux de stress maison (ComputeSharp/Vortice + intrinsics C#), ou téléchargement d'outils tiers ?
- Proposer l'auto-tuning ADLX comme bouton séparé pour les Radeon ?

**Sources**

- https://archive.docs.nvidia.com/gameworks/content/gameworkslibrary/coresdk/nvapi/group__gpupstate.html
- https://github.com/NVIDIA/nvapi
- https://www.tomshardware.com/news/nvidias-oc-scanner-receives-major-changes-for-rtx-3000-series-gpus
- https://www.nvidia.com/en-us/geforce/news/nvidia-app-beta-update-av1-performance-tuning/
- https://github.com/GPUOpen-LibrariesAndSDKs/ADLX/blob/main/SDK/Include/IGPUAutoTuning.h
- https://github.com/GPUOpen-LibrariesAndSDKs/ADLX/blob/main/SDK/Include/IGPUTuning.h
- https://gpuopen.com/manuals/adlx/adlx-sdk-references/adlx-interfaces/gpu-tuning/iadlxgpuautotuning/
- https://intel.github.io/drivers.gpu.control-library/Control/api.html
- https://raw.githubusercontent.com/namazso/PawnIO.Modules/main/RyzenSMU.p
- https://raw.githubusercontent.com/namazso/PawnIO.Modules/main/AMDFamily17.p
- https://raw.githubusercontent.com/namazso/PawnIO.Modules/main/IntelMSR.p
- https://raw.githubusercontent.com/namazso/PawnIO.Modules/0.2.2/IntelMSR.p
- https://api.github.com/repos/namazso/PawnIO.Modules/releases
- https://github.com/irusanov/ZenStates-Core/blob/master/Hardware/Smu/Settings/Zen2Settings.cs
- https://github.com/irusanov/ZenStates-Core/blob/master/Hardware/Smu/Settings/Zen3Settings.cs
- https://github.com/irusanov/ZenStates-Core/blob/master/Hardware/Smu/Settings/Zen4Settings.cs
- https://github.com/irusanov/ZenStates-Core/blob/master/Hardware/Smu/Settings/Zen5Settings.cs
- https://github.com/irusanov/ZenStates-Core/blob/master/Cpu.cs
- https://github.com/FlyGoat/RyzenAdj/blob/master/lib/api.c
- https://github.com/sp00n/corecycler
- https://github.com/PrimeO7/How-to-undervolt-AMD-RYZEN-5800X3D-Guide-with-PBO2-Tuner
- https://docs.amd.com/r/en-US/68886-ryzen-master-user-guide/Curve-Optimizer
- https://hardwarecanucks.com/forum/threads/ryzen-master-auto-curve-optimizer.84738/
- https://www.intel.com/content/www/us/en/support/articles/000094219/processors.html
- https://www.intel.com/content/www/us/en/support/articles/000093813/processors/processor-utilities-and-programs.html
- https://www.intel.com/content/www/us/en/security-center/advisory/intel-sa-00289.html
- https://github.com/kitsunyan/intel-undervolt
- https://learn.microsoft.com/en-us/answers/questions/4105727/undervolting-with-hypervisor-virtual-machine-for-w
- https://learn.microsoft.com/en-us/windows/security/hardware-security/enable-virtualization-based-protection-of-code-integrity
- https://community.intel.com/t5/Blogs/Tech-Innovation/Client/Intel-Core-13th-and-14th-Gen-Desktop-Instability-Root-Cause/post/1633239
- https://www.theregister.com/2024/09/26/intel_0x12b_raptor_lake/
- https://community.intel.com/t5/Mobile-and-Desktop-Processors/Intel-Core-13th-and-14th-Gen-Vmin-Shift-Instabilty-Update-New/m-p/1686948
- https://www.tomshardware.com/pc-components/cpus/msi-details-how-to-make-your-intel-cpu-run-cooler-without-losing-performance-recommends-disabling-cep
- https://learn.microsoft.com/en-us/windows-hardware/drivers/display/timeout-detection-and-recovery
- https://learn.microsoft.com/en-us/answers/questions/2660096/event-19-whea-logger-a-connected-hardware-error-ha
- https://www.rambus.com/interface-ip/gddr/gddr6-controller/

### F5 — faisable, taille M

**Approche recommandée**

Lecture par cœur, sans droits administrateur :
- GetSystemCpuSetInformation (kernel32) donne, pour chaque processeur logique : Parked, EfficiencyClass (cœurs P/E), CoreIndex (paires SMT), LastLevelCacheIndex (regroupement par CCD/L3) et NumaNodeIndex.
- Vérifié sur le PC de Denis (i5-13500T, Windows 11 22631) : 6 cœurs P de classe 1 avec SMT, 8 cœurs E de classe 0, et au repos les seconds fils SMT sont parqués.
- En complément, par PdhAddEnglishCounterW (déjà utilisé par PdhCounterSampler) :
  - « \Processor Information(*)\Parking Status » : 0/1 par instance, vérifié localement ; le nom français est « État de parcage », d'où l'usage des noms anglais ;
  - « % Processor Utility » par instance, même mesure que le Gestionnaire des tâches ;
  - « % Processor Performance » pour la fréquence relative.
- Échantillonner à 1 s dans la SamplingLoop existante et afficher le % du temps parqué sur la fenêtre, car l'état bascule vite.

Visuel :
- Tuiles regroupées par L3/CCD puis par classe d'efficacité (libellés P/E seulement s'il y a au moins 2 classes), fils SMT côte à côte, remplissage = charge, hachures = parqué.
- X3D détecté par des tailles de L3 différentes (GetLogicalProcessorInformationEx, RelationCache), libellé « CCD avec 3D V-Cache ».

Réglages, sur le plan actif, en AC et DC, via PowerWriteACValueIndex / PowerWriteDCValueIndex + PowerSetActiveScheme (ou powercfg comme PowerPlanService) :
- CPMINCORES 0cc5b647-c1df-4637-891a-dec35c318583 et CPMAXCORES ea062031-0e34-4ff1-9b6d-eb1059334028 ;
- sur les CPU hybrides, aussi CPMINCORES1 …318584 et CPMAXCORES1 …334029 ;
- en avancé sur les hybrides : SCHEDPOLICY 93b8b6dc-0698-4d1c-9ee4-0644e900c85d (0 tous, 1 performants, 2 préférer performants, 3 efficaces, 4 préférer efficaces, 5 auto), SHORTSCHEDPOLICY bae08b81-2d5e-4688-ad6a-13243356654b, HETEROPOLICY 7f2f5cfa-f10c-4823-b5e1-e93ae85f46b5.
- Valeurs d'origine capturées puis restaurées, comme les tweaks existants.
- Préréglages : « Windows (défaut) », « Tous les cœurs actifs » (CPMINCORES = CPMINCORES1 = 100 ; d'après Microsoft, 100 % désactive le parking), « Économie » (CPMAXCORES réduit).

À corriger dans l'existant : le tweak « core-parking » de WindowsPerformanceSettingsService n'écrit que CPMINCORES. Sur la machine hybride de Denis, CPMINCORES1 vaut 0, séparément de CPMINCORES = 4. Les cœurs de classe 1 (P chez Intel) continuent donc probablement d'être parqués. À confirmer sur machine.

X3D bi-CCD : si le service amd3dvcache existe, ne pas proposer « tous les cœurs actifs » sans avertissement ; se contenter d'afficher l'état et la préférence.

**Alternatives écartées**

- ETW Microsoft-Windows-Kernel-Processor-Power : plus lourd, demande l'administrateur, inutile pour un affichage à 1 s.
- CPPC / HWP : donnent des préférences de performance, pas l'état parqué.
- Écriture directe dans le registre des plans : l'API powrprof suffit.
- Modifier les profils PPM GameMode / LowLatency / Constrained : possible seulement par package de provisioning OEM, hors portée.
- Clé amd3dvcache\Preferences en écriture : format non documenté par AMD, lecture seule tout au plus.

**Couverture matérielle**

- Lecture : Windows 10 et 11, x64 et ARM64 (API CPU sets depuis Windows 10 ; Microsoft marque CPMinCores « Supported » sur Arm sous Windows 11). Aucun pilote nécessaire.
- Intel hybride 12e gen et plus : 2 classes. Meteor Lake et Lunar Lake ont des cœurs LP-E ; le nombre de valeurs d'EfficiencyClass y est à vérifier.
- AMD : une seule classe en général ; Strix Point (Zen 5 + Zen 5c) à vérifier.
- X3D bi-CCD : le parking du CCD sans V-Cache en jeu dépend du pilote AMD 3D V-Cache Performance Optimizer, de Game Mode / Game Bar et du plan Équilibré.
- Snapdragon X : lecture probable, non vérifiée ; marquer expérimental.
- Écriture du plan : faite en administrateur, comme le reste de l'app.

**Risques**

- Faibles côté matériel.
- « Tous les cœurs actifs » augmente la consommation et la chauffe au repos, surtout sur portable.
- Sur X3D bi-CCD, forcer le dé-parking fait perdre des performances en jeu (consensus communautaire, non documenté par AMD, à vérifier).
- Les outils OEM (Armoury Crate, Vantage) et le plan « Performances ultimes » écrasent ou dupliquent les valeurs.
- Les profils PPM internes (GameMode, Constrained) peuvent passer outre.

**Règles du projet (CLAUDE.md)**

Aucun, pas d'accès à l'EC. À ajouter au diagnostic (règle 4) : nombre de classes, API CPU sets disponible, valeurs CPMIN/MAX(1) du plan actif, présence d'amd3dvcache. Afficher N/D avec une explication si l'API est absente (règle 3).

**Questions pour Denis**

- Intégrer le parking aux profils (F18/F19), ou le garder comme réglage indépendant ?
- Sur X3D bi-CCD : masquer « tous les cœurs actifs », ou le permettre avec un avertissement ?
- Exposer les valeurs secteur et batterie séparément, ou une seule valeur ?
- Corriger tout de suite le tweak existant pour qu'il écrive aussi CPMINCORES1 ?

**Sources**

- https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-system_cpu_set_information
- https://learn.microsoft.com/en-us/windows-hardware/customize/power-settings/configure-processor-power-management-options
- https://learn.microsoft.com/en-us/windows-hardware/customize/power-settings/options-for-core-parking-cpmincores
- https://learn.microsoft.com/en-us/windows-hardware/customize/power-settings/configuration-for-hetero-power-scheduling-schedulingpolicy
- https://www.phoronix.com/news/AMD-3DV-Cache-Optimizer-Linux
- https://github.com/cocafe/vcache-tray
- https://hothardware.com/news/amds-chipset-driver-for-v-cache-cpus
- https://community.amd.com/t5/processors/7950x3d-ryzen-master-and-core-parking-problem/m-p/591237

### F6 — partiel, taille L

**Approche recommandée**

Lecture retenue : « toutes marques de cartes AMD, Intel » = cartes graphiques Radeon et Arc (à confirmer). Le code existe déjà (AdlxGpuBackend, IgclGpuBackend). Il reste à le vérifier sur de vraies cartes, génération par génération, et à ne lever le bandeau expérimental qu'une fois vérifié (drapeau par génération, comme ILaptopFanProvider.IsVerified).

Outil intégré « vérification matériel », exporté en rapport : pour chaque réglage, lire, écrire une petite valeur, relire, remettre d'origine, et contrôler IsAtFactory.

Points à vérifier sur ADLX :
1) Emplacements de vtable de IGPUTuningServices : ceux du code (8 à 11, 14 à 17) sont conformes à l'en-tête officiel.
2) RDNA1 à 3 en valeurs absolues, RDNA4 en décalages (l'en-tête dit « Start from Navi4+ … offset ») : conversion vers un décalage à contrôler.
3) Plages et pas (IntRange.Step).
4) VRAM : fréquence max ; le réglage « memory timing » n'est pas géré.
5) Limite de puissance en %.
6) Courbe de ventilateur, zero RPM, restauration de la courbe d'origine.
7) Persistance au redémarrage et après plantage (Adrenalin affiche alors « Default Radeon WattMan settings have been restored »).
8) ADLX_RESET_NEEDED quand l'auto-tuning Adrenalin est actif.
9) Coexistence avec Adrenalin ouvert.
10) Choix du bon GPU sur APU + carte dédiée.
11) Est-ce que RX 5000 renvoie Tuning2 ? Le sample ADLX parle seulement de « pre-Navi » pour Tuning1 et de « post-Navi » pour Tuning2.

Ajouts ADLX optionnels : bouton auto-tuning (IADLXGPUAutoTuning) et préréglages IADLXGPUPresetTuning.

Points à vérifier sur IGCL :
1) Persistance de ctlOverclockWaiverSet.
2) Choix entre fonctions V1 et V2, et unités annoncées par ctlOverclockGetProperties.
3) Tension : SkatterBencher signale qu'une erreur de décimale peut permettre plus de 2 V. Bornes strictes et contrôle d'unité indispensables.
4) Vitesse mémoire V2 (Battlemage) et CTL_RESULT_ERROR_CORE_OVERCLOCK_RESET_REQUIRED.
5) ctlOverclockResetToDefault.
6) Ventilateurs en vitesse fixe puis mode par défaut, sur les cartes partenaires.
7) Persistance au redémarrage.
8) Coexistence avec Intel Graphics Software.

Cartes minimales :
- AMD : RX 6600/6700 (RDNA2), RX 7600/7800 XT (RDNA3), RX 9060/9070 (RDNA4), si possible RX 5700 (RDNA1) et un portable Radeon.
- Intel : Arc A750/A770 (Alchemist), B580 (Battlemage).
- Pilotes : le dernier WHQL et un pilote d'environ 12 mois.

**Alternatives écartées**

- ADL (ancienne API AMD) : remplacée par ADLX pour le réglage.
- Tables PowerPlay dans le registre (façon MorePowerTool) : redémarrage du pilote nécessaire, risque de carte inutilisable, hors de ce qu'AMD expose.
- Intel : aucune autre API publique que IGCL.

**Couverture matérielle**

- AMD post-Navi (ManualGraphicsTuning2) : oui, si ADLX déclare le réglage.
- Pré-Navi Polaris/Vega (Tuning1) : non géré aujourd'hui.
- iGPU Radeon et Radeon portables : rarement.
- Intel Arc dédiées Alchemist et Battlemage : oui.
- iGPU Intel (Xe, Arc intégrés) : non, IGCL répond « overclock non supporté ».
- Version minimale d'Adrenalin pour ADLX et d'Intel pour les fonctions V2 : non trouvée dans les sources consultées, à relever pendant les tests.

**Risques**

- Vtable erronée = plantage natif, déjà atténué par AdlxProbeGuard.
- Tension absolue hors plage sur RDNA1 à 3.
- Erreur d'unité de tension chez Intel.
- Réglages persistants dans le pilote → carte bloquée sur un OC instable au démarrage (boucle de TDR) : le journal et la remise d'origine au lancement décrits en F4 couvrent ce cas.
- Adrenalin ou Intel Graphics Software qui écrasent les réglages.
- Renonciation de garantie Intel : elle engage l'utilisateur.

**Règles du projet (CLAUDE.md)**

Règle 6 : c'est le cœur de F6, tout reste « expérimental » jusqu'à vérification réelle, par génération. Pas de conflit avec l'EC.

**Questions pour Denis**

- F6 vise-t-il bien les cartes graphiques Radeon et Arc, et non les cartes mères ?
- Comment accéder aux cartes : achat, prêt, testeurs bêta qui renvoient le rapport de vérification ?
- Faut-il prendre en charge les Radeon pré-Navi (ManualGraphicsTuning1) ?
- Exposer l'auto-tuning et les préréglages ADLX ?
- Quel texte afficher pour la renonciation de garantie Intel ?

**Sources**

- https://github.com/GPUOpen-LibrariesAndSDKs/ADLX/blob/main/SDK/Include/IGPUTuning.h
- https://github.com/GPUOpen-LibrariesAndSDKs/ADLX/blob/main/SDK/Include/IGPUManualGFXTuning.h
- https://github.com/GPUOpen-LibrariesAndSDKs/ADLX/blob/main/Samples/CPP/GPUTuning/ManualGraphicsTuning/mainManualGraphicsTuning.cpp
- https://github.com/GPUOpen-LibrariesAndSDKs/ADLX/blob/main/SDK/Include/IGPUAutoTuning.h
- https://community.amd.com/t5/drivers-software/default-radeon-wattman-settings-have-been-restored-due-to/td-p/145909
- https://intel.github.io/drivers.gpu.control-library/Control/api.html
- https://github.com/intel/drivers.gpu.control-library
- https://skatterbencher.com/arc-oc-tool/

### Remarques transverses

- Constat important, en dehors du sujet mais bloquant pour tout réglage CPU Intel. Le module IntelMSR embarqué dans LibreHardwareMonitorLib 0.9.6 n'expose que ioctl_read_msr. Vérifié localement en lisant la ressource LibreHardwareMonitor.Resources.PawnIo.IntelMSR.bin du paquet NuGet (chargement reflection-only), et sur le tag 0.2.2 de PawnIO.Modules. Or IntelPowerLimitBackend écrit PL1/PL2 par ioctl_write_msr : sur tout Intel, cette écriture échoue donc très probablement aujourd'hui. L'écriture MSR (0x610 et OC mailbox 0x150) n'apparaît que dans PawnIO.Modules 0.2.4 (17/03/2026) ; 0x601, 0x607/0x608 et 0x1A4 viennent après. LHM master l'intègre (commits 4058577 et 75e0106), mais aucune release LHM n'est sortie après 0.9.6 (14/02/2026). Les commentaires de PawnIoDriver et d'IntelPowerLimitBackend (0x601 « autorisé en écriture ») ne correspondent pas à la version embarquée. Options : embarquer les .bin officiels ≥ 0.2.4 (LGPL-2.1, signés par namazso) ou attendre une LHM plus récente. À vérifier sur un PC Intel, et à ajouter au diagnostic (liste des ioctl du module chargé).
- Côté AMD, les modules embarqués conviennent : RyzenSMU expose send_smu_command et read/write_smu_register, AMDFamily17 expose read_smn sans liste blanche. C'est ce qui rend le Curve Optimizer faisable sans rien livrer de plus.
- PawnIO signé ne charge que des modules signés (seule l'édition « Unrestricted » ne vérifie pas la signature). Impossible d'écrire ses propres modules : toute capacité matérielle dépend des modules officiels.
- Le moteur de charge vérifiée (compute GPU, noyaux CPU, VRAM) doit être commun à F1 (bench), F2 (diagnostic) et F4 (OC). Le construire une seule fois, en premier, dans le cadre de F1.
- Le journal de session et la reprise après plantage de F4 (écriture directe, remise d'origine au lancement, lecture des événements Kernel-Power 41 / 6008 / WHEA / 4101) servent aussi à F18 (bascule automatique de profils) et à l'actuel « Appliquer au démarrage ».
- Diagnostic « Compatibilité de ce PC » à enrichir : état VBS/HVCI (Win32_DeviceGuard.VirtualizationBasedSecurityStatus, à 2 sur le PC de Denis), révision du microcode (registre « Update Revision », lisible), outils concurrents actifs (Afterburner, Adrenalin, Ryzen Master, XTU, ThrottleStop, NVIDIA App), ioctl disponibles dans chaque module PawnIO.
- Licences : ZenStates-Core est en GPL-3.0 (ne pas lier, ne reprendre que les numéros de commandes), CoreCycler en CC BY-NC-SA 4.0 (non commercial), PawnIO.Modules en LGPL-2.1 (redistribution des binaires possible), ComputeSharp et Vortice.Windows en MIT.
- Ordre conseillé : F5 d'abord (petit, sans risque, vérifiable sur le PC de Denis), puis F6 (dépend de l'accès à des cartes), puis F4 (XL, dépend du moteur de charge de F1 et des vérifications de F6).
- Incertitudes non levées : état des deltas NVAPI après un TDR ; persistance des réglages ADLX et IGCL au redémarrage ; CO SMU refusée ou non quand PBO est désactivé dans le BIOS ; fonctionnement du SMU AMD sous VBS ; effet réel d'un dé-parking forcé sur X3D bi-CCD ; CO sur les APU Renoir et Lucienne ; nombre de classes d'efficacité sur Meteor/Lunar Lake, Strix Point et Snapdragon X ; versions minimales de pilotes pour ADLX et IGCL V2.


## F9 bascule GPU dédié / intégré sur portable, F16 OC d'écran (fréquence au-delà du natif), F17 choix de l'écran de l'overlay « fenêtre PCPerfSuite »

### F9 — partiel, taille L

**Approche recommandée**

Recommandation : pour la version 1, pas de vraie bascule matérielle. L'app fait un « Endormir le GPU dédié » sans aucune écriture dans le firmware, en trois couches, pour toutes les marques.

(1) Panneau d'état, en lecture seule.
- Présence du dGPU et état d'alimentation D0/D3 : CM_Get_DevNode_PropertyW(DEVPKEY_Device_PowerData), champ CM_POWER_DATA.PD_MostRecentPowerState.
- Écrans branchés sur le dGPU : QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS). Si le targetInfo.adapterId d'un chemin est le LUID du dGPU (LUID obtenu par DXGI IDXGIAdapter1::GetDesc1), l'écran est sur le dGPU. Si cet écran est de type DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL ou DISPLAYPORT_EMBEDDED, c'est l'écran interne : le MUX, Advanced Optimus ou ADS est en mode dGPU, et le dGPU ne peut pas dormir.
- Processus qui tiennent le dGPU éveillé : compteurs PDH « \GPU Process Memory(pid_*_luid_<LUID dGPU>_*)\Dedicated Usage » et « \GPU Engine(pid_*_luid_<LUID dGPU>_*)\Utilization Percentage ». Ce sont les données du Gestionnaire des tâches ; elles demandent un pilote WDDM 2.0 ou plus. L'app a déjà PdhCounterSampler.
- Mode GPU du constructeur, lu seulement quand l'interface existe : ASUS DSTS 0x00090020 (Eco) et 0x00090016 (MUX), Lenovo LENOVO_GAMEZONE_DATA GetIGPUModeStatus et GetGSyncStatus. Ce sont des lectures, comme celles que fait déjà AsusFanProvider.

(2) Action « Libérer le GPU dédié ». Pour chaque processus trouvé, l'app propose :
- d'écrire HKEY_USERS\<SID de la session>\Software\Microsoft\DirectX\UserGpuPreferences : nom de valeur = chemin complet de l'exe, donnée « GpuPreference=1; » (1 = économie d'énergie, 2 = hautes performances, 0 = laisser Windows décider). Il faut réécrire seulement la paire GpuPreference et garder les autres paires de la chaîne ;
- puis de fermer proprement l'application (WM_CLOSE, jamais de kill forcé sur les processus protégés par ProcessTerminationGuard) et de la relancer.
Ensuite, c'est le pilote NVIDIA, AMD ou Intel qui met lui-même le dGPU en D3cold (GC6 ou BOCO). C'est la méthode « tuer les processus » de Lenovo Legion Toolkit et de MsiGT, que LLT juge la plus efficace. Le bouton n'est actif que si le mode Hybride est actif et qu'aucun écran n'est branché sur le dGPU (même règle que LLT).

(3) Éditeur de préférence GPU par application (« Toujours sur le GPU intégré / dédié »), réutilisable par F18 et F19.

Prérequis : quand le dGPU est en D3, suspendre la lecture du groupe GPU de LibreHardwareMonitor, sinon PCPerfSuite réveille lui-même la carte.

Pour la vraie bascule (Eco, MUX), l'app affiche un message adapté à la marque, par exemple : « Sur ce ROG, le mode Eco se règle dans Armoury Crate ou G-Helper ».

**Alternatives écartées**

- Mode Eco ASUS (DEVS 0x00090020, Vivobook 0x00090120, via \\.\ATKACPI IOCTL 0x0022240C ou WMI AsusAtkWmi_WMNB.DEVS, ce que fait G-Helper) : écarté. C'est une écriture ACPI qui coupe l'alimentation du dGPU, contraire à la règle 5. Autres défauts : il faut tuer les applis et arrêter les services NVIDIA avant, c'est refusé si un XG Mobile ou un écran externe est branché, et après environ 15 jours en Eco, Windows peut supprimer le périphérique et son pilote (retour en « Carte de base Microsoft », code 10).
- MUX ASUS « Ultimate » (DEVS 0x00090016 / 0x00090026) : écarté. Écriture ACPI et redémarrage obligatoire.
- Lenovo GameZone SetIGPUModeStatus (« iGPU only », déconnecte le dGPU sans redémarrer) et SetGSyncStatus (Hybride désactivé = MUX, redémarrage) : écartés, ce sont des écritures WMI/ACPI.
- HP BIOS WMI GpuMode (Hybrid, Discrete, Optimus ; équivalent du Setup UEFI, redémarrage) : écarté, écriture de réglage firmware.
- MSI GPU Switch : écarté. MsiGT écrit la variable UEFI MsiDCVarData et « arme » la bascule en écrivant le registre EC 0xD1. C'est une écriture EC directe, contraire à la règle 5.
- Dell/Alienware, Razer, Acer : aucune interface publique trouvée (réglage BIOS ou utilitaire du constructeur seulement).
- Advanced Optimus et ADS (Windows 11 24H2 update 2025.01D, WDDM 3.2) : ils sont pilotés par le pilote ou par Windows. Microsoft écrit « The plan is to have a public API » : il n'y a donc pas d'API aujourd'hui. On se limite à la détection.
- Désactivation du périphérique (CM_Disable_DevNode sans CM_DISABLE_PERSIST, ou SetupDiCallClassInstaller DIF_PROPERTYCHANGE/DICS_DISABLE) : écartée en v1. Écran noir si l'écran interne ou un écran externe est câblé sur le dGPU ; applis D3D qui plantent (périphérique retiré) ; aucune garantie que l'alimentation soit vraiment coupée (non vérifié) ; le pilote NVIDIA peut réactiver ou signaler des erreurs. Seul avantage : elle n'est pas persistante, le redémarrage réactive la carte. LLT l'utilise seulement comme moyen bref d'évincer les processus.
- Arrêter les services NVIDIA (NvContainerLocalSystem, NVDisplay.ContainerLocalSystem, comme G-Helper) : inutile sans coupure ACPI, et risqué.

**Couverture matérielle**

Partie « endormir sans couper » :
- Tous les portables à double GPU : NVIDIA Optimus, AMD Radeon dédiée + APU, Intel Arc + iGPU. Windows 10 1803 ou plus pour la préférence par application (DXGI EnumAdapterByGpuPreference) ; pilote WDDM 2.0 ou plus pour les compteurs GPU par processus.
- Admin non requis pour HKCU ; l'app est admin de toute façon.
- Pas de redémarrage : seules les applis concernées sont relancées.
- Limites : Microsoft rappelle que l'application a toujours le dernier mot sur le GPU qu'elle utilise ; le respect de la préférence par les applis OpenGL et Vulkan dépend du pilote (non vérifié).
- Aucun effet si un écran externe est câblé sur le dGPU, ce qui est souvent le cas du HDMI sur les portables gaming, ou si l'écran interne est en mode MUX dGPU ou Advanced Optimus dGPU. L'app le détecte et le dit.

Partie « mode constructeur » : lecture seulement, sur ASUS (ATK) et Lenovo Legion/LOQ (GameZone). HP OMEN est lisible via BIOS WMI mais n'est pas encore implémenté. MSI, Dell, Razer, Acer : N/D, avec le message « marque pas encore prise en charge ».

PC de bureau : fonction masquée, avec le message « réservé aux portables à double GPU ».

**Risques**

- Fermer ou relancer des applis de l'utilisateur : perte de travail non enregistré si mal fait. Il faut WM_CLOSE avec confirmation, jamais de kill par défaut.
- Préférence GPU inscrite pour une appli qui en a besoin (jeu) : baisse de performances si l'utilisateur oublie. Il faut une liste visible et un bouton « rétablir ».
- Écriture dans la mauvaise ruche sous élévation par un autre compte.
- Certains programmes de sécurité surveillent la fermeture de processus en masse.
- Aucun risque matériel : rien n'est écrit dans l'EC, l'ACPI ou le BIOS.
- Si Denis choisit plus tard d'ajouter les écritures constructeur : écran noir (MUX sans pilote), GPU qui disparaît (Eco plus nettoyage PnP de Windows), plantage pendant la bascule, et risque juridique et de support accru.

Classement des options :
- Pas d'écriture EC ni ACPI par l'app : préférence par application (registre), fermeture de processus.
- Pas d'écriture directe, mais l'OS évalue les méthodes ACPI d'alimentation standard (chemin normal de Windows) : la désactivation du périphérique.
- Écritures firmware : ASUS Eco et MUX, Lenovo GameZone Set*, HP GpuMode (réglage BIOS), MSI (registre EC 0xD1 et variable UEFI).

**Règles du projet (CLAUDE.md)**

- Solution recommandée : aucun conflit. La lecture du mode constructeur (ASUS DSTS, Lenovo Get*) est conforme à la règle 5, en lecture seule.
- Conflit direct avec la règle 5 (« ne jamais écrire, alimentation ») : ASUS Eco et MUX (DEVS), Lenovo SetIGPUModeStatus et SetGSyncStatus, MSI (écriture du registre EC 0xD1). Écartés.
- HP GpuMode : écrit un réglage BIOS, pas l'EC en temps réel à notre connaissance, mais reste une écriture firmware d'alimentation. Considéré en conflit tant que Denis n'a pas tranché.
- Règle 6 : tout décodage du mode constructeur non vérifié sur une vraie machine est marqué « expérimental ».

**Questions pour Denis**

- La règle 5 exclut-elle aussi les réglages d'alimentation GPU que le constructeur expose lui-même dans son utilitaire (Eco ASUS, iGPU only Lenovo, MUX) ? Ou voulez-vous une exception écrite, marquée expérimental, par marque vérifiée ?
- L'app peut-elle fermer et relancer les applis qui tiennent le dGPU éveillé (après confirmation), ou doit-elle seulement les lister avec un conseil ?
- Faut-il un mode « automatique sur batterie » (libérer le dGPU au débranchement du secteur) ? Il recoupe F18 (profils automatiques).
- La désactivation temporaire du périphérique (CM_Disable_DevNode non persistant) doit-elle exister en option « expert », malgré ses risques ?
- L'éditeur de préférence GPU par application doit-il aussi être proposé sur les PC de bureau qui ont un iGPU et une carte dédiée ?

**Sources**

- https://raw.githubusercontent.com/seerge/g-helper/main/app/AsusACPI.cs
- https://raw.githubusercontent.com/seerge/g-helper/main/app/Gpu/GPUModeControl.cs
- https://raw.githubusercontent.com/seerge/g-helper/main/app/Gpu/NVidia/NvidiaGpuControl.cs
- https://github.com/seerge/g-helper/issues/5920
- https://github.com/seerge/g-helper
- https://github.com/BartoszCichecki/LenovoLegionToolkit
- https://raw.githubusercontent.com/BartoszCichecki/LenovoLegionToolkit/master/LenovoLegionToolkit.Lib/Features/Hybrid/HybridModeFeature.cs
- https://omenmon.github.io/cli
- https://github.com/tim-rue/MsiGT
- https://learn.microsoft.com/windows-hardware/drivers/display/automatic-display-switch
- https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_6/nf-dxgi1_6-idxgifactory6-enumadapterbygpupreference
- https://learn.microsoft.com/en-us/answers/questions/5641645/how-to-get-the-special-process-gpu-usage-with-the
- https://devblogs.microsoft.com/directx/gpus-in-the-task-manager/
- https://learn.microsoft.com/en-us/windows-hardware/drivers/install/devpkey-device-powerdata
- https://learn.microsoft.com/en-us/windows/win32/api/cfgmgr32/nf-cfgmgr32-cm_disable_devnode
- https://learn.microsoft.com/en-us/windows/win32/api/wingdi/ne-wingdi-displayconfig_video_output_technology
- https://www.ghacks.net/2021/10/29/how-to-assign-graphics-performance-preferences-to-windows-11-programs/
- https://www.hwinfo.com/forum/threads/nvidia-laptop-gpu-optimus-will-not-sleep.10805/

### F16 — partiel, taille L

**Approche recommandée**

Recommandation v1 : utiliser seulement les API des pilotes, avec essai et retour automatique. Pas de surcharge EDID en v1.

(1) Inventaire : le service d'écrans (voir F17) donne, pour chaque écran, l'adaptateur qui le pilote (LUID, donc la marque) et le type de sortie.

(2) NVIDIA : NvAPIWrapper.Net (déjà référencé, 0.8.1) expose NvAPI_DISP_TryCustomDisplay, SaveCustomDisplay, RevertCustomDisplayTrial, EnumCustomDisplay et DeleteCustomDisplay, via DisplayDevice.TrialCustomResolution, SaveCustomResolution, RevertCustomResolution et DeleteCustomResolution.
- L'identifiant d'écran se déduit de « \\.\DISPLAYn » par NvAPI_DISP_GetDisplayIdByDisplayName.
- Le timing se calcule avec DisplayApi.GetTiming (NvAPI_DISP_GetTiming, type CVT-RB).
- L'essai n'est pas enregistré. SaveCustomDisplay(isThisMonitorIdOnly = true) n'est appelé qu'après confirmation ; « Restaurer » appelle DeleteCustomDisplay.
- NvAPI_DISP_GetMonitorCapabilities (NV_MONITOR_CAPS_TYPE_GENERIC : isTrueGsync, supportVRR) sert à repérer les écrans à module G-Sync et à refuser. Ce champ n'est peut-être pas exposé par NvAPIWrapper 0.8.1 ; sinon, P/Invoke direct.

(3) AMD : ADLX, en réutilisant AdlxGpuBackend et AdlxProbeGuard. IADLXDisplayServices::GetCustomResolution(display), puis IsSupported, puis CreateNewResolution, avec ADLX_CustomResolution (refreshRate en Hz entiers, timingStandard CVT_RB). Le mode est ensuite appliqué par ChangeDisplaySettingsExW avec dwflags = 0 (changement dynamique non enregistré). Après confirmation : CDS_UPDATEREGISTRY. Sans confirmation : ChangeDisplaySettingsExW(null, null, IntPtr.Zero, 0, IntPtr.Zero), qui revient au mode du registre, puis DeleteResolution. Refus si l'écran est en mode dupliqué ou Eyefinity (limite documentée par ADLX).

(4) Intel (UHD, Iris Xe, Arc) et dalles de portable pilotées par l'iGPU : N/D en v1, avec le message « Intel pas encore pris en charge ». IGCL n'offre que des modes source (SourceX et SourceY, sans fréquence) ; la seule voie Intel est la surcharge EDID (ctlEdidManagement OVERRIDE_EDID, ou la surcharge EDID du registre Windows, prioritaire sur celle d'IGCL).

(5) Protocole de test, identique pour toutes les marques :
- l'utilisateur choisit la fréquence, par paliers conseillés de +5 Hz ; aucune boucle automatique ;
- l'app applique le mode, puis affiche une confirmation sur tous les écrans (validation au clavier possible), avec un compte à rebours de 15 s et un retour automatique sans clic ;
- un processus de surveillance séparé annule aussi si l'interface se fige ;
- l'app vérifie que la fréquence appliquée côté PC est la bonne (QueryDisplayConfig targetInfo.refreshRate, et DwmGetCompositionTimingInfo) ;
- l'app guide ensuite un test des sauts d'images avec appareil photo (motif TestUFO frameskipping, pose de 1/10 s ou plus).

(6) Phase 2, marquée « expérimental », seulement si Denis l'accepte : surcharge EDID dans le registre (valeurs binaires sous « Device Parameters\EDID_OVERRIDE\<n° de bloc> » de la clé matérielle du moniteur, format documenté par Microsoft pour les INF). Code maison, sans redistribuer CRU. Filet de sécurité : une entrée RunOnce qui annule la surcharge à la session suivante tant qu'elle n'est pas confirmée.

**Alternatives écartées**

- Surcharge EDID façon CRU pour tous les GPU en v1 : écartée. Elle est persistante et peut donner un écran noir dès le démarrage. CRU signale aussi un possible blocage au démarrage avec plusieurs écrans NVIDIA, et que NVIDIA ignore les surcharges quand DSC est actif. Elle demande d'analyser et de réécrire l'EDID (DTD, extensions CTA-861 et DisplayID, plage FreeSync) puis de redémarrer le pilote graphique. restart64.exe est un binaire propriétaire de ToastyX, non redistribuable ; le redémarrage (périphérique moniteur ou adaptateur) doit être réécrit et vérifié.
- IGCL ctlGetSetCustomMode : écartée, modes source uniquement, pas de fréquence.
- Réutiliser la boîte « Conserver ces paramètres d'affichage ? » de Windows : impossible, c'est l'application Paramètres qui l'implémente et aucune API publique ne l'expose.
- Détection logicielle automatique des sauts d'images : impossible. L'image est perdue dans l'électronique de l'écran, invisible pour le PC ; les captures d'écran ne la montrent pas.
- Recherche automatique de la fréquence maximale en boucle : écartée. Un écran « hors plage » peut rester noir pendant la recherche, et les sauts d'images ne sont pas détectables sans l'œil de l'utilisateur.

**Couverture matérielle**

NVIDIA GeForce :
- Oui pour les écrans pilotés par la carte NVIDIA : PC de bureau, écran externe branché sur le dGPU, dalle interne en mode MUX dGPU.
- Non pour la dalle d'un portable Optimus en mode hybride (pilotée par l'iGPU, NVAPI renvoie NVAPI_INVALID_DISPLAY_ID).
- Non quand DSC est actif : restriction signalée par des utilisateurs et par CRU, non documentée par NVIDIA, et DSC n'est pas détectable par le nvapi.h public.
- Non pour les écrans à module G-Sync (arrêt décidé ici, les fréquences arbitraires y sont signalées refusées).

AMD Radeon (pilote qui fournit ADLX) : oui, sauf en mode dupliqué ou Eyefinity. Le comportement sur les APU et dalles de portable n'est pas vérifié.

Intel : N/D en v1.

Dalles de portable en général : peu de marge et souvent verrouillées. CRU rappelle que les dalles de portable n'ont en général pas de scaler.

Windows 10 ou 11 ; admin requis seulement pour une éventuelle surcharge EDID (HKLM).

**Risques**

- Écran noir « hors plage » : couvert par le retour automatique après 15 s et le processus de surveillance.
- Sauts d'images silencieux (l'écran affiche moins d'images que la fréquence annoncée) : seul le test guidé avec appareil photo les révèle.
- Artefacts ou overdrive mal réglé à la nouvelle fréquence.
- VRR : la plage FreeSync ou G-Sync Compatible décrite dans l'EDID ne suit pas le nouveau mode, donc le VRR peut se couper ou scintiller.
- HDR et DSC : interactions non documentées.
- Mode enregistré par NVIDIA (SaveCustomDisplay) ou AMD qui reste après une mise à jour de pilote ou un changement d'écran : il faut un inventaire et un bouton « Restaurer ».
- Surcharge EDID (phase 2) : écran noir au démarrage. Récupération possible par le mode sans échec, car la carte de base Microsoft ignore les surcharges selon CRU.
- Écriture dans HKLM\SYSTEM\CurrentControlSet\Enum\DISPLAY : peut alerter des antivirus.
- Aucun cas de dommage matériel trouvé dans les sources consultées. Aucune source sur la garantie : à formuler prudemment dans l'avertissement.

**Règles du projet (CLAUDE.md)**

Aucun avec la règle 5 : rien n'est écrit dans l'EC. Règle 6 : les voies AMD et, plus tard, EDID ou Intel sont marquées « expérimental » tant qu'elles n'ont pas été vérifiées sur une vraie machine. Règle 3 : chaque refus a sa raison propre (écran sur l'iGPU, module G-Sync, mode dupliqué, Intel pas encore pris en charge, DSC soupçonné).

**Questions pour Denis**

- Accepte-t-on, même en phase 2 et marquée expérimental, une surcharge EDID persistante dans le registre (seule voie pour Intel et la plupart des dalles de portable) ? Ou se limite-t-on aux API NVIDIA et AMD avec essai et retour ?
- Cible : écrans externes seulement, ou aussi les dalles de portable (peu de gain, plus de risques) ?
- Paliers choisis par l'utilisateur (+5 Hz) avec test caméra guidé, ou assistant qui propose les paliers lui-même (toujours sans boucle automatique) ?
- Texte d'avertissement et de responsabilité à afficher avant le premier essai : qui le valide ?
- Refuser d'office les écrans à module G-Sync et désactiver la fonction quand le VRR est actif, ou seulement avertir ?

**Sources**

- https://github.com/NVIDIA/nvapi (nvapi.h : NvAPI_DISP_TryCustomDisplay, SaveCustomDisplay, RevertCustomDisplayTrial, GetTiming, GetMonitorCapabilities isTrueGsync)
- https://gpuopen.com/manuals/adlx/adlx-sdk-references/adlx-interfaces/display/iadlxdisplaycustomresolution/
- https://raw.githubusercontent.com/GPUOpen-LibrariesAndSDKs/ADLX/main/SDK/Include/IDisplaySettings.h
- https://raw.githubusercontent.com/GPUOpen-LibrariesAndSDKs/ADLX/main/SDK/Include/ADLXStructures.h
- https://raw.githubusercontent.com/intel/drivers.gpu.control-library/master/include/igcl_api.h
- https://github.com/MicrosoftDocs/windows-driver-docs/blob/staging/windows-driver-docs-pr/display/overriding-monitor-edids.md
- https://www.monitortests.com/forum/Thread-Custom-Resolution-Utility-CRU
- https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-changedisplaysettingsexw
- https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/nf-dwmapi-dwmgetcompositiontiminginfo
- https://www.testufo.com/frameskipping
- https://learn.microsoft.com/en-us/answers/questions/3928732/i-cant-make-a-customize-resolution
- https://www.overclock.net/threads/custom-static-refresh-rates-on-vrr-monitors.1809965/

### F17 — faisable, taille M

**Approche recommandée**

Recommandation : trois modes dans l'onglet Overlay, mémorisés dans OverlaySettings.
- « Écran principal » : défaut, comportement actuel.
- « Cet écran » : liste des écrans avec leurs noms conviviaux, plus un bouton « Identifier » qui affiche un numéro sur chaque écran.
- « Écran du jeu » : suit la fenêtre au premier plan.

(1) Nouveau service dans Core (par exemple Core/Hardware/Displays/DisplayTopology.cs), réutilisé par F9 et F16.
- QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS).
- Pour chaque chemin, DisplayConfigGetDeviceInfo :
  - GET_SOURCE_NAME donne viewGdiDeviceName (« \\.\DISPLAYn ») ;
  - GET_TARGET_NAME donne monitorFriendlyDeviceName, monitorDevicePath, edidManufactureId, edidProductCodeId, outputTechnology et connectorInstance.
- Rapprochement avec les HMONITOR : EnumDisplayMonitors puis GetMonitorInfoW(MONITORINFOEXW), en comparant szDevice à viewGdiDeviceName. rcMonitor est en pixels physiques, puisque le processus est PerMonitorV2.
- DPI : GetDpiForMonitor(MDT_EFFECTIVE_DPI).

(2) Identifiant stable, enregistré sous la forme { MonitorDevicePath, EdidManufactureId, EdidProductCodeId, FriendlyName }. Résolution dans cet ordre :
- chemin exact ;
- sinon mêmes identifiants EDID, en départageant deux écrans identiques par le numéro de série WmiMonitorID.SerialNumberID (root\wmi, InstanceName rapproché du chemin) ;
- sinon repli sur l'écran principal, avec le message « L'écran « DELL U2720Q » n'est pas branché : l'overlay s'affiche sur l'écran principal ».
Ne jamais enregistrer « \\.\DISPLAYn » : ce numéro n'est pas garanti stable.

(3) Placement dans OverlayWindow.Reposition, qui utilise aujourd'hui SystemParameters.PrimaryScreenWidth :
- calculer la position en pixels physiques à partir du rcMonitor de l'écran cible, avec la taille = ActualWidth × facteur DPI de cet écran ;
- appliquer par SetWindowPos(hwnd, HWND_TOPMOST, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE) plutôt que Left/Top. Ces propriétés sont ambiguës en PerMonitorV2 avec des DPI différents (dotnet/wpf #4127, toujours ouvert) ;
- s'abonner à Window.DpiChanged pour replacer après le changement d'échelle, en arrêtant dès que la position ne bouge plus.

(4) Changements de configuration : Microsoft.Win32.SystemEvents.DisplaySettingsChanged, ou WM_DISPLAYCHANGE via un hook HwndSource. On résout à nouveau l'écran cible, repli sur l'écran principal, retour automatique quand l'écran revient. Rien de cela n'est géré aujourd'hui.

(5) Mode « Écran du jeu » : réutiliser le hook EVENT_SYSTEM_FOREGROUND de TopmostKeeper, puis MonitorFromWindow(GetForegroundWindow(), MONITOR_DEFAULTTONEAREST). Ignorer les fenêtres de PCPerfSuite et du shell (barre des tâches, bureau), sinon l'overlay saute d'écran à chaque clic sur la barre des tâches.

(6) Écrans dupliqués (clone) : un seul HMONITOR pour plusieurs cibles, affiché sous la forme « A + B (dupliqués) ».

Le canal RTSS n'est pas concerné : RTSS dessine dans le jeu, sur l'écran du jeu.

**Alternatives écartées**

- System.Windows.Forms.Screen : écartée. Pas de nom convivial, noms « \\.\DISPLAYn » instables, et dépendance WinForms alors que le projet Core a UseWindowsForms=false.
- Positionnement WPF par Left/Top en DIP : écarté (ambiguïté multi-DPI, dotnet/wpf #4127 et #3105).
- EnumDisplayDevices seul : écarté. Noms génériques (« Moniteur PnP générique ») et pas d'identifiants EDID.
- Un overlay par écran simultanément : pas écarté techniquement (plusieurs OverlayWindow), mais hors besoin exprimé. Question pour Denis.

**Couverture matérielle**

- Toutes les marques et tous les GPU : ce sont des API de Windows (QueryDisplayConfig depuis Windows 7, PerMonitorV2 depuis Windows 10 1703). Aucun droit admin nécessaire.
- Nom convivial vide pour certaines cibles (écran forcé sans EDID, certains adaptateurs ou docks) : repli « Écran 2 (1920×1080) ».
- Jeu en plein écran exclusif sur l'écran choisi : overlay fenêtre invisible (limite déjà connue ; seul RTSS dessine).
- Jeu en plein écran sur un autre écran : aucun problème, l'overlay est traversant et ne prend jamais le focus (WS_EX_NOACTIVATE), donc le jeu ne se minimise pas.

**Risques**

Faibles.
- Boucle de replacement entre DpiChanged et SizeChanged si la convergence n'est pas vérifiée.
- Overlay perdu hors écran après un débranchement si WM_DISPLAYCHANGE n'est pas géré.
- Saut d'écran intempestif en mode « Écran du jeu », d'où le filtrage des fenêtres du shell.
- Un overlay posé sur le même écran que le jeu oblige le DWM à composer : léger surcoût et perte du « flip » indépendant, d'après le blog DirectX sur les optimisations plein écran. Le poser sur un autre écran évite ce coût, ce qui est un argument en faveur de F17.
- Le numéro de série EDID est une donnée d'identification du matériel : à exclure du rapport copié, comme IsPersonal.

**Règles du projet (CLAUDE.md)**

Aucun conflit. À ajouter au diagnostic Compatibilité (règle 4) : une ligne « Écrans » avec, pour chacun, nom, résolution, fréquence, échelle et GPU qui le pilote.

**Questions pour Denis**

- Mode par défaut : « Écran principal » (comportement actuel) ou « Écran du jeu » ?
- Un seul overlay, ou la possibilité d'en afficher un sur plusieurs écrans à la fois ?
- Ancrage et marges communs à tous les écrans, ou mémorisés par écran ?
- Si l'écran choisi est débranché : repli sur l'écran principal avec message (proposé), ou overlay masqué ?

**Sources**

- https://learn.microsoft.com/en-us/windows/win32/api/wingdi/ns-wingdi-displayconfig_target_device_name
- https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-querydisplayconfig
- https://learn.microsoft.com/en-us/windows/win32/api/wingdi/ns-wingdi-displayconfig_source_device_name
- https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getmonitorinfow
- https://learn.microsoft.com/en-us/windows/win32/api/shellscalingapi/nf-shellscalingapi-getdpiformonitor
- https://github.com/dotnet/wpf/issues/4127
- https://github.com/dotnet/wpf/issues/3105
- https://learn.microsoft.com/en-us/windows/win32/wmicoreprov/wmimonitorid
- https://devblogs.microsoft.com/directx/demystifying-full-screen-optimizations/

### Remarques transverses

- Ordre conseillé : F17 d'abord (taille M, sans risque). Elle crée le service d'inventaire des écrans (QueryDisplayConfig, DisplayConfigGetDeviceInfo, LUID de l'adaptateur, technologie de sortie, identifiants EDID) qu'utilisent aussi F9 (écrans branchés sur le GPU dédié, écran interne sur le dGPU = MUX actif) et F16 (quel GPU pilote l'écran à overclocker).
- Défaut déjà présent, à corriger avant ou avec F9 : HardwareMonitorService lit en permanence le groupe GPU de LibreHardwareMonitor (IsGpuEnabled = true, src/PCPerfSuite.Core/Hardware/HardwareMonitorService.cs). Sur un portable Optimus, PCPerfSuite empêche probablement lui-même le GPU dédié de s'endormir, comme cela est signalé pour HWiNFO et LHM. Il faut suspendre la lecture quand l'état du périphérique (DEVPKEY_Device_PowerData) vaut D3 et afficher « GPU dédié en veille » plutôt que N/D. Ce point n'a pas été vérifié sur une vraie machine.
- Règle 5 et F9 : toutes les vraies bascules connues passent par une écriture dans le firmware du constructeur. ASUS DEVS et Lenovo GameZone Set* sont des méthodes ACPI, MSI écrit directement le registre EC 0xD1 plus une variable UEFI, HP modifie un réglage BIOS. Seule la voie « endormir sans couper » (préférence GPU par application et libération des processus) est compatible avec la règle. Il faut que Denis tranche explicitement avant tout développement de bascule matérielle.
- Un même mécanisme de sécurité peut servir à F16 et aux fonctions d'OC d'autres sujets (F4, F6) : appliquer à l'essai, fenêtre de confirmation sur tous les écrans avec compte à rebours de 15 s, retour automatique, processus de surveillance séparé qui annule si l'interface se fige. À factoriser dans Core plutôt que de le réécrire à chaque fois.
- HKCU sous élévation : la préférence GPU par application (F9, et F18/F19 si les profils l'utilisent) doit être écrite dans la ruche de l'utilisateur connecté (HKEY_USERS\<SID>) quand SessionUser.IsOtherProfile est vrai. Sinon elle vise le mauvais compte, sans rien qui l'explique.
- API non documentées : NvAPI_GPU_QueryActiveApps (PhysicalGPU.GetActiveApplications dans NvAPIWrapper) n'est pas dans le nvapi.h public. On lui préfère les compteurs PDH « GPU Engine » et « GPU Process Memory », documentés et indépendants du fabricant. Le format de UserGpuPreferences n'est connu officiellement que par ses valeurs 0/1/2 : ne modifier que GpuPreference et préserver les autres clés de la chaîne.
- Diagnostic « Compatibilité de ce PC » à compléter : pour F17, une ligne « Écrans » ; pour F9, l'état du GPU dédié (D0/D3), les écrans branchés dessus et le mode constructeur lu ; pour F16, la voie disponible par écran (NVAPI, ADLX, surcharge EDID ou N/D) avec sa raison (DSC, module G-Sync, écran sur l'iGPU d'un portable Optimus, mode dupliqué, Intel pas encore pris en charge). Le numéro de série EDID est exclu du rapport copié.
- Incertitudes non levées, à vérifier sur de vraies machines : (1) est-ce que lire DEVPKEY_Device_PowerData et les compteurs PDH GPU réveille le dGPU ? (2) la désactivation du périphérique coupe-t-elle réellement son alimentation ? (3) comment appliquer une surcharge EDID sans outil tiers (redémarrer le périphérique moniteur ou l'adaptateur) ? (4) NvAPIWrapper 0.8.1 expose-t-il isTrueGsync ? (5) la personnalisation ADLX fonctionne-t-elle sur les dalles de portable pilotées par un APU Ryzen ? (6) la préférence GPU Windows est-elle respectée par les applis OpenGL/Vulkan ?
- Aucun binaire n'a été téléchargé ni exécuté. Seuls des en-têtes texte publics (nvapi.h, en-têtes ADLX, igcl_api.h) ont été récupérés pour lecture, dans le dossier scratchpad de la session, hors du dépôt. Le dépôt n'a pas été modifié.


## F8 : gestion RGB multi-marques (ventilateurs, carte mère, RAM, GPU, périphériques) pour PCPerfSuite, WPF .NET 8, diffusion publique

### F8 — partiel, taille L

**Approche recommandée**

RECOMMANDATION : PCPerfSuite ne parle JAMAIS directement au matériel RGB (ni SMBus, ni protocole USB rétro-conçu, ni EC). Il pilote deux fournisseurs, OpenRGB et Windows Dynamic Lighting, et laisse la main aux logiciels constructeur quand ils sont présents. Périmètre V1 = L.

(b) FOURNISSEUR PRINCIPAL = OpenRGB, client du SDK réseau. C'est la seule source qui couvre ventilateurs ARGB (via carte mère ou contrôleur), carte mère, RAM, GPU, AIO et périphériques toutes marques.
- Client : OpenRGB.NET 3.1.1 (MIT, net8.0, sans dépendance). Il gère au plus le protocole 4 (constante MaxProtocolNumber = 4). Le serveur OpenRGB 1.0 (protocole 6) négocie la plus haute version commune, d'après la doc SDK et la communication de la 1.0 (« rétro-compatible dans les deux sens »). Conséquence du protocole 4 : les appareils sont adressés par index, donc il faut tout relire sur DeviceListUpdated. N'écrire notre propre client (ou forker) que si les identifiants uniques du protocole 6 deviennent nécessaires.
- Connexion TCP sur 127.0.0.1:6742, avec délai et réessais : la détection OpenRGB au démarrage prend plusieurs secondes (SMBus, USB).
- OpenRGB 1.0 (sortie le 12/09/2026, GPLv2) est installé séparément par l'utilisateur, jamais embarqué. Bouton dans Paramètres › Installations : lien direct résolu par https://codeberg.org/api/v1/repos/OpenRGB/OpenRGB/releases/latest, asset OpenRGB_<ver>_Windows_64_<hash>.msi.
- Démarrage : soit l'option « service d'arrière-plan » de la 1.0, soit PCPerfSuite lance le binaire installé avec « OpenRGB.exe --server --server-host 127.0.0.1 --noautoconnect ». Le processus lancé hérite des droits admin, nécessaires à PawnIO pour la RAM et la carte mère. Si un OpenRGB tourne déjà avec son serveur, on s'y connecte simplement.

(a) SECOND FOURNISSEUR = Windows Dynamic Lighting, API Windows.Devices.Lights.LampArray.
- Le code WinRT va dans un assemblage séparé, par ex. PCPerfSuite.Lighting.WinRT, en TFM net8.0-windows10.0.22621.0 (projection CsWinRT via Microsoft.Windows.SDK.NET.Ref), avec SupportedOSPlatformVersion 10.0.19041.0 et des gardes ApiInformation.
- Appels : DeviceWatcher sur LampArray.GetDeviceSelector(), puis LampArray.FromIdAsync. Propriétés lues : IsAvailable, AvailabilityChanged, LampArrayKind, LampCount, HardwareVendorId/ProductId. Écriture : SetColor, ou SetColorsForIndices avec des Windows.UI.Color.
- En V1 : inventaire, bouton « identifier » (clignotement) pendant que la fenêtre a le focus, et bouton vers ms-settings:personalization-lighting.
- Pas d'effets persistants par cette voie : sans identité de paquet, Windows n'applique les couleurs d'une app que tant qu'elle a le focus.

(c) SDK CONSTRUCTEURS = pas en V1. On se contente d'un détecteur des logiciels constructeur (processus et services) : Armoury Crate/LightingService, iCUE, Synapse/Chroma, G HUB, MSI Center/Mystic Light, GCC/RGB Fusion, NZXT CAM, L-Connect 3, SignalRGB. Les noms exacts restent à relever sur de vraies machines. Si l'un tourne, les appareils de sa marque sont affichés « gérés par X » en lecture seule, et OpenRGB n'est ni lancé ni piloté automatiquement.

(d) ARCHITECTURE, dans src/PCPerfSuite.Core/Hardware/Lighting/ :
- ILightingProvider { Id, DisplayName, IsVerified, Task<LightingProviderStatus> DetectAsync(ct), Devices, Task<LightingResult> ApplyAsync(LightingDevice, LightingEffect, ct), Release() }.
- LightingProviderStatus distingue : NotInstalled, NotRunning, VendorSoftwareConflict(nom), NeedsAdmin, UnsupportedOs, NoDevice, Ready.
- LightingDevice { StableKey (fournisseur + VID:PID + série/emplacement), Name, Kind (Motherboard, Dram, Gpu, Cooler, FanController, LedStrip, Keyboard, Mouse, Headset, Other), Bus (Hid, SmBus, GpuI2c, AcpiWmi, Unknown), LedCount, Zones, HardwareModes, CanWrite, ReadOnlyReason }.
- Dédoublonnage par VID/PID quand un appareil est vu à la fois par Dynamic Lighting et par OpenRGB : on préfère Dynamic Lighting, protocole officiel.
- Un seul LightingEffectEngine, côté App ou Core. À « Quitter » : Release, sans aucune écriture.

LECTURE / ÉCRITURE
- Lecture : inventaire, zones, nombre de LED, mode actif et couleurs (OpenRGB le rapporte ; Dynamic Lighting ne rapporte pas les couleurs courantes).
- Écriture : couleurs et mode, uniquement.
- Jamais de SaveMode, qui écrit en flash dans l'appareil.
- Jamais de ResizeZone automatique : le nombre de LED des bandes ARGB se règle dans OpenRGB.

EFFETS V1 : Éteint, Couleur fixe, Respiration, Couleur selon température.
- Fixe et respiration utilisent de préférence le mode matériel natif du contrôleur (UpdateMode sur le mode Static/Breathing qu'il expose) : zéro CPU, et l'effet survit à la fermeture de l'app.
- À défaut, mode Direct avec rendu logiciel à 20-30 Hz, réservé aux appareils USB/HID, jamais aux appareils SMBus.
- Couleur selon température : on réutilise FanTempSource (CPU, GPU, la plus chaude, carte mère) et le SamplingLoop existant. Dégradé à 2-3 paliers avec hystérésis, écriture au plus 1 fois par seconde et seulement quand la couleur change au-delà d'un seuil.

DIAGNOSTIC : rubrique « Éclairage RGB » dans CompatibilityViewModel. Elle liste les fournisseurs, la version d'OpenRGB et le protocole négocié, les appareils par bus, les logiciels en conflit et la raison de chaque absence.

V2, optionnelle (XL) : éclairage en arrière-plan via Dynamic Lighting (« ambient »). Il faut un petit processus assistant NON élevé, PCPerfSuite.Lighting.exe, avec une identité de paquet (paquet « external location » signé par un certificat de production, extension com.microsoft.windows.lighting). Le processus admin le pilote par un tube nommé à accès restreint. Adaptateurs SDK constructeur (iCUE, Chroma, Aura) seulement s'ils sont réclamés, via RGB.NET.

**Alternatives écartées**

- Accès SMBus/I2C direct via PawnIO (nos propres modules RAM/carte mère) : écarté. Les protocoles sont rétro-conçus et il y a eu des briques (cartes Gigabyte Aorus Z390 par un dump SMBus à l'adresse 0x68, RGB de cartes MSI bloqué par certains modes Mystic Light). Cela entre en collision avec les lectures SPD de LibreHardwareMonitor. Et PawnIO n'exécute que des modules signés : il faudrait très probablement les faire signer par son auteur (non vérifié).
- Réimplémenter les protocoles USB HID propriétaires (Corsair, NZXT, Lian Li, Razer…) : écarté. Cela revient à refaire OpenRGB, avec une maintenance sans fin.
- SDK constructeurs en V1 : écartés.
  - Chacun exige son logiciel installé et lancé : Aura SDK 3.1 via LightingService (COM AuraServiceLib, SwitchMode/ReleaseControl) ; iCUE SDK 4.0.84 avec iCUE ≥ 4.31 lancé ; Chroma avec Synapse (SDK passé sous la marque WYVRN) ; LED SDK Logitech avec G HUB ; Mystic Light SDK avec MSI Center et droits admin.
  - Les EULA sont flous sur la redistribution des DLL : celui d'iCUE est muet malgré le dossier « redist », la licence Logitech est non transférable.
  - Aucun SDK public chez NZXT ni Lian Li. Gigabyte : SDK RGB Fusion non trouvé en accès public (page 403).
  - Six intégrations à maintenir, pour un gain nul quand le logiciel constructeur fait déjà le travail.
- RGB.NET comme socle (LGPL-2.1, version 3.2.0 de mars 2026, net8.0, fournisseurs Asus/Corsair/Logitech/Razer/SteelSeries/MSI/OpenRGB…) : écarté en V1. Il repose sur les DLL natives des constructeurs, qu'on ne peut pas redistribuer, et sa LGPL pose problème avec la publication single-file actuelle. C'est pourtant la meilleure base si des SDK constructeurs deviennent nécessaires en V2.
- SignalRGB : écarté. Son API HTTP est réservée à l'abonnement Pro (d'après la doc de l'intégration Home Assistant) et c'est une app concurrente fermée.
- Embarquer le binaire OpenRGB (GPLv2) dans l'installeur : écarté. Cela entraîne les obligations GPL (offre de source) et nous ferait porter le support de ses bugs matériels. Installation séparée, comme PawnIO.
- Dynamic Lighting comme unique source : écarté. Très peu de ventilateurs, de cartes mères ou de RAM exposent du HID LampArray : ASUS le fait pour les en-têtes ARGB, pas pour la RAM.
- Écrire notre propre client SDK OpenRGB : repoussé. OpenRGB.NET suffit ; on ne forke que si le protocole 6 devient indispensable.

**Couverture matérielle**

WINDOWS DYNAMIC LIGHTING
- Windows 11 22621.2361+ avec KB5030509 (en pratique 23H2 et plus ; Denis est en 22631). Sous Windows 10, l'API existe depuis 1809 mais en premier plan seulement et presque aucun appareil : à afficher « non pris en charge ».
- Aucun droit admin requis.
- Appareils HID LampArray annoncés :
  - Logitech G LIGHTSYNC (tous annoncés compatibles ; probablement via le service logi_lamparray_service de G HUB, non vérifié), Razer récents, ASUS ROG (claviers et souris), HP OMEN, Victus et HyperX, SteelSeries, Acer.
  - Cartes mères ASUS Intel 600/700 (d'après la FAQ ASUS, cela inclut la TUF B760-PLUS WIFI de Denis), AM4 X570/B550, AM5 X670/B650. Pilotage via Armoury Crate ou, sur certaines cartes, une option BIOS. Les en-têtes ARGB sont couverts, pas la RAM (fil du forum ROG).
  - Portables ROG Zephyrus et Strix 2024-2025, TUF A 2024-2025 (clavier).
  - MSI, Gigabyte et ASRock : aucune liste officielle trouvée, donc inconnu.

OPENROGB 1.0
- Couverture bureau très large : cartes mères ASUS Aura, MSI Mystic Light, Gigabyte RGB Fusion 2, ASRock Polychrome ; RAM ENE/Aura, Corsair, G.Skill, Kingston… ; GPU de partenaires via I2C ; contrôleurs Corsair, NZXT Hue/Kraken, Lian Li Uni Hub ; périphériques Razer, Logitech, Corsair, SteelSeries. La liste exacte est sur openrgb.org/devices_1.0.html (page dynamique, non vérifiée ligne à ligne ici).
- RAM et vieilles cartes mères : PawnIO et droits admin obligatoires (déjà le cas de PCPerfSuite).
- Ventilateurs : seulement ceux branchés sur un en-tête ARGB ou un contrôleur pris en charge. Les en-têtes RGB 12 V 4 broches n'offrent qu'une couleur par en-tête.
- Qualité variable selon le modèle. RAM ENE : détection intermittente, gels (T-Force Delta), CPU élevé.
- Lian Li sans L-Connect : partiel (UNI FAN SL V2/SL-INF, pas d'écran LCD ni de pompe). À noter : L-Connect 3 intègre lui-même OpenRGB (bêta).
- NZXT et Corsair sans leur logiciel : via OpenRGB seulement, et en conflit si CAM ou iCUE tourne.

PORTABLES
- Clavier seulement s'il est HID : LampArray, ou USB via OpenRGB.
- Claviers pilotés par l'EC via WMI/ACPI (ASUS TUF, Lenovo, HP Omen, Acer, MSI) : non pris en charge, par choix (règle 5).

AUTRES CAS
- Sans droits admin : Dynamic Lighting fonctionne ; OpenRGB perd la RAM et la carte mère SMBus.
- Windows ARM64 : OpenRGB 1.0 ne publie que des builds Windows x86 32 et 64 bits, donc Dynamic Lighting seulement.

**Risques**

MATÉRIEL
- Les écritures SMBus d'OpenRGB (RAM, carte mère) sont le seul vrai risque de brique.
- Historique d'incidents :
  - Gigabyte Aorus Z390 briquées par un dump à l'adresse 0x68 ;
  - RGB de cartes MSI bloqué par certains modes (corrigé) ;
  - une barrette G.Skill Trident Z dont le RGB a cessé de fonctionner juste après l'installation (ticket #2367, cause non établie) ;
  - gels système avec des T-Force Delta DDR5 (ENE).
- OpenRGB prévient lui-même que le risque demeure.
- Parade : RAM et carte mère SMBus en opt-in « expérimental » avec avertissement, jamais activées par défaut, et jamais de SaveMode.

COLLISION SMBUS AVEC LIBREHARDWAREMONITOR
- PCPerfSuite lit déjà la température SPD des barrettes (IsMemoryEnabled, via RAMSPDToolkit qui prend le mutex partagé Global\Access_SMBUS.HTP.Method).
- Le mutex partagé d'OpenRGB (MR !1436, 2022) était une case à cocher, pas le comportement par défaut. Son défaut dans la 1.0 n'est PAS vérifié.
- Sans ce mutex : températures fausses, ou appareil laissé dans un état invalide.
- Précédent déjà connu dans le code : un détenteur du mutex (Afterburner, Armoury Crate) peut bloquer Computer.Open.

CONFLITS AVEC LES LOGICIELS CONSTRUCTEUR
Deux programmes qui pilotent le même contrôleur produisent clignotements et états invalides. OpenRGB, Lian Li et NZXT demandent tous de fermer les autres logiciels.

CPU
RAM ENE + PawnIO : environ 10 % de CPU avec 4 barrettes en effet dynamique (ticket #5124). D'où : aucun effet logiciel sur le SMBus, et 1 Hz maximum pour la couleur selon température.

SÉCURITÉ
- Le serveur SDK d'OpenRGB n'a aucune authentification. L'adresse d'écoute par défaut diffère selon les sources (0.0.0.0 ou 127.0.0.1) : toujours passer --server-host 127.0.0.1.
- Impact si un autre processus local s'y connecte : faible (pilotage de l'éclairage).
- Lancer OpenRGB depuis un processus admin revient à lancer un exécutable tiers avec les droits admin. Ne lancer que le binaire installé par le MSI officiel, sous Program Files.

SIGNATURE
Non vérifié : le MSI d'OpenRGB est-il signé Authenticode ? S'il ne l'est pas, OfficialInstaller (qui exige signature + éditeur attendu) ne peut pas l'installer tel quel. Il faudra alors soit ouvrir la page officielle (comme pour RTSS), soit épingler un SHA-256 par version.

ANTI-TRICHE / ANTIVIRUS
PawnIO a remplacé WinRing0 (plus de mise en quarantaine par Defender), mais un pilote noyau reste sensible aux anti-triches. Non vérifié pour OpenRGB 1.0.

JURIDIQUE
- OpenRGB (GPLv2) : pas de redistribution. Lui parler par socket est l'usage prévu du SDK (programme séparé) ; OpenRGB.NET est sous MIT.
- RGB.NET (LGPL-2.1) : la DLL doit rester remplaçable, ce qui coince avec la publication single-file.
- iCUE : EULA muet sur la redistribution. Logitech : licence non transférable. Razer/WYVRN : conditions non vérifiées.

DYNAMIC LIGHTING EN ARRIÈRE-PLAN
- Un processus élevé perd très probablement l'identité de paquet : constaté sur des apps MSIX, non vérifié pour un paquet « external location ». D'où le processus assistant non élevé.
- Il faut un certificat de production : un certificat auto-signé est refusé chez l'utilisateur.
- Les priorités Windows sont liées au port USB : un appareil rebranché ailleurs perd sa priorité.

TAILLE
Passer en TFM windows10.0.x ajoute Microsoft.Windows.SDK.NET.dll, lourd (à mesurer). À isoler dans un assemblage chargé à la demande.

**Règles du projet (CLAUDE.md)**

- Règle 5 (contrôleur embarqué des portables en lecture seule) : conflit direct. Le rétroéclairage RGB des claviers de portables passe souvent par l'EC via WMI/ACPI (ASUS ATK, Lenovo, HP Omen, Acer, MSI), et certains pilotes OpenRGB pour portables écrivent par cette voie.
  Parade : sur tout châssis qui n'est pas un bureau avéré (même logique que MachineInfo.SoftwareFanControlRefused, donc Chassis != Desktop), n'écrire que sur les appareils HID/USB (Dynamic Lighting, ou OpenRGB avec un emplacement HID). Les autres sont affichés « non pris en charge sur portable : contrôleur embarqué ». Le tri se fait par le champ location d'OpenRGB ; son format exact est à vérifier.
- Règle 5 et ventilateurs : pas de conflit. Le module RGB ne touche que les LED, jamais la vitesse.
- Règle 2 : sockets, COM et WinRT en best-effort avec délai. Un OpenRGB injoignable est un statut, pas une exception.
- Règle 3 : messages distincts pour « OpenRGB non installé ou non démarré », « Windows antérieur à 23H2 », « géré par Armoury Crate / iCUE… », « RAM RGB désactivée par prudence », « portable : contrôleur embarqué », « sans administrateur : RAM et carte mère indisponibles ».
- Règle 4 : nouvelle rubrique dans CompatibilityViewModel, reprise dans « Copier le rapport ».
- Règle 6 : chaque couple fournisseur + famille d'appareils reste « expérimental » tant qu'il n'a pas été vérifié sur une vraie machine. La TUF B760 de Denis permet de vérifier d'emblée Aura via OpenRGB et Dynamic Lighting.
- Hors CLAUDE.md : la publication single-file actuelle est incompatible en pratique avec la LGPL si RGB.NET est adopté (les DLL doivent rester à côté de l'exe).

**Questions pour Denis**

- Quelle est la licence de PCPerfSuite (fermée, MIT, GPL…) ? Elle décide si RGB.NET (LGPL) est utilisable avec la publication single-file, et comment présenter la dépendance à OpenRGB (GPL).
- Acceptes-tu une dépendance à OpenRGB, installé à part comme PawnIO, avec son avertissement de risque matériel ? Sinon, le RGB se limite à Dynamic Lighting : sûr, mais quasiment aucun ventilateur ni aucune RAM.
- RAM RGB et carte mère via SMBus : jamais, opt-in « expérimental » avec avertissement (ma recommandation), ou actif par défaut ?
- La règle 5 couvre-t-elle aussi le rétroéclairage des claviers de portables (écriture dans l'EC via WMI) ? Je recommande que oui.
- Quand Armoury Crate, iCUE, Synapse, G HUB, CAM ou L-Connect est détecté : PCPerfSuite s'efface (lecture seule, recommandé), ou propose d'arrêter le logiciel constructeur ?
- La couleur selon température doit-elle continuer quand l'app est dans la zone de notification ? Si oui, cela impose OpenRGB ou l'assistant Dynamic Lighting en arrière-plan.
- Es-tu prêt à payer un certificat de signature de code de production (ex. Azure Trusted Signing) et à ajouter un paquet d'identité MSIX « external location » à l'installation ? C'est le prérequis de Dynamic Lighting en arrière-plan (V2).
- OpenRGB : PCPerfSuite doit-il le démarrer et l'arrêter lui-même (serveur caché, lié à 127.0.0.1), ou seulement se connecter à une instance ou au service lancé par l'utilisateur ?
- Quel matériel as-tu pour tester (TUF B760 Aura, RAM RGB ?, ventilateurs ARGB ?, périphériques Logitech/Razer/Corsair ?) afin de sortir des familles du statut « expérimental » ?
- En mode technicien (F3) : le RGB est-il simplement exclu ? Je recommande de ne jamais lancer de scan SMBus OpenRGB sur la machine d'un client.

**Sources**

- https://learn.microsoft.com/en-us/windows/apps/develop/devices-sensors/lighting-dynamic-lamparray
- https://learn.microsoft.com/en-us/uwp/api/windows.devices.lights.lamparray
- https://learn.microsoft.com/en-us/windows-hardware/design/component-guidelines/dynamic-lighting-devices
- https://support.microsoft.com/en-us/windows/hardware/input-devices/control-dynamic-lighting-devices-in-windows
- https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/grant-identity-to-nonpackaged-apps
- https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/winrt-apis-desktop-apps
- https://github.com/microsoft/Windows-universal-samples/tree/main/Samples/LampArray
- https://github.com/microsoft/Dynamic-Lighting-AutoRGB
- https://www.asus.com/support/faq/1051416/
- https://www.tomshardware.com/pc-components/motherboards/asus-tests-bios-update-enabling-microsoft-dynamic-lighting-control-makes-it-easier-to-avoid-armory-crate-software
- https://rog-forum.asus.com/t5/gaming-motherboards/windows-dynamic-lighting-ram-rgb/td-p/1135866
- https://github.com/openai/codex/issues/48421
- https://codeberg.org/OpenRGB/OpenRGB
- https://codeberg.org/OpenRGB/OpenRGB/releases
- https://codeberg.org/api/v1/repos/OpenRGB/OpenRGB/releases/latest
- https://codeberg.org/OpenRGB/OpenRGB/raw/branch/master/Documentation/OpenRGBSDK.md
- https://codeberg.org/OpenRGB/OpenRGB/raw/branch/master/LICENSE
- https://gitlab.com/CalcProgrammer1/OpenRGB/-/blob/master/Documentation/SMBusAccess.md
- https://gitlab.com/CalcProgrammer1/OpenRGB/-/merge_requests/2885
- https://gitlab.com/CalcProgrammer1/OpenRGB/-/merge_requests/1436
- https://gitlab.com/OpenRGBDevelopers/OpenRGB-Wiki/-/blob/stable/User-Documentation/Frequently-Asked-Questions.md
- https://openrgb-wiki.readthedocs.io/en/latest/
- https://gitlab.com/CalcProgrammer1/OpenRGB/-/issues/2367
- https://gitlab.com/CalcProgrammer1/OpenRGB/-/issues/2427
- https://gitlab.com/CalcProgrammer1/OpenRGB/-/work_items/5124
- https://github.com/CalcProgrammer1/OpenRGB/blob/master/cli.cpp
- https://openrgb.org/devices_1.0.html
- https://github.com/diogotr7/OpenRGB.NET
- https://www.nuget.org/packages/OpenRGB.NET
- https://github.com/DarthAffe/RGB.NET
- https://www.nuget.org/packages/RGB.NET.Core
- https://github.com/DarthAffe/RGB.NET/tree/master/RGB.NET.Devices.Corsair
- https://github.com/DarthAffe/RGB.NET/tree/master/RGB.NET.Devices.Logitech
- https://github.com/Blacktempel/RAMSPDToolkit
- https://raw.githubusercontent.com/LibreHardwareMonitor/LibreHardwareMonitor/master/LibreHardwareMonitorLib/Hardware/Mutexes.cs
- https://www.asus.com/microsite/aurareadydevportal/installation.html
- https://www.asus.com/microsite/aurareadydevportal/tutorial_csharp.html
- https://corsairofficial.github.io/cue-sdk/
- https://github.com/CorsairOfficial/cue-sdk/releases
- https://doc.wyvrn.com/docs/chroma-sdk/
- https://assets.razerzone.com/dev_portal/REST/html/md__r_e_s_t_external_01_8init.html
- https://github.com/meskill/mystic-light-sdk
- https://github.com/tylerszabo/RGB-Fusion-Tool
- https://support.nzxt.com/hc/en-us/articles/46234819749019-Does-the-NZXT-CAM-software-control-RAM-RGB
- https://lian-li.com/l-connect-3-x-openrgb/
- https://lian-li.com/faq/faq_l-connect-3/
- https://github.com/SteelSeries/gamesense-sdk
- https://docs.signalrgb.com/guides/account-billing/about-pro-features/

### Remarques transverses

- Synergie avec PawnIO : OpenRGB 1.0 exige lui aussi PawnIO pour la RAM et la carte mère sous Windows, donc le bouton d'installation existant sert aux deux. Mais deux programmes vont alors accéder au SMBus en même temps (LibreHardwareMonitor pour la température SPD, OpenRGB pour les LED). Avant tout développement, faire un test d'1-2 h sur la machine de Denis : OpenRGB 1.0 et PCPerfSuite ensemble, en vérifiant le mutex Global\Access_SMBUS.HTP.Method (défaut en 1.0 non vérifié), les températures SPD et l'absence de gel de Computer.Open.
- F10 (liens de téléchargement directs) : OpenRGB fournit un lien direct stable via l'API Gitea de Codeberg (https://codeberg.org/api/v1/repos/OpenRGB/OpenRGB/releases/latest, asset *_Windows_64_*.msi). Il faut ajouter codeberg.org aux hôtes autorisés d'OfficialInstaller et vérifier d'abord si le MSI est signé Authenticode (non vérifié). S'il ne l'est pas, le contrôle éditeur actuel ne peut pas s'appliquer.
- F18/F19 (profils et groupes de profils) : prévoir l'éclairage comme une dimension facultative d'un profil (ex. « Bureautique : éteint », « Gaming : couleur selon température GPU »), avec un LightingEffect sérialisable dans settings.json. Le moteur d'effets doit pouvoir changer d'effet sans relancer OpenRGB.
- F3 (mode technicien) : le RGB n'apporte rien au diagnostic de performance. Au plus, une ligne du rapport pour l'inventaire des contrôleurs RGB et les logiciels constructeur en conflit (ex. Armoury Crate + iCUE installés ensemble, source connue de bugs de ventilateurs et de capteurs). Ne jamais lancer de scan SMBus OpenRGB sur la machine d'un client en mode technicien.
- Changer de TFM (net8.0-windows vers net8.0-windows10.0.22621.0) pour Dynamic Lighting servira peut-être à d'autres fonctions WinRT (F17 écrans ?, notifications). Décider une fois pour toutes : TFM global, ou assemblage WinRT isolé (recommandé pour la taille et le chargement à la demande).
- Découpage suggéré en conversations. Conversation 1 : socle Core/Hardware/Lighting + fournisseur OpenRGB + détecteur des logiciels constructeur + rubrique Compatibilité + bouton Installations (modèle fort, effort élevé : sécurité et règles 2-6 à tenir). Conversation 2 : fournisseur Dynamic Lighting + moteur d'effets (fixe, respiration, température) + onglet UI (effort moyen). Conversation 3, seulement après la décision sur le certificat : assistant non élevé avec identité de paquet pour l'éclairage en arrière-plan. Les adaptateurs SDK constructeur via RGB.NET restent hors périmètre sauf demande explicite.
- Incertitudes à ne pas arrondir : défaut du mutex SMBus partagé d'OpenRGB 1.0 ; adresse d'écoute par défaut du serveur SDK (0.0.0.0 ou 127.0.0.1 selon les sources) ; signature Authenticode du MSI OpenRGB ; perte de l'identité de paquet d'un processus « external location » lancé en administrateur ; passage probable des appareils Logitech en LampArray par un service de G HUB ; prise en charge Dynamic Lighting des cartes mères MSI, Gigabyte et ASRock ; conditions de redistribution des SDK Corsair, Logitech et Razer.


## Gestionnaires système et limites par processus (F7, F10, F11, F12, F13, F14, F15) pour PCPerfSuite, app WPF .NET 8 grand public lancée en administrateur

### F14 — partiel, taille L

**Approche recommandée**

Recommandation : limites « douces » par défaut, plafond dur CPU en option, jamais de plafond dur de RAM. Pas de plafond de débit disque. Réseau limité au sens montant. Tout part de l'onglet Processus (clic droit > Limiter…) et d'une liste de règles par nom ou chemin d'exe.

1) CPU
- Priorité : SetPriorityClass. Refuser REALTIME, et HIGH seulement après un avertissement.
- Mode efficacité, comme le Gestionnaire des tâches : SetProcessInformation(ProcessPowerThrottling) avec ControlMask=StateMask=PROCESS_POWER_THROTTLING_EXECUTION_SPEED (EcoQoS), plus IDLE ou BELOW_NORMAL.
- Nombre de cœurs : SetProcessAffinityMask. Au-delà de 64 threads logiques, passer par les groupes de processeurs ou les CPU Sets.
- Plafond dur en % (option) : Job Object nommé (CreateJobObject), puis AssignProcessToJobObject (droits PROCESS_SET_QUOTA|PROCESS_TERMINATE), puis SetInformationJobObject(JobObjectCpuRateControlInformation, ENABLE|HARD_CAP, CpuRate = % x 100). Le % porte sur TOUT le CPU du PC, pas sur un cœur. Ne jamais mettre KILL_ON_JOB_CLOSE. Un processus ne peut pas quitter un job : « lever la limite » veut dire réécrire ControlFlags=0.

2) RAM
- Pas de JOB_OBJECT_LIMIT_PROCESS_MEMORY ni JOB_MEMORY : ce sont des limites de mémoire engagée, l'allocation échoue et l'app plante.
- À la place : priorité mémoire basse (SetProcessInformation ProcessMemoryPriority) et bouton « Vider le jeu de travail » (EmptyWorkingSet).
- Alerte au-delà de X Go : JobObjectNotificationLimitInformation avec JobMemoryLimit. Windows notifie sans refuser l'allocation.
- En « expérimental » seulement : plafond de RAM physique par SetProcessWorkingSetSizeEx + QUOTA_LIMITS_HARDWS_MAX_ENABLE. Pas de plantage, mais des défauts de page qui ralentissent l'app.

3) Disque
- Seulement la priorité E/S : NtSetInformationProcess(ProcessIoPriority=33, valeurs 0/1/2 = très basse/basse/normale). API non documentée, donc marquée expérimentale.
- Plus la priorité mémoire.
- Aucun plafond en Mo/s : SetIoRateControlInformationJobObject est « not supported » depuis Windows 10 1607.

4) Réseau
- Plafond montant par nom d'exe : stratégie QoS via WMI ROOT\StandardCimv2 MSFT_NetQosPolicySettingData (c'est ce que fait New-NetQosPolicy -AppPathNameMatchCondition x.exe -ThrottleRateActionBitsPerSecond).
- Magasin ActiveStore : la stratégie disparaît au redémarrage, et PCPerfSuite la recrée à son lancement. Pas de résidu si l'app est désinstallée.
- Variante par PID, à tester : JobObjectNetRateControlInformation (MaxBandwidth, trafic sortant seulement).
- Sens descendant : impossible sans pilote noyau WFP.

Persistance et enfants
- Règles dans settings.json, appliquées au démarrage de chaque processus via les événements ProcessStart de la session ETW noyau déjà ouverte (ProcessIoTracer). Pas de sondage WMI.
- Un job capte seul les enfants créés APRÈS l'affectation. Les enfants déjà lancés sont ajoutés en parcourant l'arbre de processus, déjà calculé pour le regroupement.
- En quittant PCPerfSuite : lever toutes les limites (ControlFlags=0, priorités d'origine mémorisées, suppression des stratégies QoS).

Ce que fait Process Lasso, et qui valide ce choix
- Priorité, affinité, priorité E/S et mode efficacité persistants par exe.
- Son « CPU Limiter » retire temporairement des cœurs (affinité) au-delà d'un seuil ; ce n'est pas un plafond de job.
- Les limites mémoire passent par des règles « Watchdog » qui redémarrent ou tuent le processus : pas de plafond dur.

**Alternatives écartées**

- Plafond dur de mémoire engagée (JOB_OBJECT_LIMIT_PROCESS_MEMORY) : fait échouer les allocations, donc plantages.
- SetIoRateControlInformationJobObject : non pris en charge depuis Windows 10 1607, d'après la doc Microsoft.
- PROCESS_MODE_BACKGROUND_BEGIN : ne s'applique qu'au processus appelant.
- Pilote minifiltre (débit disque) ou callout WFP (réseau descendant) : pilote noyau signé EV + signature d'attestation Microsoft + compatibilité HVCI. Hors de portée d'un développeur seul, et risque BSOD.
- Ralentissement par suspension et reprise cyclique des threads (NtSuspendProcess) : saccades, et interblocages possibles.
- Réglage « Watchdog » qui tue le processus : trop dangereux pour un outil grand public.

**Couverture matérielle**

Indépendant de la marque de CPU, de GPU et de carte mère : ce sont des API Windows. Couverture réelle :

- Job Objects imbriqués et contrôle du débit CPU : Windows 8 et plus. Échec (erreur 50) si le Dynamic Fair Share Scheduling est actif, en session Bureau à distance.
- Contrôle réseau par job : Windows 10 et plus.
- EcoQoS : véritable niveau EcoQoS sur Windows 11 seulement (« LowQoS » sous Windows 10). Effet plus net sur les CPU hybrides (Intel 12e génération et plus, Snapdragon X), où les tâches partent sur les cœurs E. Sur un Ryzen de bureau, l'effet se limite à une baisse de fréquence, sans doute faible.
- Administrateur obligatoire pour les processus des autres comptes et les services.
- Toujours refusé, quel que soit le PC :
  - processus protégés (PPL : antivirus, csrss, services anti-triche) ;
  - processus déjà dans un job qui pose des restrictions d'interface (l'imbrication est alors impossible, cas typique des processus « bac à sable » des navigateurs ; exemple exact à vérifier).
- Stratégies QoS (réseau) : conçues pour les PC en domaine. Sur un PC domestique hors domaine, la bride de débit n'est pas vérifiée. Des retours signalent qu'il faut la clé « Do not use NLA » pour le marquage DSCP : à tester sur une vraie machine avant de l'annoncer.

**Risques**

- Plafond CPU dur : l'app tourne par à-coups à chaque intervalle d'ordonnancement. Saccades garanties sur un jeu ou un flux audio. Le réserver aux tâches de fond, avec un avertissement.
- Anti-triche (EAC, BattlEye, Vanguard) : ouvrir un handle d'écriture sur le processus d'un jeu peut être refusé ou repéré. Risque de bannissement faible mais non nul. Liste d'exclusion : ne jamais toucher un jeu sans le demander, et refuser les processus protégés.
- Brider dwm, audiodg, explorer ou un service système peut figer la session. Réutiliser ProcessTerminationGuard pour refuser les processus critiques.
- Si PCPerfSuite plante, la limite reste sur le processus jusqu'à sa fermeture (l'association à un job est définitive). La doc dit qu'un job ne se rouvre que par son nom. Non vérifié : si le nom survit à la fermeture du dernier handle ; il est probable qu'il disparaisse, le job restant vivant par ses processus. Au redémarrage de PCPerfSuite, il n'est alors peut-être plus possible de lever la limite.
- API non documentées (ProcessIoPriority) : peuvent changer avec une version de Windows.
- Le réglage existant « Désactiver le power throttling » (PowerThrottlingOff=1) risque de neutraliser l'EcoQoS par processus. À vérifier, et à signaler dans l'interface.
- Stratégies QoS écrites dans le magasin « localhost » au lieu d'ActiveStore : elles survivraient à la désinstallation.

**Règles du projet (CLAUDE.md)**

Aucun conflit avec la règle du contrôleur embarqué. Règles 2 et 3 : chaque refus doit dire pourquoi :
- accès refusé ou processus protégé ;
- anti-triche ;
- job existant avec restrictions d'interface ;
- DFSS actif ;
- EcoQoS absent sous Windows 10 ;
- réseau non vérifié hors domaine.

Règle 6 : priorité E/S (API non documentée), plafond réseau et plafond de RAM physique marqués « expérimental ». Règle 4 : ajouter au diagnostic « Compatibilité de ce PC » l'état des Job Objects, EcoQoS, QoS réseau et DFSS.

**Questions pour Denis**

- La cible : brider des applis de fond pendant que tu joues (navigateur, Discord, mises à jour), ou aussi brider un jeu ? Le second cas expose à l'anti-triche.
- Plafond CPU dur en %, avec ses saccades, ou seulement les modes doux (priorité, mode efficacité, nombre de cœurs) dans la première version ?
- RAM : on refuse la limite dure et on propose alerte + priorité mémoire + « vider », ou tu veux quand même le plafond de RAM physique en expérimental ?
- Réseau : une bride en envoi seul (upload) te suffit ? Le sens descendant demanderait un pilote noyau signé, que je déconseille.
- En quittant PCPerfSuite : on lève toutes les limites (proposé), ou elles restent actives ?

**Sources**

- https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-jobobject_cpu_rate_control_information
- https://learn.microsoft.com/en-us/windows/win32/api/jobapi2/nf-jobapi2-setinformationjobobject
- https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects
- https://learn.microsoft.com/en-us/windows/win32/procthread/nested-jobs
- https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-jobobject_extended_limit_information
- https://learn.microsoft.com/en-us/windows/win32/api/memoryapi/nf-memoryapi-setprocessworkingsetsizeex
- https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-setprocessinformation
- https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-setpriorityclass
- https://learn.microsoft.com/en-us/windows/win32/api/jobapi2/nf-jobapi2-setioratecontrolinformationjobobject
- https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-jobobject_net_rate_control_information
- https://learn.microsoft.com/en-us/powershell/module/netqos/new-netqospolicy
- https://learn.microsoft.com/en-us/windows-server/networking/technologies/qos/qos-policy-manage
- https://www.cisco.com/c/en/us/support/docs/quality-of-service-qos/qos-configuration-monitoring/221868-enable-dscp-qos-tagging-on-windows-machi.html
- https://github.com/MScholtes/Priority/blob/master/Priority.cpp
- https://bitsum.com/automation/
- https://bitsum.com/product-update/process-lasso-9-3-introducing-the-cpu-limiter/

### F15 — faisable, taille S

**Approche recommandée**

Recommandation : une carte « Animations et effets » dans Optimisation Windows. Chaque interrupteur passe par SystemParametersInfoW avec SPIF_UPDATEINIFILE|SPIF_SENDCHANGE : Windows écrit lui-même UserPreferencesMask, MinAnimate, etc., et diffuse WM_SETTINGCHANGE. Pas de déconnexion. Ne jamais écrire les octets de UserPreferencesMask à la main : leur disposition n'est pas documentée.

Correspondances :
- Interrupteur principal « Effets d'animation » (celui de Paramètres > Accessibilité > Effets visuels) : SPI_SETCLIENTAREAANIMATION. C'est la valeur que lit UISettings.AnimationsEnabled, d'après l'implémentation de Wine. À confirmer sur une vraie machine : basculer dans Paramètres, puis relire SPI_GETCLIENTAREAANIMATION.
- Réduire/agrandir les fenêtres : SPI_SETANIMATION (ANIMATIONINFO.iMinAnimate).
- Menus : SPI_SETMENUANIMATION (+ SPI_SETMENUFADE).
- Listes déroulantes : SPI_SETCOMBOBOXANIMATION.
- Défilement fluide : SPI_SETLISTBOXSMOOTHSCROLLING.
- Info-bulles : SPI_SETTOOLTIPANIMATION / SPI_SETTOOLTIPFADE.
- Fondu de sélection : SPI_SETSELECTIONFADE.
- Contenu des fenêtres pendant le déplacement : SPI_SETDRAGFULLWINDOWS (valeur dans uiParam).
- Ombres : SPI_SETDROPSHADOW, SPI_SETCURSORSHADOW.
- Coupure globale : SPI_SETUIEFFECTS (interrupteur maître de tous les effets d'interface).
- Hors SPI : animations de la barre des tâches (HKCU\...\Explorer\Advanced\TaskbarAnimations) et transparence (HKCU\...\Themes\Personalize\EnableTransparency). Registre + WM_SETTINGCHANGE ; si Explorer ne relit pas à chaud, l'afficher « après redémarrage de l'Explorateur » (à vérifier).

Restauration
- Au premier changement, mémoriser toutes les valeurs SPI_GET* dans settings.json, sur le modèle d'OriginalPowerValues.
- Bouton « Rétablir mes réglages d'origine ».
- Deux préréglages : « Réactif » (tout coupé sauf le lissage des polices) et « Mes réglages d'origine ».

Au passage, revoir le réglage existant « visual-effects-performance ». Il n'écrit que VisualFXSetting=2 : c'est probablement le seul état du bouton radio de la boîte Performances, sans effet réel à lui seul (à vérifier). Le remplacer par ces appels SPI.

**Alternatives écartées**

- Écrire directement UserPreferencesMask ou MinAnimate : bits non documentés, et effet seulement après déconnexion.
- DWM : plus d'API globale (DwmEnableComposition est obsolète depuis Windows 8). DWMWA_TRANSITIONS_FORCEDISABLED ne vaut que pour une fenêtre donnée.
- Lancer SystemPropertiesPerformance.exe : pas pilotable.
- Stratégie de groupe « DisallowAnimations » : c'est une stratégie, pas un réglage utilisateur, et elle est difficile à restaurer proprement.

**Couverture matérielle**

Tous les PC sous Windows 10 et 11, quelle que soit la marque : ce sont des réglages du profil utilisateur, sans lien avec le matériel. Pas besoin d'administrateur (HKCU).

Limites :
- Les jeux et beaucoup d'applis ignorent ces réglages. Le gain porte surtout sur la réactivité perçue du bureau, pas sur les FPS ; c'est mon estimation, rien ne l'a mesuré.
- PCPerfSuite suit lui-même SPI_CLIENTAREAANIMATION : RevealPanel lit SystemParameters.ClientAreaAnimation.

**Risques**

Faibles. Points d'attention :
- Si l'app est élevée avec les identifiants d'un AUTRE compte (UAC « par-dessus l'épaule »), SPI et HKCU s'appliquent au profil de l'administrateur, pas à celui de la personne connectée. Réutiliser SessionUser : désactiver la carte avec ce message dans ce cas.
- Certaines personnes ont besoin de ces réglages pour l'accessibilité, d'où l'importance de la restauration.
- Redémarrer l'Explorateur pour la barre des tâches ferme les fenêtres de l'Explorateur ouvertes : le demander avant.

**Règles du projet (CLAUDE.md)**

Aucun avec la règle du contrôleur embarqué. Règle 3 : expliquer le cas « app lancée sous un autre compte ». Règle 4 : ajouter au diagnostic le compte ciblé et les valeurs lues.

**Questions pour Denis**

- Un seul interrupteur « Couper les animations », ou le panneau détaillé réglage par réglage (proposé, avec deux préréglages) ?
- On y range aussi la transparence et les ombres, ou seulement les animations ?
- On en profite pour remplacer le réglage « Effets visuels : privilégier les performances », probablement inopérant tel quel ?

**Sources**

- https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-systemparametersinfow
- https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-animationinfo
- https://learn.microsoft.com/en-us/uwp/api/windows.ui.viewmanagement.uisettings.animationsenabled
- https://deepwiki.com/wine-mirror/wine/10.2-winrt-device-and-ui-apis
- https://learn.microsoft.com/en-us/answers/questions/2185159/registry-key-to-turn-on-show-animations-in-windows
- https://learn.microsoft.com/en-us/windows/apps/develop/composition/composition-tailoring

### F7 — partiel, taille L

**Approche recommandée**

Recommandation : un onglet « Pilotes » construit sur l'inventaire de F12, avec Windows Update comme SEULE source d'installation automatique. Pour les pilotes GPU et ceux des constructeurs de portables, PCPerfSuite détecte et oriente, il ne télécharge pas lui-même.

1) Inventaire
- Par périphérique : version, date, fournisseur et INF du pilote (propriétés DEVPKEY de F12).
- Signataire : WMI Win32_PnPSignedDriver (Signer, IsSigned).
- Pilotes âgés, et périphériques en erreur, surtout Code 28 « aucun pilote ».

2) Mises à jour Windows Update
- COM « Microsoft.Update.Session » en dynamic (COMReference n'est pas pris en charge par dotnet build).
- CreateUpdateSearcher().Search("IsInstalled=0 and Type='Driver'"), en tâche de fond : la recherche peut durer plusieurs minutes.
- Afficher titre, classe, version et date via IWindowsDriverUpdate.
- Sélection pilote par pilote, puis UpdateDownloader et UpdateInstaller. Signaler RebootRequired.
- Exclure toujours la classe « Firmware » (BIOS/UEFI, contrôleur embarqué) : elle reste à Windows Update ou à l'outil du constructeur.

3) Avant toute installation
- Point de restauration : WMI root\default SystemRestore.CreateRestorePoint(description, 10 = DEVICE_DRIVER_INSTALL, 100 = BEGIN_SYSTEM_CHANGE).
- Si la protection système est désactivée sur C:, le dire sans l'activer d'office.
- Si un point existe déjà dans les dernières 24 h, Windows n'en crée pas de nouveau (réglage SystemRestorePointCreationFrequency) : l'expliquer.
- Rendre la carte graphique au pilote (overclock et ventilateurs GPU) et libérer NVAPI, ADLX et IGCL avant un pilote graphique.

4) Outils de technicien
- « Sauvegarder tous les pilotes » : pnputil /export-driver * <dossier> (Windows 10 1607+).
- « Installer un pilote .inf » : pnputil /add-driver x.inf /install. La sortie de pnputil est traduite dans la langue de Windows : ne lire que le code de sortie, ou utiliser DiInstallDriver / UpdateDriverForPlugAndPlayDevices (newdev.dll).

5) GPU
- Afficher la version installée (déjà connue via NVAPI, ADLX et IGCL).
- Proposer d'ouvrir l'outil du fabricant : NVIDIA App, AMD Software, Intel Graphics Software ou Intel DSA.
- En option « expérimental », pour NVIDIA seulement : afficher la dernière version via l'adresse non documentée AjaxDriverService.php?func=DriverManualLookup, qu'utilisent TinyNvidiaUpdateChecker et NVCleanstall.

6) DDU
- Non réimplémenté. Il figure dans la boîte à outils F10 ; PCPerfSuite peut le lancer.
- Pas d'automatisation du redémarrage en mode sans échec par bcdedit.

7) Portables
- Détecter et proposer de lancer l'outil du constructeur : Dell Command Update (dcu-cli), Lenovo System Update ou Vantage, HP Support Assistant ou HPIA, MyASUS.
- Les pilotes audio, pavé tactile et touches de fonction sont personnalisés par le constructeur.

**Alternatives écartées**

- Télécharger et installer soi-même les pilotes AMD, Intel ou Realtek : aucune API publique. Les serveurs d'AMD sont réputés protégés contre les liens directs (contrôle du Referer), ce que je n'ai pas vérifié, et ce ne serait pas à contourner. Un pilote Realtek générique casse souvent les fonctions ajoutées par le constructeur du PC.
- Base de pilotes façon « Driver Booster » : redistribution interdite et risque élevé.
- Microsoft Update Catalog : pas d'API officielle.
- Réimplémenter DDU : complexe et risqué. Sa licence interdit la redistribution, on se contente du lien officiel.
- Tout installer d'un coup, sans sélection.

**Couverture matérielle**

- Windows Update : toutes marques. La couverture dépend de ce que chaque fabricant publie sur Windows Update, souvent moins récent que sur son site pour les GPU. Les PC gérés par WSUS, Intune ou une stratégie ExcludeWUDriversInQualityUpdate peuvent ne rien renvoyer : le détecter et le dire. Le service Windows Update ne doit pas être désactivé.
- NVIDIA (dernière version) : GeForce de bureau et portables, par une adresse non documentée.
- AMD et Intel : pas de vérification de version automatique possible.
- pnputil /add-driver et /export-driver : Windows 10 1607 et plus.
- Point de restauration : seulement si la protection système est active.
- Administrateur requis.

**Risques**

- Un mauvais pilote peut provoquer un écran bleu ou un écran noir. Le point de restauration et la sauvegarde pnputil servent de filet, mais ils peuvent manquer.
- Mises à jour de firmware (BIOS, contrôleur embarqué) proposées par Windows Update : risque de rendre la machine inutilisable. D'où leur exclusion.
- Installer un pilote GPU pendant que PCPerfSuite garde NVAPI, ADLX ou IGCL ouvert, ou un overclock appliqué : plantage de l'app ou overclock perdu.
- L'adresse NVIDIA non documentée peut changer ou disparaître sans préavis.
- Juridique : lancer un téléchargement à la demande de l'utilisateur depuis le serveur du fabricant reste acceptable, à condition de ne rien contourner. Il ne faut jamais mettre en cache ni rehéberger des pilotes : les licences l'interdisent.

**Règles du projet (CLAUDE.md)**

Règle 5 : les mises à jour de la classe Firmware (BIOS, contrôleur embarqué) sont exclues de toute installation lancée par PCPerfSuite. Règle 6 : la recherche de version NVIDIA est marquée « expérimental ». Règles 3 et 4 : expliquer « aucune mise à jour » selon la cause (PC géré, service arrêté, pas d'Internet, fabricant absent de Windows Update), et reporter le résumé dans « Compatibilité de ce PC ».

**Questions pour Denis**

- Périmètre de la première version : inventaire + Windows Update + sauvegarde des pilotes (proposé), ou aussi le téléchargement direct des pilotes GPU ?
- Tu acceptes d'utiliser l'adresse NVIDIA non documentée, affichée « expérimental » ?
- Si la protection système est désactivée : on propose de l'activer (réglage système + espace disque) ou on prévient seulement ?
- En mode technicien : « installer tout ce que propose Windows Update » d'un clic, ou toujours pilote par pilote ?

**Sources**

- https://learn.microsoft.com/en-us/windows/win32/api/wuapi/nf-wuapi-iupdatesearcher-search
- https://learn.microsoft.com/en-us/windows-hardware/drivers/devtest/pnputil-command-syntax
- https://learn.microsoft.com/en-us/windows/win32/api/srrestoreptapi/nf-srrestoreptapi-srsetrestorepointw
- https://learn.microsoft.com/en-us/windows/win32/sr/createrestorepoint-systemrestore
- https://github.com/ElPumpo/TinyNvidiaUpdateChecker
- https://github.com/ZenitH-AT/nvidia-update
- https://www.wagnardsoft.com/forums/viewtopic.php?t=1091
- https://www.wagnardsoft.com/display-driver-uninstaller-ddu
- https://www.intel.com/content/www/us/en/support/articles/000091878/graphics.html

### F10 — partiel, taille M

**Approche recommandée**

Recommandation : une page « Boîte à outils », en extension de Paramètres > Installations. Pour chaque outil : rôle, éditeur, licence (usage personnel ou professionnel), version, taille, état (installé, portable présent). Boutons « Télécharger » (le lien direct est affiché et copiable), « Page officielle » et « Lancer ».

Source de vérité
- Un catalogue JSON hébergé par Denis, par exemple dans le dépôt GitHub de PCPerfSuite, lu en HTTPS.
- Régénéré chaque jour par une GitHub Action à partir des manifestes winget-pkgs (InstallerUrl, InstallerSha256, InstallerType, fichiers imbriqués) et de l'API GitHub Releases pour les outils hébergés sur GitHub.
- L'app ne lit jamais les pages web des éditeurs.

Téléchargement
- Réutiliser OfficialInstaller : HTTPS à chaque redirection, liste d'hôtes autorisés par outil, fichier verrouillé puis vérifié. Relever la taille maximale à environ 300 Mo (Cinebench et OCCT dépassent 200 Mo).
- Ajouter un contrôle SHA-256 obligatoire.
- Contrôle Authenticode et éditeur quand l'outil est signé.
- Outils portables (zip) : extraction dans %ProgramData%\PCPerfSuite\Tools\<id>\<version>, avec niveau d'intégrité « Élevé » comme RestrictToElevatedProcesses, puisqu'ils seront lancés en administrateur.
- Repli : si winget est présent, winget download ou winget install --id X --exact.

État vérifié le 28/09/2026
- PawnIO : lien stable github.com/namazso/PawnIO.Setup/releases/latest/download/PawnIO_setup.exe (redirige vers la 2.2.0). Aucune restriction de licence relevée.
- OCCT Personal : lien stable www.ocbase.com/download/edition:Personal/os:Windows, qui renvoie OCCT.exe (206 Mo). Usage commercial interdit.
- CPU-Z 3.01 : download.cpuid.com/cpu-z/cpu-z_3.01-en.exe, versionné. Freeware, usage personnel et professionnel.
- GPU-Z 2.71 : l'URL du manifeste winget (us1-dl.techpowerup.com) a répondu 404 à une requête HEAD, donc lien instable. Usage professionnel autorisé, redistribution dans un produit commercial interdite.
- HWiNFO : manifeste winget bloqué à la 7.72 de 2024, donc inutilisable. Gratuit en usage non commercial seulement, licence Pro en entreprise.
- CrystalDiskInfo 9.9.2 et CrystalDiskMark 9.0.3 : SourceForge, versionnés. Licence libre, à confirmer.
- Cinebench R23 : lien stable installer.maxon.net/cinebench/CinebenchR23.zip (262 Mo), mais version ancienne ; 2024 et 2026 absents de winget. La licence gratuite interdit les « services de benchmark commerciaux » pour des tiers.
- Prime95 30.19 : download.mersenne.ca/…/p95v3019b20.win64.zip, versionné, absent de winget. Licence à vérifier.
- y-cruncher : cdn.numberworld.org, versionné. Non commercial seulement.
- FurMark 2.10.2 : chemin geeks3d.com avec un hachage qui change à chaque version. En usage commercial, le « Geeks3D PRO Pack » est probablement requis (à vérifier).
- DDU 18.1.5.3 : www.wagnardsoft.com/DDU/download/…_setup.exe, versionné. Redistribution interdite : lien seulement.
- NVCleanstall 1.19 : TechPowerUp, versionné.
- OpenRGB 1.0 : GitHub Releases (le nom du fichier contient le hash du commit). GPL.
- RTSS 7.3.7 et Afterburner 4.6.6 : miroir Guru3D ftp.nluug.nl, direct et versionné (n° de build dans le nom). Le lien MSI redirige vers une page.
- 7-Zip 26.03 : www.7-zip.org/a/7z2603-x64.exe, versionné. Licence LGPL.
- MemTest86 : clé USB de démarrage, pas un outil Windows (le zip officiel a répondu 403 à HEAD). Édition Free sans restriction d'usage.
- TestMem5 : aucune source officielle vérifiable, à écarter. Proposer plutôt mdsched.exe (Diagnostic mémoire Windows, intégré).
- ISLC : présent dans winget (Wagnardsoft.ISLC).

**Alternatives écartées**

- Liens « latest » codés en dur dans l'app : ils n'existent que pour PawnIO et OCCT ; tous les autres portent le numéro de version.
- Analyser les pages de téléchargement des éditeurs : fragile, et souvent contraire à leurs conditions.
- Rehéberger les binaires : interdit par la plupart des licences (DDU, GPU-Z dans un produit commercial…).
- Dépendre uniquement de winget : absent avant la première ouverture de session, dans Windows Sandbox, et sur certaines éditions LTSC ou Server.
- Contourner des jetons ou des protections contre les liens directs (TechPowerUp, Guru3D) : à exclure.

**Couverture matérielle**

Indépendant du matériel. Dépend de la disponibilité des éditeurs et d'Internet. Couverture réelle :
- lien stable « dernière version » pour 2 outils seulement : PawnIO, OCCT Personal ;
- lien direct versionné, tenable via le catalogue mis à jour par la CI, pour la plupart ;
- rien d'exploitable aujourd'hui pour HWiNFO et GPU-Z, qui restent en « page officielle ».

Winget : Windows 10 1809 et plus, après la première ouverture de session.

**Risques**

- Intégrité : un catalogue compromis pourrait pousser un binaire malveillant lancé en administrateur. Parades : liste d'hôtes par outil figée dans l'app, SHA-256 obligatoire, Authenticode et éditeur quand c'est possible, et idéalement catalogue signé par une clé publique embarquée.
- Plusieurs outils sont probablement non signés (Prime95, y-cruncher : à confirmer). Cela heurte la politique actuelle d'OfficialInstaller, qui exige signature et éditeur.
- Licences : un technicien en boutique est un usage COMMERCIAL. HWiNFO, OCCT, y-cruncher, Cinebench (benchmark pour un tiers) et probablement FurMark exigent alors une licence payante, ou l'interdisent.
- Antivirus : certains outils bas niveau ou de stress sont parfois signalés, et un zip extrait dans ProgramData peut l'être aussi.
- Liens morts entre deux passages de la CI : afficher « lien indisponible, ouvrir la page officielle ».

**Règles du projet (CLAUDE.md)**

Aucun avec les règles matérielles. Conserver le principe du README : rien n'est jamais installé sans action de l'utilisateur. À trancher : la vérification actuelle « signature + éditeur obligatoires » ne passe pas pour les outils non signés.

**Questions pour Denis**

- Usage visé : particuliers seulement, ou aussi techniciens en boutique ? Dans le second cas, on retire ou on marque « licence pro requise » HWiNFO, OCCT, y-cruncher, Cinebench et FurMark.
- Tu acceptes d'héberger un catalogue JSON (dépôt GitHub public de PCPerfSuite ?) mis à jour par une GitHub Action, et de le signer ?
- Pour un outil non signé : SHA-256 du manifeste winget seul, ou on l'écarte ?
- Installer les outils, ou les garder portables dans un dossier PCPerfSuite ? Et quelle liste finale (proposée : CPU-Z, GPU-Z, CrystalDiskInfo/Mark, OCCT, Prime95, FurMark, Cinebench, DDU, NVCleanstall, OpenRGB, RTSS/Afterburner, PawnIO, 7-Zip, MemTest86, ISLC) ?

**Sources**

- https://github.com/microsoft/winget-pkgs
- https://learn.microsoft.com/en-us/windows/package-manager/winget/
- https://github.com/microsoft/winget-pkgs/tree/master/manifests/r/REALiX/HWiNFO
- https://github.com/microsoft/winget-pkgs/tree/master/manifests/g/Guru3D/RTSS
- https://www.hwinfo.com/licenses/
- https://www.ocbase.com/occt-personal-use
- https://www.techpowerup.com/gpuz/
- https://cpuid.com/softwares/cpu-z.html
- https://www.numberworld.org/y-cruncher/license.html
- https://www.maxon.net/en/legal/eula
- https://www.memtest86.com/tech_license-information.html
- https://geeks3d.com/g3dpp/
- https://www.wagnardsoft.com/display-driver-uninstaller-ddu

### F11 — faisable, taille L

**Approche recommandée**

Recommandation : une vue en lecture complète, plus un sous-ensemble d'opérations sûres en première version. Les opérations destructrices passent derrière un mode expert, ou attendent la deuxième version.

Lecture
- WMI root\Microsoft\Windows\Storage : MSFT_Disk, MSFT_Partition, MSFT_Volume et leurs associations (MSFT_DiskToPartition, MSFT_PartitionToVolume).
- Barre graphique par disque, comme dans Gestion des disques.
- État BitLocker : root\CIMV2\Security\MicrosoftVolumeEncryption, Win32_EncryptableVolume.
- Santé : déjà lue via MSFT_PhysicalDisk (DiskHealthService).

Opérations de la première version
1. Initialiser un disque RAW : MSFT_Disk.Initialize, GPT par défaut.
2. Créer une partition sur l'espace libre et la formater : CreatePartition(UseMaximumSize, AssignDriveLetter), puis MSFT_Volume.Format (NTFS ou exFAT, formatage rapide).
3. Ajouter, changer ou retirer une lettre : AddAccessPath / RemoveAccessPath.
4. Renommer un volume : SetFileSystemLabel.
5. Étendre une partition dans l'espace libre contigu : GetSupportedSize, puis Resize.
6. Mettre en ligne ou hors ligne un disque non système.

Mode expert ou deuxième version : réduire (borné par le minimum de GetSupportedSize), supprimer une partition (DeleteObject), effacer un disque (Clear), convertir MBR/GPT (ConvertStyle, disque vide uniquement).

Mise en œuvre
- System.Management, ManagementObject.InvokeMethod.
- Lire ReturnValue et ExtendedStatus (MSFT_StorageExtendedStatus).
- Opérations longues avec RunAsJob, suivies via MSFT_StorageJob. Délai maximal pour ne jamais figer l'app.

Garde-fous indispensables
- Rien de destructeur sur un disque IsBoot, IsSystem ou BootFromDisk.
- Jamais sur les partitions EFI, MSR ou de récupération (GptType), sur les disques dynamiques (GUID LDM), Storage Spaces (BusType 16) ou virtuels.
- Jamais sur le disque qui porte le fichier d'échange ou d'hibernation, ni sur celui d'où tourne PCPerfSuite.
- Volume BitLocker verrouillé ou chiffré : explication, sans action.
- Aperçu « avant / après », et saisie du numéro de disque et du nom du volume pour confirmer.
- Journal de chaque opération, repris dans le diagnostic.

**Alternatives écartées**

- API VDS (IVdsService) : obsolète, remplacée par la Storage Management API.
- Piloter diskpart.exe par script : sortie traduite dans la langue de Windows et impossible à analyser proprement, erreurs mal remontées.
- Cmdlets PowerShell Storage : même fournisseur WMI, mais un processus PowerShell en plus.
- Clone complet de la Gestion des disques dès la première version : le risque de perte de données est disproportionné.
- Déplacer une partition : non pris en charge par l'API.

**Couverture matérielle**

Toutes marques de disques (NVMe, SATA, USB, SD) sous Windows 8 et plus, API de Windows 10/11. Administrateur obligatoire pour toute modification.
- RAID Intel RST ou AMD RAIDXpert : le volume apparaît comme un seul disque (BusType RAID). Les opérations restent possibles sur le disque logique, pas sur les membres de la grappe.
- Storage Spaces : lecture seule.
- Disques dynamiques : lecture seule.
- Clés USB à plusieurs partitions : gérées depuis Windows 10 1703.
- Cas fréquent sous Windows 11 : la partition de récupération WinRE suit directement C:, donc C: ne peut pas être étendu. L'expliquer, ne pas la supprimer.

**Risques**

- Perte de données : risque élevé, par nature.
- Supprimer une partition de récupération d'usine : plus de réinitialisation possible.
- Disque d'un autre système (Linux, macOS) vu en RAW et « initialisé » par erreur : avertir que RAW ne veut pas dire vide.
- BitLocker : formater ou redimensionner un volume chiffré demande de le déverrouiller ou de suspendre le chiffrement.
- Fournisseur WMI Storage parfois lent ou bloqué : délais et annulation nécessaires.
- Numéros de disque instables d'un démarrage à l'autre : identifier par UniqueId ou Guid, pas par Number.

**Règles du projet (CLAUDE.md)**

Aucun avec la règle du contrôleur embarqué. Règles 2 et 3 : chaque refus doit dire sa raison (disque système, BitLocker, disque dynamique, Storage Spaces, sans administrateur). Règle 4 : ajouter au diagnostic la liste des disques et partitions avec leurs attributs.

**Questions pour Denis**

- Première version : lecture + opérations sûres (proposé), clone complet de la Gestion des disques, ou lecture seule avec un bouton « Ouvrir la Gestion des disques » ?
- On autorise « étendre C: » quand l'espace libre suit directement C: ?
- Réduire, supprimer et effacer : dans un mode expert protégé par une confirmation saisie, ou jamais dans PCPerfSuite ?

**Sources**

- https://learn.microsoft.com/en-us/windows-hardware/drivers/storage/msft-disk
- https://learn.microsoft.com/en-us/windows-hardware/drivers/storage/msft-partition
- https://learn.microsoft.com/en-us/windows-hardware/drivers/storage/msft-volume
- https://learn.microsoft.com/en-us/windows-hardware/drivers/storage/msft-partition-resize
- https://learn.microsoft.com/en-us/windows/win32/secprov/win32-encryptablevolume

### F12 — faisable, taille M

**Approche recommandée**

Recommandation : P/Invoke CfgMgr32 pour la lecture, SetupAPI pour les actions (comme devmgmt). Pas d'analyse du texte de pnputil, qui est traduit dans la langue de Windows.

Énumération
- CM_Get_Device_ID_List_SizeW / CM_Get_Device_ID_ListW sur tous les nœuds, présents ou non.
- Pour les périphériques absents (« fantômes ») : CM_Locate_DevNodeW(CM_LOCATE_DEVNODE_PHANTOM).
- État : CM_Get_DevNode_Status (bits DN_*, dont DN_DISABLEABLE, et code problème CM_PROB_*).
- Propriétés via CM_Get_DevNode_PropertyW : FriendlyName, DeviceDesc, Class/ClassGuid, Manufacturer, DriverVersion, DriverDate, DriverProvider, DriverInfPath, LocationInfo, IsPresent, LastArrivalDate/LastRemovalDate, parent et enfants.
- Vue par classe (noms traduits via SetupDiGetClassDescriptionW) et vue par connexion.

Codes problème
- Traduire les CM_PROB_* en messages français avec leur code : 10 (ne démarre pas), 22 (désactivé), 28 (pas de pilote), 31, 43 (arrêté après erreur), 45 (non connecté), 52 (signature).
- Ces messages alimentent aussi le rapport technicien (F3) et le gestionnaire de pilotes (F7).

Actions
- Activer ou désactiver : SetupDiSetClassInstallParams(DIF_PROPERTYCHANGE, DICS_ENABLE/DICS_DISABLE, DICS_FLAG_GLOBAL), puis SetupDiCallClassInstaller. On lit DI_NEEDREBOOT pour annoncer un redémarrage.
- Repli : CM_Enable_DevNode / CM_Disable_DevNode. CM_DISABLE_PERSIST (Windows 10+) seulement si l'utilisateur veut que la désactivation survive au redémarrage.
- Rechercher les modifications matérielles : CM_Reenumerate_DevNode sur la racine.
- Désinstaller un périphérique, fantôme compris : DiUninstallDevice (newdev.dll).
- Supprimer un paquet de pilote : DiUninstallDriver, ou pnputil /delete-driver, après export.

Garde-fous
- Refuser si DN_DISABLEABLE est absent.
- Refuser les classes System, Computer, Processor, Volume, et les contrôleurs de stockage qui portent le disque de démarrage.
- Carte graphique : refuser si c'est la seule. Clavier ou souris : refuser si c'est le dernier. Carte réseau : prévenir en session à distance.
- Sur portable, refuser le contrôleur embarqué ACPI, la batterie et les zones thermiques.
- Nettoyage des fantômes : liste cochée, périphériques absents et non critiques seulement, avec confirmation.

**Alternatives écartées**

- Analyser le texte de pnputil /enum-devices : traduit, et /format n'existe que pour /enum-containers d'après la doc.
- WMI Win32_PnPEntity pour tout : plus lent, et sans les actions. Il reste utile pour Win32_PnPSignedDriver (signataire, dans F7).
- Rétablir le pilote précédent : aucune API publique documentée à ma connaissance (non confirmé). On renvoie au Gestionnaire de périphériques pour cette action.

**Couverture matérielle**

Tous PC et toutes marques sous Windows 10/11. La lecture fonctionne largement sans administrateur ; toute modification l'exige. CM_DISABLE_PERSIST : Windows 10 et plus. Les commandes pnputil de périphérique demandent Windows 10 2004 et plus ; elles restent en repli seulement.

**Risques**

- Désactiver le contrôleur du disque de démarrage : PC qui ne démarre plus (INACCESSIBLE_BOOT_DEVICE).
- Désactiver la seule carte graphique : écran noir, qui dure même après redémarrage si CM_DISABLE_PERSIST a été utilisé.
- Désactiver clavier ou souris : utilisateur bloqué.
- Supprimer les fantômes d'anciens disques USB : leurs lettres sont perdues.
- Supprimer les fantômes d'anciennes cartes réseau : une configuration IP fixe peut être perdue. C'est parfois le but recherché, mais il faut le dire.
- Supprimer un paquet de pilote encore utilisé : périphérique sans pilote.

**Règles du projet (CLAUDE.md)**

Règle 5, dans son esprit : ne jamais désactiver le contrôleur embarqué, la batterie ou les zones thermiques d'un portable. Cela pourrait priver la machine de sa gestion thermique. Règles 3 et 4 : raison de chaque refus, et résumé (nombre de périphériques en erreur, fantômes) dans « Compatibilité de ce PC ».

**Questions pour Denis**

- On inclut le nettoyage des périphériques fantômes (utile au technicien, mais destructif) ?
- Désactiver « jusqu'au redémarrage » par défaut, avec l'option « définitivement » (CM_DISABLE_PERSIST), ou l'inverse ?
- Pour « rétablir le pilote précédent » et « mettre à jour le pilote depuis un dossier », un renvoi au Gestionnaire de périphériques de Windows te va ?

**Sources**

- https://learn.microsoft.com/en-us/windows/win32/api/cfgmgr32/nf-cfgmgr32-cm_disable_devnode
- https://learn.microsoft.com/en-us/windows/win32/api/cfgmgr32/nf-cfgmgr32-cm_get_devnode_status
- https://learn.microsoft.com/en-us/windows-hardware/drivers/install/cm-prob-phantom
- https://learn.microsoft.com/en-us/windows-hardware/drivers/install/device-manager-error-messages
- https://learn.microsoft.com/en-us/windows-hardware/drivers/devtest/pnputil-command-syntax

### F13 — partiel, taille M

**Approche recommandée**

« Allocation de RAM » peut vouloir dire cinq choses. Recommandation : livrer 1, 3 et 4, proposer 2 en bouton manuel « expert », et renvoyer 5 à F14.

1) Répartition de la RAM, façon RAMMap ou Gestionnaire des tâches. Faisable, taille S.
- Compteurs PDH documentés, lus avec PdhAddEnglishCounter comme dans PdhCounterSampler : \Memory\Standby Cache Core Bytes, Standby Cache Normal Priority Bytes, Standby Cache Reserve Bytes, Modified Page List Bytes, Free & Zero Page List Bytes, Cache Bytes, Pool Paged Bytes, Pool Nonpaged Bytes, Committed Bytes, Commit Limit.
- Mémoire compressée : jeu de travail du processus « Memory Compression ». C'est une approximation.
- Le détail exact par priorité existe via NtQuerySystemInformation(SystemMemoryListInformation), mais c'est non documenté : inutile ici.

2) Purge de la liste d'attente, comme ISLC. Faisable, non documenté.
- NtSetSystemInformation(SystemMemoryListInformation=80, commande MemoryPurgeStandbyList=4, d'après les en-têtes phnt), après activation du privilège SeProfileSingleProcessPrivilege (administrateur).
- Bouton manuel seulement, sans minuterie, en « expérimental ». Utile surtout contre un ancien bogue de saccades de Windows 10 1803-1809 ; sinon, cela vide un cache que Windows réutilise déjà tout seul.

3) Fichier d'échange. Faisable, taille S.
- WMI Win32_ComputerSystem.AutomaticManagedPagefile, et Win32_PageFileSetting (InitialSize, MaximumSize, disque).
- Redémarrage nécessaire. Refuser de le désactiver complètement sans avertissement.

4) Mémoire réservée au GPU intégré.
- AMD : ADLX IADLXSystem3::GetVariableGraphicsMemory, puis IADLXVariableGraphicsMemory (IsSupported, GetAvailableOptions, GetOption, SetOption), ADLX 1.5 minimum. PCPerfSuite a déjà ADLX. D'après la doc AMD, SetOption DÉCLENCHE lui-même le redémarrage : confirmation obligatoire, et proposer d'enregistrer son travail.
- Intel : le « Shared GPU Memory Override » d'Intel Graphics Software n'a pas d'API publique, rien dans igcl_api.h. On explique et on ouvre Intel Graphics Software.
- Taille réservée par le BIOS (UMA Frame Buffer) : impossible depuis Windows.

5) Limite de RAM par processus : voir F14.

**Alternatives écartées**

- « Nettoyeur de RAM » automatique périodique (purge et vidage des jeux de travail en boucle) : fait baisser les performances en détruisant le cache, et entretient le mythe de la « RAM libre ».
- Modifier la mémoire partagée GPU de Windows (la moitié de la RAM) par des bidouilles de registre non prises en charge.
- Écrire la taille UMA dans le BIOS via le WMI d'un constructeur : pas d'interface générique, et c'est du firmware.

**Couverture matérielle**

- Répartition (1) et fichier d'échange (3) : tous les PC sous Windows 10/11.
- Purge (2) : administrateur requis ; toutes versions actuelles, mais API non documentée.
- AMD VGM (4) : seulement les APU AMD qui le prennent en charge, à commencer par les Ryzen AI 300 (Strix Point), avec au moins 16 Go de RAM d'après AMD et un pilote Adrenalin récent. Ailleurs, IsSupported renvoie faux, donc N/D avec la raison.
- Intel : Core Ultra séries 1 et 2 avec Intel Graphics Software ; lecture et réglage impossibles par l'app.
- PC de bureau à carte graphique dédiée : sans objet.

**Risques**

- Purge de la liste d'attente : baisse de performances temporaire (le cache est à reconstruire), et API non documentée.
- Fichier d'échange désactivé ou trop petit : plantages quand la limite de mémoire engagée est atteinte, et plus de fichier de vidage après un écran bleu.
- VGM : redémarrage immédiat, travail perdu. Chaque Go donné au GPU est retiré à Windows.

**Règles du projet (CLAUDE.md)**

Aucun avec la règle du contrôleur embarqué : VGM est un réglage du pilote graphique, pas du firmware. Règle 6 : purge de la liste d'attente marquée « expérimental ». Règle 3 : pour VGM, distinguer « APU non compatible », « pilote trop ancien (ADLX < 1.5) » et « Intel : réglable seulement dans Intel Graphics Software ». Règle 4 : ajouter au diagnostic la prise en charge de VGM et l'état du fichier d'échange.

**Questions pour Denis**

- Que voulais-tu dire par « Allocation de RAM » : la vue de répartition (1), la purge du cache (2), le fichier d'échange (3), la RAM donnée au GPU intégré (4), ou la limite par processus (5, déjà dans F14) ?
- Si la purge est retenue : bouton manuel seulement (proposé), ou purge automatique façon ISLC, que je déconseille ?
- Pour la RAM du GPU intégré AMD, tu acceptes que l'app déclenche elle-même le redémarrage exigé par le pilote, après confirmation ?

**Sources**

- https://github.com/winsiderss/phnt/blob/master/ntexapi.h
- https://github.com/MicrosoftDocs/win32/blob/docs/desktop-src/Memory/memory-performance-information.md
- https://mouri.moe/en/2021/11/14/Defrag-memory-with-NT-API/
- https://gpuopen.com/manuals/adlx/adlx-sdk-references/adlx-interfaces/system/iadlxvariablegraphicsmemory/
- https://gpuopen.com/manuals/adlx/adlx-sdk-references/adlx-interfaces/system/iadlxvariablegraphicsmemory/setoption/
- https://www.intel.com/content/www/us/en/support/articles/000101789/graphics.html
- https://github.com/intel/drivers.gpu.control-library/blob/master/include/igcl_api.h
- https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-pagefilesetting
- https://videocardz.com/newz/amd-introduces-afmf2-and-variable-graphics-memory-for-ryzen-ai-300-strix-point

### Remarques transverses

- Licences et « mode technicien » (F3) : un technicien en boutique fait un usage COMMERCIAL. HWiNFO gratuit (non commercial seulement), OCCT Personal (interdit en entreprise), y-cruncher (non commercial), Cinebench (licence gratuite interdisant le benchmark pour un tiers) et probablement FurMark exigent une licence payante, ou l'interdisent. Cela touche F10, mais aussi F1 et F3 si PCPerfSuite s'appuie sur ces outils pour ses mesures. Autorisés : CPU-Z, GPU-Z (usage seulement), MemTest86 Free, 7-Zip, OpenRGB (GPL). À faire valider par Denis avant de concevoir le bench.
- Tout ce qui demanderait un pilote noyau maison est hors de portée : débit disque par processus (minifiltre), réseau descendant par appli (callout WFP). Il faudrait un certificat EV, la signature d'attestation Microsoft, la compatibilité HVCI, et accepter un risque d'écran bleu. Je recommande de l'annoncer comme non pris en charge.
- Bogue probable dans le code existant : le réglage « visual-effects-performance » (WindowsPerformanceSettingsService.cs) n'écrit que VisualFXSetting=2. Cette valeur semble n'être que l'état du bouton radio de la boîte Performances ; les effets réels passent par UserPreferencesMask et SystemParametersInfo. À vérifier sur une vraie machine et à corriger avec F15.
- Réglages HKCU et SystemParametersInfo (F15, Mode Jeu, effets visuels) : si l'app est élevée avec les identifiants d'un autre compte, ils atterrissent dans le profil de l'administrateur. Réutiliser SessionUser (déjà utilisé pour le lancement au démarrage) pour griser ces réglages avec leur raison.
- Généraliser le modèle OriginalPowerValues : pour chaque modification système (limites de processus, animations, pilotes, périphériques désactivés, fichier d'échange), mémoriser l'état d'origine et tenir un journal. Il sert à la fois à « tout rétablir » et au compte rendu du technicien (F3).
- API non documentées identifiées, à marquer « expérimental » et à entourer de try/catch (règles 2 et 6) : NtSetInformationProcess(ProcessIoPriority), NtSetSystemInformation(SystemMemoryListInformation), adresse NVIDIA AjaxDriverService.
- Anti-triche : toute fonction qui ouvre des handles d'écriture sur d'autres processus (F14, et déjà « Terminer » dans Processus) peut être refusée par les processus protégés, ou repérée par EAC, BattlEye ou Vanguard. Prévoir une liste d'exclusion et des messages clairs.
- Infrastructure à réutiliser : la session ETW de ProcessIoTracer (événements de démarrage de processus pour les règles F14), ProcessTerminationGuard (processus critiques), OfficialInstaller et AuthenticodeVerifier (F10, à compléter par un SHA-256), le WMI Storage de DiskHealthService (F11), le backend ADLX (VGM, F13), PdhCounterSampler avec PdhAddEnglishCounter (F13).
- Ordre conseillé : F12 (inventaire des périphériques) d'abord. C'est la base de F7 (pilotes) et du rapport technicien (codes problème, pilotes manquants).
- README : il affirme que Guru3D ne fournit aucun lien direct stable pour RTSS. Or le manifeste winget utilise le miroir Guru3D ftp.nluug.nl, qui sert un zip direct (versionné par numéro de build). Le catalogue F10 pourrait donc proposer un téléchargement direct de RTSS et d'Afterburner, sous réserve des conditions de Guru3D.
- WUA depuis .NET 8 : une COMReference ne se compile pas avec dotnet build (MSB4803). Passer par Type.GetTypeFromProgID("Microsoft.Update.Session") et dynamic, ou par des interfaces [ComImport] écrites à la main.
- Interaction entre réglages : le réglage existant « Désactiver le power throttling » (PowerThrottlingOff=1) risque de rendre inopérant le mode efficacité (EcoQoS) par processus de F14. À vérifier, et à signaler dans l'onglet.
- Regroupement de conversations proposé pour ce sujet :
- A : F12 + F7 (périphériques puis pilotes, même inventaire).
- B : F10 (extension de Paramètres > Installations et du catalogue).
- C : F14, à rapprocher de F5 (core parking et affinité), pour les limites par processus.
- D : F15 + correction du réglage VisualFXSetting (petit).
- E : F11 seul (risque de perte de données, relecture approfondie conseillée).
- F : F13, une fois le sens précisé par Denis (compteurs RAM + ADLX VGM).
