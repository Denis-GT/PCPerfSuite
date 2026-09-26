using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCPerfSuite.App.Metrics;
using PCPerfSuite.App.Utils;
using PCPerfSuite.Core.Hardware;
using PCPerfSuite.Core.Hardware.Cpu;
using PCPerfSuite.Core.Hardware.Fans;
using PCPerfSuite.Core.Hardware.LaptopFans;
using PCPerfSuite.Core.PowerSettings;
using PCPerfSuite.Core.Processes;
using PCPerfSuite.Core.SystemInfo;

namespace PCPerfSuite.App.ViewModels;

/// <summary>Ligne du diagnostic : une fonction ou une source de données, et ce qu'elle donne sur ce PC.</summary>
public sealed record CompatibilityRow(string Title, string Status, string Detail, bool IsSupported);

/// <summary>
/// "Compatibilité de ce PC" (Paramètres) : ce que PCPerfSuite peut lire et piloter sur cette machine, et pourquoi
/// le reste manque. Le même contenu se copie en texte, à joindre à un signalement : c'est ainsi qu'on confirme les
/// prises en charge expérimentales (ventilateurs des portables Lenovo, HP, MSI, Acer) sur du vrai matériel.
/// </summary>
public sealed partial class CompatibilityViewModel : ObservableObject
{
    private readonly HardwareMonitorService _hardware;
    private readonly MonitoringViewModel _monitoring;
    private readonly ProcessesViewModel _processes;
    private readonly FanCurvesViewModel _fans;
    private readonly GpuControlViewModel _gpu;
    private readonly CpuControlViewModel _cpu;

    private MetricSample? _lastSample;

    /// <summary>Nombre de groupes de capteurs déjà lus lors de la dernière construction du diagnostic.</summary>
    private int _groupsReadAtBuild;

    public ObservableCollection<CompatibilityRow> Rows { get; } = new();

    /// <summary>Métriques que ce PC ne fournit pas, avec leur raison.</summary>
    public ObservableCollection<CompatibilityRow> UnavailableMetrics { get; } = new();

    [ObservableProperty] private string? copyStatus;

    public CompatibilityViewModel(HardwareMonitorService hardware, MonitoringViewModel monitoring,
        ProcessesViewModel processes, FanCurvesViewModel fans, GpuControlViewModel gpu, CpuControlViewModel cpu)
    {
        _hardware = hardware;
        _monitoring = monitoring;
        _processes = processes;
        _fans = fans;
        _gpu = gpu;
        _cpu = cpu;

        _monitoring.MetricsUpdated += OnMetricsUpdated;
        Refresh();
    }

    /// <summary>Le diagnostic est reconstruit à chaque fois qu'un groupe de capteurs est lu pour la première fois
    /// (avant, ses valeurs ne sont pas absentes, juste pas encore arrivées), puis seulement à la demande.</summary>
    private void OnMetricsUpdated(MetricSample sample)
    {
        _lastSample = sample;
        if (sample.Hardware.GroupsEverRead.Count <= _groupsReadAtBuild) return;

        _groupsReadAtBuild = sample.Hardware.GroupsEverRead.Count;
        Refresh();
    }

    [RelayCommand]
    private void Refresh()
    {
        Rows.Clear();
        foreach (CompatibilityRow row in BuildRows()) Rows.Add(row);

        UnavailableMetrics.Clear();
        if (_lastSample is { } sample)
        {
            foreach (MetricDefinition metric in _monitoring.MyMetrics.Definitions.Where(m => m.Read(sample).IsUnavailable))
            {
                UnavailableMetrics.Add(new CompatibilityRow($"{metric.Category.Name} · {metric.Label}", "N/D", metric.UnavailableHint, false));
            }
        }

        CopyStatus = null;
    }

    private IEnumerable<CompatibilityRow> BuildRows()
    {
        MachineInfo machine = MachineInfo.Current;
        HardwareSnapshot? snapshot = _lastSample?.Hardware;

        string identity = string.Join(" ", new[] { machine.Manufacturer, machine.Model }.Where(s => s.Length > 0));
        yield return new CompatibilityRow("Machine", machine.IsLaptop ? "Portable" : "PC de bureau",
            identity.Length > 0 ? identity : "Fabricant et modèle non communiqués par le BIOS", true);

        bool elevated = ElevationHelper.IsAdministrator();
        bool pawnIoInstalled = PawnIoDriver.IsInstalled;
        yield return new CompatibilityRow("Droits administrateur", elevated ? "Oui" : "Non",
            elevated
                ? (pawnIoInstalled
                    ? "Tous les capteurs accessibles."
                    : "La plupart des capteurs sont accessibles ; certains (températures et tensions CPU, sondes de carte "
                      + "mère, limites de puissance) demandent aussi le pilote PawnIO — voir la ligne « Pilote PawnIO » ci-dessous.")
                : "Sans administrateur, la plupart des capteurs et tous les réglages matériels sont inaccessibles.",
            elevated);

        // PawnIO remplace WinRing0 depuis LibreHardwareMonitor 0.9.5 pour l'accès bas niveau (MSR, Super I/O) :
        // sans lui, une grande partie des capteurs CPU/carte mère reste "N/D" même app lancée en administrateur.
        // Se réinstalle depuis l'onglet Réglages CPU, qui propose déjà le bouton de téléchargement.
        yield return new CompatibilityRow("Pilote PawnIO", pawnIoInstalled ? $"Installé ({PawnIoDriver.Version})" : "Non installé",
            pawnIoInstalled
                ? "Utilisé par LibreHardwareMonitor pour les capteurs bas niveau et par PCPerfSuite pour les limites de puissance CPU."
                : (PawnIoDriver.UnavailableReason ?? "Pilote PawnIO indisponible.")
                  + " À installer depuis l'onglet « Réglages CPU » (bouton « Installer PawnIO »).",
            pawnIoInstalled);

        yield return SessionUser.OtherProfileMessage is { } otherProfile
            ? new CompatibilityRow("Compte Windows", SessionUser.ProcessAccount, otherProfile, false)
            : new CompatibilityRow("Compte Windows", SessionUser.ProcessAccount,
                "L'app tourne sous le compte de la session : fichiers temporaires et caches nettoyés sont bien ceux de cet utilisateur.", true);

        yield return AppSettingsStore.LastError is { } settingsError
            ? new CompatibilityRow("Enregistrement des réglages", "En échec", settingsError, false)
            : new CompatibilityRow("Enregistrement des réglages", "OK",
                "Les réglages de PCPerfSuite s'enregistrent normalement dans %LOCALAPPDATA%\\PCPerfSuite.", true);

        yield return CrashLog.LastError is { } crash
            ? new CompatibilityRow("Dernière erreur interne", "Signalée", crash, false)
            : new CompatibilityRow("Dernière erreur interne", "Aucune",
                $"Les erreurs inattendues sont journalisées dans {CrashLog.FilePath}.", true);

        if (snapshot is not null)
        {
            yield return new CompatibilityRow("CPU", snapshot.Cpu.Name, "Charge, températures, puissance et fréquences selon ce que le processeur expose.", true);
        }

        yield return CpuPowerLimitRow();

        string gpus = machine.VideoControllers.Count > 0 ? string.Join(", ", machine.VideoControllers) : "Aucun GPU identifié par Windows";
        yield return new CompatibilityRow("GPU", snapshot?.Gpu?.Name ?? "Non lu", gpus, snapshot?.Gpu is not null);

        yield return _gpu.IsAvailable
            ? new CompatibilityRow("Contrôle GPU (overclocking)", "Disponible", "Via le pilote NVIDIA (NVAPI).", true)
            : new CompatibilityRow("Contrôle GPU (overclocking)", "Non disponible", _gpu.UnavailableMessage, false);

        yield return FanReadingRow(machine, snapshot);

        yield return FanIdentificationRow(snapshot);

        yield return _fans.Fans.Count > 0
            ? new CompatibilityRow("Pilotage des ventilateurs", $"{_fans.Fans.Count} pilotable(s)", "Onglet Ventilateurs.", true)
            : new CompatibilityRow("Pilotage des ventilateurs", "Non disponible", _fans.NoFansMessage, false);

        if (snapshot is not null) yield return MemoryRow(snapshot.Memory);

        yield return ProcessCpuScaleRow();

        if (snapshot is not null)
        {
            bool hasBoardTemps = snapshot.Motherboard.SystemTempC is not null;
            yield return new CompatibilityRow("Sondes de la carte mère", hasBoardTemps ? "Disponibles" : "Aucune",
                hasBoardTemps ? snapshot.Motherboard.Name : MetricCatalog.MotherboardHint, hasBoardTemps);
        }

        if (snapshot is not null && snapshot.Disks.Count > 0)
        {
            yield return DiskNamesRow(snapshot.Disks);
        }

        bool rtss = IsRtssRunning();
        yield return new CompatibilityRow("RTSS (FPS et overlay en plein écran)", rtss ? "Lancé" : "Non lancé",
            rtss ? "Les FPS sont lus pendant les jeux." : "Installer et lancer RivaTuner Statistics Server (gratuit, guru3d.com) pour les FPS.", rtss);
    }

    /// <summary>Le maximum des champs de limite de puissance (onglet Processeur) et sa source : la limite lue dans
    /// le processeur, ou un repli — avec ce que le processeur a répondu, valeurs brutes comprises. Sans cette ligne,
    /// un signalement « le maximum est faux sur mon PC » ne dit pas quel registre a manqué ni ce qu'il contenait.
    /// « OK » seulement quand la valeur vient du processeur : un repli s'affiche « -- » avec sa raison.</summary>
    private CompatibilityRow CpuPowerLimitRow()
    {
        const string title = "Limite de puissance CPU (maximum)";

        // Limites illisibles : pilote absent, app sans administrateur, processeur ou marque non pris en charge…
        // la raison est celle que l'onglet Processeur affiche déjà.
        if (_cpu.MaxWattsInfo is not { } info)
        {
            return new CompatibilityRow(title, "Non disponible",
                _cpu.UnavailableReason.Length > 0
                    ? _cpu.UnavailableReason
                    : "Les limites de puissance de ce processeur n'ont pas pu être lues.",
                false);
        }

        string status = $"Max {info.Watts:0} W · {info.SourceLabel}" + (info.IsExperimental ? " (expérimental)" : "");

        var detail = new List<string> { info.Explanation, $"Relevés : {info.RawValues}." };
        if (info.IsExperimental) detail.Add(CpuMaxWattsInfo.ExperimentalNotice);

        // Limites lisibles mais pas modifiables (verrouillées par le BIOS…) : le maximum reste à connaître.
        if (!_cpu.IsPowerLimitAvailable && _cpu.UnavailableReason.Length > 0) detail.Add(_cpu.UnavailableReason);

        return new CompatibilityRow(title, status, string.Join(" ", detail), info.FromProcessor);
    }

    /// <summary>Noms de modèle et lettres des disques, et par qui ils sont fournis : certains contrôleurs NVMe
    /// ne donnent pas leur nom à LibreHardwareMonitor, qui laisse alors un texte vide ou invisible — le repli
    /// passe par Windows (WMI). Sans cette ligne, un signalement « mes disques n'ont pas de nom » ne permet pas
    /// de savoir laquelle des deux sources a manqué.</summary>
    private static CompatibilityRow DiskNamesRow(IReadOnlyList<DiskSnapshot> disks)
    {
        const string title = "Noms, lettres et charge des disques";

        int fromLibre = disks.Count(d => d.NameSource == DiskNameSource.LibreHardwareMonitor);
        int fromWindows = disks.Count(d => d.NameSource == DiskNameSource.WindowsWmi);
        int unnamed = disks.Count(d => d.NameSource == DiskNameSource.Unknown);
        int withLetters = disks.Count(d => d.Volumes.Count > 0);

        var detail = new List<string>
        {
            $"Nom de modèle : {fromLibre} lu(s) par le capteur, {fromWindows} par Windows (repli), {unnamed} introuvable(s).",
            $"Lettres de lecteur : {withLetters} disque(s) sur {disks.Count} en ont.",
            $"Charge : {disks.Count(d => d.ActivityPercent is not null)} disque(s) sur {disks.Count} la fournissent.",
        };
        if (HardwareMonitorService.DiskVolumesError is { } error)
        {
            detail.Add($"Lecture des lettres impossible : {error}");
        }

        bool ok = unnamed == 0 && HardwareMonitorService.DiskVolumesError is null
                 && disks.All(d => d.ActivityPercent is not null);
        return new CompatibilityRow(title, ok ? "Lus" : "Incomplets", string.Join(" ", detail), ok);
    }

    /// <summary>Mémoire : ce qui est mesuré, ce qui est décrit, et surtout PAR QUI. Sans ce dernier point, un
    /// signalement « la RAM s'affiche N/D » ne permet pas de savoir laquelle des trois sources a manqué —
    /// c'est exactement ce qui a rendu ce défaut difficile à trouver sur un mini-PC HP.</summary>
    private static CompatibilityRow MemoryRow(MemorySnapshot memory)
    {
        const string title = "Mémoire";

        var sources = new List<string>();
        if (memory.Sources.HasFlag(MemorySource.LibreHardwareMonitor)) sources.Add("LibreHardwareMonitor");
        if (memory.Sources.HasFlag(MemorySource.Windows)) sources.Add("Windows (GlobalMemoryStatusEx)");
        if (memory.Sources.HasFlag(MemorySource.Wmi)) sources.Add("WMI (Win32_PhysicalMemory)");

        if (memory.TotalGb is not { } total)
        {
            return new CompatibilityRow(title, "Non lue",
                "Aucune source n'a répondu, ce qui ne devrait pas arriver sous Windows. " + MetricCatalog.RamHint, false);
        }

        var description = new List<string> { $"{total:0.0} Go" };
        if (memory.TypeLabel is { Length: > 0 } type) description.Add(type);
        if (memory.SpeedMhz is { } speed) description.Add($"{speed} MHz");

        description.Add(memory.Modules.Count switch
        {
            0 => "barrettes non décrites par le BIOS",
            var n when memory.SlotCount is { } slots => $"{n} barrette(s) sur {slots} emplacement(s)",
            var n => $"{n} barrette(s)",
        });

        description.Add(sources.Count > 0 ? $"Source : {string.Join(" + ", sources)}." : "Source inconnue.");
        if (memory.ModulesUnavailableReason is { } reason) description.Add(reason);

        return new CompatibilityRow(title, "Lue", string.Join(" · ", description), true);
    }

    /// <summary>Comment le %CPU de l'onglet Processus est mis à l'échelle. Le temps processeur brut que donne
    /// Windows ne tient pas compte de la fréquence réelle des cœurs, alors que le Gestionnaire des tâches,
    /// qui compte en cycles, si : sans correction les valeurs sont trop basses, et avec une mauvaise
    /// correction elles le sont encore plus sur un PC qui se sous-cadence au repos.</summary>
    private CompatibilityRow ProcessCpuScaleRow()
    {
        const string title = "Mesure du %CPU par processus";

        return _processes.CpuScaleSource switch
        {
            CpuScaleSource.Calibrated => new CompatibilityRow(title, "Calibrée",
                $"Facteur {_processes.CpuScaleFactor:0.00}, mesuré en continu sur « % Processor Utility », la charge totale "
                + "qu'affiche le Gestionnaire des tâches. La somme des lignes suit donc ce total.", true),
            CpuScaleSource.Performance => new CompatibilityRow(title, "Approchée",
                $"Facteur {_processes.CpuScaleFactor:0.00}, déduit de « % Processor Performance » (fréquence moyenne des cœurs). "
                + "La calibration n'a pas encore pu se faire : elle demande quelques secondes d'activité mesurable.", true),
            _ => new CompatibilityRow(title, "Temps processeur brut",
                "Les compteurs de performance Windows n'ont pas répondu : les %CPU sont sous-évalués sur toute machine "
                + "qui dépasse sa fréquence nominale. Vérifier que le service « Journaux et alertes de performance » n'est pas désactivé.",
                false),
        };
    }

    private CompatibilityRow FanReadingRow(MachineInfo machine, HardwareSnapshot? snapshot)
    {
        LaptopFanService laptop = _hardware.LaptopFans;
        int count = snapshot?.Fans.Count ?? 0;
        const string title = "Lecture des ventilateurs";

        return laptop.Support switch
        {
            LaptopFanSupport.Active => new CompatibilityRow(title, $"{count} lu(s)",
                $"Portable {laptop.Vendor} : lecture via l'interface du constructeur (lecture seule)." +
                (laptop.IsVerified ? "" : " Prise en charge expérimentale : merci de signaler toute valeur incohérente avec l'utilitaire du constructeur."),
                count > 0),
            LaptopFanSupport.InterfaceMissing => new CompatibilityRow(title, count > 0 ? $"{count} lu(s)" : "Aucun",
                $"Portable {laptop.Vendor} : ce modèle n'expose pas l'interface de ventilateurs attendue" +
                (laptop.DetectionError is { } error ? $" ({error})." : "."),
                count > 0),
            LaptopFanSupport.UnsupportedVendor => new CompatibilityRow(title, count > 0 ? $"{count} lu(s)" : "Aucun",
                $"Portable {(machine.Manufacturer.Length > 0 ? machine.Manufacturer : "de marque inconnue")} : marque pas encore prise en " +
                "charge pour les ventilateurs (ASUS, Lenovo, HP, MSI et Acer le sont).",
                count > 0),
            _ => new CompatibilityRow(title, count > 0 ? $"{count} lu(s)" : "Aucun",
                count > 0 ? "Via la carte mère et le GPU." : "La carte mère n'expose pas ses ventilateurs, ou sa puce de gestion n'est pas reconnue.",
                count > 0),
        };
    }

    /// <summary>Comment les ventilateurs ont été identifiés : ce que la carte mère a nommé, ce qui n'est qu'un numéro de canal
    /// (et pourquoi), les doublons GPU écartés, les connecteurs sans ventilateur détecté. Sans cette ligne, « tous mes
    /// ventilateurs s'appellent Fan #N » ne dit pas si la carte est absente de la table de noms ou si la lecture a échoué.</summary>
    private CompatibilityRow FanIdentificationRow(HardwareSnapshot? snapshot)
    {
        const string title = "Identification des ventilateurs";

        if (snapshot is null || snapshot.Fans.Count == 0)
        {
            string why = ElevationHelper.IsAdministrator()
                ? "Aucun ventilateur lu : voir la ligne « Lecture des ventilateurs » ci-dessus."
                : "PCPerfSuite n'est pas lancé en administrateur : les ventilateurs de la carte mère ne sont pas lisibles.";
            return new CompatibilityRow(title, "Sans objet", why, false);
        }

        FanInventory inventory = FanInventory.Of(snapshot.Fans);
        var parts = new List<string>();
        bool namesMissing = false;

        if (inventory.BoardFans > 0)
        {
            string unknownBoard = new MotherboardSnapshot().Name;
            string board = snapshot.Motherboard.Name is { Length: > 0 } name && name != unknownBoard ? $"carte {name}" : "cette carte mère";

            if (inventory.BoardFansNamed == inventory.BoardFans)
            {
                parts.Add($"Noms fournis par la carte mère : {inventory.BoardFans} sur {inventory.BoardFans}.");
            }
            else
            {
                namesMissing = true;
                string chips = string.Join(", ", inventory.ChipsWithoutNames);
                parts.Add($"Noms fournis par la carte mère : {inventory.BoardFansNamed} sur {inventory.BoardFans} ({chips} ; {board} absente de la table de "
                          + "noms de LibreHardwareMonitor, la marque ou le modèle n'est pas encore pris en charge pour le nom des connecteurs). "
                          + "Les ventilateurs sont numérotés, et le crayon de l'onglet Ventilateurs permet de les nommer.");
            }
        }

        if (inventory.LaptopFans > 0)
        {
            parts.Add($"{inventory.LaptopFans} ventilateur(s) de portable, nommés par l'interface du constructeur (lecture seule).");
        }

        if (_fans.GpuCoolerCount > 0)
        {
            string duplicates = _fans.GpuDuplicatesDiscarded > 0
                ? $" ; {_fans.GpuDuplicatesDiscarded} lecture(s) en double de LibreHardwareMonitor écartée(s)"
                : "";
            parts.Add($"{_fans.GpuCoolerCount} ventilateur(s) GPU piloté(s) par le pilote {_fans.GpuDriverName}{duplicates}.");
        }
        else if (inventory.GpuFans > 0)
        {
            parts.Add($"{inventory.GpuFans} ventilateur(s) GPU lu(s) par LibreHardwareMonitor ; le pilote graphique n'expose pas de pilotage " +
                      "de ventilateur pour cette carte (limite du pilote ou de la carte), ils restent pilotables quand la bibliothèque le permet.");
        }

        if (_fans.EmptyHeaderCount > 0)
        {
            parts.Add($"{_fans.EmptyHeaderCount} connecteur(s) sans ventilateur détecté : 0 tr/min alors que la carte mère les alimente ; " +
                      "un ventilateur sans fil de vitesse (2 broches, hub) se présente pareil, ils restent pilotables.");
        }

        if (_fans.CustomizedCount > 0)
        {
            parts.Add($"{_fans.CustomizedCount} ventilateur(s) renommé(s) ou rangé(s) à la main par l'utilisateur.");
        }

        // Des noms absents sur la carte mère ne sont pas une panne, mais c'est une prise en charge incomplète : la ligne
        // le dit, sauf si l'utilisateur a déjà nommé ses ventilateurs.
        bool ok = !namesMissing || _fans.CustomizedCount > 0;
        return new CompatibilityRow(title, ok ? "Identifiés" : "Numéros seulement", string.Join(" ", parts), ok);
    }

    /// <summary>Une ligne du rapport pour un ventilateur : de quoi retrouver son nom, son connecteur et ce qu'on en a fait.
    /// Ces champs suffisent pour ajouter une carte à une table de noms, ou vérifier la règle des connecteurs vides
    /// (vitesse et commande lues).</summary>
    private string DescribeFan(FanReading fan)
    {
        FanControlItemViewModel? item = _fans.FindItem(fan);
        var notes = new List<string>();

        if (_fans.IsGpuDuplicate(fan))
        {
            notes.Add(_fans.GpuCoolerIdFor(fan) is { } cooler
                ? $"doublon du cooler {cooler}, écarté de l'onglet"
                : "écarté de l'onglet : la carte est pilotée par son pilote graphique");
        }
        else if (item?.IsEmptyHeader == true)
        {
            notes.Add("connecteur sans ventilateur détecté");
        }

        if (item is { HasCustomIdentity: true })
        {
            notes.Add($"corrigé par l'utilisateur : « {item.DisplayName} », {FanCategoryInfo.Title(item.Category)}");
        }

        string chip = fan.Channel is { } channel ? $"{fan.HardwareName}, canal {channel + 1}" : fan.HardwareName;
        string control = fan.PercentControlSensorId ?? "aucune (lecture seule)";
        string suffix = notes.Count > 0 ? $" · {string.Join(" · ", notes)}" : "";

        return $" - [{FanCategoryInfo.Title(fan.Category)}] {fan.Label} · nom lu « {fan.SensorName} » ({chip}) : "
               + $"{fan.Rpm?.ToString("0") ?? "N/D"} RPM, {fan.PercentControl?.ToString("0") ?? "N/D"} % "
               + $"· capteur {fan.SensorId} · commande {control}{suffix}";
    }

    /// <summary>Met en forme une mesure éventuellement absente pour le rapport : « N/D » explicite, jamais un
    /// zéro qui laisserait croire à une valeur mesurée.</summary>
    private static string Value(float? value)
        => value?.ToString("0.##", CultureInfo.InvariantCulture) ?? "N/D";

    private static bool IsRtssRunning()
    {
        Process[] processes = Process.GetProcessesByName("RTSS");
        foreach (Process process in processes) process.Dispose();
        return processes.Length > 0;
    }

    [RelayCommand]
    private void CopyReport()
    {
        Refresh();

        var text = new StringBuilder();
        string version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "?";
        text.AppendLine($"PCPerfSuite {version} — compatibilité de ce PC");
        text.AppendLine($"Windows {Environment.OSVersion.Version}");
        text.AppendLine();

        foreach (CompatibilityRow row in Rows)
        {
            text.AppendLine($"[{(row.IsSupported ? "OK" : "--")}] {row.Title} : {row.Status}");
            text.AppendLine($"     {row.Detail}");
        }

        text.AppendLine();
        text.AppendLine(UnavailableMetrics.Count == 0 ? "Aucune métrique non disponible." : "Métriques non disponibles :");
        foreach (CompatibilityRow row in UnavailableMetrics) text.AppendLine($" - {row.Title}");

        if (_lastSample is { } sample)
        {
            text.AppendLine();
            text.AppendLine("Ventilateurs lus :");
            if (sample.Hardware.Fans.Count == 0) text.AppendLine(" - Aucun ventilateur lu.");
            foreach (FanReading fan in sample.Hardware.Fans) text.AppendLine(DescribeFan(fan));

            // Détail de la mémoire : sans lui, un « la RAM s'affiche N/D » ne dit pas quelle source a manqué.
            MemorySnapshot memory = sample.Hardware.Memory;
            text.AppendLine();
            text.AppendLine("Mémoire :");
            text.AppendLine($" - Utilisée : {Value(memory.UsedGb)} Go · disponible : {Value(memory.AvailableGb)} Go "
                            + $"· totale : {Value(memory.TotalGb)} Go · charge : {Value(memory.LoadPercent)} %");
            text.AppendLine($" - Virtuelle utilisée : {Value(memory.VirtualUsedGb)} Go sur {Value(memory.VirtualTotalGb)} Go");
            text.AppendLine($" - Sources ayant répondu : {(memory.Sources == MemorySource.None ? "aucune" : memory.Sources.ToString())}");
            text.AppendLine($" - Emplacements : {memory.SlotCount?.ToString() ?? "non communiqués"}");

            if (memory.Modules.Count == 0)
            {
                text.AppendLine($" - Aucune barrette décrite. {memory.ModulesUnavailableReason ?? ""}".TrimEnd());
            }
            foreach (MemoryModuleInfo module in memory.Modules)
            {
                text.AppendLine($" - {module.Slot ?? "emplacement inconnu"} : {module.CapacityGb?.ToString("0.#") ?? "N/D"} Go, "
                                + $"{module.TypeLabel ?? "type N/D"}, {module.SpeedMhz?.ToString() ?? "N/D"} MHz, "
                                + $"{module.FormFactorLabel ?? "format N/D"}, {module.Manufacturer ?? "marque N/D"} "
                                + $"{module.PartNumber ?? ""}, {module.TemperatureC?.ToString("0") ?? "N/D"} °C");
            }

            // Capteurs mémoire bruts de LibreHardwareMonitor : c'est là qu'on voit si la bibliothèque a
            // renommé un capteur ou exposé un matériel inattendu sur cette machine.
            text.AppendLine();
            text.AppendLine("Capteurs mémoire bruts (LibreHardwareMonitor) :");
            IReadOnlyList<string> raw = _hardware.DescribeMemorySensors();
            if (raw.Count == 0) text.AppendLine(" - Aucun matériel mémoire énuméré.");
            foreach (string line in raw) text.AppendLine($" - {line}");
        }

        try
        {
            Clipboard.SetText(text.ToString());
            CopyStatus = "Rapport copié dans le presse-papiers.";
        }
        catch (Exception ex)
        {
            CopyStatus = $"Copie impossible : {ex.Message}";
        }
    }
}
