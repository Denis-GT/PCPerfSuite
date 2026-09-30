using PCPerfSuite.Core.Hardware;
using PCPerfSuite.Core.Hardware.Gpu;

namespace PCPerfSuite.Core.Tests;

/// <summary>Armement de la sécurité thermique, refus partiel et relectures de GpuControlService, sur un pilote factice.</summary>
public class GpuControlServiceTests
{
    /// <summary>Pilote factice : garde les valeurs écrites, sait refuser la mémoire (comme ADLX) et compte les relectures.</summary>
    private sealed class FakeBackend : IGpuTuningBackend
    {
        public int Core { get; set; }
        public int Memory { get; set; }
        public float Power { get; set; } = 100;
        public bool RefuseMemory { get; set; }
        public int OverclockReads { get; private set; }
        public int Restores { get; private set; }

        public GpuVendor Vendor => GpuVendor.Amd;
        public bool RequiresOverclockWaiver => false;
        public bool TryAcceptOverclockWaiver() => true;
        public bool TryInitialize() => true;

        public GpuControlSnapshot? GetSnapshot() => new()
        {
            Name = "Radeon",
            Vendor = GpuVendor.Amd,
            PowerLimitSupported = true,
            PowerLimitPercent = Power,
            PowerLimitMinPercent = 50,
            PowerLimitMaxPercent = 120,
            PowerLimitDefaultPercent = 100,
        };

        public GpuIdentity? GetIdentity() => new(GpuVendor.Amd, "Radeon");

        public GpuOverclockSnapshot? GetOverclock()
        {
            OverclockReads++;
            return new GpuOverclockSnapshot
            {
                CoreOffsetSupported = true,
                MemoryOffsetSupported = true,
                CoreOffsetMhz = Core,
                MemoryOffsetMhz = Memory,
                CoreOffsetMinMhz = -500,
                CoreOffsetMaxMhz = 500,
                MemoryOffsetMinMhz = -500,
                MemoryOffsetMaxMhz = 500,
            };
        }

        public GpuPerformanceLimit? GetActiveLimit() => null;

        public bool TrySetPowerLimitPercent(float percent)
        {
            Power = percent;
            return true;
        }

        public bool TryRestorePowerLimitDefault()
        {
            Power = 100;
            return true;
        }

        public bool TrySetClockOffsets(int coreMhz, int memoryMhz)
        {
            Core = coreMhz;
            if (RefuseMemory) return false;
            Memory = memoryMhz;
            return true;
        }

        public bool TrySetTemperatureLimit(int celsius) => false;
        public bool TrySetVoltage(int value) => false;

        public void RestoreOverclockDefaults()
        {
            Restores++;
            Core = 0;
            Memory = 0;
            Power = 100;
        }

        public bool TrySetFanPercent(int coolerId, int percent) => false;
        public bool TryRestoreFanAuto() => false;
        public void Dispose() { }
    }

    private static (GpuControlService Service, FakeBackend Backend) Create(Action<FakeBackend>? setup = null)
    {
        var backend = new FakeBackend();
        setup?.Invoke(backend);
        var service = new GpuControlService(() => backend);
        Assert.True(service.TryInitialize());
        return (service, backend);
    }

    [Fact]
    public void RaisingTheCoreOffset_Arms()
    {
        (GpuControlService service, _) = Create();

        service.TrySetClockOffsets(150, 0);

        Assert.True(service.ThermalSafety.IsArmed);
    }

    [Fact]
    public void LoweringOnly_DoesNotArm()
    {
        (GpuControlService service, _) = Create();

        service.TrySetClockOffsets(-100, -200);
        service.TrySetPowerLimitPercent(80);

        Assert.False(service.ThermalSafety.IsArmed);
    }

    [Fact]
    public void AnotherToolsOverclock_InABlockTheAppDidNotWrite_DoesNotArm()
    {
        // Afterburner a posé +200 MHz ; l'app ne fait que baisser la limite de puissance.
        (GpuControlService service, _) = Create(b => b.Core = 200);

        service.TrySetPowerLimitPercent(80);

        Assert.False(service.ThermalSafety.IsArmed);
    }

    [Fact]
    public void PartialRefusal_StillCountsAsTouched()
    {
        // ADLX : cœur posé, mémoire refusée, réponse « refusé ». La carte est bien overclockée.
        (GpuControlService service, FakeBackend backend) = Create(b => b.RefuseMemory = true);

        Assert.False(service.TrySetClockOffsets(150, 500));

        Assert.True(service.ThermalSafety.IsArmed);
        service.Dispose();
        Assert.Equal(1, backend.Restores);
    }

    [Fact]
    public void FullRefusal_IsNotTouched()
    {
        (GpuControlService service, FakeBackend backend) = Create(b => b.RefuseMemory = true);

        // Rien de posé : le cœur demandé vaut 0, la mémoire est refusée.
        Assert.False(service.TrySetClockOffsets(0, 500));

        Assert.False(service.ThermalSafety.IsArmed);
        service.Dispose();
        Assert.Equal(0, backend.Restores);
    }

    [Fact]
    public void ApplyAndVerify_ReadsTheCardOnceForReportAndArming()
    {
        (GpuControlService service, FakeBackend backend) = Create();

        GpuApplyReport report = service.ApplyAndVerify(new GpuOverclockRequest
        {
            CoreOffsetMhz = 150,
            MemoryOffsetMhz = 400,
            PowerLimitPercent = 110,
        });

        Assert.True(report.AllRetained);
        Assert.True(service.ThermalSafety.IsArmed);
        Assert.Equal(1, backend.OverclockReads);
    }

    [Fact]
    public void RestoringDefaults_Disarms()
    {
        (GpuControlService service, _) = Create();
        service.TrySetClockOffsets(150, 0);

        service.RestoreOverclockDefaults();

        Assert.False(service.ThermalSafety.IsArmed);
    }

    [Fact]
    public void Identity_IsReadAtInitialization()
    {
        (GpuControlService service, _) = Create();

        Assert.Equal(new GpuIdentity(GpuVendor.Amd, "Radeon"), service.Identity);
    }
}
