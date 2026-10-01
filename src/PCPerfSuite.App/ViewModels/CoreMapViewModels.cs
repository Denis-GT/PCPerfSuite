using CommunityToolkit.Mvvm.ComponentModel;
using PCPerfSuite.Core.Hardware.Cpu;

namespace PCPerfSuite.App.ViewModels;

/// <summary>Un fil d'exécution (processeur logique) du visuel par cœur. Mis à jour chaque seconde tant que le
/// sous-onglet est affiché ; « -- » tant que rien n'a été relevé.</summary>
public sealed partial class CoreThreadViewModel : ObservableObject
{
    public CoreThreadViewModel(LogicalProcessorId id) => Id = id;

    public LogicalProcessorId Id { get; }

    [ObservableProperty] private bool hasValue;
    [ObservableProperty] private double load;

    /// <summary>Part du temps parqué sur la fenêtre, de 0 à 1 ; NaN quand l'état parqué n'est pas lu.</summary>
    [ObservableProperty] private double parkedShare = double.NaN;

    [ObservableProperty] private bool isParkedNow;
    [ObservableProperty] private string loadText = "--";
    [ObservableProperty] private string toolTip = "Pas encore relevé.";

    /// <param name="parkedShare">Part parquée, null si l'état parqué n'est pas lu sur ce PC.</param>
    /// <param name="windowSeconds">Durée de la fenêtre de la part parquée, pour l'info-bulle.</param>
    public void Update(double? utility, bool? parkedNow, double? parkedShare, double? performance, int windowSeconds)
    {
        HasValue = utility is not null;
        Load = utility ?? 0;
        LoadText = utility is { } u ? $"{u:0}" : "--";
        ParkedShare = parkedShare ?? double.NaN;
        IsParkedNow = parkedNow == true;

        string where = Id.Group == 0 ? $"Processeur logique {Id.Index}" : $"Processeur logique {Id.Index} (groupe {Id.Group})";
        var lines = new List<string>
        {
            where,
            utility is { } load ? $"Charge : {load:0} %" : "Charge : N/D",
            parkedShare is { } share
                ? $"Parqué {share:P0} du temps sur {windowSeconds} s{(parkedNow == true ? ", parqué en ce moment" : "")}"
                : "État parqué : N/D sur ce PC",
        };
        if (performance is { } perf) lines.Add($"Fréquence : {perf:0} % de la fréquence de base");
        ToolTip = string.Join("\n", lines);
    }
}

/// <summary>Un cœur physique : son nom court (« P0 », « E3 », « C5 ») et ses fils SMT côte à côte.</summary>
public sealed class CoreTileViewModel
{
    public CoreTileViewModel(string label, IReadOnlyList<CoreThreadViewModel> threads)
    {
        Label = label;
        Threads = threads;
    }

    public string Label { get; }
    public IReadOnlyList<CoreThreadViewModel> Threads { get; }
}

/// <summary>Les cœurs d'une classe d'efficacité dans un groupe de cache. <see cref="Title"/> est null sur un processeur
/// à une seule classe : « cœurs performants » n'y voudrait rien dire.</summary>
public sealed class CoreClassViewModel
{
    public CoreClassViewModel(string? title, IReadOnlyList<CoreTileViewModel> cores)
    {
        Title = title;
        Cores = cores;
    }

    public string? Title { get; }
    public IReadOnlyList<CoreTileViewModel> Cores { get; }
}

/// <summary>Un groupe de cache L3 (CCD, CCX, ou tout le processeur) et ses classes de cœurs.</summary>
public sealed class CoreClusterViewModel
{
    public CoreClusterViewModel(string? title, string? cacheText, bool isVCache, IReadOnlyList<CoreClassViewModel> classes)
    {
        Title = title;
        CacheText = cacheText;
        IsVCache = isVCache;
        Classes = classes;
    }

    /// <summary>« CCD 1 », null quand le processeur n'a qu'un groupe de cache.</summary>
    public string? Title { get; }

    /// <summary>« L3 de 96 Mo », null si Windows n'a pas donné la taille.</summary>
    public string? CacheText { get; }

    /// <summary>Le CCD avec 3D V-Cache d'un Ryzen X3D à deux CCD.</summary>
    public bool IsVCache { get; }

    public IReadOnlyList<CoreClassViewModel> Classes { get; }

    public bool HasHeader => Title is not null || CacheText is not null || IsVCache;
}

/// <summary>Bâtit le visuel par cœur depuis la topologie : groupes de cache, puis classes (libellées P et E seulement
/// s'il y en a au moins deux), puis cœurs et leurs fils.</summary>
public static class CoreMapBuilder
{
    public static IReadOnlyList<CoreClusterViewModel> Build(CpuTopology topology, CpuPlatform platform)
    {
        IReadOnlyList<int> classes = topology.EfficiencyClasses;
        var counters = new Dictionary<string, int>();

        return topology.Clusters.Select((cluster, index) => new CoreClusterViewModel(
                ClusterTitle(topology, platform, index),
                cluster.L3Bytes is { } l3 ? $"L3 de {FormatBytes(l3)}" : null,
                isVCache: platform.Vendor == CpuVendor.Amd && cluster.HasLargerL3,
                cluster.Classes.Select(klass => new CoreClassViewModel(
                        ClassTitle(classes, klass.EfficiencyClass),
                        klass.Cores.Select(core =>
                        {
                            string prefix = CorePrefix(classes, klass.EfficiencyClass);
                            int number = counters.TryGetValue(prefix, out int n) ? n : 0;
                            counters[prefix] = number + 1;
                            return new CoreTileViewModel($"{prefix}{number}",
                                core.Threads.Select(t => new CoreThreadViewModel(t.Id)).ToList());
                        }).ToList()))
                    .ToList()))
            .ToList();
    }

    /// <summary>Résumé de la topologie : « 6 cœurs performants à 2 fils, 8 cœurs efficaces · 20 fils ».</summary>
    public static string Summary(CpuTopology topology)
    {
        IReadOnlyList<int> classes = topology.EfficiencyClasses;
        var parts = topology.Clusters.SelectMany(c => c.Cores)
            .GroupBy(core => core.EfficiencyClass)
            .OrderByDescending(g => g.Key)
            .Select(g =>
            {
                int threads = g.Max(core => core.Threads.Count);
                string kind = classes.Count < 2 ? "cœurs"
                    : g.Key == classes[0] ? "cœurs performants"
                    : classes.Count == 2 ? "cœurs efficaces" : $"cœurs de classe {g.Key}";
                return $"{g.Count()} {kind}{(threads > 1 ? $" à {threads} fils" : "")}";
            });

        string clusters = topology.Clusters.Count > 1 ? $" · {topology.Clusters.Count} groupes de cache L3" : "";
        return $"{string.Join(", ", parts)} · {topology.LogicalProcessors.Count} fils{clusters}";
    }

    private static string? ClusterTitle(CpuTopology topology, CpuPlatform platform, int index)
    {
        if (topology.Clusters.Count < 2) return null;

        // Zen 3 et suivants : un seul CCX par CCD. Zen et Zen 2 : deux CCX par CCD, chacun son L3.
        return platform.Vendor == CpuVendor.Amd && platform.Family >= 0x19 ? $"CCD {index + 1}"
            : platform.Vendor == CpuVendor.Amd && platform.Family == 0x17 ? $"CCX {index + 1}"
            : $"Groupe de cache {index + 1}";
    }

    private static string? ClassTitle(IReadOnlyList<int> classes, int efficiencyClass)
    {
        if (classes.Count < 2) return null;
        if (efficiencyClass == classes[0]) return "Cœurs performants (P)";
        return classes.Count == 2 ? "Cœurs efficaces (E)" : $"Cœurs de classe {efficiencyClass}";
    }

    private static string CorePrefix(IReadOnlyList<int> classes, int efficiencyClass)
    {
        if (classes.Count < 2) return "C";
        if (efficiencyClass == classes[0]) return "P";
        return classes.Count == 2 ? "E" : $"C{efficiencyClass}-";
    }

    private static string FormatBytes(long bytes)
        => bytes >= 1024 * 1024 ? $"{bytes / (1024.0 * 1024):0.#} Mo" : $"{bytes / 1024.0:0} Ko";
}
