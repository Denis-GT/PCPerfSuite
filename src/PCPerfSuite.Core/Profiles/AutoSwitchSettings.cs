using System.Text.Json;
using System.Text.Json.Serialization;
using PCPerfSuite.Core.Hardware;
using PCPerfSuite.Core.Processes;
using PCPerfSuite.Core.Safety;

namespace PCPerfSuite.Core.Profiles;

/// <summary>
/// Bloc « AutoSwitch » de settings.json : la bascule automatique de profils selon l'usage (#9). Écrit par la bascule et
/// son sous-onglet (AppSettingsStore.Update), jamais par la page Groupes. Tolérant comme tout settings.json (une
/// exception de lecture remet le fichier à zéro) : rien d'obligatoire, aucun enum, des collections qui peuvent manquer,
/// et ce qu'une version plus récente a écrit est conservé. L'historique, lui, vit dans usage.json (UsageHistory).
///
/// Le groupe de chaque usage n'est pas noté ici : il se déduit de <see cref="ProfileGroup.Usage"/>
/// (<see cref="UsageGroupResolver"/>), que la page Groupes sait déjà modifier.
/// </summary>
public sealed class AutoSwitchSettings
{
    /// <summary>La bascule tourne : relevé de l'usage (historique compris) et bascules. Désactivée par défaut, et rien
    /// n'est enregistré tant qu'elle l'est.</summary>
    public bool Enabled { get; set; }

    /// <summary>Une bulle discrète à chaque bascule (décision de Denis, désactivable).</summary>
    public bool NotifyEachSwitch { get; set; } = true;

    /// <summary>À la génération, prendre aussi en main les ventilateurs laissés au BIOS (décoché par défaut).</summary>
    public bool IncludeBiosFans { get; set; }

    /// <summary>Règles par application, dans l'ordre : la première qui correspond l'emporte.</summary>
    public List<AutoSwitchRule>? Rules { get; set; } = new();

    /// <summary>Explication de chaque groupe généré (clé = <see cref="ProfileGroup.Id"/>), avec la révision à laquelle
    /// elle a été écrite : une révision différente veut dire que le groupe a changé depuis.</summary>
    public Dictionary<string, GeneratedExplanation>? Explanations { get; set; } = new();

    /// <summary>Dernière génération des groupes (bandeau « affiner »).</summary>
    public DateTimeOffset? LastGenerationUtc { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    /// <summary>Remet d'aplomb un bloc édité à la main : listes manquantes recréées, règles nulles retirées, identifiant
    /// absent ou en double régénéré, explications nulles retirées. Vrai si quelque chose a changé ; ne lève jamais.</summary>
    public bool Normalize()
    {
        bool changed = false;
        if (Rules is null)
        {
            Rules = new List<AutoSwitchRule>();
            changed = true;
        }

        changed |= Rules.RemoveAll(r => r is null) > 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (AutoSwitchRule rule in Rules)
        {
            if (string.IsNullOrWhiteSpace(rule.Id) || !seen.Add(rule.Id))
            {
                rule.Id = AutoSwitchRule.NewId();
                seen.Add(rule.Id);
                changed = true;
            }
        }

        if (Explanations is null)
        {
            Explanations = new Dictionary<string, GeneratedExplanation>();
            changed = true;
        }

        foreach (string key in Explanations.Where(p => p.Value is null).Select(p => p.Key).ToList())
        {
            Explanations.Remove(key);
            changed = true;
        }

        return changed;
    }
}

/// <summary>Une règle par application : cette application (<see cref="Match"/>) → un usage, ou un groupe précis.</summary>
public sealed class AutoSwitchRule
{
    public string Id { get; set; } = NewId();

    public ApplicationMatch? Match { get; set; }

    /// <summary>Usage visé (<see cref="ProfileGroupUsage"/>), quand la règle ne vise pas un groupe.</summary>
    public string? Usage { get; set; }

    /// <summary>Groupe visé (<see cref="ProfileGroup.Id"/>) ; l'emporte sur <see cref="Usage"/>.</summary>
    public string? GroupId { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>Nom affiché de l'application (« game »).</summary>
    public string? Label { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    public static string NewId() => Guid.NewGuid().ToString("D");

    [JsonIgnore]
    public string DisplayLabel => !string.IsNullOrWhiteSpace(Label) ? Label.Trim()
        : Match?.Path is { } path && ApplicationPaths.Normalize(path) is { } normalized ? ApplicationPaths.DisplayName(normalized)
        : Match?.FileName ?? "application";
}

/// <summary>Explication lisible d'un groupe généré, et la révision du groupe à laquelle elle correspond.</summary>
public sealed class GeneratedExplanation
{
    public int Revision { get; set; }

    public List<string>? Lines { get; set; } = new();

    public DateTimeOffset GeneratedUtc { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Règles par application, en logique pure.</summary>
public static class AutoSwitchRules
{
    /// <summary>
    /// La première règle active et utilisable qui correspond à <paramref name="appPath"/>, et dont la cible existe
    /// (usage connu, ou groupe présent) ; null sinon. <paramref name="publisherOf"/> n'est consulté que pour une règle qui
    /// exige un éditeur. Ne lève jamais.
    /// </summary>
    public static UsageRuleTarget? Find(
        IEnumerable<AutoSwitchRule>? rules, string? appPath, Func<string, string?>? publisherOf, Func<string, bool> groupExists)
    {
        if (rules is null || string.IsNullOrWhiteSpace(appPath)) return null;

        try
        {
            foreach (AutoSwitchRule rule in rules)
            {
                if (rule is not { Enabled: true, Match: { IsUsable: true } match }) continue;

                string? groupId = !string.IsNullOrWhiteSpace(rule.GroupId) && groupExists(rule.GroupId) ? rule.GroupId : null;
                string? usage = groupId is null && ProfileGroupUsage.All.Contains(rule.Usage) ? rule.Usage : null;
                if (groupId is null && usage is null) continue;
                if (!match.Matches(appPath, publisherOf)) continue;

                return new UsageRuleTarget(rule.Id, usage, groupId, rule.DisplayLabel);
            }
        }
        catch (Exception)
        {
            // Une règle illisible ne doit pas arrêter la bascule : aucune règle ne correspond.
        }

        return null;
    }

    /// <summary>Vrai si au moins une règle active exige un éditeur (le cache des éditeurs ne sert qu'alors).</summary>
    public static bool AnyRequiresPublisher(IEnumerable<AutoSwitchRule>? rules)
        => rules?.Any(r => r is { Enabled: true, Match.RequiresPublisher: true }) == true;
}

/// <summary>Le groupe retenu pour une cible : null s'il n'y en a pas ; <see cref="Suspended"/> s'il est suspendu après un
/// incident, ou s'il porte les mêmes valeurs relevées qu'un groupe suspendu (on n'y bascule pas, <see cref="SuspendedBy"/>
/// nomme alors ce groupe) ; <see cref="Candidates"/> : nombre de groupes pour cet usage (plusieurs : signalé).</summary>
public sealed record UsageGroupChoice(ProfileGroup? Group, bool Suspended, int Candidates, string? SuspendedBy = null)
{
    public static UsageGroupChoice None { get; } = new(null, false, 0);
}

/// <summary>Quel groupe appliquer pour une cible du classifieur, d'après les groupes de la page Profils.</summary>
public static class UsageGroupResolver
{
    /// <summary>
    /// Une règle vers un groupe : ce groupe. Un usage : les groupes de cet usage qui touchent à quelque chose ; parmi ceux
    /// qui ne sont pas suspendus, un groupe fait à la main l'emporte sur un groupe généré (l'utilisateur a remplacé le
    /// sien), puis le plus récemment modifié. Si tous sont suspendus, le premier est rendu avec <c>Suspended</c>.
    /// </summary>
    public static UsageGroupChoice Resolve(ProfileGroupsSettings store, UsageTarget target)
    {
        if (target.GroupId is { } id)
        {
            ProfileGroup? group = store.Find(id);
            if (group is null || group.IsEmpty) return UsageGroupChoice.None;
            ProfileGroup? by = SuspendedBy(store, group);
            return new UsageGroupChoice(group, by is not null, 1, by?.Name);
        }

        if (target.Usage is not { } usage) return UsageGroupChoice.None;

        List<ProfileGroup> candidates = store.Groups
            .Where(g => string.Equals(g.Usage, usage, StringComparison.Ordinal) && !g.IsEmpty)
            .ToList();
        if (candidates.Count == 0) return UsageGroupChoice.None;

        ProfileGroup? best = candidates
            .Where(g => SuspendedBy(store, g) is null)
            .OrderBy(g => g.IsGenerated ? 1 : 0)
            .ThenByDescending(g => g.UpdatedUtc ?? g.CreatedUtc ?? DateTimeOffset.MinValue)
            .FirstOrDefault();
        return best is null
            ? new UsageGroupChoice(candidates[0], true, candidates.Count, SuspendedBy(store, candidates[0])?.Name)
            : new UsageGroupChoice(best, false, candidates.Count);
    }

    /// <summary>
    /// Le groupe suspendu qui empêche de poser <paramref name="group"/> : lui-même, ou un autre groupe suspendu qui porte
    /// les mêmes valeurs relevées (même overclock GPU, mêmes watts). Les deux groupes de jeu générés reprennent le même
    /// overclock de l'onglet GPU : celui qui a planté ne doit pas revenir par l'autre. Null si rien ne l'en empêche.
    /// </summary>
    public static ProfileGroup? SuspendedBy(ProfileGroupsSettings store, ProfileGroup group)
    {
        if (store.Suspensions is not { Count: > 0 } suspensions) return null;
        if (suspensions.ContainsKey(group.Id)) return group;

        return store.Groups.FirstOrDefault(other =>
            !ReferenceEquals(other, group) && suspensions.ContainsKey(other.Id) && SharesRaisedValues(other, group));
    }

    private static bool SharesRaisedValues(ProfileGroup suspended, ProfileGroup group)
    {
        bool sameOverclock = suspended.Gpu is { ParsedKind: ProfilePartKind.Values, Values: { } a }
                             && group.Gpu is { ParsedKind: ProfilePartKind.Values, Values: { } b }
                             && GpuOverclockRaise.IsRaisedProfile(a)
                             && a.CoreClockOffsetMhz == b.CoreClockOffsetMhz
                             && a.MemoryClockOffsetMhz == b.MemoryClockOffsetMhz
                             // Décalages relevés et identiques : le même OC, quelle que soit la puissance. Sans décalage,
                             // l'OC est la puissance et la tension, qui doivent alors être les mêmes.
                             && (a.CoreClockOffsetMhz > 0 || a.MemoryClockOffsetMhz > 0 || SamePowerAndVoltage(a, b));
        bool sameWatts = suspended.Cpu is { ParsedKind: ProfilePartKind.Values, Values.SustainedWatts: { } x }
                         && group.Cpu is { ParsedKind: ProfilePartKind.Values, Values.SustainedWatts: { } y }
                         && Math.Abs(x - y) <= 1f;
        return sameOverclock || sameWatts;
    }

    /// <summary>Les deux profils portent la même limite de puissance et la même tension.</summary>
    private static bool SamePowerAndVoltage(GpuOverclockProfile a, GpuOverclockProfile b)
        => (a.PowerLimitPercent, b.PowerLimitPercent) switch
           {
               (null, null) => true,
               ({ } x, { } y) => Math.Abs(x - y) <= 0.5f,
               _ => false,
           }
           && a.GetVoltage() == b.GetVoltage();
}
