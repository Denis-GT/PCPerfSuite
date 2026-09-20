using System.Collections.ObjectModel;
using System.Diagnostics;
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
    private readonly FanCurvesViewModel _fans;
    private readonly GpuControlViewModel _gpu;

    private MetricSample? _lastSample;

    /// <summary>Nombre de groupes de capteurs déjà lus lors de la dernière construction du diagnostic.</summary>
    private int _groupsReadAtBuild;

    public ObservableCollection<CompatibilityRow> Rows { get; } = new();

    /// <summary>Métriques que ce PC ne fournit pas, avec leur raison.</summary>
    public ObservableCollection<CompatibilityRow> UnavailableMetrics { get; } = new();

    [ObservableProperty] private string? copyStatus;

    public CompatibilityViewModel(HardwareMonitorService hardware, MonitoringViewModel monitoring, FanCurvesViewModel fans, GpuControlViewModel gpu)
    {
        _hardware = hardware;
        _monitoring = monitoring;
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
