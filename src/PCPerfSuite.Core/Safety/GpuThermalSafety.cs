using System.Globalization;
using PCPerfSuite.Core.Hardware;

namespace PCPerfSuite.Core.Safety;

/// <summary>
/// Sécurité thermique de l'overclocking GPU : si la carte reste trop chaude alors que l'app a relevé l'un de ses
/// réglages, on la rend d'origine, on relit ce qu'elle a retenu et on prévient. Elle complète le ventilateur passé à
/// 100 % dès 88 °C (onglet Ventilateurs), qui ne retire pas l'overclock.
///
/// Armée seulement quand l'app a elle-même relevé quelque chose (voir <see cref="GpuControlService.RefreshArming"/>) :
/// un overclock posé par un autre outil n'est jamais retiré. Le point chaud ne compte que s'il est lu : les RTX 50 ne
/// le publient pas, la sécurité tient alors sur le cœur seul. Ne plus lire aucune température pendant le délai, après
/// en avoir lu, déclenche aussi : l'app ne surveille plus rien alors que l'overclock tient.
///
/// Appelée sur le thread d'interface (relevé du monitoring), fenêtre cachée comprise.
/// </summary>
public sealed class GpuThermalSafety
{
    public static readonly ThermalLimit CoreLimit = new("cœur", 90f, TimeSpan.FromSeconds(15));

    public static readonly ThermalLimit HotSpotLimit = new("point chaud", 105f, TimeSpan.FromSeconds(15));

    private readonly IGpuOverclockTarget _target;
    private readonly ThermalGuard _guard;

    public GpuThermalSafety(IGpuOverclockTarget target)
    {
        _target = target;
        _guard = new ThermalGuard([CoreLimit, HotSpotLimit], lossDelay: CoreLimit.Delay);
    }

    public IReadOnlyList<ThermalLimit> Limits => _guard.Limits;

    /// <summary>Vrai tant qu'un réglage relevé par l'app tient : la température doit alors arriver à chaque relevé.</summary>
    public bool IsArmed { get; private set; }

    /// <summary>Dernier état de la surveillance ; <see cref="ThermalState.NotMonitorable"/> tant que rien n'a été lu.</summary>
    public ThermalState State { get; private set; } = ThermalState.NotMonitorable;

    /// <summary>Dernières températures reçues (null : non lue à ce relevé).</summary>
    public float? LastCoreTempC { get; private set; }

    public float? LastHotSpotTempC { get; private set; }

    /// <summary>Vrai dès qu'une température a été reçue pendant cette session : sert à dire « ce GPU ne la publie pas ».</summary>
    public bool CoreEverRead { get; private set; }

    public bool HotSpotEverRead { get; private set; }

    /// <summary>Message du dernier déclenchement et son heure, null tant qu'il n'y en a pas eu.</summary>
    public string? LastTripMessage { get; private set; }

    public DateTimeOffset? LastTripAt { get; private set; }

    /// <summary>Levé après un retour d'origine d'office, avec le message à afficher.</summary>
    public event Action<string>? EmergencyRestored;

    /// <summary>Arme ou désarme ; chaque changement repart de zéro, pour qu'une chaleur d'avant ne compte pas.</summary>
    public void UpdateArming(bool raised)
    {
        if (raised == IsArmed) return;

        IsArmed = raised;
        _guard.Reset();
        State = ThermalState.NotMonitorable;
    }

    /// <summary>À appeler à chaque relevé, avec la température du cœur et celle du point chaud (null si non lues, ou
    /// si le relevé n'a pas de GPU).</summary>
    public void Note(DateTimeOffset now, float? coreTempC, float? hotSpotTempC)
    {
        LastCoreTempC = coreTempC;
        LastHotSpotTempC = hotSpotTempC;
        CoreEverRead |= coreTempC is not null;
        HotSpotEverRead |= hotSpotTempC is not null;

        if (!IsArmed) return;

        ThermalVerdict verdict = _guard.Note(now, [coreTempC, hotSpotTempC]);
        State = verdict.State;
        if (verdict.State is ThermalState.Tripped or ThermalState.Lost) Trip(now, verdict);
    }

    private void Trip(DateTimeOffset now, ThermalVerdict verdict)
    {
        IsArmed = false;
        _guard.Reset();

        try { _target.RestoreOverclockDefaults(); }
        catch { /* best-effort : la relecture ci-dessous dira ce qu'il en est */ }

        GpuOverclockSnapshot? overclock = null;
        GpuControlSnapshot? power = null;
        try
        {
            overclock = _target.GetOverclock();
            power = _target.GetSnapshot();
        }
        catch
        {
            // Relecture impossible : le message le dit.
        }

        string message = DescribeTrip(verdict, overclock, power);
        LastTripMessage = message;
        LastTripAt = now;
        EmergencyRestored?.Invoke(message);
    }

    /// <summary>Le message d'un déclenchement : la cause, puis ce que la carte a relu après le retour d'origine.</summary>
    public static string DescribeTrip(ThermalVerdict verdict, GpuOverclockSnapshot? overclock, GpuControlSnapshot? power)
    {
        string cause = verdict.State == ThermalState.Lost
            ? $"la température du GPU n'est plus lue depuis {CoreLimit.Delay.TotalSeconds:0} s alors que l'overclock tenait"
            : $"le GPU est resté à {verdict.TemperatureC?.ToString("0", CultureInfo.CurrentCulture) ?? "?"} °C ({verdict.Sensor}) " +
              $"pendant {CoreLimit.Delay.TotalSeconds:0} s";

        if (overclock is null && power is null)
            return $"Sécurité : {cause}. Retour aux réglages d'origine demandé, mais la carte n'a pas pu être relue pour le vérifier.";

        string readBack = GpuOverclockRaise.DescribeReadBack(overclock, power);
        return GpuOverclockRaise.IsRaised(overclock, power)
            ? $"Sécurité : {cause}, mais le retour aux réglages d'origine a échoué (relu : {readBack})."
            : $"Sécurité : {cause}, les réglages d'origine ont été rétablis (relu : {readBack}).";
    }
}
