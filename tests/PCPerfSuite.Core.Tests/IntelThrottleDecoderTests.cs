using PCPerfSuite.Core.Hardware.Cpu.Throttle;

namespace PCPerfSuite.Core.Tests;

/// <summary>Décodage des registres MSR de bridage d'Intel : bits d'état seulement, jamais les bits « log », et rien
/// qu'un processeur n'annonce pas.</summary>
public class IntelThrottleDecoderTests
{
    private static readonly IntelThermalFeatures All = new(true, true, true, true);

    [Fact]
    public void Features_FromCpuIdLeaf6()
    {
        IntelThermalFeatures features = IntelThermalFeatures.FromCpuId(eax: (1 << 4) | (1 << 6) | (1 << 7), ecx: 1);

        Assert.Equal(All, features);
        Assert.Equal(IntelThermalFeatures.None, IntelThermalFeatures.FromCpuId(0, 0));
    }

    [Fact]
    public void Package_StatusBits_AreDecoded()
    {
        PackageThermalStatus status = IntelThrottleDecoder.DecodePackage((1UL << 0) | (1UL << 2) | (1UL << 10), All);

        Assert.Equal(new PackageThermalStatus(true, true, true), status);
    }

    [Fact]
    public void Package_LogBitsAlone_AreNotAThrottle()
    {
        // Bits 1, 3 et 11 : « log » collants, restés à 1 depuis un bridage ancien.
        PackageThermalStatus status = IntelThrottleDecoder.DecodePackage((1UL << 1) | (1UL << 3) | (1UL << 11) | (0x3FUL << 16), All);

        Assert.Equal(new PackageThermalStatus(false, false, false), status);
    }

    [Fact]
    public void Package_WithoutPackageThermalFeature_IsUnknown()
    {
        PackageThermalStatus status = IntelThrottleDecoder.DecodePackage(ulong.MaxValue, All with { PackageThermal = false });

        Assert.Equal(new PackageThermalStatus(null, null, null), status);
    }

    [Fact]
    public void Package_WithoutPowerLimitNotification_LeavesPowerUnknown()
    {
        PackageThermalStatus status = IntelThrottleDecoder.DecodePackage(1UL << 10, All with { PowerLimitNotification = false });

        Assert.Null(status.PowerLimit);
        Assert.False(status.Thermal);
    }

    [Fact]
    public void Core_AllStatusBits()
    {
        CoreThermalStatus status = IntelThrottleDecoder.DecodeCore((1UL << 0) | (1UL << 2) | (1UL << 10) | (1UL << 12) | (1UL << 14), All);

        Assert.Equal(new CoreThermalStatus(true, true, true, true, true), status);
    }

    [Fact]
    public void Core_LogBitsAlone_AreNotAThrottle()
    {
        CoreThermalStatus status = IntelThrottleDecoder.DecodeCore((1UL << 1) | (1UL << 3) | (1UL << 11) | (1UL << 13) | (1UL << 15), All);

        Assert.Equal(new CoreThermalStatus(false, false, false, false, false), status);
    }

    [Fact]
    public void Core_WithoutCurrentLimitFeature_LeavesCurrentAndCrossDomainUnknown()
    {
        CoreThermalStatus status = IntelThrottleDecoder.DecodeCore((1UL << 12) | (1UL << 14), All with { CurrentAndCrossDomainLimits = false });

        Assert.Null(status.CurrentLimit);
        Assert.Null(status.CrossDomain);
    }

    [Theory]
    [InlineData(0x0064_0000UL, 100, 0)]         // i5-13500T : TjMax 100 °C, sans décalage
    [InlineData(0x0569_0000UL, 105, 5)]         // TjMax 105 °C, décalage TCC de 5 °C
    [InlineData(0x0000_0000UL, null, null)]     // registre vide : pas plausible
    [InlineData(0x00FF_0000UL, null, null)]     // 255 °C : pas plausible
    public void TemperatureTarget_TjMaxAndTccOffset(ulong raw, int? tjMax, int? offset)
    {
        (int? decodedTjMax, int? decodedOffset) = IntelThrottleDecoder.DecodeTemperatureTarget(raw);

        Assert.Equal(tjMax, decodedTjMax);
        Assert.Equal(offset, decodedOffset);
    }

    [Fact]
    public void ThrottleTemperature_IsTjMaxMinusTheOffset()
    {
        var reading = new CpuThrottleReading { Source = CpuThrottleSource.IntelMsr, TjMaxC = 105, TccOffsetC = 5 };

        Assert.Equal(100, reading.ThrottleTemperatureC);
    }

    [Fact]
    public void BaseAndTurboRatios()
    {
        Assert.Equal(3500, IntelThrottleDecoder.DecodeBaseMhz(0x23_00UL));
        Assert.Equal(5300, IntelThrottleDecoder.DecodeMaxTurboMhz(0x3030_3434_3535_3535UL));
        Assert.Null(IntelThrottleDecoder.DecodeBaseMhz(0));
    }

    [Fact]
    public void PowerThrottled_UsesTheRaplTimeUnit_AndHandlesWraparound()
    {
        double unit = IntelThrottleDecoder.DecodeTimeUnitSeconds(0x000A_0E03UL); // 1 / 2^10 s

        Assert.Equal(1.0 / 1024, unit);
        // 512 unités en 1 s = 0,5 s bridée sur 1 s.
        Assert.Equal(50, IntelThrottleDecoder.ThrottledPercent(1000, 1512, unit, TimeSpan.FromSeconds(1)));
        // Compteur 32 bits revenu à zéro.
        Assert.Equal(50, IntelThrottleDecoder.ThrottledPercent(0xFFFF_FF00UL, 0x100UL, unit, TimeSpan.FromSeconds(1)));
        Assert.Null(IntelThrottleDecoder.ThrottledPercent(0, 5000, unit, TimeSpan.FromSeconds(1)));
        Assert.Null(IntelThrottleDecoder.ThrottledPercent(0, 10, unit, TimeSpan.Zero));
    }

    [Fact]
    public void PerfLimitReasons_StatusBitsOnly()
    {
        IntelPerfLimitReasons reasons = IntelThrottleDecoder.DecodePerfLimitReasons((1UL << 1) | (1UL << 10) | (1UL << 26));

        Assert.True(reasons.Thermal);
        Assert.True(reasons.PackagePl1);
        Assert.False(reasons.PackagePl2);
        Assert.True(reasons.Any);
        Assert.False(IntelThrottleDecoder.DecodePerfLimitReasons(0xFFFF_0000UL).Any);
    }

    [Fact]
    public void EffectiveMhz_FromAperfMperf()
    {
        Assert.Equal(4200, IntelThrottleDecoder.EffectiveMhz(1000, 13000, 1000, 11000, baseMhz: 3500));
    }

    [Fact]
    public void EffectiveMhz_SurvivesA64BitWraparound()
    {
        Assert.Equal(3500, IntelThrottleDecoder.EffectiveMhz(ulong.MaxValue - 99, 900, ulong.MaxValue - 99, 900, baseMhz: 3500));
    }

    [Fact]
    public void EffectiveMhz_IdleProcessorOrAbsurdResult_IsNull()
    {
        Assert.Null(IntelThrottleDecoder.EffectiveMhz(10, 20, 500, 500, baseMhz: 3500));
        Assert.Null(IntelThrottleDecoder.EffectiveMhz(0, 1_000_000, 0, 10, baseMhz: 3500));
    }

    [Fact]
    public void Reading_IsThrottled_OnlyWhenAReasonIsActive()
    {
        Assert.False(new CpuThrottleReading { Source = CpuThrottleSource.IntelMsr, Thermal = false, PowerLimit = null }.IsThrottled);
        Assert.True(new CpuThrottleReading { Source = CpuThrottleSource.IntelMsr, PowerLimit = true }.IsThrottled);
    }

    [Fact]
    public void WindowsCounters_EffectiveMhz()
    {
        Assert.Equal(4200, new ProcessorPerformanceSample(120, 3500, 0, 0).EffectiveMhz);
        Assert.Null(new ProcessorPerformanceSample(120, null, 0, 0).EffectiveMhz);
    }

    [Fact]
    public void WindowsCounters_PerformanceLimit_IsAHundredWithoutLimit()
    {
        Assert.False(new ProcessorPerformanceSample(165, 1600, 100, 0).IsLimited);
        Assert.True(new ProcessorPerformanceSample(80, 1600, 72, 0).IsLimited);
        Assert.Null(new ProcessorPerformanceSample(80, 1600, null, null).IsLimited);
    }
}
