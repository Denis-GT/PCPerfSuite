using System.Globalization;
using PCPerfSuite.Core.Compatibility;

namespace PCPerfSuite.Core.Profiles;

/// <summary>Ce que la page Profils sait et que le diagnostic reprend : la conformité du groupe actif (avec l'heure de
/// la vérification) et le dernier rapport d'application. Null tant que rien n'a été vérifié ou appliqué.</summary>
public sealed record ProfileGroupsStatus(string? Conformity, DateTimeOffset? CheckedUtc, string? LastReport);

/// <summary>
/// Ligne « Groupes de profils » du diagnostic « Compatibilité de ce PC » : nombre de groupes, groupe actif et
/// conformité, dernier rapport, bail de réglage en cours, groupes suspendus après un incident. Tout vient de la mémoire
/// de la page (aucune lecture de fichier ni du matériel ici). « Expérimental » : la fonction n'a pas encore été vérifiée
/// sur une vraie machine (règle 6).
/// </summary>
public sealed class ProfileGroupsRowProvider : ICompatibilityRowProvider
{
    public const string RowTitle = "Groupes de profils";

    private readonly Func<ProfileGroupsSettings> _settings;
    private readonly TuningLease _lease;
    private readonly Func<ProfileGroupsStatus> _status;

    public ProfileGroupsRowProvider(Func<ProfileGroupsSettings> settings, TuningLease lease, Func<ProfileGroupsStatus> status)
    {
        _settings = settings;
        _lease = lease;
        _status = status;
    }

    public string Title => RowTitle;

    public IReadOnlyList<CompatibilityRow> GetRows()
    {
        ProfileGroupsSettings settings = _settings();
        ProfileGroupsStatus status = _status();
        TuningLeaseHolder? holder = _lease.Holder;

        int count = settings.Groups.Count;
        string countText = count switch
        {
            0 => "aucun groupe",
            1 => "1 groupe",
            _ => $"{count.ToString(CultureInfo.InvariantCulture)} groupes",
        };

        var details = new List<string>();
        if (settings.Active is { } active && settings.Find(active.GroupId) is { } group)
        {
            string conformity = status.Conformity is null
                ? "conformité pas encore vérifiée"
                : status.CheckedUtc is { } checkedUtc
                    ? $"{status.Conformity} (vérifié à {checkedUtc.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)})"
                    : status.Conformity;
            string startup = active.MadeStartupState ? "" : ", sans en faire l'état de démarrage";
            details.Add($"Groupe actif : « {group.Name} »{startup} ; {conformity}.");
        }
        else
        {
            details.Add("Aucun groupe appliqué.");
        }

        if (status.LastReport is { Length: > 0 } report) details.Add($"Dernier rapport : {report.Replace(Environment.NewLine, " ")}");

        details.Add(holder is null ? "Bail de réglage libre." : holder.Describe(_lease.UtcNow, _lease.LocalTimeZone));

        List<string> suspended = settings.Suspensions
            .Select(s => settings.Find(s.Key) is { } g ? $"« {g.Name} » ({s.Value.Cause})" : null)
            .OfType<string>()
            .ToList();
        if (suspended.Count > 0) details.Add($"Suspendu(s) après un incident : {string.Join(", ", suspended)}.");

        details.Add("Expérimental : pas encore vérifié sur une vraie machine.");

        return [new CompatibilityRow(RowTitle, countText, string.Join(" ", details), IsSupported: suspended.Count == 0)];
    }
}
