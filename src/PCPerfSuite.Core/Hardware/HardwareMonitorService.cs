using System.Diagnostics;
using System.Net.NetworkInformation;
using LibreHardwareMonitor.Hardware;
using PCPerfSuite.Core.Hardware.Cpu;
using PCPerfSuite.Core.Hardware.Cpu.Throttle;
using PCPerfSuite.Core.Hardware.Fans;
using PCPerfSuite.Core.Hardware.LaptopFans;
using PCPerfSuite.Core.Hardware.Memory;
using PCPerfSuite.Core.Hardware.Storage;
using PCPerfSuite.Core.Overlay;
using PCPerfSuite.Core.SystemInfo;

namespace PCPerfSuite.Core.Hardware;

/// <summary>
/// Encapsule LibreHardwareMonitorLib pour exposer un instantané typé et
/// indépendant du fabricant (Intel/AMD, NVIDIA/AMD/Intel) plutôt que l'arbre
/// brut de capteurs. Nécessite les droits administrateur pour la plupart des
/// capteurs, qui passent par le pilote PawnIO (à installer séparément : sans lui, les
/// capteurs bas niveau comme les températures CPU restent vides).
/// </summary>
public sealed partial class HardwareMonitorService : IFanController, IDisposable
{
    private readonly Computer _computer;
    private readonly UpdateVisitor _visitor = new();
    private bool _disposed;

    /// <summary>Sérialise le relevé et la fermeture. Le relevé tourne sur son propre thread pendant que le
    /// thread d'interface peut, lui, appeler <see cref="Dispose"/> (fermeture de la fenêtre). La boucle de
    /// relevé abandonne son attente au bout de quelques secondes : sans ce verrou, _computer.Close()
    /// s'exécuterait pendant qu'un relevé encore en vol lit des objets LibreHardwareMonitor déjà fermés,
    /// et le plantage arriverait dans du code natif, à la fermeture — introuvable depuis un signalement.</summary>
    private readonly object _gate = new();

    /// <summary>Part du temps qu'un groupe de capteurs peut passer à se lire en cadence automatique. Un CPU Intel,
    /// que LibreHardwareMonitor lit cœur par cœur (~30 ms mesurés sur un i5-13500T), est ainsi relu toutes les
    /// ~600 ms, et un groupe quasi gratuit à chaque relevé.</summary>
    public const double AutoReadBudget = 0.05;

    /// <summary>Plafond de la cadence automatique, pour qu'un groupe très coûteux reste quand même à jour.</summary>
    public static readonly TimeSpan MaxAutoInterval = TimeSpan.FromSeconds(5);

    private readonly SensorReadSchedule[] _schedules =
        Enum.GetValues<SensorGroup>().Select(group => new SensorReadSchedule(group)).ToArray();

    /// <summary>Charge CPU (groupe CpuLoad), lue indépendamment de la lecture LibreHardwareMonitor du CPU.</summary>
    private readonly PdhCounterSampler _cpuLoad = new(PdhCounterSampler.ProcessorUtility);

    /// <summary>Batterie (groupe Battery), lue auprès du pilote Windows plutôt que par LibreHardwareMonitor : il faut
    /// l'état secteur et la capacité nominale, que la lib n'expose pas toutes.</summary>
    private readonly BatteryReader _battery = new();

    // Dernières valeurs lues hors LibreHardwareMonitor, reprises dans les relevés où leur groupe n'est pas relu.
    private float? _lastCpuLoad;
    private RtssFrameStats? _lastGame;
    private BatterySnapshot? _lastBattery;
    private CpuThrottleReading? _lastCpuThrottle;

    /// <summary>Bridage CPU (groupe Cpu), créé à la première lecture, sur le thread du relevé : il charge des modules
    /// PawnIO, ce qui n'a rien à faire sur le thread de l'interface au démarrage.</summary>
    private CpuThrottleSampler? _cpuThrottle;

    /// <summary>Dernier relevé complet, rendu tel quel si le suivant échoue.</summary>
    private HardwareSnapshot? _lastSnapshot;

    /// <summary>Relevés consécutifs rendus depuis le cache faute de mieux.</summary>
    private int _staleTicks;

    /// <summary>Au-delà, l'échec n'est plus un accident : l'exception repart chez l'appelant, qui affiche
    /// le bandeau d'erreur. Resservir indéfiniment le dernier relevé afficherait des valeurs mortes sous
    /// les traits d'une mesure — une courbe parfaitement plate, aucun message, et un utilisateur qui croit
    /// son processeur à 42 °C alors que plus rien n'est lu.</summary>
    private const int MaxStaleTicks = 3;
    private IReadOnlyList<LaptopFanReading> _lastLaptopFans = Array.Empty<LaptopFanReading>();

    /// <summary>Marque du GPU piloté par l'onglet GPU. Dans un PC à deux GPU (iGPU + carte dédiée), le
    /// relevé "GPU" porte alors sur cette carte-là, pour que températures et fréquences affichées
    /// correspondent à celle qu'on overclocke. Null : le dernier GPU rencontré, comme avant.</summary>
    public GpuVendor? PreferredGpuVendor { get; set; }

    /// <summary>Nom de la carte pilotée par l'onglet GPU (NVAPI/ADLX/IGCL), pour départager deux GPU de
    /// la même marque (APU Ryzen + Radeon dédiée, UHD + Arc) que <see cref="PreferredGpuVendor"/> seul ne
    /// distingue pas : sans lui, le relevé retombait sur le premier des deux rencontré par
    /// LibreHardwareMonitor, parfois l'iGPU plutôt que la carte réellement overclockée/ventilée.</summary>
    public string? PreferredGpuName { get; set; }

    /// <summary>Groupes lus au moins une fois depuis le démarrage : une valeur encore nulle après la lecture de
    /// son groupe n'est pas "en attente" mais absente de ce PC.</summary>
    private readonly bool[] _everRead = new bool[Enum.GetValues<SensorGroup>().Length];

    /// <summary>Ventilateurs des portables, lus via l'interface du constructeur (LibreHardwareMonitor ne les voit pas).</summary>
    public LaptopFanService LaptopFans { get; }

    /// <summary>Termine quand <see cref="Computer.Open"/> a fini de recenser le matériel. Lancé en tâche de
    /// fond dès la construction plutôt qu'exécuté ici : un incident déjà observé (MSI Afterburner ou
    /// Armoury Crate tenant le mutex SMBus) bloque cet appel indéfiniment, et l'exécuter sur le thread
    /// appelant — celui de l'interface, avant l'affichage de la fenêtre — laissait alors l'app sans
    /// fenêtre ni icône, en apparence non démarrée.</summary>
    private readonly Task _openTask;

    private readonly long _openStartedTimestamp = Stopwatch.GetTimestamp();

    /// <summary>Faux tant que le matériel n'a pas fini d'être recensé (voir <see cref="_openTask"/>).
    /// Les onglets affichent alors les métriques en attente (« -- ») plutôt que rien, et les écritures
    /// (ventilateurs) sont refusées le temps que ce soit prêt, plutôt que de risquer de toucher
    /// <see cref="_computer"/> pendant qu'il est encore en cours d'ouverture sur l'autre thread.</summary>
    public bool IsReady => _openTask.IsCompleted;

    /// <summary>Depuis combien de temps l'ouverture est en cours, pour afficher un message si elle traîne
    /// (voir le diagnostic de compatibilité). Zéro une fois prête.</summary>
    public TimeSpan InitializingDuration => IsReady ? TimeSpan.Zero : Stopwatch.GetElapsedTime(_openStartedTimestamp);

    public HardwareMonitorService()
    {
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsMotherboardEnabled = true,
            IsStorageEnabled = true,
            IsNetworkEnabled = true,
            // Alimentations connectées (Corsair HXi/RMi...) : seule mesure de la conso totale d'un PC fixe.
            IsPsuEnabled = true,
        };

        _openTask = Task.Run(() =>
        {
            _computer.Open();
            // Avant que IsReady ne passe à vrai, donc avant toute écriture de l'app : l'état trouvé en arrivant.
            FanChipDiagnostic.Record(_computer, "démarrage, avant toute écriture de l'app");
        });

        LaptopFans = new LaptopFanService(MachineInfo.Current);
        _leases.Changed += OnLeasesChanged;
    }

    /// <summary>Relevé du tick <paramref name="tick"/> : chaque groupe n'est relu que si c'est son tour.</summary>
    /// <param name="epoch">Change avec la durée du tick (voir <see cref="TickInterval"/>) ; les numéros de tick repartent alors de zéro.</param>
    /// <exception cref="ObjectDisposedException">Le service a été fermé : l'appelant traite déjà l'échec
    /// d'un relevé, et à ce stade l'app est en train de se fermer.</exception>
    public HardwareSnapshot GetSnapshot(long epoch, long tick)
    {
        // Attend hors du verrou : si l'ouverture est bloquée (voir _openTask), Dispose() doit pouvoir
        // fermer l'app sans attendre après elle.
        _openTask.GetAwaiter().GetResult();

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            try
            {
                HardwareSnapshot snapshot = GetSnapshotCore(epoch, tick);
                _staleTicks = 0;
                return _lastSnapshot = snapshot;
            }
            catch (Exception) when (_lastSnapshot is not null && _staleTicks < MaxStaleTicks)
            {
                // Un capteur qui lève au passage — pilote rechargé, matériel retiré à chaud — ne doit pas
                // priver l'utilisateur de tous les autres : le relevé précédent tient quelques ticks, le
                // temps que la cause disparaisse. Si elle ne disparaît pas, l'exception repart chez
                // l'appelant, qui affiche et journalise : une valeur qu'on ne sait plus lire doit se dire.
                _staleTicks++;
                return _lastSnapshot;
            }
        }
    }

    private HardwareSnapshot GetSnapshotCore(long epoch, long tick)
    {
        long start = Stopwatch.GetTimestamp();

        var timings = new List<HardwareReadTiming>();
        var groupDurations = new TimeSpan[_schedules.Length];
        var groupRead = new bool[_schedules.Length];

        // Échéances évaluées une fois pour tout le relevé : le matériel d'un même groupe est relu ensemble.
        TimeSpan tickInterval = TickInterval;
        bool[] due = _schedules.Select(schedule => schedule.IsDue(epoch, tick, tickInterval)).ToArray();

        if (due[(int)SensorGroup.CpuLoad])
        {
            long loadStart = Stopwatch.GetTimestamp();
            // Le Gestionnaire des tâches plafonne aussi l'utilité à 100 %.
            _lastCpuLoad = _cpuLoad.Sample() is { } load ? (float)Math.Clamp(load, 0, 100) : null;
            RecordRead(SensorGroup.CpuLoad, "cpuload", "Charge CPU (compteur Windows)", loadStart, timings, groupDurations, groupRead);
        }

        if (due[(int)SensorGroup.Fps])
        {
            long fpsStart = Stopwatch.GetTimestamp();
            _lastGame = RtssFrameStatsReader.TryReadForeground();
            RecordRead(SensorGroup.Fps, "rtss", "FPS (RTSS)", fpsStart, timings, groupDurations, groupRead);
        }

        if (due[(int)SensorGroup.Battery])
        {
            long batteryStart = Stopwatch.GetTimestamp();
            _lastBattery = _battery.Read();
            RecordRead(SensorGroup.Battery, "battery", "Batterie (pilote Windows)", batteryStart, timings, groupDurations, groupRead);
        }

        if (due[(int)SensorGroup.Cpu])
        {
            long throttleStart = Stopwatch.GetTimestamp();
            _lastCpuThrottle = ReadCpuThrottle() ?? _lastCpuThrottle;
            RecordRead(SensorGroup.Cpu, "throttle", "Bridage CPU (MSR, PM table, compteurs Windows)", throttleStart, timings, groupDurations, groupRead);
        }

        if (due[(int)SensorGroup.Motherboard] && LaptopFans.Support == LaptopFanSupport.Active)
        {
            long fansStart = Stopwatch.GetTimestamp();
            _lastLaptopFans = LaptopFans.Read();
            RecordRead(SensorGroup.Motherboard, "laptopfans", $"Ventilateurs {LaptopFans.Vendor} (WMI)", fansStart, timings, groupDurations, groupRead);
        }

        foreach (IHardware hardware in _computer.Hardware)
        {
            int group = (int)GroupOf(hardware.HardwareType);
            if (!due[group]) continue;

            long hardwareStart = Stopwatch.GetTimestamp();
            hardware.Accept(_visitor);
            TimeSpan duration = Stopwatch.GetElapsedTime(hardwareStart);

            groupDurations[group] += duration;
            groupRead[group] = true;
            timings.Add(new HardwareReadTiming
            {
                Identifier = hardware.Identifier.ToString(),
                Name = hardware.Name,
                Duration = duration,
            });
        }

        for (int group = 0; group < _schedules.Length; group++)
        {
            if (!groupRead[group]) continue;
            _schedules[group].RecordRead(epoch, tick, groupDurations[group], tickInterval);
            _everRead[group] = true;
        }

        LaptopFanReading? laptopGpuFan = _lastLaptopFans.FirstOrDefault(f => f.Role == LaptopFanRole.Gpu);

        var cpu = new CpuSnapshot();
        GpuSnapshot? gpu = null;
        bool gpuIsPreferred = false;
        var memory = new MemorySnapshot();
        // Températures par emplacement (« DIMM #0 »), relevées sur les matériels « barrette » de
        // LibreHardwareMonitor et raccordées ensuite à la fiche WMI, qui nomme les mêmes emplacements.
        var memoryTemperatures = new List<float?>();
        var motherboard = new MotherboardSnapshot();
        var fans = new List<FanReading>();
        var disks = new List<DiskSnapshot>();
        float? uploadRate = null;
        float? downloadRate = null;
        float? psuPower = null;

        // Mode éco : les matériels en pause ne sont pas relus, inutile d'analyser leurs capteurs figés. Le réseau
        // (liste des cartes à chaque tick), les disques et la mémoire sont les plus chers à analyser. CPU, GPU et
        // carte mère le restent toujours : les courbes de ventilateurs et la sécurité thermique en dépendent.
        bool skipMemory = IsSuspended(SensorGroup.Memory);
        bool skipStorage = IsSuspended(SensorGroup.Storage);
        bool skipNetwork = IsSuspended(SensorGroup.Network);

        foreach (IHardware hardware in _computer.Hardware)
        {
            switch (hardware.HardwareType)
            {
                case HardwareType.Memory when skipMemory:
                case HardwareType.Storage when skipStorage:
                case HardwareType.Network when skipNetwork:
                    break;

                case HardwareType.Cpu:
                    cpu = ReadCpu(hardware, _lastCpuLoad);
                    break;

                case HardwareType.GpuNvidia:
                case HardwareType.GpuAmd:
                case HardwareType.GpuIntel:
                    bool preferred = IsPreferredGpu(hardware.HardwareType);
                    // À marque égale (deux GPU de la même marque : APU + carte dédiée, UHD + Arc), le nom
                    // de la carte réellement pilotée (NVAPI/ADLX/IGCL) départage plutôt que de garder le
                    // premier des deux rencontré par LibreHardwareMonitor.
                    bool nameMatch = preferred && GpuNameMatches(hardware.Name);
                    bool gpuIsNameMatch = gpu is not null && GpuNameMatches(gpu.Name);
                    if (PreferredGpuVendor is null || gpu is null
                        || (preferred && !gpuIsPreferred)
                        || (preferred && gpuIsPreferred && nameMatch && !gpuIsNameMatch))
                    {
                        gpu = ReadGpu(hardware, laptopGpuFan);
                        gpuIsPreferred = preferred;
                    }
                    CollectFans(hardware, SensorGroup.Gpu, fans);
                    break;

                case HardwareType.Memory:
                    // FUSION, et non affectation. LibreHardwareMonitor expose plusieurs matériels de type
                    // Memory : « Virtual Memory », « Total Memory », puis UNE BARRETTE par module dès que
                    // le SPD est lisible sur le SMBus. Une barrette ne porte ni utilisation ni charge —
                    // seulement une capacité, des timings et une température — si bien qu'une affectation
                    // sèche faisait gagner le dernier matériel rencontré et effaçait les bonnes valeurs :
                    // la RAM s'affichait « N/D » sur les PC dont le SPD est lisible, pendant que le
                    // Gestionnaire des tâches, lui, montrait tout. Chaque champ est donc écrit par le
                    // premier matériel qui le fournit, comme le font déjà les branches Stockage et Réseau.
                    memory = MergeMemory(memory, ReadMemory(hardware));
                    CollectMemoryModuleTemperatures(hardware, memoryTemperatures);
                    break;

                case HardwareType.Motherboard:
                    motherboard = ReadMotherboard(hardware);
                    CollectFans(hardware, SensorGroup.Motherboard, fans);
                    foreach (IHardware sub in hardware.SubHardware)
                    {
                        CollectFans(sub, SensorGroup.Motherboard, fans);
                    }
                    break;

                case HardwareType.Storage:
                    disks.Add(ReadDisk(hardware));
                    break;

                case HardwareType.Network:
                    // Une carte virtuelle (VPN, commutateur Hyper-V, WSL) mesure souvent le même trafic que
                    // l'adaptateur physique sous-jacent : la sommer avec lui double le débit affiché.
                    if (!IsPhysicalNetworkAdapter(hardware.Name)) break;

                    uploadRate = AddIfPresent(uploadRate, FindSensor(hardware, SensorType.Throughput, "Upload Speed")?.Value);
                    downloadRate = AddIfPresent(downloadRate, FindSensor(hardware, SensorType.Throughput, "Download Speed")?.Value);
                    break;

                case HardwareType.Psu:
                    // "Total watts" chez Corsair : la puissance fournie au PC, toutes sorties confondues.
                    psuPower = AddIfPresent(psuPower, (FindSensor(hardware, SensorType.Power, "Total")
                        ?? hardware.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Power))?.Value);
                    break;
            }
        }

        CollectLaptopFans(_lastLaptopFans, fans);

        return new HardwareSnapshot
        {
            Cpu = cpu,
            Gpu = gpu,
            Memory = skipMemory ? memory : CompleteMemory(memory, memoryTemperatures),
            Motherboard = motherboard,
            Fans = FanIdentification.Label(fans),
            Disks = disks,
            Network = new NetworkSnapshot { UploadBytesPerSecond = uploadRate, DownloadBytesPerSecond = downloadRate },
            Battery = _lastBattery,
            PsuPowerWatts = psuPower,
            ReadTimings = timings,
            ReadDuration = Stopwatch.GetElapsedTime(start),
            GroupStatuses = _schedules.Select(schedule => schedule.GetStatus(tickInterval)).ToArray(),
            GroupsRead = Enum.GetValues<SensorGroup>().Where(group => groupRead[(int)group]).ToArray(),
            GroupsEverRead = Enum.GetValues<SensorGroup>().Where(group => _everRead[(int)group]).ToArray(),
            Game = _lastGame,
            CpuThrottle = _lastCpuThrottle,
        };
    }

    /// <summary>Bridage CPU, en fichiers dédiés (Cpu/Throttle) : balayage des cœurs seulement sous bail de cadence.
    /// Une erreur imprévue ne fait pas échouer le relevé : la dernière valeur reste.</summary>
    private CpuThrottleReading? ReadCpuThrottle()
    {
        try
        {
            _cpuThrottle ??= new CpuThrottleSampler(CpuPlatformDetector.Detect());
            return _cpuThrottle.Read(measuring: IsCadenceLeased(SensorGroup.Cpu));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Bridage CPU : {ex}");
            return null;
        }
    }

    /// <summary>Dernier relevé complet, pour le diagnostic (lu depuis n'importe quel thread) ; null avant le premier.</summary>
    public HardwareSnapshot? LastSnapshot => Volatile.Read(ref _lastSnapshot);

    /// <summary>Compte une lecture faite hors LibreHardwareMonitor (charge CPU, FPS) dans les durées du relevé.</summary>
    private static void RecordRead(SensorGroup group, string identifier, string name, long readStart,
        List<HardwareReadTiming> timings, TimeSpan[] groupDurations, bool[] groupRead)
    {
        TimeSpan duration = Stopwatch.GetElapsedTime(readStart);
        groupDurations[(int)group] += duration;
        groupRead[(int)group] = true;
        timings.Add(new HardwareReadTiming { Identifier = identifier, Name = name, Duration = duration });
    }

    private static SensorGroup GroupOf(HardwareType type) => type switch
    {
        HardwareType.Cpu => SensorGroup.Cpu,
        HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel => SensorGroup.Gpu,
        HardwareType.Memory => SensorGroup.Memory,
        HardwareType.Storage => SensorGroup.Storage,
        HardwareType.Network => SensorGroup.Network,
        HardwareType.Psu => SensorGroup.Battery,
        // Carte mère et le reste de ce que LibreHardwareMonitor rattache à la carte (Super I/O, contrôleur embarqué).
        _ => SensorGroup.Motherboard,
    };

    private static CpuSnapshot ReadCpu(IHardware hardware, float? sampledLoad)
    {
        // Charge mesurée à chaque relevé quand Windows la fournit, sinon celle de LibreHardwareMonitor (relue moins souvent).
        float? load = sampledLoad
                       ?? FindSensor(hardware, SensorType.Load, "CPU Total")?.Value
                       ?? hardware.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Load)?.Value;

        float? package = FindSensor(hardware, SensorType.Temperature, "CPU Package")?.Value;

        // Intel publie "Core Max" ; ailleurs (AMD : "Core (Tctl/Tdie)"...) on prend le max des sondes "Core",
        // sans les "CPU Core #n Distance to TjMax" d'Intel qui sont des marges, pas des températures.
        float? maxCore = FindSensor(hardware, SensorType.Temperature, "Core Max")?.Value
                         ?? hardware.Sensors
                             .Where(s => s.SensorType == SensorType.Temperature
                                         && s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase)
                                         && !s.Name.Contains("Distance", StringComparison.OrdinalIgnoreCase))
                             .Select(s => s.Value)
                             .Where(v => v.HasValue)
                             .DefaultIfEmpty()
                             .Max();

        float? power = ReadTotalPackagePower(hardware);

        float? maxClock = hardware.Sensors
            .Where(s => s.SensorType == SensorType.Clock)
            .Select(s => s.Value)
            .Where(v => v.HasValue)
            .DefaultIfEmpty()
            .Max();

        // Tension du package : "CPU Core" chez Intel, "Core (SVI2 TFN)" chez AMD Zen. Les tensions par cœur
        // ("CPU Core #n", "Core #n VID") sont écartées par le '#'.
        float? coreVoltage = hardware.Sensors.FirstOrDefault(s =>
            s.SensorType == SensorType.Voltage
            && s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase)
            && !s.Name.Contains('#'))?.Value;

        return new CpuSnapshot
        {
            Name = hardware.Name,
            LoadPercent = load,
            MaxCoreLoadPercent = FindSensor(hardware, SensorType.Load, "CPU Core Max")?.Value,
            PackageTempC = package ?? maxCore,
            MaxCoreTempC = maxCore,
            PowerWatts = power,
            MaxClockMhz = maxClock,
            CoreVoltage = coreVoltage,
        };
    }

    // Noms de capteur "puissance totale du package" connus, par ordre de priorité : Intel expose "CPU Package"
    // (RAPL), AMD "Package" (sans préfixe "CPU" : FindSensor("CPU Package") ne le matche donc jamais) ou,
    // selon la génération, "CPU PPT"/"Total Power". On ne retient volontairement qu'un nom qui identifie
    // explicitement le TOTAL du package, jamais un domaine partiel ("Core Power", "SOC Power" chez AMD) :
    // un domaine partiel donnerait une puissance CPU artificiellement basse, potentiellement bloquée à 0 W
    // sur les plateformes où ce domaine précis n'est pas peuplé sans le pilote PawnIO.
    private static readonly string[] TotalPackagePowerNames = { "CPU Package", "Package", "CPU PPT", "Total Power" };

    /// <summary>Puissance totale du CPU. Renvoie null (affiché "N/D", jamais "0 W") si aucun capteur reconnu
    /// comme "total du package" n'existe sur ce PC, plutôt que de retomber sur n'importe quel capteur de
    /// puissance au hasard (un domaine partiel bloqué à 0 W est une valeur valide, donc indiscernable d'une
    /// vraie mesure si on ne sait pas ce qu'on a pris).</summary>
    private static float? ReadTotalPackagePower(IHardware hardware)
    {
        foreach (string name in TotalPackagePowerNames)
        {
            if (FindSensor(hardware, SensorType.Power, name)?.Value is { } value) return value;
        }
        return null;
    }

    /// <summary>Interfaces réseau "physiques" vues par Windows (Ethernet, Wi-Fi, actives), pour ne pas
    /// sommer le débit d'une carte virtuelle (VPN, commutateur Hyper-V, WSL) avec celui de l'adaptateur
    /// qu'elle relaie réellement. Relu à chaque relevé du groupe Réseau (rarement dû) : brancher/débrancher
    /// un câble ou activer un VPN doit se refléter sans attendre un redémarrage de l'app.</summary>
    private static readonly NetworkInterfaceType[] PhysicalAdapterTypes =
    {
        NetworkInterfaceType.Ethernet, NetworkInterfaceType.Ethernet3Megabit, NetworkInterfaceType.FastEthernetT,
        NetworkInterfaceType.FastEthernetFx, NetworkInterfaceType.GigabitEthernet, NetworkInterfaceType.Wireless80211,
    };

    private static bool IsPhysicalNetworkAdapter(string hardwareName)
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces().Any(nic =>
                PhysicalAdapterTypes.Contains(nic.NetworkInterfaceType)
                && nic.OperationalStatus == OperationalStatus.Up
                && (hardwareName.Contains(nic.Description, StringComparison.OrdinalIgnoreCase)
                    || nic.Description.Contains(hardwareName, StringComparison.OrdinalIgnoreCase)));
        }
        catch
        {
            // Best-effort : si Windows ne répond pas, on préfère compter la carte (comportement d'avant ce
            // correctif) plutôt que de faire disparaître tout le débit réseau.
            return true;
        }
    }

    private bool IsPreferredGpu(HardwareType type) => (PreferredGpuVendor, type) switch
    {
        (GpuVendor.Nvidia, HardwareType.GpuNvidia) => true,
        (GpuVendor.Amd, HardwareType.GpuAmd) => true,
        (GpuVendor.Intel, HardwareType.GpuIntel) => true,
        _ => false,
    };

    /// <summary>Comparaison tolérante : NVAPI/ADLX/IGCL et LibreHardwareMonitor n'orthographient pas
    /// forcément le nom de la carte à l'identique (espaces, sigles ® / (R), suffixes). L'un contenant
    /// l'autre suffit à départager deux GPU de la même marque.</summary>
    private bool GpuNameMatches(string hardwareName)
    {
        if (string.IsNullOrWhiteSpace(PreferredGpuName) || string.IsNullOrWhiteSpace(hardwareName)) return false;

        string a = NormalizeGpuName(PreferredGpuName);
        string b = NormalizeGpuName(hardwareName);
        return a.Length > 0 && b.Length > 0 && (a.Contains(b, StringComparison.OrdinalIgnoreCase) || b.Contains(a, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeGpuName(string name) =>
        new string(name.Where(char.IsLetterOrDigit).ToArray());

    /// <param name="laptopFan">Ventilateur GPU du portable, repris quand le pilote graphique n'en expose aucun
    /// (NVAPI, ADLX et IGCL ne voient pas les ventilateurs pilotés par le contrôleur embarqué d'un portable).</param>
    private static GpuSnapshot ReadGpu(IHardware hardware, LaptopFanReading? laptopFan)
    {
        string vendor = hardware.HardwareType switch
        {
            HardwareType.GpuNvidia => "NVIDIA",
            HardwareType.GpuAmd => "AMD",
            HardwareType.GpuIntel => "Intel",
            _ => "Inconnu",
        };

        var fanSensor = hardware.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Fan);
        var fanControl = hardware.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Control);

        return new GpuSnapshot
        {
            Name = hardware.Name,
            Vendor = vendor,
            LoadPercent = FindSensor(hardware, SensorType.Load, "GPU Core")?.Value
                          ?? hardware.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Load)?.Value,
            CoreTempC = FindSensor(hardware, SensorType.Temperature, "GPU Core")?.Value,
            HotSpotTempC = FindSensor(hardware, SensorType.Temperature, "GPU Hot Spot")?.Value,
            // "GPU Memory Junction" chez NVIDIA, "GPU Memory" chez AMD.
            MemoryJunctionTempC = FindSensor(hardware, SensorType.Temperature, "Memory")?.Value,
            CoreClockMhz = FindSensor(hardware, SensorType.Clock, "GPU Core")?.Value,
            MemoryClockMhz = FindSensor(hardware, SensorType.Clock, "GPU Memory")?.Value,
            PowerWatts = FindSensor(hardware, SensorType.Power, "GPU Package")?.Value
                         ?? hardware.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Power)?.Value,
            VramUsedMb = FindSensor(hardware, SensorType.SmallData, "GPU Memory Used")?.Value,
            VramTotalMb = FindSensor(hardware, SensorType.SmallData, "GPU Memory Total")?.Value,
            FanRpm = fanSensor is null ? laptopFan?.Rpm : fanSensor.Value,
            FanPercent = fanSensor is null ? laptopFan?.Percent : fanControl?.Value,
        };
    }

    /// <summary>
    /// Matériels et capteurs mémoire tels que LibreHardwareMonitor les expose, en texte brut, pour le rapport
    /// de « Compatibilité de ce PC ». C'est la seule façon, depuis un signalement, de voir qu'une machine
    /// énumère un matériel inattendu ou que la bibliothèque a renommé un capteur : sans cette liste, la RAM
    /// vide d'un mini-PC restait impossible à diagnostiquer à distance.
    /// </summary>
    public IReadOnlyList<string> DescribeMemorySensors()
    {
        if (!IsReady) return new[] { "Capteurs en cours d'initialisation…" };

        lock (_gate)
        {
            if (_disposed) return Array.Empty<string>();

            var lines = new List<string>();
            foreach (IHardware hardware in _computer.Hardware)
            {
                if (hardware.HardwareType != HardwareType.Memory) continue;

                lines.Add($"{hardware.Name} [{hardware.Identifier}]");
                foreach (ISensor sensor in hardware.Sensors)
                {
                    lines.Add($"    {sensor.SensorType} « {sensor.Name} » = {sensor.Value?.ToString("0.###") ?? "null"}");
                }
            }
            return lines;
        }
    }

    /// <summary>Un matériel, ou un capteur, dont le nom parle de mémoire virtuelle ne décrit jamais la RAM
    /// physique. Selon la version de la bibliothèque, les deux familles vivent sur un seul matériel — les
    /// capteurs « Memory Used » et « Virtual Memory Used » côte à côte — ou sur deux matériels distincts,
    /// « Generic Memory » et « Virtual Memory ». D'où les deux niveaux de contrôle.</summary>
    private static bool NamesVirtualMemory(string name)
        => name.Contains("Virtual", StringComparison.OrdinalIgnoreCase);

    private static MemorySnapshot ReadMemory(IHardware hardware)
    {
        // Le matériel porteur prime sur le nom du capteur. Quand « Virtual Memory » est un matériel à part,
        // rien n'oblige ses capteurs à porter le mot « Virtual » dans LEUR nom : les trier sur ce seul mot
        // laissait alors passer de la mémoire virtuelle pour de la RAM physique, et affichait une mémoire
        // utilisée supérieure à la mémoire installée.
        bool hardwareIsVirtual = NamesVirtualMemory(hardware.Name)
                                 || NamesVirtualMemory(hardware.Identifier.ToString());

        ISensor[] virtualSensors = hardwareIsVirtual
            ? hardware.Sensors.ToArray()
            : hardware.Sensors.Where(s => NamesVirtualMemory(s.Name)).ToArray();

        ISensor[] physicalSensors = hardwareIsVirtual
            ? Array.Empty<ISensor>()
            : hardware.Sensors.Except(virtualSensors).ToArray();

        // Une barrette n'expose qu'une « Capacity » : aucun de ces noms ne lui correspond, elle ne
        // contribue donc rien. C'est voulu — sa capacité n'est pas de la mémoire utilisée.
        float? used = PhysicalValue(physicalSensors, SensorType.Data, "Memory Used");
        float? available = PhysicalValue(physicalSensors, SensorType.Data, "Memory Available");
        float? load = PhysicalValue(physicalSensors, SensorType.Load, "Memory")
                      ?? physicalSensors.FirstOrDefault(s => s.SensorType == SensorType.Load)?.Value;

        float? virtualUsed = ExactSensor(virtualSensors, SensorType.Data, "Virtual Memory Used")?.Value
                             ?? ExactSensor(virtualSensors, SensorType.Data, "Memory Used")?.Value;

        return new MemorySnapshot
        {
            UsedGb = used,
            AvailableGb = available,

            // Pas de total déduit de « utilisée + disponible » : les deux peuvent venir de capteurs
            // différents, et l'addition a affiché 37 Go de RAM installée sur une machine qui en a 16. Le
            // total vient de Windows, ou à défaut de la somme des barrettes (voir CompleteMemory).
            LoadPercent = load,
            VirtualUsedGb = virtualUsed,
            Sources = used is not null || load is not null || virtualUsed is not null
                ? MemorySource.LibreHardwareMonitor
                : MemorySource.None,
        };
    }

    /// <summary>Cherche d'abord le nom exact, puis seulement à défaut par sous-chaîne — un nom qui change
    /// d'une version de la bibliothèque à l'autre ne doit pas vider une colonne entière. L'ordre compte :
    /// « Memory Used » est contenu dans « Virtual Memory Used », et la recherche par sous-chaîne seule
    /// rendait l'un pour l'autre selon l'ordre d'énumération. Le repli est sans danger ici, les capteurs
    /// virtuels ayant déjà été écartés de <paramref name="physicalSensors"/>.</summary>
    private static float? PhysicalValue(ISensor[] physicalSensors, SensorType type, string name)
        => (ExactSensor(physicalSensors, type, name) ?? FindSensor(physicalSensors, type, name))?.Value;

    /// <summary>Complète <paramref name="current"/> par <paramref name="addition"/>, champ par champ, sans
    /// jamais écraser une valeur déjà lue : sur une machine où plusieurs matériels mémoire coexistent,
    /// celui qui répond gagne, quel que soit son rang dans l'énumération. La source ajoutée n'est déclarée
    /// que si elle a réellement comblé quelque chose — le diagnostic doit nommer ce qui a répondu, pas ce
    /// qu'on a interrogé.</summary>
    private static MemorySnapshot MergeMemory(MemorySnapshot current, MemorySnapshot addition)
    {
        bool contributed =
            (current.UsedGb is null && addition.UsedGb is not null)
            || (current.AvailableGb is null && addition.AvailableGb is not null)
            || (current.TotalGb is null && addition.TotalGb is not null)
            || (current.LoadPercent is null && addition.LoadPercent is not null)
            || (current.VirtualUsedGb is null && addition.VirtualUsedGb is not null)
            || (current.VirtualTotalGb is null && addition.VirtualTotalGb is not null);

        return new MemorySnapshot
        {
            UsedGb = current.UsedGb ?? addition.UsedGb,
            AvailableGb = current.AvailableGb ?? addition.AvailableGb,
            TotalGb = current.TotalGb ?? addition.TotalGb,
            LoadPercent = current.LoadPercent ?? addition.LoadPercent,
            VirtualUsedGb = current.VirtualUsedGb ?? addition.VirtualUsedGb,
            VirtualTotalGb = current.VirtualTotalGb ?? addition.VirtualTotalGb,
            Modules = current.Modules.Count > 0 ? current.Modules : addition.Modules,
            SlotCount = current.SlotCount ?? addition.SlotCount,
            TypeLabel = current.TypeLabel ?? addition.TypeLabel,
            SpeedMhz = current.SpeedMhz ?? addition.SpeedMhz,
            ModulesUnavailableReason = current.ModulesUnavailableReason ?? addition.ModulesUnavailableReason,
            Sources = contributed ? current.Sources | addition.Sources : current.Sources,
        };
    }

    /// <summary>Températures des barrettes exposées par un matériel mémoire. Seules les barrettes en
    /// portent, et seulement quand leur sonde est lisible sur le SMBus : la liste reste souvent vide.</summary>
    private static void CollectMemoryModuleTemperatures(IHardware hardware, List<float?> temperatures)
    {
        foreach (ISensor sensor in hardware.Sensors)
        {
            // Les barrettes exposent aussi des seuils ("Thermal Sensor High Limit"...) qui sont des
            // températures sans en être : seul le capteur nommé d'après l'emplacement est une mesure.
            if (sensor.SensorType != SensorType.Temperature) continue;
            if (!sensor.Name.StartsWith("DIMM", StringComparison.OrdinalIgnoreCase)) continue;

            temperatures.Add(sensor.Value);
        }
    }

    /// <summary>Dernière étape du relevé mémoire : poser les valeurs de Windows, laisser
    /// LibreHardwareMonitor combler ce qui manque, puis y raccrocher la fiche matérielle. Après cet appel,
    /// aucune valeur d'utilisation ne doit manquer sur une machine sous Windows.</summary>
    private static MemorySnapshot CompleteMemory(MemorySnapshot memory, List<float?> temperatures)
    {
        // Windows EN PREMIER. GlobalMemoryStatusEx est la source du Gestionnaire des tâches : présente sur
        // tous les PC, sans pilote ni droits d'administrateur, et insensible au nom que LibreHardwareMonitor
        // donne à ses capteurs. L'ordre inverse — celui d'origine — laissait une valeur fausse de la
        // bibliothèque verrouiller le champ pour de bon, puisque la fusion ne réécrit jamais par-dessus une
        // valeur déjà posée : le repli Windows ne pouvait alors corriger que ce qui manquait, jamais ce qui
        // était faux.
        if (SystemMemoryReader.Read() is { } windows)
        {
            memory = MergeMemory(new MemorySnapshot
            {
                UsedGb = windows.UsedGb,
                AvailableGb = windows.AvailableGb,
                TotalGb = windows.TotalGb,
                LoadPercent = windows.LoadPercent,
                VirtualUsedGb = windows.VirtualUsedGb,
                VirtualTotalGb = windows.VirtualTotalGb,
                Sources = MemorySource.Windows,
            }, memory);
        }

        MemoryModuleReader.Report report = MemoryModuleReader.Current;
        IReadOnlyList<MemoryModuleInfo> modules = WithTemperatures(report.Modules, temperatures);

        // À défaut de total lu, la somme des barrettes décrites par le BIOS. Elle dépasse un peu ce que
        // Windows déclare — une part de la mémoire est réservée au matériel — mais elle donne le repère
        // qui manque pour juger des valeurs d'utilisation.
        float? total = memory.TotalGb ?? SumCapacity(modules);
        bool usageCoherent = UsageFitsIn(memory, total);

        // Utilisation, disponibilité ET charge viennent du même matériel : elles tombent ensemble, sinon
        // l'app affiche « N/D » d'un côté et une charge de 118 % de l'autre. Et la source écartée cesse
        // d'être annoncée comme ayant répondu, sans quoi le rapport de bug désigne un coupable innocent.
        MemorySource sources = usageCoherent ? memory.Sources : memory.Sources & ~MemorySource.LibreHardwareMonitor;

        return new MemorySnapshot
        {
            UsedGb = usageCoherent ? memory.UsedGb : null,
            AvailableGb = usageCoherent ? memory.AvailableGb : null,
            TotalGb = total,
            LoadPercent = usageCoherent ? memory.LoadPercent : null,
            VirtualUsedGb = memory.VirtualUsedGb,
            VirtualTotalGb = memory.VirtualTotalGb,
            Modules = modules,
            SlotCount = report.SlotCount,
            TypeLabel = CommonValue(report.Modules.Select(m => m.TypeLabel)),
            SpeedMhz = CommonValue(report.Modules.Select(m => m.SpeedMhz)),
            ModulesUnavailableReason = report.UnavailableReason,
            Sources = report.HasModules ? sources | MemorySource.Wmi : sources,
        };
    }

    private static float? SumCapacity(IReadOnlyList<MemoryModuleInfo> modules)
    {
        double sum = modules.Sum(module => module.CapacityGb ?? 0);
        return sum > 0 ? (float)sum : null;
    }

    /// <summary>
    /// Dernier filet : une mémoire utilisée — ou disponible — supérieure à la mémoire installée est
    /// forcément fausse. Mieux vaut « N/D », que le diagnostic sait expliquer et que l'utilisateur peut
    /// signaler, qu'un nombre auquel il va croire.
    ///
    /// Ce n'est pas ce filet qui corrige les 18,8 Go affichés sur une machine de 16 Go : dès que Windows
    /// répond, utilisée, disponible et totale viennent toutes de lui et sont cohérentes par construction,
    /// et c'est l'ordre des sources qui fait le travail. Le filet ne sert que dans l'angle mort qui reste —
    /// GlobalMemoryStatusEx muet, total déduit des barrettes, valeurs d'utilisation venues d'ailleurs.
    /// </summary>
    private static bool UsageFitsIn(MemorySnapshot memory, float? total)
    {
        if (total is not { } installed || installed <= 0) return true;

        // Les sources ne datent pas de la même milliseconde, et le total de Windows exclut la mémoire
        // réservée au matériel : une marge est nécessaire pour ne pas écarter des valeurs justes.
        float ceiling = installed * 1.05f;
        return (memory.UsedGb ?? 0) <= ceiling && (memory.AvailableGb ?? 0) <= ceiling;
    }

    /// <summary>Raccroche les températures lues par LibreHardwareMonitor aux barrettes décrites par WMI, dans
    /// l'ordre des emplacements — le seul lien commun aux deux sources. S'il n'y a pas autant de
    /// températures que de barrettes, aucune n'est attribuée : une sonde posée sur la mauvaise barrette
    /// serait pire qu'une case vide.</summary>
    private static IReadOnlyList<MemoryModuleInfo> WithTemperatures(
        IReadOnlyList<MemoryModuleInfo> modules, List<float?> temperatures)
    {
        if (modules.Count == 0 || temperatures.Count != modules.Count) return modules;

        return modules.Select((module, index) => new MemoryModuleInfo
        {
            Slot = module.Slot,
            BankLabel = module.BankLabel,
            CapacityGb = module.CapacityGb,
            SpeedMhz = module.SpeedMhz,
            TypeLabel = module.TypeLabel,
            FormFactorLabel = module.FormFactorLabel,
            Manufacturer = module.Manufacturer,
            PartNumber = module.PartNumber,
            TemperatureC = temperatures[index],
        }).ToList();
    }

    /// <summary>Valeur partagée par toutes les barrettes, ou null si elles diffèrent : annoncer « DDR5 » sur
    /// un PC qui mêle deux types serait faux, et sur ce point mieux vaut ne rien dire.</summary>
    private static T? CommonValue<T>(IEnumerable<T?> values) where T : struct
    {
        List<T> present = values.Where(v => v.HasValue).Select(v => v!.Value).Distinct().ToList();
        return present.Count == 1 ? present[0] : null;
    }

    private static string? CommonValue(IEnumerable<string?> values)
    {
        List<string> present = values.Where(v => v is { Length: > 0 }).Select(v => v!)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return present.Count == 1 ? present[0] : null;
    }

    private static MotherboardSnapshot ReadMotherboard(IHardware hardware)
    {
        // Les capteurs de la carte mère vivent souvent sur le sous-matériel (puce Super I/O).
        List<ISensor> subSensors = hardware.SubHardware.SelectMany(sub => sub.Sensors).ToList();

        List<ISensor> allTempSensors = hardware.Sensors.Concat(subSensors)
            .Where(s => s.SensorType == SensorType.Temperature)
            .ToList();

        ISensor? systemSensor = allTempSensors.FirstOrDefault(s =>
            s.Name.Contains("System", StringComparison.OrdinalIgnoreCase)
            || s.Name.Contains("Motherboard", StringComparison.OrdinalIgnoreCase));

        ISensor? vrmSensor = allTempSensors.FirstOrDefault(s =>
            s.Name.Contains("VRM", StringComparison.OrdinalIgnoreCase));

        // LibreHardwareMonitor ne nomme "System"/"VRM"... que sur les cartes mères présentes dans sa
        // table de correspondance par modèle. Sur les autres (beaucoup de cartes récentes), la puce
        // Super I/O reste nommée génériquement ("Temperature #1"...) et les deux Contains ci-dessus ne
        // trouvent rien : on se rabat sur les deux premières sondes plutôt que d'afficher deux "--" qui
        // donneraient à tort l'impression que ces données sont perdues.
        systemSensor ??= allTempSensors.FirstOrDefault();
        vrmSensor ??= allTempSensors.FirstOrDefault(s => s != systemSensor);

        List<SensorReading> otherTemps = allTempSensors
            .Where(s => s != systemSensor && s != vrmSensor)
            .Select(s => new SensorReading { Name = s.Name, Value = s.Value })
            .ToList();

        List<SensorReading> voltages = hardware.Sensors.Concat(subSensors)
            .Where(s => s.SensorType == SensorType.Voltage)
            .Select(s => new SensorReading { Name = s.Name, Value = s.Value })
            .ToList();

        return new MotherboardSnapshot
        {
            Name = hardware.Name,
            SystemTempLabel = FriendlyOrRawName(systemSensor, "Température système"),
            SystemTempC = systemSensor?.Value,
            VrmTempLabel = FriendlyOrRawName(vrmSensor, "VRM"),
            VrmTempC = vrmSensor?.Value,
            OtherTemperatures = otherTemps,
            Voltages = voltages,
        };
    }

    /// <summary>Le libellé français quand le nom du capteur confirme qu'il s'agit bien de cette zone,
    /// sinon son nom brut (repli générique "Temperature #N") pour ne jamais mal étiqueter une valeur
    /// dont on n'est pas sûr qu'elle corresponde vraiment au système/au VRM.</summary>
    private static string FriendlyOrRawName(ISensor? sensor, string friendlyName)
    {
        if (sensor is null) return friendlyName;

        bool confirmedByName = sensor.Name.Contains("System", StringComparison.OrdinalIgnoreCase)
            || sensor.Name.Contains("Motherboard", StringComparison.OrdinalIgnoreCase)
            || sensor.Name.Contains("VRM", StringComparison.OrdinalIgnoreCase);

        return confirmedByName ? friendlyName : sensor.Name;
    }

    private static DiskSnapshot ReadDisk(IHardware hardware)
    {
        float? usedPercent = FindSensor(hardware, SensorType.Load, "Used Space")?.Value;

        // « Total Activity » : temps d'occupation du disque, l'équivalent de la colonne « Activité » du Gestionnaire
        // des tâches. LibreHardwareMonitor le tire des compteurs de performance de Windows, donc il ne dépend ni de
        // la marque ni du type de disque. Borné : un compteur peut brièvement dépasser 100.
        float? activity = FindSensor(hardware, SensorType.Load, "Total Activity")?.Value;
        if (activity is { } busy) activity = Math.Clamp(busy, 0, 100);

        float? readRate = FindSensor(hardware, SensorType.Throughput, "Read Rate")?.Value;
        float? writeRate = FindSensor(hardware, SensorType.Throughput, "Write Rate")?.Value;
        float? temp = hardware.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature)?.Value;

        // LibreHardwareMonitor (via DiskInfoToolkit) publie un capteur "Life" normalisé — un % de vie
        // restante déjà calculé quel que soit le fabricant, à partir de quel que soit l'attribut SMART
        // spécifique au vendeur qu'il a réussi à lire ("SSD Life Left", "Percent Lifetime Used"...). On le
        // cherche en priorité : plus fiable et plus répandu que les noms d'attributs bruts ci-dessous.
        // "Remaining Life"/"Percentage Used" restent en repli pour les cas où seul l'attribut brut existe :
        // le premier est déjà un % restant, le second (convention NVMe standard) une usure consommée,
        // donc vie restante = 100 - valeur. Si rien de tout ça n'existe (la plupart des HDD), reste null
        // (affiché "--").
        float? remainingLife = FindSensor(hardware, SensorType.Level, "Life")?.Value
                                ?? FindSensor(hardware, SensorType.Level, "Remaining Life")?.Value;
        if (remainingLife is null)
        {
            float? percentageUsed = FindSensor(hardware, SensorType.Level, "Percentage Used")?.Value;
            if (percentageUsed is { } used) remainingLife = Math.Clamp(100 - used, 0, 100);
        }

        (string name, DiskNameSource nameSource) = ResolveDiskName(hardware);
        IReadOnlyList<DiskVolume> volumes = DiskIndexOf(hardware) is { } diskIndex
            ? DiskVolumeReader.ForDisk(diskIndex)
            : Array.Empty<DiskVolume>();

        return new DiskSnapshot
        {
            Name = name,
            NameSource = nameSource,
            Volumes = volumes,
            Identifier = hardware.Identifier.ToString(),
            UsedPercent = usedPercent,
            ActivityPercent = activity,
            ReadRateBytesPerSecond = readRate,
            WriteRateBytesPerSecond = writeRate,
            TemperatureC = temp,
            RemainingLifePercent = remainingLife,
        };
    }

    /// <summary>hardware.Name (le modèle lu par LibreHardwareMonitor) est vide sur certains contrôleurs
    /// NVMe/ponts dont l'IDENTIFY échoue alors que le reste (SMART, débit...) se lit sans problème :
    /// dans ce cas, on retombe sur Win32_DiskDrive, indexé par le même numéro de disque physique que
    /// LibreHardwareMonitor place en dernier segment de son identifiant ("/nvme/0", "/ssd/2"...).</summary>
    private static (string Name, DiskNameSource Source) ResolveDiskName(IHardware hardware)
    {
        if (CleanDiskName(hardware.Name) is { } name) return (name, DiskNameSource.LibreHardwareMonitor);

        if (DiskIndexOf(hardware) is { } index
            && DiskModelReader.ModelsByIndex.TryGetValue(index, out string? model)
            && CleanDiskName(model) is { } wmiName)
        {
            return (wmiName, DiskNameSource.WindowsWmi);
        }

        return (new DiskSnapshot().Name, DiskNameSource.Unknown); // "Disque inconnu"
    }

    /// <summary>Raison de l'échec de la dernière lecture WMI des lettres de lecteur, null si elle a réussi
    /// (ou n'a pas encore eu lieu). Pour le diagnostic de compatibilité.</summary>
    public static string? DiskVolumesError => DiskVolumeReader.LastError;

    /// <summary>Numéro de disque physique : le dernier segment de l'identifiant LibreHardwareMonitor
    /// ("/nvme/0" → 0), le même que \\.\PhysicalDriveN et Win32_DiskDrive.Index.</summary>
    private static int? DiskIndexOf(IHardware hardware)
    {
        string identifier = hardware.Identifier.ToString();
        int lastSlash = identifier.LastIndexOf('/');
        return lastSlash >= 0 && int.TryParse(identifier[(lastSlash + 1)..], out int index) ? index : null;
    }

    /// <summary>Le nom lu par LibreHardwareMonitor n'est pas toujours vide quand il est inutilisable : sur
    /// certains contrôleurs il arrive rempli de caractères nuls ou d'espaces insécables, invisibles à l'écran
    /// mais qui passent un simple test « vide ou espaces » (char.IsWhiteSpace('\0') est faux). On retire donc
    /// les caractères de contrôle et de mise en forme, et un nom sans aucune lettre ni chiffre est traité
    /// comme absent pour que le repli WMI prenne le relais.</summary>
    internal static string? CleanDiskName(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;

        var clean = new System.Text.StringBuilder(raw.Length);
        foreach (char c in raw)
        {
            if (char.IsControl(c)
                || System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format)
            {
                continue;
            }
            clean.Append(c);
        }

        string name = clean.ToString().Trim();
        return name.Any(char.IsLetterOrDigit) ? name : null;
    }

    private static void CollectFans(IHardware hardware, SensorGroup group, List<FanReading> into)
    {
        var fanSensors = hardware.Sensors.Where(s => s.SensorType == SensorType.Fan);
        var controlSensors = hardware.Sensors.Where(s => s.SensorType == SensorType.Control).ToList();
        bool onGpu = group == SensorGroup.Gpu;

        foreach (ISensor fan in fanSensors)
        {
            // Tente d'associer le capteur de contrôle (%) qui porte souvent le même index/nom que le capteur RPM.
            var matchingControl = controlSensors.FirstOrDefault(c => c.Index == fan.Index)
                                   ?? controlSensors.ElementAtOrDefault(fan.Index);

            into.Add(new FanReading
            {
                HardwareName = hardware.Name,
                Group = group,
                SensorName = fan.Name,
                Category = FanIdentification.Categorize(fan.Name, onGpu),
                Channel = fan.Index,
                HardwareId = hardware.Identifier.ToString(),
                // « GPU Fan 1 » est le nom que la bibliothèque donne au canal du pilote graphique, pas celui d'un
                // connecteur : ces ventilateurs sont numérotés dans leur catégorie, comme ceux sans nom.
                NameFromHardware = !onGpu && !FanIdentification.IsGenericName(fan.Name),
                Rpm = fan.Value,
                PercentControl = matchingControl?.Value,
                SensorId = fan.Identifier.ToString(),
                PercentControlSensorId = matchingControl?.Control is not null ? matchingControl.Identifier.ToString() : null,
            });
        }
    }

    /// <summary>Ventilateurs du portable, en lecture seule (aucun capteur de contrôle associé).</summary>
    private void CollectLaptopFans(IReadOnlyList<LaptopFanReading> readings, List<FanReading> into)
    {
        string hardwareName = LaptopFans.IsVerified ? LaptopFans.Vendor! : $"{LaptopFans.Vendor}, expérimental";

        foreach (LaptopFanReading fan in readings)
        {
            into.Add(new FanReading
            {
                HardwareName = hardwareName,
                Group = SensorGroup.Motherboard,
                SensorName = fan.Name,
                Category = FanIdentification.Categorize(fan.Name, onGpu: false, fan.Role),
                HardwareId = $"laptop/{LaptopFans.Vendor!.ToLowerInvariant()}",
                NameFromHardware = !FanIdentification.IsGenericName(fan.Name),
                Rpm = fan.Rpm,
                PercentControl = fan.Percent,
                SensorId = $"laptop/{LaptopFans.Vendor!.ToLowerInvariant()}/{fan.Key}",
            });
        }
    }

    private static ISensor? FindSensor(IHardware hardware, SensorType type, string nameContains)
        => FindSensor(hardware.Sensors, type, nameContains);

    private static ISensor? FindSensor(IEnumerable<ISensor> sensors, SensorType type, string nameContains)
    {
        return sensors.FirstOrDefault(s =>
            s.SensorType == type && s.Name.Contains(nameContains, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Correspondance sur le nom entier, pour les familles de capteurs dont les noms se contiennent
    /// les uns les autres.</summary>
    private static ISensor? ExactSensor(IEnumerable<ISensor> sensors, SensorType type, string name)
    {
        return sensors.FirstOrDefault(s =>
            s.SensorType == type && string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private static float? AddIfPresent(float? total, float? value)
        => value is { } v ? (total ?? 0) + v : total;

    /// <summary>Bascule un ventilateur en pilotage logiciel et applique un % cible (0-100), borné aux
    /// limites que la puce Super I/O accepte réellement. Retourne false si le capteur est introuvable
    /// (carte mère débranchée du point de vue LibreHardwareMonitor — ne devrait pas arriver en usage normal),
    /// ou si la machine est un portable (voir <see cref="LaptopControlRefused"/>).</summary>
    public bool TrySetFanPercent(string controlSensorId, float percent)
    {
        // Le matériel n'a pas fini d'être recensé (voir IsReady) : _computer.Hardware est encore en
        // cours de construction sur l'autre thread, le parcourir ici serait une course sur le même objet.
        if (!IsReady) return false;
        if (LaptopControlRefused()) return false;

        lock (_gate)
        {
            if (_disposed) return false;

            IControl? control = FindControl(controlSensorId);
            if (control is null) return false;

            try
            {
                float clamped = Math.Clamp(percent, control.MinSoftwareValue, control.MaxSoftwareValue);
                RunWithIsaBus(FanWriteBusTimeout, () => control.SetSoftware(clamped));
                return true;
            }
            catch (Exception)
            {
                // Best-effort (règle 2) : un pilote qui refuse l'écriture ne doit jamais planter l'app.
                return false;
            }
        }
    }

    /// <summary>Nom du verrou global qui protège l'accès aux puces Super I/O : LibreHardwareMonitor le crée,
    /// et les utilitaires des fabricants (Armoury Crate, AI Suite, MSI Center, HWiNFO…) le respectent aussi.</summary>
    private const string IsaBusMutexName = @"Global\Access_ISABUS.HTP.Method";

    /// <summary>Attente du verrou pour une consigne de courbe : manquée, elle est renvoyée au relevé suivant.</summary>
    private static readonly TimeSpan FanWriteBusTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>Attente du verrou pour rendre un ventilateur au BIOS : cette écriture-là n'est jamais retentée
    /// (voir <see cref="RunWithIsaBus"/>), elle doit passer.</summary>
    private static readonly TimeSpan FanReleaseBusTimeout = TimeSpan.FromSeconds(1);

    /// <summary>Attente du verrou à la fermeture, où chaque ventilateur est rendu au BIOS une dernière fois.</summary>
    private static readonly TimeSpan CloseBusTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Exécute <paramref name="action"/> en tenant le verrou du bus ISA, attendu jusqu'à <paramref name="timeout"/>.
    /// LibreHardwareMonitor n'attend ce verrou que 10 ms avant d'écrire sur la puce des ventilateurs (Nuvoton) et,
    /// passé ce délai, abandonne l'écriture SANS le signaler : <see cref="IControl.SetDefault"/> se croit alors fait
    /// et ne réessaiera jamais. Avec un logiciel qui interroge la puce en continu (Armoury Crate, observé), ou notre
    /// propre relevé en cours, la main n'était pas rendue au BIOS à la fermeture : la puce restait en mode manuel,
    /// sans plus personne pour la piloter, et les ventilateurs de boîtier s'emballaient jusqu'au lancement suivant.
    /// Un mutex Windows appartient à un thread : tant que celui-ci le tient, l'attente de 10 ms de
    /// LibreHardwareMonitor sur ce même thread réussit tout de suite. Sans le verrou (introuvable, ou toujours
    /// occupé), l'action s'exécute quand même et LibreHardwareMonitor tente sa chance comme avant.
    /// </summary>
    /// <returns>Vrai si le verrou a été obtenu (ou n'existe pas sur ce PC, faute de puce Super I/O).</returns>
    private static bool RunWithIsaBus(TimeSpan timeout, Action action)
    {
        if (!Mutex.TryOpenExisting(IsaBusMutexName, out Mutex? mutex))
        {
            action();
            return true;
        }

        using (mutex)
        {
            bool acquired;
            try
            {
                acquired = mutex.WaitOne(timeout);
            }
            catch (AbandonedMutexException)
            {
                // Le processus qui le tenait s'est terminé sans le rendre : le verrou est à nous.
                acquired = true;
            }

            try
            {
                action();
            }
            finally
            {
                if (acquired) mutex.ReleaseMutex();
            }
            return acquired;
        }
    }

    /// <summary>
    /// Règle de compatibilité 5 : sur un portable, le refroidissement appartient au contrôleur embarqué
    /// du constructeur, et une écriture malheureuse peut laisser la machine sans ventilation. Certains
    /// portables (barebones Clevo/Tongfang, quelques MSI et Gigabyte) embarquent pourtant une puce
    /// Super I/O que LibreHardwareMonitor sait piloter : le garde-fou est donc ici, au plus près de
    /// l'écriture, et pas seulement dans le ViewModel qui remplit la liste.
    ///
    /// Le refus couvre aussi le châssis indéterminé. La détection reposait sur WMI seul, qui avale ses
    /// erreurs : sur un portable au dépôt WMI cassé, la machine passait pour un PC de bureau et l'écriture
    /// était autorisée — exactement ce que cette méthode existe pour empêcher.
    /// </summary>
    private static bool LaptopControlRefused() => MachineInfo.Current.SoftwareFanControlRefused;

    bool IFanController.TrySetPercent(string fanId, float percent) => TrySetFanPercent(fanId, percent);

    bool IFanController.TrySetAuto(string fanId) => TrySetFanAuto(fanId);

    /// <summary>Rend le pilotage du ventilateur au firmware de la carte mère (courbe BIOS par défaut).</summary>
    public bool TrySetFanAuto(string controlSensorId)
    {
        if (!IsReady) return false;
        if (LaptopControlRefused()) return false;

        lock (_gate)
        {
            if (_disposed) return false;

            IControl? control = FindControl(controlSensorId);
            if (control is null) return false;

            try
            {
                RunWithIsaBus(FanReleaseBusTimeout, control.SetDefault);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    private IControl? FindControl(string controlSensorId)
    {
        foreach (IHardware hardware in _computer.Hardware)
        {
            IControl? found = FindControlIn(hardware, controlSensorId) ?? hardware.SubHardware
                .Select(sub => FindControlIn(sub, controlSensorId))
                .FirstOrDefault(c => c is not null);

            if (found is not null) return found;
        }
        return null;
    }

    private static IControl? FindControlIn(IHardware hardware, string controlSensorId)
    {
        ISensor? sensor = hardware.Sensors.FirstOrDefault(s =>
            s.SensorType == SensorType.Control && s.Identifier.ToString() == controlSensorId);
        return sensor?.Control;
    }

    /// <summary>Compte rendu du retour des ventilateurs au BIOS à la fermeture, pour le journal d'erreurs.
    /// Null tant que <see cref="Dispose"/> n'a pas fermé le matériel, ou quand tout s'est bien passé.</summary>
    public string? FanReleaseProblem { get; private set; }

    /// <summary>Ferme les sources de capteurs. Attend qu'un relevé encore en vol se termine — quelques
    /// centaines de millisecondes au pire — plutôt que de lui retirer le matériel sous les pieds.
    /// La fermeture de LibreHardwareMonitor rend chaque ventilateur au BIOS (<c>SuperIOHardware.Close</c>) :
    /// elle se fait en tenant le verrou du bus ISA, pour la même raison que <see cref="TrySetFanAuto"/>.
    /// Limite connue : après un arrêt brutal (processus tué, débogueur arrêté), la puce reste en mode manuel et
    /// LibreHardwareMonitor prend ce mode pour l'état initial au lancement suivant. Seul un redémarrage du PC
    /// rétablit alors le réglage du BIOS.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _cpuLoad.Dispose();
            _cpuThrottle?.Dispose();
            _battery.Dispose();
            LaptopFans.Dispose();
        }

        if (_openTask.IsCompleted)
        {
            try
            {
                // Les ventilateurs pilotés viennent d'être rendus au BIOS (FanCurvesViewModel.Dispose) : l'état
                // que l'app laisse derrière elle, à comparer avec celui du démarrage suivant.
                FanChipDiagnostic.Record(_computer, "fermeture, après le retour des ventilateurs au BIOS");

                if (!RunWithIsaBus(CloseBusTimeout, _computer.Close))
                {
                    FanReleaseProblem = "le verrou d'accès à la puce des ventilateurs est resté occupé par un autre "
                        + "logiciel (Armoury Crate, AI Suite, MSI Center, HWiNFO… ?) : leur retour au BIOS n'est pas garanti.";
                }
            }
            catch (Exception ex)
            {
                FanReleaseProblem = $"la fermeture du matériel a échoué ({ex.Message}) : le retour des ventilateurs au BIOS n'est pas garanti.";
            }
        }
        else
        {
            // L'ouverture est toujours bloquée (voir _openTask) : fermer _computer maintenant toucherait
            // le même objet natif que le thread qui l'ouvre encore, avec un risque de plantage natif non
            // rattrapable. On ferme dès que l'ouverture se débloque, sans jamais attendre ici — l'app doit
            // pouvoir se fermer tout de suite même si le matériel reste coincé.
            _ = _openTask.ContinueWith(
                _ => { try { _computer.Close(); } catch { /* best-effort à la fermeture */ } },
                TaskScheduler.Default);
        }
    }
}
