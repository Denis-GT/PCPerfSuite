using PCPerfSuite.Core.Compatibility;
using PCPerfSuite.Core.SystemInfo;

namespace PCPerfSuite.Core.PowerSettings.Animations;

/// <summary>
/// Diagnostic « Animations et effets » : la valeur lue de chaque effet, ceux que PCPerfSuite a modifiés, et le compte
/// dont le profil serait réglé (en donnée personnelle, masquée dans le rapport copié).
/// </summary>
public sealed class WindowsAnimationsRowProvider : ICompatibilityRowProvider
{
    public const string RowTitle = WindowsAnimationSettings.OwnerTitle;
    public const string AccountRowTitle = "Animations et effets : compte visé";

    private const string Experimental =
        "Expérimental : l'effet de chaque interrupteur n'a pas encore été vérifié sur une vraie machine.";

    private sealed record Reading(AnimationSnapshot Snapshot, string ProcessAccount, string? InteractiveAccount);

    private readonly WindowsAnimationSettings _settings;
    private Reading? _reading;

    public WindowsAnimationsRowProvider(WindowsAnimationSettings settings) => _settings = settings;

    public string Title => RowTitle;

    /// <summary>Hors du thread d'interface : le compte de la session se lit en parcourant les processus.</summary>
    public Task RefreshAsync(CancellationToken cancellationToken)
    {
        AnimationSnapshot snapshot = _settings.Read();
        Volatile.Write(ref _reading, new Reading(snapshot, SessionUser.ProcessAccount, SessionUser.InteractiveAccount));
        return Task.CompletedTask;
    }

    public IReadOnlyList<CompatibilityRow> GetRows()
    {
        Reading? reading = Volatile.Read(ref _reading);
        return BuildRows(reading?.Snapshot, reading?.ProcessAccount, reading?.InteractiveAccount);
    }

    /// <summary>Les lignes, isolées ici pour être testées sans Windows.</summary>
    public static IReadOnlyList<CompatibilityRow> BuildRows(AnimationSnapshot? snapshot, string? processAccount, string? interactiveAccount)
    {
        if (snapshot is null) return [new CompatibilityRow(RowTitle, "Pas encore lu", "Lecture des effets visuels en cours.", true)];

        int unreadable = snapshot.Readings.Count(r => r.Current is null);
        int changed = snapshot.ChangedCount;

        string status = snapshot.UnavailableReason is not null
            ? "Grisé : autre compte"
            : changed switch
            {
                0 => "Aucun modifié par PCPerfSuite",
                1 => "1 réglage modifié par PCPerfSuite",
                _ => $"{changed} réglages modifiés par PCPerfSuite",
            };

        string values = string.Join(" ; ", snapshot.Readings.Select(r =>
            $"{r.Setting.Name} : {WindowsAnimationSettings.OnOff(r.Current)}"
            + (r.IsChanged ? $" (origine : {WindowsAnimationSettings.OnOff(r.Original)})" : "")));

        var detail = new List<string> { $"Valeurs lues : {values}." };
        if (snapshot.UiEffectsOff) detail.Add("L'interrupteur général des effets est coupé : les effets qui en dépendent ne jouent pas.");
        if (unreadable > 0) detail.Add($"{unreadable} réglage(s) illisible(s) : une stratégie de groupe les verrouille peut-être.");
        if (snapshot.UnavailableReason is { } reason) detail.Add(reason);
        detail.Add(Experimental);

        var rows = new List<CompatibilityRow>
        {
            new(RowTitle, status, string.Join(" ", detail), snapshot.UnavailableReason is null && unreadable == 0),
        };

        if (processAccount is not null)
        {
            string interactive = interactiveAccount is null
                ? "compte de la session non déterminé"
                : string.Equals(interactiveAccount, processAccount, StringComparison.OrdinalIgnoreCase)
                    ? "c'est celui de la personne connectée"
                    : $"personne connectée : {interactiveAccount}";
            rows.Add(new CompatibilityRow(AccountRowTitle, processAccount,
                $"Les effets visuels s'écrivent dans le profil de ce compte ({interactive}).",
                snapshot.UnavailableReason is null, IsPersonal: true));
        }

        return rows;
    }
}
