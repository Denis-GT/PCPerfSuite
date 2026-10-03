namespace PCPerfSuite.Core.Benchmark.Session;

/// <summary>État de l'attente de retour au repos.</summary>
public sealed record IdleReturnState(bool Done, string Reason);

/// <summary>
/// Retour au repos entre deux tests, en logique pure : on attend que le processeur revienne à <see cref="ToleranceC"/>
/// de sa température de départ, <see cref="MaxWait"/> au plus ; sans température lisible, une pause fixe
/// (<see cref="PauseWithoutTemperature"/>). Ainsi le second test ne paie pas la chaleur du premier. Durées expérimentales.
/// </summary>
public sealed class IdleReturn
{
    public const float ToleranceC = 3;
    public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan PauseWithoutTemperature = TimeSpan.FromSeconds(10);

    private readonly float? _baselineC;
    private readonly DateTimeOffset _startedAt;

    public IdleReturn(float? baselineC, DateTimeOffset startedAt)
    {
        _baselineC = baselineC;
        _startedAt = startedAt;
    }

    public float? BaselineC => _baselineC;

    public float? TargetC => _baselineC is { } b ? b + ToleranceC : null;

    public IdleReturnState Check(DateTimeOffset now, float? currentC)
    {
        TimeSpan waited = now - _startedAt;
        if (_baselineC is not { } baseline)
        {
            return waited >= PauseWithoutTemperature
                ? new IdleReturnState(true, $"pause de {PauseWithoutTemperature.TotalSeconds:0} s (température non lue)")
                : new IdleReturnState(false, $"pause de {PauseWithoutTemperature.TotalSeconds:0} s, température non lue");
        }

        if (currentC is { } current && current <= baseline + ToleranceC)
        {
            return new IdleReturnState(true, $"retour à {current:0} °C (base {baseline:0} °C) en {waited.TotalSeconds:0} s");
        }
        if (waited >= MaxWait)
        {
            return new IdleReturnState(true, currentC is { } c
                ? $"{MaxWait.TotalSeconds:0} s écoulées, encore {c:0} °C (base {baseline:0} °C)"
                : $"{MaxWait.TotalSeconds:0} s écoulées, température plus lue");
        }
        return new IdleReturnState(false, currentC is { } t ? $"{t:0} °C, retour vers {baseline + ToleranceC:0} °C" : "en attente d'une température");
    }
}
