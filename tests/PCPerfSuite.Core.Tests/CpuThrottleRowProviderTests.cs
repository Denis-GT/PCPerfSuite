using PCPerfSuite.Core.Compatibility;
using PCPerfSuite.Core.Hardware.Cpu.Throttle;
using PCPerfSuite.Core.Safety.Events;

namespace PCPerfSuite.Core.Tests;

/// <summary>Ligne « Raisons de bridage CPU » et textes partagés avec la carte de Monitoring.</summary>
public class CpuThrottleRowProviderTests
{
    private static readonly SystemEventReadResult NoFirmwareEvent = new([], null);

    [Fact]
    public void BeforeTheFirstReading_SaysNotYetRead()
    {
        Assert.Equal("Pas encore lu", CpuThrottleRowProvider.BuildRow(null, null).Status);
    }

    [Fact]
    public void IntelNotThrottled_WithTjMaxAndFrequencies()
    {
        var reading = new CpuThrottleReading
        {
            Source = CpuThrottleSource.IntelMsr,
            Thermal = false, Prochot = false, PowerLimit = false,
            TjMaxC = 100, BaseMhz = 1600, MaxTurboMhz = 4800,
            PerformancePercent = 165, EffectiveMhz = 2640, PerformanceLimitPercent = 100,
            FineReasonsUnavailable = new Unavailable(UnavailableCause.HardwareOrDriver,
                "raisons fines (MSR 0x64F) non autorisées par le module PawnIO IntelMSR 0.2.11"),
        };

        CompatibilityRow row = CpuThrottleRowProvider.BuildRow(reading, NoFirmwareEvent);

        Assert.True(row.IsSupported);
        Assert.StartsWith("Non bridé", row.Status);
        Assert.Contains("TjMax 100 °C", row.Detail);
        Assert.Contains("Fréquence effective 2640 MHz", row.Detail);
        Assert.Contains("0x64F", row.Detail);
        Assert.Contains("Aucune limitation par le micrologiciel", row.Detail);
    }

    [Fact]
    public void Throttled_ListsTheActiveReasons()
    {
        var reading = new CpuThrottleReading { Source = CpuThrottleSource.IntelMsr, Thermal = true, PowerLimit = true, Prochot = false };

        Assert.Equal("Bridé : thermique, puissance (registres MSR (Intel, PawnIO))", CpuThrottleRowProvider.BuildRow(reading, null).Status);
    }

    [Fact]
    public void WithoutPawnIo_FallsBackOnWindowsCounters_AndSaysWhy()
    {
        var reading = new CpuThrottleReading
        {
            Source = CpuThrottleSource.WindowsCounters,
            PerformanceLimitPercent = 72,
            Unavailable = new Unavailable(UnavailableCause.MissingRights, "registres MSR illisibles (pilote PawnIO absent)"),
        };

        CompatibilityRow row = CpuThrottleRowProvider.BuildRow(reading, NoFirmwareEvent);

        Assert.False(row.IsSupported);
        Assert.Equal("N/D, compteurs Windows seulement", row.Status);
        Assert.Contains("PawnIO absent", row.Detail);
        Assert.Contains("Windows garantit 72 % de la fréquence nominale", row.Detail);
    }

    [Fact]
    public void Amd_ShowsValuesLimitsAndTheTableVersion()
    {
        var reading = new CpuThrottleReading
        {
            Source = CpuThrottleSource.AmdPmTable,
            PowerLimit = true, Thermal = false, CurrentLimit = false,
            Amd = new AmdPowerLimits(0x380805, 141, 142, 60, 95, 90, 140, 70, 90),
        };

        CompatibilityRow row = CpuThrottleRowProvider.BuildRow(reading, NoFirmwareEvent);

        Assert.Contains("version 0x380805", row.Status);
        Assert.Contains("PPT 141/142 W", row.Detail);
    }

    [Fact]
    public void FirmwareLimitations_AreCounted()
    {
        var events = new SystemEventReadResult(
            [new SystemEventRecord(SystemEventKind.FirmwareLimited, 37, DateTimeOffset.UtcNow)], null);

        CompatibilityRow row = CpuThrottleRowProvider.BuildRow(new CpuThrottleReading { Source = CpuThrottleSource.WindowsCounters }, events);

        Assert.Contains("Kernel-Processor-Power 37) : 1 fois", row.Detail);
    }

    [Fact]
    public void Reasons_NullWhenNothingIsReadable_NoneWhenAllAreClear()
    {
        Assert.Null(CpuThrottleText.Reasons(new CpuThrottleReading { Source = CpuThrottleSource.WindowsCounters }));
        Assert.Equal("aucun", CpuThrottleText.Reasons(new CpuThrottleReading { Source = CpuThrottleSource.IntelMsr, Thermal = false }));
    }

    [Fact]
    public void CoreSweep_IsSummarized()
    {
        var reading = new CpuThrottleReading
        {
            Source = CpuThrottleSource.IntelMsr,
            Thermal = true,
            Cores = new CoreThrottleSweep(20, 4, 0, 2, null, null, 4100, 4800),
        };

        CompatibilityRow row = CpuThrottleRowProvider.BuildRow(reading, null);

        Assert.Contains("Balayage de 20 processeurs logiques : thermique 4", row.Detail);
        Assert.Contains("courant N/D", row.Detail);
    }
}
