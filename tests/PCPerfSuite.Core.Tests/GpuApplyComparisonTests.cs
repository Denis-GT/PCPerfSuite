using PCPerfSuite.Core.Hardware;
using PCPerfSuite.Core.Hardware.Gpu;

namespace PCPerfSuite.Core.Tests;

/// <summary>Demandé / retenu : seule la relecture de la carte fait foi.</summary>
public class GpuApplyComparisonTests
{
    private static readonly GpuApplyAccepted AllAccepted = new(true, true, true, true);

    private static GpuOverclockSnapshot Oc(int core, int memory, int temperature = 83, int voltage = 0) => new()
    {
        CoreOffsetSupported = true,
        MemoryOffsetSupported = true,
        CoreOffsetMhz = core,
        MemoryOffsetMhz = memory,
        TemperatureLimitSupported = true,
        TemperatureLimitC = temperature,
        VoltageSupported = true,
        Voltage = voltage,
        VoltageUnit = GpuVoltageUnit.Millivolts,
        VoltageIsOffset = true,
    };

    private static GpuControlSnapshot Power(float percent) => new() { Name = "GPU", PowerLimitSupported = true, PowerLimitPercent = percent };

    [Fact]
    public void ReadBackAsRequested_IsRetained()
    {
        var request = new GpuOverclockRequest { CoreOffsetMhz = 150, MemoryOffsetMhz = 500, PowerLimitPercent = 110 };

        GpuApplyReport report = GpuApplyComparison.Compare(request, AllAccepted, Oc(150, 500), Power(110.2f));

        Assert.True(report.AllRetained);
        Assert.Equal([GpuSetting.CoreOffset, GpuSetting.MemoryOffset, GpuSetting.PowerLimit], report.Items.Select(i => i.Setting));
        Assert.Equal("Relu sur la carte : cœur +150 MHz ; mémoire +500 MHz ; puissance 110 %.", report.Describe());
    }

    [Fact]
    public void DriverAcceptedButReadBackDiffers_IsTrimmed()
    {
        // NVAPI accepte l'appel et rabote en silence.
        var request = new GpuOverclockRequest { CoreOffsetMhz = 300 };

        GpuApplyReport report = GpuApplyComparison.Compare(request, AllAccepted, Oc(250, 0), null);

        GpuApplyItem core = Assert.Single(report.Items);
        Assert.Equal(GpuApplyStatus.Trimmed, core.Status);
        Assert.Equal(250, core.Retained);
        Assert.False(report.AllRetained);
        Assert.Contains("+250 MHz au lieu de +300 MHz demandés", report.Describe());
    }

    [Fact]
    public void OneMegahertzOff_IsStillRetained()
    {
        // Conversion kHz → MHz du pilote.
        GpuApplyReport report = GpuApplyComparison.Compare(new GpuOverclockRequest { CoreOffsetMhz = 150 }, AllAccepted, Oc(149, 0), null);

        Assert.True(report.AllRetained);
    }

    [Fact]
    public void DriverRefusal_IsRefused_WhateverTheReadBack()
    {
        var accepted = new GpuApplyAccepted(Clocks: false, PowerLimit: null, TemperatureLimit: null, Voltage: null);

        GpuApplyReport report = GpuApplyComparison.Compare(new GpuOverclockRequest { CoreOffsetMhz = 150 }, accepted, Oc(150, 0), null);

        Assert.True(report.AnyRefused);
        Assert.Contains("refusé par le pilote", report.Describe());
    }

    [Fact]
    public void CardUnreadable_IsNotReadBack()
    {
        var request = new GpuOverclockRequest { CoreOffsetMhz = 150, PowerLimitPercent = 90 };

        GpuApplyReport report = GpuApplyComparison.Compare(request, AllAccepted, null, null);

        Assert.All(report.Items, item => Assert.Equal(GpuApplyStatus.NotReadBack, item.Status));
        Assert.Contains("envoyé, non relu", report.Describe());
    }

    [Fact]
    public void TemperatureAndVoltage_AreComparedInTheCardsUnits()
    {
        var request = new GpuOverclockRequest { TemperatureLimitC = 88, Voltage = -50 };

        GpuApplyReport report = GpuApplyComparison.Compare(request, AllAccepted, Oc(0, 0, temperature: 87, voltage: -50), null);

        Assert.Equal(GpuApplyStatus.Trimmed, report.Find(GpuSetting.TemperatureLimit)?.Status);
        Assert.Equal(GpuApplyStatus.Retained, report.Find(GpuSetting.Voltage)?.Status);
        Assert.Contains("tension -50 mV", report.Describe());
    }

    [Fact]
    public void NothingRequested_IsEmpty()
    {
        GpuApplyReport report = GpuApplyComparison.Compare(new GpuOverclockRequest(), AllAccepted, Oc(0, 0), Power(100));

        Assert.Empty(report.Items);
        Assert.Equal("", report.Describe());
    }
}
