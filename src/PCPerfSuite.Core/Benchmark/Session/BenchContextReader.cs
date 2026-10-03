using System.Reflection;
using System.Runtime.InteropServices;
using PCPerfSuite.Core.Compatibility;
using PCPerfSuite.Core.Hardware;
using PCPerfSuite.Core.Hardware.Cpu;
using PCPerfSuite.Core.Hardware.Memory;
using PCPerfSuite.Core.SystemInfo;

namespace PCPerfSuite.Core.Benchmark.Session;

/// <summary>Le contexte machine d'une session de bench : ce qui explique un écart entre deux PC ou deux jours.</summary>
public sealed record BenchContext(
    string CpuName, string CpuVendor, int CpuFamily, int CpuModel, bool CpuIsHybrid, int? PhysicalCores, int LogicalProcessors,
    long? LargestL3Bytes, bool? HasMixedL3Sizes,
    double? RamTotalGb, string? RamType, int? RamSpeedMhz, int RamModules,
    IReadOnlyList<string> GpuNames,
    string Manufacturer, string Model, string Chassis,
    string Windows, string AppVersion, string Runtime, bool IsAdministrator,
    float? CpuSustainedWatts, float? CpuBurstWatts, float? GpuPowerLimitPercent,
    string? FansDescription,
    string? VendorMode, Unavailable? VendorModeUnavailable,
    string? PcieLink, Unavailable? PcieLinkUnavailable,
    PowerModeReading PowerMode,
    IReadOnlyList<string> Problems);

/// <summary>Sources optionnelles du contexte, fournies par l'app ; chacune peut lever ou rendre null.</summary>
public sealed class BenchContextSources
{
    public Func<CpuPowerLimitSnapshot?>? CpuPowerLimits { get; init; }

    public Func<GpuControlSnapshot?>? GpuControl { get; init; }

    public Func<HardwareSnapshot?>? LastSnapshot { get; init; }

    /// <summary>Texte des courbes de ventilation en vigueur, écrit par la page des ventilateurs ; null si non pilotées.</summary>
    public string? FansDescription { get; init; }

    public CpuPlatform? Platform { get; init; }

    public CpuTopology? Topology { get; init; }
}

/// <summary>
/// Lecture du contexte, best-effort : chaque source qui manque devient une ligne de <see cref="BenchContext.Problems"/>,
/// jamais une exception. Le mode constructeur des portables (Silence / Turbo) n'est lu par aucun module
/// <c>LaptopFans</c> à ce jour : il est « N/D » avec sa raison, comme le lien PCIe que #4 n'a pas encore livré.
/// </summary>
public static class BenchContextReader
{
    public static BenchContext Read(BenchContextSources? sources = null)
    {
        sources ??= new BenchContextSources();
        var problems = new List<string>();

        CpuPlatform? platform = Try(() => sources.Platform ?? CpuPlatformDetector.Detect(), "processeur", problems);
        CpuTopology? topology = Try(() => sources.Topology ?? CpuTopology.Read().Topology, "topologie", problems);
        HardwareSnapshot? snapshot = Try(() => sources.LastSnapshot?.Invoke(), "relevé", problems);
        CpuPowerLimitSnapshot? cpuLimits = Try(() => sources.CpuPowerLimits?.Invoke(), "limites CPU", problems);
        GpuControlSnapshot? gpu = Try(() => sources.GpuControl?.Invoke(), "GPU", problems);
        SystemMemoryReader.Reading? memory = Try(SystemMemoryReader.Read, "mémoire", problems);
        MemoryModuleReader.Report? modules = Try(() => MemoryModuleReader.Current, "barrettes", problems);
        MachineInfo? machine = Try(() => MachineInfo.Current, "machine", problems);
        PowerModeReading power = Try(() => PowerModeReader.Read(), "alimentation", problems) ?? new PowerModeReading(null, "non lu", null, null, "non lu");

        long? l3 = topology?.Clusters.Select(c => c.L3Bytes).Where(b => b is > 0).Max();
        int? speed = modules?.Modules.Select(m => m.SpeedMhz).Where(s => s is > 0).Max();
        string? ramType = snapshot?.Memory.TypeLabel;

        return new BenchContext(
            platform?.Name ?? snapshot?.Cpu.Name ?? "processeur inconnu",
            platform?.Vendor.ToString() ?? "inconnu",
            platform?.Family ?? 0,
            platform?.Model ?? 0,
            platform?.IsHybrid ?? topology?.IsHybrid ?? false,
            topology?.PhysicalCoreCount,
            topology?.LogicalProcessors.Count ?? Environment.ProcessorCount,
            l3,
            topology?.HasMixedL3Sizes,
            memory?.TotalGb,
            ramType,
            speed,
            modules?.Modules.Count ?? 0,
            machine?.VideoControllers ?? (gpu is not null ? new[] { gpu.Name } : Array.Empty<string>()),
            machine?.Manufacturer ?? "inconnu",
            machine?.Model ?? "inconnu",
            machine?.Chassis.ToString() ?? "inconnu",
            Environment.OSVersion.VersionString,
            AppVersion(),
            RuntimeInformation.FrameworkDescription,
            ElevationHelper.IsAdministrator(),
            cpuLimits?.SustainedWatts,
            cpuLimits?.BurstWatts,
            gpu is { PowerLimitSupported: true } ? gpu.PowerLimitPercent : null,
            sources.FansDescription,
            null,
            new Unavailable(UnavailableCause.UnsupportedModel, machine?.IsLaptop == true
                ? "mode constructeur (Silence / Turbo) non lu sur cette marque"
                : "mode constructeur : sans objet sur un PC de bureau"),
            null,
            new Unavailable(UnavailableCause.HardwareOrDriver, "relevé des liens PCIe à venir (#4)"),
            power,
            problems);
    }

    public static string AppVersion()
    {
        Assembly? entry = Assembly.GetEntryAssembly();
        string? informational = entry?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational)) return informational.Split('+')[0];
        return entry?.GetName().Version?.ToString() ?? "inconnue";
    }

    private static T? Try<T>(Func<T?> read, string what, List<string> problems)
    {
        try
        {
            return read();
        }
        catch (Exception ex)
        {
            problems.Add($"{what} : {ex.GetType().Name}");
            return default;
        }
    }
}
