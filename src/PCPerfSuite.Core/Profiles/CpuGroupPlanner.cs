using System.Globalization;
using PCPerfSuite.Core.Hardware.Cpu;

namespace PCPerfSuite.Core.Profiles;

/// <summary>Une valeur du plan d'alimentation à écrire, sur secteur et sur batterie.</summary>
public sealed record CpuPlanWrite(CpuPowerSetting Setting, uint Ac, uint Dc);

/// <summary>Limites en watts à poser ; <paramref name="Burst"/> null sur un processeur à une seule limite.</summary>
public sealed record CpuWattsTarget(float Sustained, float? Burst);

/// <summary>
/// Ce que la partie processeur d'un groupe demande à l'onglet, déjà décidé : les écritures du plan (seulement les
/// valeurs qui changent), les watts à poser, ou le retour à l'origine, et ce qui a été écarté avec sa raison
/// (<see cref="Items"/>). <see cref="RaisesWatts"/> : des watts au-dessus de l'origine, donc une période probatoire.
/// <see cref="TouchedSettings"/> et <see cref="TouchesWatts"/> : ce que le groupe règle, écrit ou déjà en place, et donc
/// ce qu'on relira pour dire s'il est encore « conforme ».
/// </summary>
public sealed record CpuGroupPlan(
    ProfilePartKind Kind,
    IReadOnlyList<CpuPlanWrite> PlanWrites,
    CpuWattsTarget? Watts,
    bool RestorePlanOrigin,
    bool RestoreWatts,
    IReadOnlyList<ReportItem> Items,
    IReadOnlyList<string> Notes,
    PowerTrend Trend,
    bool RaisesWatts,
    IReadOnlyList<CpuPowerSetting>? TouchedSettings = null,
    bool TouchesWatts = false)
{
    public bool HasWork => PlanWrites.Count > 0 || Watts is not null || RestorePlanOrigin || RestoreWatts;

    /// <summary>Le groupe touche au plan d'alimentation, dont les réglages restent après la fermeture (D7).</summary>
    public bool TouchesPlan => PlanWrites.Count > 0 || RestorePlanOrigin;

    /// <summary>Un plan sans écriture, avec ce qu'il faut en dire.</summary>
    public static CpuGroupPlan Nothing(ProfilePartKind kind, params ReportItem[] items)
        => new(kind, [], null, false, false, items, [], PowerTrend.Same, false);

    /// <summary>Le même plan, sans ses watts (ligne du journal impossible à écrire, par exemple).</summary>
    public CpuGroupPlan WithoutWatts(ReportItem reason)
        => this with { Watts = null, RaisesWatts = false, TouchesWatts = false, Items = [.. Items, reason], Trend = PowerTrend.Same };
}

/// <summary>
/// Décide, en logique pure, ce que la partie processeur d'un groupe peut poser sur CE PC :
/// <list type="bullet">
/// <item>réglages du plan : un identifiant inconnu ici est ignoré et compté, une option de liste absente est refusée
/// plutôt que rabotée, une valeur numérique est bornée, une valeur déjà en place n'est pas réécrite ;</item>
/// <item>watts : seulement sur le même processeur (<see cref="CpuIdentity.Matches"/>), si ce PC les écrit et que
/// l'avertissement de l'onglet a été accepté, jamais la valeur « sans limite » du BIOS, et jamais une hausse demandée
/// par un pilote automatique après une sécurité thermique dans la session.</item>
/// </list>
/// </summary>
public static class CpuGroupPlanner
{
    public const string PlanLabel = "Plan d'alimentation";
    public const string WattsLabel = "Limites en watts";

    /// <summary>Écart en dessous duquel des watts sont tenus pour égaux (même seuil que la sécurité thermique).</summary>
    private const float WattsTolerance = 1f;

    /// <param name="checkIdentity">Faux pour un profil de l'onglet Processeur, qui ne porte pas d'identité et a toujours
    /// été posé tel quel sur ce PC.</param>
    public static CpuGroupPlan Plan(ProfileGroupCpuPart part, CpuTargetState state, bool isManual, bool checkIdentity = true)
    {
        switch (part.ParsedKind)
        {
            case ProfilePartKind.Unknown:
                return CpuGroupPlan.Nothing(ProfilePartKind.Unknown,
                    ReportItem.Ignored(DimensionReport.Title(ProfileDimension.Cpu), "partie écrite par une version plus récente de PCPerfSuite, ignorée"));
            case ProfilePartKind.Empty:
                return CpuGroupPlan.Nothing(ProfilePartKind.Empty,
                    ReportItem.Ignored(DimensionReport.Title(ProfileDimension.Cpu), "partie vide, rien à poser"));
            case ProfilePartKind.Origin:
                return PlanOrigin(state);
        }

        CpuProfile values = part.Values!;
        var items = new List<ReportItem>();
        var writes = new List<CpuPlanWrite>();
        var touched = new List<CpuPowerSetting>();
        int unknown = 0;
        int inPlace = 0;

        foreach ((string id, CpuProfilePowerValue? value) in values.PowerSettings ?? new Dictionary<string, CpuProfilePowerValue>())
        {
            if (value is null || (value.Ac is null && value.Battery is null)) continue;

            CpuPowerSettingReading? reading = state.Settings.FirstOrDefault(r => r.Setting.Id == id);
            if (reading is null)
            {
                unknown++;
                continue;
            }

            CpuPowerSetting setting = reading.Setting;
            uint? ac = value.Ac is { } rawAc ? setting.Sanitize(rawAc) : reading.Ac;
            uint? dc = state.HasBattery
                ? value.Battery is { } rawDc ? setting.Sanitize(rawDc) : reading.Dc
                : ac;

            if (ac is null || dc is null)
            {
                uint refused = (ac is null ? value.Ac : value.Battery) ?? 0;
                items.Add(ReportItem.Ignored(setting.Label, $"« {setting.Label} » : valeur {refused} absente de ce PC, ignorée"));
                continue;
            }

            touched.Add(setting);
            if (ac == reading.Ac && dc == reading.Dc)
            {
                inPlace++;
                continue;
            }

            writes.Add(new CpuPlanWrite(setting, ac.Value, dc.Value));
        }

        if (inPlace > 0)
        {
            items.Add(ReportItem.Applied(PlanLabel,
                inPlace > 1 ? $"{inPlace} réglages du plan déjà en place" : "1 réglage du plan déjà en place"));
        }

        if (unknown > 0)
        {
            items.Add(ReportItem.Ignored(PlanLabel, unknown > 1
                ? $"{unknown} réglages du plan absents de ce PC, ignorés"
                : "1 réglage du plan absent de ce PC, ignoré"));
        }

        CpuWattsTarget? watts = PlanWatts(values, checkIdentity ? part.CapturedOn : state.Identity, state, isManual, items,
            out PowerTrend trend, out bool raises, out bool touchesWatts, checkIdentity);
        return new CpuGroupPlan(ProfilePartKind.Values, writes, watts, false, false, items, [], trend, raises, touched, touchesWatts);
    }

    private static CpuWattsTarget? PlanWatts(
        CpuProfile values, CpuIdentity? capturedOn, CpuTargetState state, bool isManual, List<ReportItem> items,
        out PowerTrend trend, out bool raises, out bool touches, bool checkIdentity)
    {
        trend = PowerTrend.Same;
        raises = false;
        touches = false;
        if (values.SustainedWatts is not { } sustained) return null;

        if (state.Watts is not { } now)
        {
            items.Add(ReportItem.Ignored(WattsLabel, $"limites en watts ignorées : {state.WattsUnavailableReason ?? "illisibles sur ce PC"}"));
            return null;
        }

        if (!state.WattsWritable)
        {
            items.Add(ReportItem.Ignored(WattsLabel, $"limites en watts ignorées : {state.WattsUnavailableReason ?? "ce PC ne permet pas de les modifier"}"));
            return null;
        }

        if (!state.RiskAccepted)
        {
            items.Add(ReportItem.Ignored(WattsLabel, "limites en watts ignorées : l'avertissement de l'onglet Processeur n'a pas été accepté"));
            return null;
        }

        if (checkIdentity && !CpuIdentity.Matches(capturedOn, state.Identity))
        {
            // Sans nom de ce côté-ci, ce n'est pas « un autre processeur » : c'est ce PC qui ne permet pas de le vérifier.
            string why = !state.Identity.HasName
                ? "le registre de Windows ne donne pas le nom de ce processeur, impossible de vérifier que c'est celui où elles ont été relevées"
                : capturedOn is null ? "relevées sur un processeur non identifié, elles ne se transposent pas"
                : $"relevées sur un autre processeur ({capturedOn.Describe()}), elles ne se transposent pas";
            items.Add(ReportItem.Ignored(WattsLabel, $"limites en watts ignorées : {why}"));
            return null;
        }

        if (sustained >= CpuMaxWattsResolver.UnlimitedWatts || !float.IsFinite(sustained))
        {
            items.Add(ReportItem.Ignored(WattsLabel, "limites en watts ignorées : valeur « sans limite » du BIOS, qui n'est pas une puissance"));
            return null;
        }

        float target = Clamp(sustained, now.Min, now.Max);
        float? burst = null;
        if (now.HasBurst)
        {
            float requested = values.BurstWatts is { } b && float.IsFinite(b) && b < CpuMaxWattsResolver.UnlimitedWatts ? b : sustained;
            burst = Math.Max(Clamp(requested, now.Min, now.Max), target);
        }

        if (Math.Abs(target - sustained) > 0.5f)
        {
            items.Add(new ReportItem(WattsLabel, ReportItemStatus.Trimmed,
                $"limite soutenue ramenée de {Watts(sustained)} à {Watts(target)} (plage de ce PC : {Watts(now.Min)} à {Watts(now.Max)})"));
        }

        trend = ApplyDirection.Combine([
            ApplyDirection.Of(target, now.Sustained, WattsTolerance),
            ApplyDirection.Of(burst, now.Burst, WattsTolerance),
        ]);

        bool sameSustained = Math.Abs(target - now.Sustained) <= 0.5f;
        bool sameBurst = burst is null || now.Burst is not { } currentBurst || Math.Abs(burst.Value - currentBurst) <= 0.5f;
        if (sameSustained && sameBurst)
        {
            items.Add(ReportItem.Applied(WattsLabel, $"limites déjà à {Watts(now.Sustained)}"));
            trend = PowerTrend.Same;
            touches = true;
            return null;
        }

        bool wouldRaise = CpuControlService.IsRaised(target, burst, now.DefaultSustained, now.DefaultBurst);
        if (wouldRaise && state.EmergencyThisSession && !isManual)
        {
            items.Add(ReportItem.Refused(WattsLabel,
                "limites en watts non reposées : la sécurité thermique les a retirées pendant cette session"));
            trend = PowerTrend.Same;
            return null;
        }

        raises = wouldRaise;
        touches = true;
        return new CpuWattsTarget(target, burst);
    }

    private static CpuGroupPlan PlanOrigin(CpuTargetState state)
    {
        var items = new List<ReportItem>();
        bool restoreWatts = false;
        PowerTrend trend = PowerTrend.Same;

        if (state.Watts is { } now && state.WattsWritable)
        {
            restoreWatts = true;
            trend = ApplyDirection.Combine([
                ApplyDirection.Of(now.DefaultSustained, now.Sustained, WattsTolerance),
                ApplyDirection.Of(now.DefaultBurst, now.Burst, WattsTolerance),
            ]);
        }
        else
        {
            items.Add(ReportItem.Ignored(WattsLabel,
                $"limites d'origine non reposées : {state.WattsUnavailableReason ?? "ce PC ne permet pas de les modifier"}"));
        }

        return new CpuGroupPlan(ProfilePartKind.Origin, [], null, RestorePlanOrigin: true, restoreWatts, items, [], trend, false,
            TouchesWatts: restoreWatts);
    }

    private static float Clamp(float value, float min, float max) => max < min ? value : Math.Clamp(value, min, max);

    public static string Watts(float value) => $"{value.ToString("0", CultureInfo.CurrentCulture)} W";
}
