namespace PCPerfSuite.Core.Safety;

/// <summary>Un seuil surveillé : la température d'un capteur ne doit pas rester à <paramref name="ThresholdC"/> ou
/// plus pendant <paramref name="Delay"/>. <paramref name="Sensor"/> est le nom affiché (« cœur », « point chaud »).</summary>
public sealed record ThermalLimit(string Sensor, float ThresholdC, TimeSpan Delay);

public enum ThermalState
{
    /// <summary>Aucun capteur n'a jamais été lu depuis le dernier armement : rien à surveiller sur ce PC.</summary>
    NotMonitorable,

    /// <summary>Températures lues, toutes sous leur seuil (ou lecture momentanément absente).</summary>
    Ok,

    /// <summary>Un capteur est au seuil, pas encore depuis assez longtemps.</summary>
    Heating,

    /// <summary>Un capteur est resté au seuil pendant tout son délai : il faut rendre les réglages d'origine.</summary>
    Tripped,

    /// <summary>Plus aucun capteur n'est lu depuis le délai de perte, alors qu'ils l'étaient : on ne voit plus rien.</summary>
    Lost,
}

/// <summary>Verdict d'un relevé : l'état, et pour <see cref="ThermalState.Heating"/> ou <see cref="ThermalState.Tripped"/>
/// le capteur en cause et sa température.</summary>
public sealed record ThermalVerdict(ThermalState State, string? Sensor = null, float? TemperatureC = null);

/// <summary>
/// Sécurité thermique en logique pure, sans matériel ni horloge : l'appelant donne l'heure et les températures de
/// chaque relevé, la classe tranche. Partagée par le CPU (98 °C pendant 15 s) et le GPU (voir
/// <see cref="GpuThermalSafety"/>) ; les fonctions à venir (#10, #11, #15, #16) la reprennent avec leurs propres seuils.
///
/// Une pointe brève n'est pas un problème : seul un capteur resté au seuil pendant tout son délai déclenche. Un capteur
/// non lu (null) ne compte pas et remet son délai à zéro. Avec un délai de perte, ne plus rien lire du tout pendant ce
/// délai, après avoir lu, donne <see cref="ThermalState.Lost"/> : la surveillance est aveugle.
/// </summary>
public sealed class ThermalGuard
{
    private readonly ThermalLimit[] _limits;
    private readonly DateTimeOffset?[] _hotSince;
    private readonly TimeSpan? _lossDelay;
    private DateTimeOffset? _lastReadAt;

    /// <param name="limits">Un seuil par capteur, dans l'ordre des températures données à <see cref="Note"/>.</param>
    /// <param name="lossDelay">Délai au-delà duquel l'absence de toute lecture est une perte ; null pour ne jamais la
    /// signaler (comportement historique du CPU).</param>
    public ThermalGuard(IReadOnlyList<ThermalLimit> limits, TimeSpan? lossDelay = null)
    {
        _limits = limits.ToArray();
        _hotSince = new DateTimeOffset?[_limits.Length];
        _lossDelay = lossDelay;
    }

    public IReadOnlyList<ThermalLimit> Limits => _limits;

    /// <summary>Un relevé : <paramref name="temperaturesC"/> dans l'ordre des seuils, null pour un capteur non lu. Après
    /// <see cref="ThermalState.Tripped"/> ou <see cref="ThermalState.Lost"/>, tout repart de zéro.</summary>
    public ThermalVerdict Note(DateTimeOffset now, IReadOnlyList<float?> temperaturesC)
    {
        bool anyRead = false;
        ThermalVerdict? heating = null;

        for (int i = 0; i < _limits.Length; i++)
        {
            float? reading = i < temperaturesC.Count ? temperaturesC[i] : null;
            if (reading is not { } temp || float.IsNaN(temp))
            {
                _hotSince[i] = null;
                continue;
            }

            anyRead = true;
            ThermalLimit limit = _limits[i];
            if (temp < limit.ThresholdC)
            {
                _hotSince[i] = null;
                continue;
            }

            _hotSince[i] ??= now;
            if (now - _hotSince[i] >= limit.Delay)
            {
                Reset();
                return new ThermalVerdict(ThermalState.Tripped, limit.Sensor, temp);
            }

            heating ??= new ThermalVerdict(ThermalState.Heating, limit.Sensor, temp);
        }

        if (anyRead)
        {
            _lastReadAt = now;
            return heating ?? new ThermalVerdict(ThermalState.Ok);
        }

        if (_lastReadAt is not { } lastRead) return new ThermalVerdict(ThermalState.NotMonitorable);

        if (_lossDelay is { } lossDelay && now - lastRead >= lossDelay)
        {
            Reset();
            return new ThermalVerdict(ThermalState.Lost);
        }

        return new ThermalVerdict(ThermalState.Ok);
    }

    /// <summary>Quand aucun relevé n'arrive plus du tout (lecture qui lève ou bloque) : vérifie seulement la perte, sans
    /// toucher aux délais de chaleur en cours, qu'un relevé vide remettrait à zéro.</summary>
    public ThermalVerdict CheckLoss(DateTimeOffset now)
    {
        if (_lastReadAt is not { } lastRead) return new ThermalVerdict(ThermalState.NotMonitorable);

        if (_lossDelay is { } lossDelay && now - lastRead >= lossDelay)
        {
            Reset();
            return new ThermalVerdict(ThermalState.Lost);
        }

        return new ThermalVerdict(ThermalState.Ok);
    }

    /// <summary>Oublie les délais en cours et les lectures passées (réarmement, après un déclenchement).</summary>
    public void Reset()
    {
        Array.Clear(_hotSince);
        _lastReadAt = null;
    }
}
