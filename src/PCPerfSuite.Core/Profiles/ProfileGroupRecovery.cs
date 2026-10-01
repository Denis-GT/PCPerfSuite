using System.Globalization;
using PCPerfSuite.Core.PowerSettings;
using PCPerfSuite.Core.Safety;
using PCPerfSuite.Core.Safety.Events;

namespace PCPerfSuite.Core.Profiles;

/// <summary>Un incident imputé à l'application d'un groupe, et ce qu'elle avait relevé.</summary>
public sealed record ProfileGroupIncidentDecision(
    string? GroupId, string Cause, bool GpuRaised, bool WattsRaised, bool MadeStartupState, string Action);

/// <summary>
/// Ce qui empêche un groupe d'être réappliqué (décision de Denis, 01/10/2026) : un arrêt anormal (brutal, forcé,
/// inattendu), un écran bleu, un TDR moins de 30 min après l'application, et un redémarrage dont la cause n'a pas pu
/// être établie (journal Système illisible, ou pas lu à temps au lancement). Un arrêt normal de Windows, ou l'app seule
/// qui s'arrête, ne compte pas.
///
/// Un arrêt est journalisé au démarrage suivant : son heure exacte n'est pas connue. Il compte parce que l'opération
/// était encore ouverte, c'est-à-dire dans les 30 min (elle est close au terme, ou à la fermeture propre de l'app).
/// Limite connue, comme pour tout le journal : une app tuée de l'extérieur laisse son opération ouverte, et un arrêt
/// survenu plus tard lui est imputé.
/// </summary>
public static class ProfileGroupIncidentPolicy
{
    public static ProfileGroupIncidentDecision? Evaluate(RecoveredEntry recovered)
    {
        SessionJournalEntry entry = recovered.Entry;
        if (!string.Equals(entry.Component, ProfileGroupProbation.Component, StringComparison.Ordinal)) return null;

        IncidentQualification qualification = recovered.Qualification;
        string? cause = qualification.Kind switch
        {
            IncidentQualificationKind.PowerLoss or IncidentQualificationKind.ForcedShutdown
                or IncidentQualificationKind.UnexpectedShutdown or IncidentQualificationKind.BlueScreen => qualification.Summary,
            IncidentQualificationKind.Unknown => $"cause non établie : {qualification.Summary}",
            _ => null,
        };

        if (cause is null && Tdr(qualification.Incidents, entry.StartedUtc) is { } tdr)
        {
            int minutes = (int)Math.Max(0, Math.Round((tdr.LoggedUtc - entry.StartedUtc).TotalMinutes));
            cause = $"pilote graphique relancé (TDR) {minutes.ToString(CultureInfo.InvariantCulture)} min après l'application";
        }

        if (cause is null) return null;

        return new ProfileGroupIncidentDecision(
            entry.Values.TryGetValue(ProfileGroupProbation.GroupKey, out string? group) && group.Length > 0 ? group : null,
            cause,
            Yes(entry, ProfileGroupProbation.GpuKey),
            Yes(entry, ProfileGroupProbation.WattsKey),
            Yes(entry, ProfileGroupProbation.StartupStateKey),
            entry.Action);
    }

    /// <summary>Le premier TDR journalisé pendant la période probatoire qui commence à <paramref name="since"/>.</summary>
    public static Incident? Tdr(IEnumerable<Incident> incidents, DateTimeOffset since)
        => incidents
            .Where(i => i.Kind == IncidentKind.DisplayDriverReset && i.LoggedUtc >= since && i.LoggedUtc <= since + ProfileGroupProbation.Window)
            .OrderBy(i => i.LoggedUtc)
            .FirstOrDefault();

    private static bool Yes(SessionJournalEntry entry, string key)
        => entry.Values.TryGetValue(key, out string? value) && string.Equals(value, "oui", StringComparison.Ordinal);
}

/// <summary>
/// Reprise au lancement des groupes de profils (étape <see cref="RecoveryStage.ProfileGroups"/>), avant toute fenêtre et
/// avant que les onglets ne réappliquent leurs réglages : pour chaque application de groupe suivie d'un incident,
/// décoche « Appliquer au démarrage » dans Processeur et/ou GPU quand le groupe en avait fait l'état de démarrage et y
/// avait relevé quelque chose (la même écriture que la sécurité thermique), et suspend le groupe. La page Profils le dit
/// ensuite ; réappliquer un groupe suspendu demande une confirmation.
///
/// C'est la seule écriture du bloc ProfileGroups hors de la page Profils : elle a lieu avant que celle-ci n'existe, et ne
/// touche qu'aux suspensions. Peut lever : StartupRecovery l'isole.
/// </summary>
public sealed class ProfileGroupRecoveryHandler : IStartupRecoveryHandler
{
    private readonly Action<Action<AppSettings>> _update;
    private readonly TimeProvider _time;

    public ProfileGroupRecoveryHandler(Action<Action<AppSettings>>? update = null, TimeProvider? time = null)
    {
        _update = update ?? AppSettingsStore.Update;
        _time = time ?? TimeProvider.System;
    }

    public string Id => ProfileGroupProbation.Component;

    public RecoveryStage Stage => RecoveryStage.ProfileGroups;

    public IReadOnlyCollection<string> Components { get; } = [ProfileGroupProbation.Component];

    public string? Handle(IReadOnlyList<RecoveredEntry> entries)
    {
        List<ProfileGroupIncidentDecision> decisions = entries
            .Select(ProfileGroupIncidentPolicy.Evaluate)
            .OfType<ProfileGroupIncidentDecision>()
            .ToList();
        if (decisions.Count == 0) return null;

        DateTimeOffset now = _time.GetUtcNow();
        bool cpuUnchecked = false, gpuUnchecked = false;
        int suspended = 0;

        _update(settings =>
        {
            settings.ProfileGroups ??= new ProfileGroupsSettings();
            settings.ProfileGroups.Suspensions ??= new Dictionary<string, ProfileGroupSuspension>();

            foreach (ProfileGroupIncidentDecision decision in decisions)
            {
                bool cpu = decision.MadeStartupState && decision.WattsRaised && settings.Cpu.ApplyAtStartup;
                bool gpu = decision.MadeStartupState && decision.GpuRaised && settings.Gpu.ApplyOverclockAtStartup;
                if (cpu) settings.Cpu.ApplyAtStartup = false;
                if (gpu) settings.Gpu.ApplyOverclockAtStartup = false;
                cpuUnchecked |= cpu;
                gpuUnchecked |= gpu;

                if (decision.GroupId is not { } id || settings.ProfileGroups.Find(id) is null) continue;

                settings.ProfileGroups.Suspensions[id] = new ProfileGroupSuspension
                {
                    SinceUtc = now,
                    Cause = decision.Cause,
                    CpuStartupUnchecked = cpu,
                    GpuStartupUnchecked = gpu,
                };
                suspended++;
            }
        });

        var parts = new List<string> { $"{decisions.Count} application(s) de groupe suivie(s) d'un incident ({decisions[0].Cause})" };
        if (suspended > 0) parts.Add($"{suspended} groupe(s) suspendu(s)");
        if (cpuUnchecked || gpuUnchecked)
        {
            string tabs = string.Join(" et ", new[] { cpuUnchecked ? "Processeur" : null, gpuUnchecked ? "GPU" : null }.OfType<string>());
            parts.Add($"« Appliquer au démarrage » décoché : {tabs}");
        }

        return string.Join(" ; ", parts);
    }
}

/// <summary>Ce que les onglets vont reposer au lancement, quand c'est l'état risqué d'un groupe.</summary>
public sealed record ProfileGroupStartupRisk(string GroupId, bool GpuRaised, bool WattsRaised);

/// <summary>
/// Au lancement, les onglets réappliquent leurs dernières valeurs si leur case « Appliquer au démarrage » est cochée
/// (le GPU dans son constructeur). Quand ces valeurs sont l'état risqué du dernier groupe appliqué, c'est une nouvelle
/// application de ce groupe : elle ouvre sa période probatoire, avant que le GPU ne soit construit. Décidé d'après les
/// seuls réglages, en logique pure. On lit l'état de démarrage (<see cref="ProfileGroupsSettings.StartupState"/>), pas
/// le groupe actif : une bascule automatique (#9), transitoire, ne doit pas masquer l'état risqué d'un groupe appliqué à
/// la main.
/// </summary>
public static class ProfileGroupStartupCheck
{
    private const float WattsTolerance = 1f;

    public static ProfileGroupStartupRisk? Evaluate(AppSettings settings)
    {
        if (settings.ProfileGroups?.EffectiveStartupState is not { MadeStartupState: true } active) return null;
        if (settings.ProfileGroups.Suspensions?.ContainsKey(active.GroupId) == true) return null;

        bool gpu = active.GpuRaised && settings.Gpu.ApplyOverclockAtStartup && active.Gpu is { } retained && SameGpu(retained, settings.Gpu);
        bool watts = active.WattsRaised && settings.Cpu.ApplyAtStartup
                     && active.Cpu?.SustainedWatts is { } sustained && settings.Cpu.SustainedWatts is { } stored
                     && Math.Abs(stored - sustained) <= WattsTolerance;

        return gpu || watts ? new ProfileGroupStartupRisk(active.GroupId, gpu, watts) : null;
    }

    /// <summary>Les valeurs enregistrées par l'onglet GPU sont celles que le groupe a laissées.</summary>
    private static bool SameGpu(GpuRetainedValues retained, GpuControlSettings saved)
        => (retained.CoreOffsetMhz is not { } core || core == saved.CoreClockOffsetMhz)
           && (retained.MemoryOffsetMhz is not { } memory || memory == saved.MemoryClockOffsetMhz)
           && (retained.PowerLimitPercent is not { } power || (saved.PowerLimitPercent is { } p && Math.Abs(p - power) <= 0.5f))
           && (retained.TemperatureLimitC is not { } temperature || temperature == saved.TemperatureLimitC)
           && (retained.Voltage is not { } voltage || voltage == saved.VoltageValue);
}
