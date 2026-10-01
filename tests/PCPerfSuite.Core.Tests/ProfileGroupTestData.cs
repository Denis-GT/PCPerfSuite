using PCPerfSuite.Core.Hardware;
using PCPerfSuite.Core.Hardware.Cpu;
using PCPerfSuite.Core.Hardware.Fans;
using PCPerfSuite.Core.Hardware.Gpu;
using PCPerfSuite.Core.Profiles;

namespace PCPerfSuite.Core.Tests;

/// <summary>Matériel fictif pour les tests des groupes de profils : un i5-14600K, une RTX 5070 Ti, deux ventilateurs.</summary>
internal static class ProfileGroupTestData
{
    public static readonly CpuIdentity I5 = new() { Vendor = "Intel", Family = 6, Model = 183, Name = "Intel(R) Core(TM) i5-14600K" };

    public static readonly GpuIdentity Rtx = new(GpuVendor.Nvidia, "NVIDIA GeForce RTX 5070 Ti", 0x10DE, 0x2C05, 0x89F21043);

    public static readonly CpuPowerSetting Boost = new()
    {
        Id = "boost",
        Guid = new Guid("be337238-0d82-4146-a960-4f3749d470c7"),
        Label = "Mode boost",
        Description = "",
        Choices = [new(0, "Désactivé"), new(1, "Activé"), new(2, "Agressif")],
    };

    public static readonly CpuPowerSetting Epp = new()
    {
        Id = "epp",
        Guid = new Guid("36687f9e-e3a5-4dbf-b1dc-15eb381c6863"),
        Label = "Préférence performance / économie",
        Description = "",
        Min = 0,
        Max = 100,
    };

    public static CpuWattsReading Watts(float sustained = 125, float? burst = 181, float defaultSustained = 125, float? defaultBurst = 181)
        => new(sustained, burst, defaultSustained, defaultBurst, 15, 253);

    public static CpuTargetState Cpu(
        uint boost = 1, uint epp = 50, bool battery = false, CpuWattsReading? watts = null, bool writable = true,
        bool accepted = true, bool emergency = false, CpuIdentity? identity = null, uint boostDc = 1, uint eppDc = 50)
        => new(
            identity ?? I5,
            battery,
            [new CpuPowerSettingReading(Boost, boost, battery ? boostDc : boost), new CpuPowerSettingReading(Epp, epp, battery ? eppDc : epp)],
            watts ?? Watts(),
            writable,
            writable ? null : "Les limites de puissance sont verrouillées par le BIOS/UEFI jusqu'au prochain démarrage.",
            accepted,
            emergency);

    public static ProfileGroupCpuPart CpuPart(CpuProfile values, CpuIdentity? capturedOn = null)
        => new() { Kind = ProfilePartKinds.Values, Values = values, CapturedOn = capturedOn ?? I5 };

    public static GpuOverclockSnapshot Overclock(int core = 0, int memory = 0, int voltage = 0, int temperature = 83) => new()
    {
        CoreOffsetSupported = true,
        CoreOffsetMhz = core,
        CoreOffsetMinMhz = -500,
        CoreOffsetMaxMhz = 250,
        MemoryOffsetSupported = true,
        MemoryOffsetMhz = memory,
        MemoryOffsetMinMhz = -1000,
        MemoryOffsetMaxMhz = 2000,
        TemperatureLimitSupported = true,
        TemperatureLimitC = temperature,
        TemperatureLimitMinC = 65,
        TemperatureLimitMaxC = 90,
        TemperatureLimitDefaultC = 83,
        VoltageSupported = true,
        Voltage = voltage,
        VoltageMin = 0,
        VoltageMax = 100,
        VoltageDefault = 0,
        VoltageUnit = GpuVoltageUnit.Percent,
    };

    public static GpuControlSnapshot Power(float percent = 100) => new()
    {
        Name = "NVIDIA GeForce RTX 5070 Ti",
        PowerLimitSupported = true,
        PowerLimitPercent = percent,
        PowerLimitMinPercent = 70,
        PowerLimitMaxPercent = 110,
        PowerLimitDefaultPercent = 100,
    };

    public static GpuTargetState Gpu(
        GpuOverclockSnapshot? overclock = null, GpuControlSnapshot? power = null, bool available = true, bool canOverclock = true,
        bool emergency = false, GpuIdentity? identity = null)
        => new(available, available ? null : "aucun pilote ne répond", canOverclock, identity ?? Rtx,
            overclock ?? Overclock(), power ?? Power(), emergency);

    public static ProfileGroupGpuPart GpuPart(GpuOverclockProfile values, GpuIdentity? capturedOn = null)
        => new() { Kind = ProfilePartKinds.Values, Values = values, CapturedOn = capturedOn ?? Rtx };

    public static FanCurveConfig Fan(string id, FanControlMode mode = FanControlMode.Curve, float manual = 50) => new()
    {
        ControlSensorId = id,
        Mode = mode,
        ManualPercent = manual,
        Points = FanCurveMath.EquilibrePoints(),
    };

    public static FanTargetState Fans(bool ready = true, bool laptop = false, params FanCurveConfig[] current)
    {
        FanCurveConfig[] configs = current.Length > 0 ? current : [Fan("cpu", FanControlMode.Auto), Fan("gpu:0", FanControlMode.Auto)];
        return new FanTargetState(
            ready,
            configs.Select(c => new FanTargetFan(c.ControlSensorId, c.ControlSensorId == "cpu" ? "CPU Fan" : "GPU", c.ControlSensorId.StartsWith("gpu:"))).ToList(),
            laptop,
            "aucune puce de pilotage reconnue",
            new FanProfile { Fans = configs.ToList() });
    }
}
