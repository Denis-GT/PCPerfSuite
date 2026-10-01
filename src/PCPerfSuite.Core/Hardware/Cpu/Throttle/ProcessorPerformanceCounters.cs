namespace PCPerfSuite.Core.Hardware.Cpu.Throttle;

/// <summary>Ce que disent les compteurs de performances de Windows sur la fréquence et sa limitation.</summary>
public sealed record ProcessorPerformanceSample(
    double? PerformancePercent,
    double? NominalMhz,
    double? PerformanceLimitPercent,
    uint? PerformanceLimitFlags)
{
    /// <summary>La performance garantie est sous la nominale : une limite retient le processeur (indice).</summary>
    public bool? IsLimited => PerformanceLimitPercent is { } limit ? limit < 99.5 : null;

    /// <summary>Fréquence effective moyenne : fréquence nominale × performance.</summary>
    public double? EffectiveMhz => PerformancePercent is { } percent && NominalMhz is { } nominal && nominal > 0
        ? nominal * percent / 100
        : null;
}

/// <summary>
/// Repli sans pilote ni droits, pour tous les processeurs : « % Processor Performance » (fréquence effective en % de
/// la nominale, plus de 100 % en turbo), « Processor Frequency » (nominale), « % Performance Limit » (performance que
/// le processeur garantit, en % de la nominale : 100 % sans limite, moins quand la politique d'alimentation, un budget
/// de puissance ou la chaleur le retient) et « Performance Limit Flags » (drapeaux non documentés par Microsoft : un
/// simple indice, jamais une raison). Compteurs ajoutés par leur nom anglais, indépendants de la langue de Windows.
/// Vérifié sur un i5-13500T au repos : 100 % et drapeaux à 0.
/// </summary>
internal sealed class ProcessorPerformanceCounters : IDisposable
{
    private const string Instance = @"\Processor Information(_Total)\";

    private readonly PdhCounterSampler _performance = new(PdhCounterSampler.ProcessorPerformance);
    private readonly PdhCounterSampler _frequency = new(Instance + "Processor Frequency");
    private readonly PdhCounterSampler _limit = new(Instance + "% Performance Limit");
    private readonly PdhCounterSampler _flags = new(Instance + "Performance Limit Flags");

    public ProcessorPerformanceSample Sample()
    {
        double? flags = _flags.Sample();
        return new ProcessorPerformanceSample(
            PerformancePercent: Plausible(_performance.Sample(), 0, 1000),
            NominalMhz: Plausible(_frequency.Sample(), 100, 10_000),
            PerformanceLimitPercent: Plausible(_limit.Sample(), 0, 100),
            PerformanceLimitFlags: flags is { } f && f >= 0 && f <= uint.MaxValue ? (uint)f : null);
    }

    private static double? Plausible(double? value, double min, double max)
        => value is { } v && !double.IsNaN(v) && v >= min && v <= max ? v : null;

    public void Dispose()
    {
        _performance.Dispose();
        _frequency.Dispose();
        _limit.Dispose();
        _flags.Dispose();
    }
}
