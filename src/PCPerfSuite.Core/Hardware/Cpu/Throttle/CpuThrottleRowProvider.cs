using PCPerfSuite.Core.Compatibility;
using PCPerfSuite.Core.Safety.Events;

namespace PCPerfSuite.Core.Hardware.Cpu.Throttle;

/// <summary>
/// Diagnostic « Raisons de bridage CPU » : d'où vient la lecture (MSR Intel, PM table AMD, compteurs Windows), ce qui
/// bridait le processeur au dernier relevé, TjMax et fréquences, les raisons fines et pourquoi elles manquent, et le
/// nombre de limitations par le micrologiciel journalisées par Windows (Kernel-Processor-Power 37) sur 30 jours.
/// </summary>
public sealed class CpuThrottleRowProvider : ICompatibilityRowProvider
{
    public const string RowTitle = "Raisons de bridage CPU";

    private readonly Func<HardwareSnapshot?> _lastSnapshot;
    private SystemEventReadResult? _firmwareEvents;

    /// <param name="lastSnapshot">Dernier relevé des capteurs (<see cref="HardwareMonitorService.LastSnapshot"/>).</param>
    public CpuThrottleRowProvider(Func<HardwareSnapshot?> lastSnapshot) => _lastSnapshot = lastSnapshot;

    public string Title => RowTitle;

    public Task RefreshAsync(CancellationToken cancellationToken)
    {
        SystemEventReadResult events = SystemEventReader.ReadLastDays(WindowsEventsRowProvider.Days,
            [SystemEventKind.FirmwareLimited], cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        Volatile.Write(ref _firmwareEvents, events);
        return Task.CompletedTask;
    }

    public IReadOnlyList<CompatibilityRow> GetRows()
    {
        HardwareSnapshot? snapshot = _lastSnapshot();
        return [BuildRow(snapshot?.CpuThrottle, Volatile.Read(ref _firmwareEvents))];
    }

    public static CompatibilityRow BuildRow(CpuThrottleReading? reading, SystemEventReadResult? firmwareEvents)
    {
        if (reading is null)
        {
            return new CompatibilityRow(RowTitle, "Pas encore lu", "Le premier relevé du processeur n'a pas encore eu lieu.", true);
        }

        string? reasons = CpuThrottleText.Reasons(reading);
        string status = reasons switch
        {
            null => $"N/D, {CpuThrottleText.Source(reading)} seulement",
            "aucun" => $"Non bridé ({CpuThrottleText.Source(reading)})",
            _ => $"Bridé : {reasons} ({CpuThrottleText.Source(reading)})",
        };

        var parts = new List<string> { "Au dernier relevé (relevé à l'ouverture de l'onglet)." };
        if (CpuThrottleText.Figures(reading) is { Length: > 0 } figures) parts.Add($"{Capitalize(figures)}.");
        if (CpuThrottleText.CounterHint(reading) is { } hint) parts.Add($"{Capitalize(hint)}.");
        if (reading.PerformanceLimitFlags is { } flags and not 0) parts.Add($"Drapeaux de limite Windows 0x{flags:X} (non documentés, simple indice).");

        if (reading.Cores is { } cores)
        {
            parts.Add($"Balayage de {cores.LogicalProcessorsRead} processeurs logiques : thermique {cores.Thermal}, PROCHOT {cores.Prochot}, "
                + $"puissance {cores.PowerLimit}, courant {Count(cores.CurrentLimit)}, autre domaine {Count(cores.CrossDomain)}.");
        }

        if (reading.FineReasons is { } fine) parts.Add($"Raisons fines (0x64F, expérimental) : {FineReasons(fine)}.");
        else if (reading.FineReasonsUnavailable is { } fineMissing) parts.Add($"N/D : {fineMissing.Reason}.");

        if (reading.Unavailable is { } missing) parts.Add($"N/D : {missing.Reason}.");

        parts.Add(firmwareEvents switch
        {
            null => "Limitations par le micrologiciel : pas encore lues.",
            { Events: { } events } => events.Count == 0
                ? $"Aucune limitation par le micrologiciel (Kernel-Processor-Power 37) sur {WindowsEventsRowProvider.Days} jours."
                : $"Vitesse limitée par le micrologiciel (Kernel-Processor-Power 37) : {events.Count} fois sur {WindowsEventsRowProvider.Days} jours.",
            { Problem: { } problem } => $"Limitations par le micrologiciel : N/D ({problem.Reason}).",
            _ => "",
        });

        if (reading.Source == CpuThrottleSource.IntelMsr)
        {
            parts.Add("Les bits « log » d'Intel ne sont ni lus comme un état ni effacés ; rien n'est écrit dans le processeur.");
        }

        return new CompatibilityRow(RowTitle, status, string.Join(" ", parts.Where(p => p.Length > 0)), reading.Unavailable is null);
    }

    private static string Count(int? value) => value is { } v ? v.ToString() : "N/D";

    private static string FineReasons(IntelPerfLimitReasons fine)
    {
        var active = new List<string>();
        if (fine.Prochot) active.Add("PROCHOT");
        if (fine.Thermal) active.Add("thermique");
        if (fine.RunningAverageThermal) active.Add("thermique moyenné");
        if (fine.VoltageRegulatorThermal) active.Add("régulateur trop chaud");
        if (fine.VoltageRegulatorCurrent) active.Add("courant du régulateur");
        if (fine.ElectricalDesign) active.Add("limite électrique");
        if (fine.PackagePl1) active.Add("PL1");
        if (fine.PackagePl2) active.Add("PL2");
        if (fine.MaxTurboLimit) active.Add("turbo multi-cœur");
        if (fine.TurboTransitionAttenuation) active.Add("atténuation du turbo");
        if (fine.ResidencyStateRegulation) active.Add("régulation d'état");
        return active.Count > 0 ? string.Join(", ", active) : "aucune";
    }

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
