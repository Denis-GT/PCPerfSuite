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

    private MetricSample? _lastSample;

    /// <summary>Nombre de groupes de capteurs déjà lus lors de la dernière construction du diagnostic.</summary>
    private int _groupsReadAtBuild;

    public ObservableCollection<CompatibilityRow> Rows { get; } = new();

    /// <summary>Métriques que ce PC ne fournit pas, avec leur raison.</summary>
    public ObservableCollection<CompatibilityRow> UnavailableMetrics { get; } = new();

    [ObservableProperty] private string? copyStatus;

    public CompatibilityViewModel(HardwareMonitorService hardware, MonitoringViewModel monitoring,
        ProcessesViewModel processes, FanCurvesViewModel fans, GpuControlViewModel gpu)
    {
        _hardware = hardware;
        _monitoring = monitoring;
        _processes = processes;
        _fans = fans;
        _gpu = gpu;

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
        yield return new CompatibilityRow("Droits administrateur", elevated ? "Oui" : "Non",
            elevated ? "Tous les capteurs accessibles." : "Sans administrateur, la plupart des capteurs et tous les réglages matériels sont inaccessibles.",
            elevated);

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

        string gpus = machine.VideoControllers.Count > 0 ? string.Join(", ", machine.VideoControllers) : "Aucun GPU identifié par Windows";
        yield return new CompatibilityRow("GPU", snapshot?.Gpu?.Name ?? "Non lu", gpus, snapshot?.Gpu is not null);

        yield return _gpu.IsAvailable
            ? new CompatibilityRow("Contrôle GPU (overclocking)", "Disponible", "Via le pilote NVIDIA (NVAPI).", true)
            : new CompatibilityRow("Contrôle GPU (overclocking)", "Non disponible", _gpu.UnavailableMessage, false);

        yield return FanReadingRow(machine, snapshot);

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

        bool rtss = IsRtssRunning();
        yield return new CompatibilityRow("RTSS (FPS et overlay en plein écran)", rtss ? "Lancé" : "Non lancé",
            rtss ? "Les FPS sont lus pendant les jeux." : "Installer et lancer RivaTuner Statistics Server (gratuit, guru3d.com) pour les FPS.", rtss);
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
            foreach (FanReading fan in sample.Hardware.Fans)
            {
                text.AppendLine($" - {fan.SensorName} ({fan.HardwareName}) : {fan.Rpm?.ToString("0") ?? "N/D"} RPM, {fan.PercentControl?.ToString("0") ?? "N/D"} %");
            }

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
