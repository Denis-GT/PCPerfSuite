using PCPerfSuite.Core.Hardware;
using PCPerfSuite.Core.Hardware.Fans;

namespace PCPerfSuite.Core.Profiles;

/// <summary>Un ventilateur à régler : sa configuration, déjà ramenée dans les limites de l'onglet, et ce que le
/// bornage a corrigé.</summary>
public sealed record FanPlanEntry(string FanId, string Name, FanCurveConfig Config, IReadOnlyList<string> SanitizeNotes);

/// <summary>Ce que la partie ventilation d'un groupe demande à l'onglet Ventilateurs. <see cref="RestoreAuto"/> :
/// l'origine, tous les ventilateurs rendus au BIOS (leurs courbes sont gardées).</summary>
public sealed record FanGroupPlan(
    ProfilePartKind Kind,
    IReadOnlyList<FanPlanEntry> Entries,
    bool RestoreAuto,
    IReadOnlyList<ReportItem> Items,
    IReadOnlyList<string> Notes)
{
    public bool HasWork => Entries.Count > 0 || RestoreAuto;

    public static FanGroupPlan Nothing(ProfilePartKind kind, IReadOnlyList<string> notes, params ReportItem[] items)
        => new(kind, [], false, items, notes);
}

/// <summary>
/// Décide, en logique pure, ce que la partie ventilation d'un groupe peut régler sur CE PC : rien avant le premier
/// relevé (on ne sait pas encore quels ventilateurs existent), rien qu'un ventilateur listé dans l'onglet (donc jamais
/// le contrôleur embarqué d'un portable, règle 5), chaque entrée bornée (<see cref="FanProfileMatcher.Sanitize"/>), et
/// celles déjà en place laissées telles quelles. Les ventilateurs absents sont nommés, avec la raison.
/// </summary>
public static class FanGroupPlanner
{
    public const string LaptopNote =
        "Portable ou châssis non identifié : seuls les ventilateurs de la carte graphique se règlent ici, par son pilote. " +
        "Ceux du PC restent au firmware du constructeur, que PCPerfSuite ne pilote pas.";

    public static FanGroupPlan Plan(ProfileGroupFansPart part, FanTargetState state, Func<string, string> absenceReason)
    {
        string title = DimensionReport.Title(ProfileDimension.Fans);
        IReadOnlyList<string> notes = state.MotherboardFansRefused ? [LaptopNote] : [];

        switch (part.ParsedKind)
        {
            case ProfilePartKind.Unknown:
                return FanGroupPlan.Nothing(ProfilePartKind.Unknown, notes,
                    ReportItem.Ignored(title, "partie écrite par une version plus récente de PCPerfSuite, ignorée"));
            case ProfilePartKind.Empty:
                return FanGroupPlan.Nothing(ProfilePartKind.Empty, notes, ReportItem.Ignored(title, "partie vide, rien à poser"));
        }

        if (!state.IsReady)
        {
            return FanGroupPlan.Nothing(part.ParsedKind, notes,
                ReportItem.Ignored(title, "ventilateurs non réglés : aucun relevé des ventilateurs pour l'instant"));
        }

        if (state.Fans.Count == 0)
        {
            return FanGroupPlan.Nothing(part.ParsedKind, notes, ReportItem.Ignored(title, $"aucun ventilateur pilotable : {state.NoFansReason}"));
        }

        if (part.ParsedKind == ProfilePartKind.Origin) return new FanGroupPlan(ProfilePartKind.Origin, [], true, [], notes);

        FanProfile values = part.Values!;
        FanProfileMatch match = FanProfileMatcher.Match(values, state.Fans.Select(f => f.FanId).ToList());

        var entries = new List<FanPlanEntry>();
        var items = new List<ReportItem>();
        var inPlace = new List<string>();

        foreach (FanCurveConfig entry in match.Applicable)
        {
            FanTargetFan fan = state.Fans.First(f => f.FanId == entry.ControlSensorId);
            SanitizedFanCurve sanitized = FanProfileMatcher.Sanitize(entry);
            if (sanitized.Config is not { } config)
            {
                items.Add(ReportItem.Ignored(fan.Name, $"« {fan.Name} » non réglé ({string.Join(", ", sanitized.Notes)})"));
                continue;
            }

            FanCurveConfig? current = state.Current.Fans.FirstOrDefault(c => c.ControlSensorId == fan.FanId);
            if (sanitized.Notes.Count == 0 && current is not null && FanConfigs.SameSettings(current, config))
            {
                inPlace.Add($"« {fan.Name} »");
                continue;
            }

            entries.Add(new FanPlanEntry(fan.FanId, fan.Name, config, sanitized.Notes));
        }

        if (inPlace.Count > 0) items.Add(ReportItem.Applied("en place", $"déjà en place : {string.Join(", ", inPlace)}"));

        foreach (FanCurveConfig entry in match.Missing)
        {
            string name = values.FanNames?.TryGetValue(entry.ControlSensorId, out string? saved) == true && !string.IsNullOrWhiteSpace(saved)
                ? saved
                : entry.ControlSensorId;
            items.Add(ReportItem.Ignored(name, $"« {name} » non réglé ({absenceReason(entry.ControlSensorId)})"));
        }

        var allNotes = new List<string>(notes);
        if (match.NotInProfile.Count > 0)
        {
            IEnumerable<string> names = match.NotInProfile.Select(id => $"« {state.Fans.First(f => f.FanId == id).Name} »");
            allNotes.Add($"pas dans ce groupe, laissés tels quels : {string.Join(", ", names)}");
        }

        return new FanGroupPlan(ProfilePartKind.Values, entries, false, items, allNotes);
    }
}

/// <summary>Comparaison de configurations de ventilateur, pour ne pas réécrire ce qui est déjà en place et pour dire
/// si l'état courant est encore celui d'un groupe.</summary>
public static class FanConfigs
{
    private const float Tolerance = 0.05f;

    /// <summary>Mêmes réglages : mode, consigne, source, bornes, hystérésis, arrêt à froid, rampes et points.</summary>
    public static bool SameSettings(FanCurveConfig a, FanCurveConfig b) => Differences(a, b).Count == 0;

    /// <summary>Ce qui diffère, en clair (« mode Auto au lieu de Courbe »), vide si rien.</summary>
    public static IReadOnlyList<string> Differences(FanCurveConfig expected, FanCurveConfig actual)
    {
        var differences = new List<string>();
        if (expected.Mode != actual.Mode) differences.Add($"mode {ModeLabel(actual.Mode)} au lieu de {ModeLabel(expected.Mode)}");
        if (!Near(expected.ManualPercent, actual.ManualPercent)) differences.Add($"consigne manuelle {actual.ManualPercent:0} % au lieu de {expected.ManualPercent:0} %");
        if (expected.Source != actual.Source) differences.Add("température suivie différente");
        if (!Near(expected.MinPercent, actual.MinPercent) || !Near(expected.MaxPercent, actual.MaxPercent)) differences.Add("bornes différentes");
        if (!Near(expected.HysteresisC, actual.HysteresisC)) differences.Add("hystérésis différente");
        if (expected.StopBelowTempC is null != actual.StopBelowTempC is null
            || (expected.StopBelowTempC is { } e && actual.StopBelowTempC is { } a && !Near(e, a)))
        {
            differences.Add("arrêt à froid différent");
        }

        if (!Near(expected.RampUpPercentPerSecond, actual.RampUpPercentPerSecond)
            || !Near(expected.RampDownPercentPerSecond, actual.RampDownPercentPerSecond))
        {
            differences.Add("rampes différentes");
        }

        if (!SamePoints(expected.Points, actual.Points)) differences.Add("courbe différente");
        return differences;
    }

    private static bool SamePoints(IReadOnlyList<FanCurvePoint>? a, IReadOnlyList<FanCurvePoint>? b)
    {
        List<FanCurvePoint> left = (a ?? []).Where(p => p is not null).OrderBy(p => p.TempC).ToList();
        List<FanCurvePoint> right = (b ?? []).Where(p => p is not null).OrderBy(p => p.TempC).ToList();
        return left.Count == right.Count && left.Zip(right).All(pair => Near(pair.First.TempC, pair.Second.TempC) && Near(pair.First.Percent, pair.Second.Percent));
    }

    private static bool Near(float a, float b) => Math.Abs(a - b) <= Tolerance;

    public static string ModeLabel(FanControlMode mode) => mode switch
    {
        FanControlMode.Auto => "Auto",
        FanControlMode.Manual => "Manuel",
        FanControlMode.Curve => "Courbe",
        _ => "inconnu",
    };
}
