using System.Globalization;
using PCPerfSuite.Core.Hardware;
using PCPerfSuite.Core.Hardware.Gpu;
using PCPerfSuite.Core.Safety;

namespace PCPerfSuite.Core.Profiles;

/// <summary>
/// Ce que la partie carte graphique d'un groupe demande à l'onglet GPU, déjà décidé : la demande à poser (seulement les
/// réglages qui changent, bornés aux plages de la carte), ou le retour à l'origine, et ce qui a été écarté avec sa
/// raison. <see cref="Raises"/> : un réglage au-dessus de l'origine, donc une période probatoire.
/// </summary>
public sealed record GpuGroupPlan(
    ProfilePartKind Kind,
    GpuOverclockRequest? Request,
    bool RestoreOrigin,
    IReadOnlyList<ReportItem> Items,
    IReadOnlyList<string> Notes,
    PowerTrend Trend,
    bool Raises,
    GpuTouched? Touched = null)
{
    public bool HasWork => Request is not null || RestoreOrigin;

    /// <summary>Le groupe règle la carte : il pose quelque chose, ou ses valeurs sont déjà en place.</summary>
    public bool TouchesCard => HasWork || (Touched is { } touched && touched != GpuTouched.None);

    /// <summary>Les réglages relus, réduits à ce que le groupe règle : l'état « retenu » à comparer ensuite.</summary>
    public GpuRetainedValues Retain(GpuRetainedValues readBack)
    {
        GpuTouched touched = Touched ?? GpuTouched.None;
        return new GpuRetainedValues
        {
            CoreOffsetMhz = touched.Core ? readBack.CoreOffsetMhz : null,
            MemoryOffsetMhz = touched.Memory ? readBack.MemoryOffsetMhz : null,
            PowerLimitPercent = touched.Power ? readBack.PowerLimitPercent : null,
            TemperatureLimitC = touched.Temperature ? readBack.TemperatureLimitC : null,
            Voltage = touched.Voltage ? readBack.Voltage : null,
        };
    }

    public static GpuGroupPlan Nothing(ProfilePartKind kind, params ReportItem[] items)
        => new(kind, null, false, items, [], PowerTrend.Same, false);

    /// <summary>Le même plan sans sa demande (ligne du journal impossible à écrire, par exemple).</summary>
    public GpuGroupPlan WithoutRequest(ReportItem reason)
        => this with { Request = null, Raises = false, Touched = GpuTouched.None, Items = [.. Items, reason], Trend = PowerTrend.Same };
}

/// <summary>Les réglages de la carte qu'un groupe règle (posés ou déjà en place).</summary>
public sealed record GpuTouched(bool Core, bool Memory, bool Power, bool Temperature, bool Voltage)
{
    public static GpuTouched None { get; } = new(false, false, false, false, false);

    public static GpuTouched All { get; } = new(true, true, true, true, true);

    public GpuTouched Union(GpuTouched other)
        => new(Core || other.Core, Memory || other.Memory, Power || other.Power, Temperature || other.Temperature, Voltage || other.Voltage);

    /// <summary>
    /// Ce que l'onglet GPU enregistre comme état de démarrage : tout ; rien sans carte pilotable (les valeurs restent
    /// celles de la carte où elles ont été faites) ; après un groupe appliqué sans en faire l'état de démarrage (D7),
    /// seulement ce que l'utilisateur a touché à la main depuis, et non les valeurs transitoires du groupe.
    /// </summary>
    public static GpuTouched ToSave(bool cardAvailable, bool transient, GpuTouched touchedSinceTransient)
        => !cardAvailable ? None : transient ? touchedSinceTransient : All;
}

/// <summary>
/// Décide, en logique pure, ce que la partie carte graphique d'un groupe peut poser sur CETTE carte : rien sans carte
/// pilotable, sans la renonciation Intel, ni sur une autre carte, même de la même marque
/// (<see cref="GpuIdentity.IsSameCard"/>) ; la tension seulement dans la même unité (50 % ≠ 50 mV) ; chaque valeur
/// bornée à la plage de la carte, et le bornage dit ; jamais une hausse demandée par un pilote automatique après une
/// sécurité thermique dans la session.
/// </summary>
public static class GpuGroupPlanner
{
    private const double OffsetTolerance = 0.5;
    private const double PowerTolerance = 0.5;

    /// <param name="checkIdentity">Faux pour un profil de l'onglet GPU, qui ne porte pas d'identité et a toujours été posé tel
    /// quel sur cette carte.</param>
    public static GpuGroupPlan Plan(ProfileGroupGpuPart part, GpuTargetState state, bool isManual, bool checkIdentity = true)
    {
        string title = DimensionReport.Title(ProfileDimension.Gpu);
        switch (part.ParsedKind)
        {
            case ProfilePartKind.Unknown:
                return GpuGroupPlan.Nothing(ProfilePartKind.Unknown,
                    ReportItem.Ignored(title, "partie écrite par une version plus récente de PCPerfSuite, ignorée"));
            case ProfilePartKind.Empty:
                return GpuGroupPlan.Nothing(ProfilePartKind.Empty, ReportItem.Ignored(title, "partie vide, rien à poser"));
        }

        if (!state.IsAvailable)
        {
            return GpuGroupPlan.Nothing(part.ParsedKind,
                ReportItem.Ignored(title, $"aucune carte pilotable : {state.UnavailableReason ?? "pilote muet"}"));
        }

        if (part.ParsedKind == ProfilePartKind.Origin) return PlanOrigin(state);

        if (!state.CanOverclock)
        {
            return GpuGroupPlan.Nothing(ProfilePartKind.Values,
                ReportItem.Ignored(title, "réglages ignorés : la renonciation de garantie Intel n'a pas été acceptée dans l'onglet GPU"));
        }

        if (checkIdentity && !GpuIdentity.IsSameCard(part.CapturedOn, state.Identity))
        {
            string where = part.CapturedOn is null ? "une carte non identifiée" : $"une autre carte ({part.CapturedOn.Describe()})";
            return GpuGroupPlan.Nothing(ProfilePartKind.Values,
                ReportItem.Ignored(title, $"réglages ignorés : relevés sur {where}, des décalages et une tension ne se transposent pas d'une carte à l'autre"));
        }

        if (state.Overclock is not { } now)
        {
            return GpuGroupPlan.Nothing(ProfilePartKind.Values, ReportItem.Ignored(title, "réglages ignorés : la carte n'a pas pu être relue"));
        }

        GpuOverclockProfile values = part.Values!;
        var items = new List<ReportItem>();
        var notes = new List<string>();
        var unsupported = new List<string>();

        int? core = null;
        if (now.CoreOffsetSupported)
        {
            core = Bounded("cœur", values.CoreClockOffsetMhz, now.CoreOffsetMinMhz, now.CoreOffsetMaxMhz, "MHz", true, items);
        }
        else if (values.CoreClockOffsetMhz != 0) unsupported.Add("fréquence du cœur");

        int? memory = null;
        if (now.MemoryOffsetSupported)
        {
            memory = Bounded("mémoire", values.MemoryClockOffsetMhz, now.MemoryOffsetMinMhz, now.MemoryOffsetMaxMhz, now.MemoryOffsetUnit, true, items);
        }
        else if (values.MemoryClockOffsetMhz != 0) unsupported.Add("fréquence mémoire");

        float? power = null;
        if (values.PowerLimitPercent is { } percent && float.IsFinite(percent))
        {
            if (state.Power is { PowerLimitSupported: true } snapshot)
            {
                power = (float)Clamp(percent, snapshot.PowerLimitMinPercent, snapshot.PowerLimitMaxPercent);
                if (Math.Abs(power.Value - percent) > PowerTolerance)
                {
                    items.Add(new ReportItem("puissance", ReportItemStatus.Trimmed,
                        $"puissance ramenée de {Number(percent)} % à {Number(power.Value)} % (plage de la carte)"));
                }
            }
            else unsupported.Add("limite de puissance");
        }

        int? temperature = null;
        if (values.TemperatureLimitC is { } celsius)
        {
            if (now.TemperatureLimitSupported)
            {
                temperature = Bounded("limite de température", celsius, now.TemperatureLimitMinC, now.TemperatureLimitMaxC, "°C", false, items);
            }
            else unsupported.Add("limite de température");
        }

        int? voltage = null;
        if (values.GetVoltage() is { } wanted)
        {
            if (!now.VoltageSupported)
            {
                if (wanted.Value != 0) unsupported.Add("tension");
            }
            else if (wanted.Unit != now.VoltageUnit)
            {
                items.Add(ReportItem.Ignored("tension",
                    $"tension ignorée : enregistrée en {UnitLabel(wanted.Unit)}, cette carte la règle en {UnitLabel(now.VoltageUnit)}"));
            }
            else
            {
                voltage = Bounded("tension", wanted.Value, now.VoltageMin, now.VoltageMax, UnitLabel(now.VoltageUnit), now.VoltageIsOffset, items);
            }
        }

        if (unsupported.Count > 0) notes.Add($"N/D sur cette carte, non posé : {string.Join(", ", unsupported)}");

        var touched = new GpuTouched(core is not null, memory is not null, power is not null, temperature is not null, voltage is not null);

        // Ce qui est déjà en place n'est pas réécrit : la bascule automatique repasse souvent par les mêmes valeurs.
        var inPlace = new List<string>();
        if (core == now.CoreOffsetMhz && now.CoreOffsetSupported) { core = null; inPlace.Add("cœur"); }
        if (memory == now.MemoryOffsetMhz && now.MemoryOffsetSupported) { memory = null; inPlace.Add("mémoire"); }
        if (power is { } p && state.Power is { } current && Math.Abs(p - current.PowerLimitPercent) <= PowerTolerance) { power = null; inPlace.Add("puissance"); }
        if (temperature == now.TemperatureLimitC && now.TemperatureLimitSupported) { temperature = null; inPlace.Add("limite de température"); }
        if (voltage == now.Voltage && now.VoltageSupported) { voltage = null; inPlace.Add("tension"); }
        if (inPlace.Count > 0) items.Add(ReportItem.Applied("en place", $"déjà en place : {string.Join(", ", inPlace)}"));

        var request = new GpuOverclockRequest
        {
            CoreOffsetMhz = core,
            MemoryOffsetMhz = memory,
            PowerLimitPercent = power,
            TemperatureLimitC = temperature,
            Voltage = voltage,
            VoltageUnit = now.VoltageUnit,
        };

        bool hasRequest = core is not null || memory is not null || power is not null || temperature is not null || voltage is not null;
        if (!hasRequest) return new GpuGroupPlan(ProfilePartKind.Values, null, false, items, notes, PowerTrend.Same, false, touched);

        PowerTrend trend = ApplyDirection.Combine([
            ApplyDirection.Of(power, state.Power?.PowerLimitPercent, PowerTolerance),
            ApplyDirection.Of(core, now.CoreOffsetMhz, OffsetTolerance),
            ApplyDirection.Of(memory, now.MemoryOffsetMhz, OffsetTolerance),
            ApplyDirection.Of(temperature, now.TemperatureLimitC, OffsetTolerance),
            ApplyDirection.Of(voltage, now.Voltage, OffsetTolerance),
        ]);

        bool raises = GpuOverclockRaise.IsRaising(request, now, state.Power);
        if (raises && state.EmergencyThisSession && !isManual)
        {
            items.Add(ReportItem.Refused(DimensionReport.Title(ProfileDimension.Gpu),
                "overclock non reposé : la sécurité thermique l'a retiré pendant cette session"));
            return new GpuGroupPlan(ProfilePartKind.Values, null, false, items, notes, PowerTrend.Same, false);
        }

        return new GpuGroupPlan(ProfilePartKind.Values, request, false, items, notes, trend, raises, touched);
    }

    private static GpuGroupPlan PlanOrigin(GpuTargetState state)
    {
        GpuOverclockSnapshot? now = state.Overclock;
        PowerTrend trend = ApplyDirection.Combine([
            state.Power is { PowerLimitSupported: true } power
                ? ApplyDirection.Of(power.PowerLimitDefaultPercent, power.PowerLimitPercent, PowerTolerance)
                : PowerTrend.Same,
            now is { CoreOffsetSupported: true } ? ApplyDirection.Of(0, now.CoreOffsetMhz, OffsetTolerance) : PowerTrend.Same,
            now is { MemoryOffsetSupported: true } ? ApplyDirection.Of(0, now.MemoryOffsetMhz, OffsetTolerance) : PowerTrend.Same,
            now is { VoltageSupported: true } ? ApplyDirection.Of(now.VoltageDefault, now.Voltage, OffsetTolerance) : PowerTrend.Same,
        ]);

        return new GpuGroupPlan(ProfilePartKind.Origin, null, RestoreOrigin: true, [], [], trend, false, GpuTouched.All);
    }

    /// <summary>Borne une valeur à la plage de la carte, et dit quand elle a été ramenée.</summary>
    private static int Bounded(string label, int requested, int min, int max, string unit, bool signed, List<ReportItem> items)
    {
        int bounded = (int)Clamp(requested, min, max);
        if (bounded != requested)
        {
            items.Add(new ReportItem(label, ReportItemStatus.Trimmed,
                $"{label} ramené de {Format(requested, unit, signed)} à {Format(bounded, unit, signed)} (plage de la carte)"));
        }

        return bounded;
    }

    /// <summary>Math.Clamp lève si la plage est inversée ; une plage absurde lue sur un pilote ne doit rien casser.</summary>
    private static double Clamp(double value, double min, double max) => max < min ? value : Math.Clamp(value, min, max);

    private static string Format(int value, string unit, bool signed)
    {
        string number = value.ToString(CultureInfo.CurrentCulture);
        return signed && value >= 0 ? $"+{number} {unit}" : $"{number} {unit}";
    }

    private static string Number(float value) => value.ToString("0", CultureInfo.CurrentCulture);

    private static string UnitLabel(GpuVoltageUnit unit) => unit == GpuVoltageUnit.Percent ? "%" : "mV";
}
