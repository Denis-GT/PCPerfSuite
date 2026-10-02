using System.Globalization;
using PCPerfSuite.Core.PowerSettings;
using PCPerfSuite.Core.Safety;
using PCPerfSuite.Core.SystemInfo;

namespace PCPerfSuite.Core.Profiles;

/// <summary>Demandeur de la bascule automatique, dans le bail de réglage et le journal de session.</summary>
public static class AutoSwitchRequester
{
    public const string Id = "bascule-auto";
    public const string Label = "la bascule automatique";
}

/// <summary>
/// Reprise au lancement de la bascule automatique (étape <see cref="RecoveryStage.AutoSwitch"/>), après celle des groupes
/// de profils : une bascule vers un groupe risqué ouvre la période probatoire de #8 (composant « groupe-profils », valeur
/// « demandeur » = <see cref="AutoSwitchRequester.Id"/>). Si un arrêt anormal, un écran bleu ou un TDR l'a suivie
/// (<see cref="ProfileGroupIncidentPolicy"/>), #8 a déjà suspendu le groupe et la bascule n'y reviendra pas d'elle-même ;
/// ce gestionnaire le note au journal des bascules (usage.json), pour que la bascule prévienne l'utilisateur au
/// lancement. Aucune fenêtre ici ; peut lever (StartupRecovery l'isole).
/// </summary>
public sealed class AutoSwitchRecoveryHandler : IStartupRecoveryHandler
{
    private readonly Func<string> _usagePath;
    private readonly Func<string, string?> _groupName;
    private readonly TimeProvider _time;

    public AutoSwitchRecoveryHandler(Func<string>? usagePath = null, Func<string, string?>? groupName = null, TimeProvider? time = null)
    {
        _usagePath = usagePath ?? (() => AppDataPaths.Current.UsageFile);
        _groupName = groupName ?? (id => AppSettingsStore.Load().ProfileGroups?.Find(id)?.Name);
        _time = time ?? TimeProvider.System;
    }

    public string Id => AutoSwitchRequester.Id;

    public RecoveryStage Stage => RecoveryStage.AutoSwitch;

    public IReadOnlyCollection<string> Components { get; } = [ProfileGroupProbation.Component];

    /// <summary>Vrai si l'opération reprise est une bascule automatique.</summary>
    public static bool IsAutoSwitch(SessionJournalEntry entry)
        => entry.Values.TryGetValue(ProfileGroupProbation.RequesterKey, out string? requester)
           && string.Equals(requester, AutoSwitchRequester.Id, StringComparison.Ordinal);

    public string? Handle(IReadOnlyList<RecoveredEntry> entries)
    {
        List<ProfileGroupIncidentDecision> decisions = entries
            .Where(e => IsAutoSwitch(e.Entry))
            .Select(ProfileGroupIncidentPolicy.Evaluate)
            .OfType<ProfileGroupIncidentDecision>()
            .ToList();
        if (decisions.Count == 0) return null;

        DateTimeOffset now = _time.GetUtcNow();
        string path = _usagePath();
        UsageHistoryRead read = UsageHistoryStore.Read(path);
        UsageHistory history = UsageHistory.FromFile(read.File, now, _time.LocalTimeZone);
        foreach (ProfileGroupIncidentDecision decision in decisions)
        {
            history.AddJournal(new AutoSwitchJournalEntry
            {
                TimeUtc = now,
                Kind = AutoSwitchJournalKinds.Incident,
                GroupId = decision.GroupId,
                GroupName = decision.GroupId is { } id ? SafeName(id) : null,
                Reason = $"{decision.Cause} après une bascule automatique : le groupe est suspendu, la bascule n'y reviendra pas d'elle-même",
            });
        }

        string? problem = UsageHistoryStore.Write(path, UsageHistoryStore.Serialize(history.ToFile()));
        string count = decisions.Count.ToString(CultureInfo.InvariantCulture);
        string note = $"{count} bascule(s) automatique(s) suivie(s) d'un incident ({decisions[0].Cause}), notée(s) au journal des bascules";
        return problem is null ? note : $"{note} ; {problem}";
    }

    private string? SafeName(string groupId)
    {
        try
        {
            return _groupName(groupId);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
