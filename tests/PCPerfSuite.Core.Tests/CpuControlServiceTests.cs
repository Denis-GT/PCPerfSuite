using PCPerfSuite.Core.Hardware.Cpu;

namespace PCPerfSuite.Core.Tests;

/// <summary>Sécurité thermique CPU (98 °C pendant 15 s) et armement, sur un backend factice et une horloge réglable.</summary>
public class CpuControlServiceTests
{
    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>Limites d'origine 65 W / 154 W ; <see cref="RetainAtMost"/> simule un firmware qui rabote.</summary>
    private sealed class FakeBackend : ICpuTuningBackend
    {
        public float Sustained { get; set; } = 65;
        public float Burst { get; set; } = 154;
        public float? RetainAtMost { get; set; }
        public bool Locked { get; set; }
        public int Restores { get; private set; }

        public string Description => "factice";
        public CpuCapability PowerLimit => Locked ? CpuCapability.ReadOnly("verrou") : CpuCapability.Full;
        public bool IsExperimental => false;

        public CpuPowerLimitSnapshot? ReadPowerLimits() => new()
        {
            SustainedWatts = Sustained,
            BurstWatts = Burst,
            DefaultSustainedWatts = 65,
            DefaultBurstWatts = 154,
            MinWatts = 5,
            MaxWatts = 400,
            MaxWattsInfo = new CpuMaxWattsInfo(400, CpuMaxWattsSource.ProcessorMaxPower, "test", false, ""),
        };

        public bool TrySetPowerLimits(float sustainedWatts, float? burstWatts, out string message)
        {
            if (Locked)
            {
                message = "verrou";
                return false;
            }

            Sustained = RetainAtMost is { } max ? Math.Min(sustainedWatts, max) : sustainedWatts;
            Burst = burstWatts ?? sustainedWatts;
            message = "ok";
            return Sustained == sustainedWatts;
        }

        public bool TryRestoreDefaults(out string message)
        {
            Restores++;
            Sustained = 65;
            Burst = 154;
            message = "ok";
            return true;
        }

        public void Dispose() { }
    }

    private static readonly CpuPlatform Intel = new() { Vendor = CpuVendor.Intel, Name = "Core", IsX64 = true };

    private static (CpuControlService Service, FakeBackend Backend, ManualTime Time) Create(Action<FakeBackend>? setup = null)
    {
        var backend = new FakeBackend();
        setup?.Invoke(backend);
        var time = new ManualTime();
        return (new CpuControlService(Intel, backend, time), backend, time);
    }

    [Fact]
    public void RaisedSustainedLimit_HotForFifteenSeconds_RestoresDefaults()
    {
        (CpuControlService service, FakeBackend backend, ManualTime time) = Create();
        string? warned = null;
        service.EmergencyRestored += m => warned = m;
        service.TrySetPowerLimits(125, 200, out _);

        service.NoteTemperature(98);
        time.Now = time.Now.AddSeconds(14);
        service.NoteTemperature(99);
        Assert.Equal(0, backend.Restores);

        time.Now = time.Now.AddSeconds(1);
        service.NoteTemperature(99);

        Assert.Equal(1, backend.Restores);
        Assert.Contains("99 °C", warned);
        Assert.False(service.NeedsTemperatureWatch);
    }

    [Fact]
    public void BriefPeak_DoesNothing()
    {
        (CpuControlService service, FakeBackend backend, ManualTime time) = Create();
        service.TrySetPowerLimits(125, 200, out _);

        service.NoteTemperature(99);
        time.Now = time.Now.AddSeconds(10);
        service.NoteTemperature(80);
        time.Now = time.Now.AddSeconds(10);
        service.NoteTemperature(99);

        Assert.Equal(0, backend.Restores);
    }

    [Fact]
    public void RaisingOnlyTheBurstLimit_Arms()
    {
        // PL2 se règle à part : 65 W / 250 W chauffe davantage que 65 W / 154 W.
        (CpuControlService service, _, _) = Create();

        service.TrySetPowerLimits(65, 250, out _);

        Assert.True(service.NeedsTemperatureWatch);
    }

    [Fact]
    public void LoweringOnly_DoesNotArm()
    {
        (CpuControlService service, _, _) = Create();

        service.TrySetPowerLimits(45, 100, out _);

        Assert.False(service.NeedsTemperatureWatch);
    }

    [Fact]
    public void TrimmedByTheFirmware_StillArmsAndIsRestoredOnExit()
    {
        // « Retenu 110 W au lieu de 125 W » : refus à la relecture, mais le registre a changé.
        (CpuControlService service, FakeBackend backend, _) = Create(b => b.RetainAtMost = 110);

        Assert.False(service.TrySetPowerLimits(125, 200, out _));

        Assert.True(service.NeedsTemperatureWatch);
        service.Dispose();
        Assert.Equal(1, backend.Restores);
    }

    [Fact]
    public void Locked_NothingIsTouched()
    {
        (CpuControlService service, FakeBackend backend, _) = Create(b => b.Locked = true);

        Assert.False(service.TrySetPowerLimits(125, 200, out _));

        Assert.False(service.NeedsTemperatureWatch);
        service.Dispose();
        Assert.Equal(0, backend.Restores);
    }

    [Fact]
    public void AfterResume_LimitsBackToFirmware_Disarms()
    {
        (CpuControlService service, FakeBackend backend, _) = Create();
        service.TrySetPowerLimits(125, 200, out _);

        // Veille S3 : le firmware repose ses limites.
        backend.Sustained = 65;
        backend.Burst = 154;
        service.RefreshAfterResume();

        Assert.False(service.NeedsTemperatureWatch);
    }

    [Fact]
    public void AfterResume_SleepTimeIsNotHeat()
    {
        (CpuControlService service, FakeBackend backend, ManualTime time) = Create();
        service.TrySetPowerLimits(125, 200, out _);
        service.NoteTemperature(99);

        time.Now = time.Now.AddHours(1);
        service.RefreshAfterResume();
        service.NoteTemperature(99);

        Assert.Equal(0, backend.Restores);
        Assert.True(service.NeedsTemperatureWatch);
    }
}
