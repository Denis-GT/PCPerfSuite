using PCPerfSuite.Core.Hardware;
using PCPerfSuite.Core.Hardware.Cpu;
using PCPerfSuite.Core.Hardware.Fans;
using PCPerfSuite.Core.Hardware.Gpu;

namespace PCPerfSuite.Core.Profiles;

/// <summary>Qui applique, et comment. <paramref name="MakeStartupState"/> à faux : les valeurs ne deviennent pas l'état
/// de démarrage des onglets, et sont rendues d'origine à la fermeture (D7), pour la bascule automatique (#9).
/// <paramref name="Lease"/> : la poignée du bail quand le demandeur le tient.</summary>
public sealed record ProfileGroupApplyContext(string RequesterId, bool IsManual, bool MakeStartupState, TuningLeaseHandle? Lease);

/// <summary>Ce que chaque onglet expose aux groupes de profils : il reste seul propriétaire de sa partie de
/// settings.json, de ses sécurités et de ses relectures. Tout s'appelle sur le fil d'interface.</summary>
public interface IProfileGroupTarget
{
    ProfileDimension Dimension { get; }

    /// <summary>Exécute tout de suite les écritures manuelles encore en attente (délai des curseurs) : sans cela, un
    /// curseur lâché juste avant écraserait le groupe, ou serait perdu sous le bail.</summary>
    void FlushPendingManualWrites();

    /// <summary>Vrai tant qu'un réglage de cette dimension est relevé au-dessus de son origine par l'app (watts, OC GPU)
    /// : la sécurité thermique le surveille, et la prudence au démarrage aussi.</summary>
    bool IsRaised { get; }
}

// ---- Processeur ----

/// <summary>Un réglage du plan d'alimentation, relu dans Windows.</summary>
public sealed record CpuPowerSettingReading(CpuPowerSetting Setting, uint Ac, uint Dc);

/// <summary>Les limites en watts relues, avec leur origine (firmware) et leurs bornes.</summary>
public sealed record CpuWattsReading(
    float Sustained, float? Burst, float DefaultSustained, float? DefaultBurst, float Min, float Max)
{
    public bool HasBurst => Burst is not null;
}

/// <summary>L'état du processeur, relu au moment de planifier.</summary>
/// <param name="Watts">Null : limites illisibles sur ce PC.</param>
/// <param name="WattsWritable">Le backend sait les écrire (sinon <paramref name="WattsUnavailableReason"/>).</param>
/// <param name="EmergencyThisSession">La sécurité thermique CPU a rendu les limites d'origine pendant cette session.</param>
public sealed record CpuTargetState(
    CpuIdentity Identity,
    bool HasBattery,
    IReadOnlyList<CpuPowerSettingReading> Settings,
    CpuWattsReading? Watts,
    bool WattsWritable,
    string? WattsUnavailableReason,
    bool RiskAccepted,
    bool EmergencyThisSession)
{
    /// <summary>L'état actuel, comme l'enregistrerait l'onglet : tous les réglages du plan, et les watts quand ils sont
    /// modifiables et déverrouillés (jamais la valeur « sans limite » du BIOS).</summary>
    public CpuProfile ToProfile(string name)
    {
        var profile = new CpuProfile { Name = name };
        foreach (CpuPowerSettingReading reading in Settings)
        {
            profile.PowerSettings[reading.Setting.Id] = new CpuProfilePowerValue
            {
                Ac = reading.Ac,
                Battery = HasBattery ? reading.Dc : null,
            };
        }

        if (Watts is { } watts && WattsWritable && RiskAccepted && watts.Sustained < CpuMaxWattsResolver.UnlimitedWatts)
        {
            profile.SustainedWatts = watts.Sustained;
            profile.BurstWatts = watts.Burst is { } burst && burst < CpuMaxWattsResolver.UnlimitedWatts ? burst : null;
        }

        return profile;
    }
}

/// <summary>Ce que l'onglet Processeur a fait d'un plan, et ce que le matériel en a retenu (seulement ce qui a été
/// touché).</summary>
public sealed record CpuApplyOutcome(DimensionReport Report, CpuProfile Retained);

public interface ICpuGroupTarget : IProfileGroupTarget
{
    CpuTargetState ReadState();

    CpuApplyOutcome Apply(CpuGroupPlan plan, ProfileGroupApplyContext context);

    /// <summary>Résumé dans les termes de ce PC : libellés et unités des réglages qu'il expose.</summary>
    string Describe(CpuProfile profile);
}

// ---- Carte graphique ----

/// <summary>L'état de la carte, relu au moment de planifier. <paramref name="UnavailableReason"/> : pourquoi rien ne se
/// règle (sans administrateur, pilote muet…), dans les termes de ce PC.</summary>
/// <param name="DriverResetThisSession">Le pilote graphique a été relancé (TDR) pendant une période d'essai de cette
/// session : un overclock instable, qu'aucun pilote automatique ne doit reposer avant la relance de l'app.</param>
public sealed record GpuTargetState(
    bool IsAvailable,
    string? UnavailableReason,
    bool CanOverclock,
    GpuIdentity? Identity,
    GpuOverclockSnapshot? Overclock,
    GpuControlSnapshot? Power,
    bool EmergencyThisSession,
    bool DriverResetThisSession = false)
{
    /// <summary>L'état actuel, comme l'enregistrerait l'onglet GPU.</summary>
    public GpuOverclockProfile ToProfile(string name) => new()
    {
        Name = name,
        CoreClockOffsetMhz = Overclock is { CoreOffsetSupported: true } core ? core.CoreOffsetMhz : 0,
        MemoryClockOffsetMhz = Overclock is { MemoryOffsetSupported: true } memory ? memory.MemoryOffsetMhz : 0,
        PowerLimitPercent = Power is { PowerLimitSupported: true } power ? power.PowerLimitPercent : null,
        TemperatureLimitC = Overclock is { TemperatureLimitSupported: true } temp ? temp.TemperatureLimitC : null,
        VoltageValue = Overclock is { VoltageSupported: true } volt ? volt.Voltage : null,
        VoltageUnit = Overclock is { VoltageSupported: true } unit ? unit.VoltageUnit : null,
    };

    /// <summary>Les réglages relus que la carte expose, pour comparer à l'état retenu.</summary>
    public GpuRetainedValues ToRetained() => new()
    {
        CoreOffsetMhz = Overclock is { CoreOffsetSupported: true } core ? core.CoreOffsetMhz : null,
        MemoryOffsetMhz = Overclock is { MemoryOffsetSupported: true } memory ? memory.MemoryOffsetMhz : null,
        PowerLimitPercent = Power is { PowerLimitSupported: true } power ? power.PowerLimitPercent : null,
        TemperatureLimitC = Overclock is { TemperatureLimitSupported: true } temp ? temp.TemperatureLimitC : null,
        Voltage = Overclock is { VoltageSupported: true } volt ? volt.Voltage : null,
    };
}

public sealed record GpuApplyOutcome(DimensionReport Report, GpuRetainedValues Retained);

public interface IGpuGroupTarget : IProfileGroupTarget
{
    GpuTargetState ReadState();

    GpuApplyOutcome Apply(GpuGroupPlan plan, ProfileGroupApplyContext context);

    string Describe(GpuOverclockProfile profile);
}

// ---- Ventilation ----

/// <summary>Un ventilateur pilotable de l'onglet.</summary>
public sealed record FanTargetFan(string FanId, string Name, bool IsGpuCooler);

/// <summary>L'état de la ventilation. <paramref name="IsReady"/> : le premier relevé est passé, on sait quels
/// ventilateurs existent. <paramref name="MotherboardFansRefused"/> : portable ou châssis indéterminé, seuls les
/// ventilateurs de la carte graphique sont listés (règle 5).</summary>
public sealed record FanTargetState(
    bool IsReady,
    IReadOnlyList<FanTargetFan> Fans,
    bool MotherboardFansRefused,
    string NoFansReason,
    FanProfile Current);

public sealed record FanApplyOutcome(DimensionReport Report, FanProfile Retained);

public interface IFanGroupTarget : IProfileGroupTarget
{
    FanTargetState ReadState();

    /// <summary>Terminée au premier relevé des ventilateurs.</summary>
    Task WhenReady { get; }

    FanApplyOutcome Apply(FanGroupPlan plan, ProfileGroupApplyContext context);

    /// <summary>Pourquoi ce ventilateur d'un profil n'existe pas ici (absent, portable, sans administrateur).</summary>
    string AbsenceReason(string fanId);

    string Describe(FanProfile profile);
}
