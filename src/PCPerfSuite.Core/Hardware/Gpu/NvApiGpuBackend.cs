using NvAPIWrapper;
using NvAPIWrapper.GPU;
using NvAPIWrapper.Native.GPU;
using NvAPIWrapper.Native.GPU.Structures;
using NvAPIWrapper.Native.Interfaces.GPU;

namespace PCPerfSuite.Core.Hardware.Gpu;

/// <summary>
/// Contrôle des GPU NVIDIA via NVAPI (NvAPIWrapper.Net) : limite de puissance, overclocking (décalages
/// d'horloge cœur/mémoire, limite de température, surtension) et ventilateurs.
///
/// L'overclocking passe par l'API P-States 2.0 (NvAPI_GPU_GetPstates20/SetPstates20) : on écrit un
/// *décalage* (delta) sur les horloges de l'état P0 plutôt qu'une fréquence absolue, exactement comme
/// le font les curseurs "Core Clock / Memory Clock" de MSI Afterburner. C'est l'API générique
/// supportée de Kepler aux générations actuelles — contrairement à ClockBoostTable/CoreVoltageBoost
/// (courbe tension/fréquence), documentées "Pascal only" dans cette librairie et donc seulement
/// proposées ici si la carte les accepte réellement (testé à l'exécution, pas supposé).
///
/// NVIDIANotSupportedException n'hérite PAS de NVIDIAApiException, d'où les catch larges.
/// </summary>
internal sealed class NvApiGpuBackend : IGpuTuningBackend
{
    /// <summary>État P0 : l'état "3D performance", le seul que l'on overclocke (comme Afterburner).</summary>
    private const PerformanceStateId OverclockState = PerformanceStateId.P0_3DPerformance;

    /// <summary>Plages de repli quand le pilote ne renvoie pas de plage exploitable (min = max = 0) :
    /// valeurs prudentes, proches de ce qu'autorisent les cartes récentes.</summary>
    private const int FallbackCoreOffsetMinMhz = -500;
    private const int FallbackCoreOffsetMaxMhz = 1000;
    private const int FallbackMemoryOffsetMinMhz = -1000;
    private const int FallbackMemoryOffsetMaxMhz = 2000;

    private PhysicalGPU? _gpu;

    public GpuVendor Vendor => GpuVendor.Nvidia;

    public bool RequiresOverclockWaiver => false;

    public bool TryAcceptOverclockWaiver() => true;

    public bool TryInitialize()
    {
        try
        {
            NVIDIA.Initialize();
            _gpu = PhysicalGPU.GetPhysicalGPUs().FirstOrDefault();
        }
        catch
        {
            _gpu = null;
        }

        return _gpu is not null;
    }

    public GpuControlSnapshot? GetSnapshot()
    {
        if (_gpu is not { } gpu) return null;

        string name;
        try
        {
            name = gpu.FullName;
        }
        catch
        {
            return null;
        }

        // Chaque bloc est lu séparément : sur les GPU portables, NVAPI refuse souvent les
        // ventilateurs et la limite de puissance (gérés par le constructeur du PC), ce qui ne doit
        // pas masquer le reste.
        GPUPowerLimitInfo? info = null;
        GPUPowerLimitPolicy? policy = null;
        try
        {
            info = gpu.PerformanceControl.PowerLimitInformation.FirstOrDefault();
            policy = gpu.PerformanceControl.PowerLimitPolicies.FirstOrDefault();
        }
        catch
        {
            // Limite de puissance non exposée.
        }

        List<GpuFanInfo> fans;
        try
        {
            fans = gpu.CoolerInformation.Coolers.Select(c => new GpuFanInfo
            {
                CoolerId = c.CoolerId,
                CurrentLevelPercent = c.CurrentLevel,
                CurrentRpm = c.CurrentFanSpeedInRPM,
                MinLevelPercent = c.CurrentMinimumLevel,
                MaxLevelPercent = c.CurrentMaximumLevel,
            }).ToList();
        }
        catch
        {
            fans = new List<GpuFanInfo>();
        }

        return new GpuControlSnapshot
        {
            Name = name,
            Vendor = GpuVendor.Nvidia,
            PowerLimitSupported = info is not null && info.MaximumPowerInPercent > info.MinimumPowerInPercent,
            PowerLimitPercent = policy?.PowerTargetInPercent ?? 100,
            PowerLimitMinPercent = info?.MinimumPowerInPercent ?? 50,
            PowerLimitMaxPercent = info?.MaximumPowerInPercent ?? 100,
            PowerLimitDefaultPercent = info?.DefaultPowerInPercent ?? 100,
            Fans = fans,
        };
    }

    public bool TrySetPowerLimitPercent(float percent)
    {
        if (_gpu is not { } gpu) return false;

        try
        {
            GPUPowerLimitInfo? info = gpu.PerformanceControl.PowerLimitInformation.FirstOrDefault();
            float min = info?.MinimumPowerInPercent ?? 50;
            float max = info?.MaximumPowerInPercent ?? 100;
            float clamped = Math.Clamp(percent, min, max);

            PrivatePowerPoliciesStatusV1 status = NvAPIWrapper.Native.GPUApi.ClientPowerPoliciesGetStatus(gpu.Handle);
            PrivatePowerPoliciesStatusV1.PowerPolicyStatusEntry[] entries = status.PowerPolicyStatusEntries
                .Select(_ => new PrivatePowerPoliciesStatusV1.PowerPolicyStatusEntry((uint)(clamped * 1000)))
                .ToArray();

            NvAPIWrapper.Native.GPUApi.ClientPowerPoliciesSetStatus(gpu.Handle, new PrivatePowerPoliciesStatusV1(entries));
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool TryRestorePowerLimitDefault()
    {
        if (_gpu is not { } gpu) return false;

        try
        {
            GPUPowerLimitInfo? info = gpu.PerformanceControl.PowerLimitInformation.FirstOrDefault();
            return info is not null && TrySetPowerLimitPercent(info.DefaultPowerInPercent);
        }
        catch
        {
            return false;
        }
    }

    public GpuOverclockSnapshot? GetOverclock()
    {
        if (_gpu is not { } gpu) return null;

        var clocks = ReadClockOffsets(gpu);
        var thermal = ReadTemperatureLimit(gpu);
        var voltage = ReadVoltageBoost(gpu);

        return new GpuOverclockSnapshot
        {
            CoreOffsetSupported = clocks.CoreOk,
            MemoryOffsetSupported = clocks.MemOk,
            CoreOffsetMhz = clocks.Core,
            CoreOffsetMinMhz = clocks.CoreMin,
            CoreOffsetMaxMhz = clocks.CoreMax,
            MemoryOffsetMhz = clocks.Mem,
            MemoryOffsetMinMhz = clocks.MemMin,
            MemoryOffsetMaxMhz = clocks.MemMax,

            TemperatureLimitSupported = thermal.Ok,
            TemperatureLimitC = thermal.Current,
            TemperatureLimitMinC = thermal.Min,
            TemperatureLimitMaxC = thermal.Max,
            TemperatureLimitDefaultC = thermal.Default,

            VoltageSupported = voltage.Ok,
            Voltage = voltage.Percent,
            VoltageMin = 0,
            VoltageMax = 100,
            VoltageDefault = 0,
            VoltageUnit = GpuVoltageUnit.Percent,
        };
    }

    public GpuPerformanceLimit? GetActiveLimit()
    {
        if (_gpu is not { } gpu) return null;

        try
        {
            PerformanceLimit limit = gpu.PerformanceControl.CurrentActiveLimit;

            GpuPerformanceLimit result = GpuPerformanceLimit.None;
            if (limit.HasFlag(PerformanceLimit.PowerLimit)) result |= GpuPerformanceLimit.Power;
            if (limit.HasFlag(PerformanceLimit.TemperatureLimit)) result |= GpuPerformanceLimit.Temperature;
            if (limit.HasFlag(PerformanceLimit.VoltageLimit)) result |= GpuPerformanceLimit.Voltage;
            if (limit.HasFlag(PerformanceLimit.NoLoadLimit)) result |= GpuPerformanceLimit.NoLoad;
            if (limit.HasFlag(PerformanceLimit.Unknown8)) result |= GpuPerformanceLimit.Other;

            return result;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Applique les décalages d'horloge cœur et mémoire (en MHz) sur l'état P0. Les deux sont
    /// écrits dans le même appel, comme le fait le pilote NVIDIA lui-même.</summary>
    public bool TrySetClockOffsets(int coreMhz, int memoryMhz)
    {
        if (_gpu is not { } gpu) return false;

        var clocks = new[]
        {
            new PerformanceStates20ClockEntryV1(PublicClockDomain.Graphics,
                new PerformanceStates20ParameterDelta(coreMhz * 1000)),
            new PerformanceStates20ClockEntryV1(PublicClockDomain.Memory,
                new PerformanceStates20ParameterDelta(memoryMhz * 1000)),
        };

        var states = new[]
        {
            new PerformanceStates20InfoV1.PerformanceState20(
                OverclockState, clocks, Array.Empty<PerformanceStates20BaseVoltageEntryV1>()),
        };

        // La structure existe en 3 versions ; le pilote n'en accepte qu'un sous-ensemble et répond
        // "IncompatibleStructureVersion" pour les autres. On part de la plus récente et on redescend,
        // comme le fait la lecture dans NvAPIWrapper.
        for (int version = 3; version >= 1; version--)
        {
            try
            {
                IPerformanceStates20Info info = version switch
                {
                    3 => new PerformanceStates20InfoV3(states, (uint)clocks.Length, 0),
                    2 => new PerformanceStates20InfoV2(states, (uint)clocks.Length, 0),
                    _ => new PerformanceStates20InfoV1(states, (uint)clocks.Length, 0),
                };

                NvAPIWrapper.Native.GPUApi.SetPerformanceStates20(gpu.Handle, info);
                return true;
            }
            catch
            {
                // Version refusée : on tente la précédente.
            }
        }

        return false;
    }

    /// <summary>Applique la limite de température (°C) à toutes les politiques thermiques actives.</summary>
    public bool TrySetTemperatureLimit(int celsius)
    {
        if (_gpu is not { } gpu) return false;

        try
        {
            List<GPUThermalLimitInfo> infos = gpu.PerformanceControl.ThermalLimitInformation.ToList();
            if (infos.Count == 0) return false;

            int min = infos.Min(i => i.MinimumTemperature);
            int max = infos.Max(i => i.MaximumTemperature);
            int clamped = max > min ? Math.Clamp(celsius, min, max) : celsius;

            List<GPUThermalLimitPolicy> policies = gpu.PerformanceControl.ThermalLimitPolicies.ToList();

            // On repart des politiques actives quand il y en a (pour conserver leur état P et leur
            // contrôleur) ; sinon on en crée une par contrôleur annoncé par la carte.
            PrivateThermalPoliciesStatusV2.ThermalPoliciesStatusEntry[] entries = policies.Count > 0
                ? policies
                    .Select(p => new PrivateThermalPoliciesStatusV2.ThermalPoliciesStatusEntry(
                        p.PerformanceStateId, p.Controller, clamped))
                    .ToArray()
                : infos
                    .Select(i => new PrivateThermalPoliciesStatusV2.ThermalPoliciesStatusEntry(i.Controller, clamped))
                    .ToArray();

            NvAPIWrapper.Native.GPUApi.SetThermalPoliciesStatus(
                gpu.Handle, new PrivateThermalPoliciesStatusV2(entries));
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Applique la surtension cœur (%) — uniquement disponible sur GPU Pascal.</summary>
    public bool TrySetVoltage(int value)
    {
        if (_gpu is not { } gpu) return false;

        try
        {
            NvAPIWrapper.Native.GPUApi.SetCoreVoltageBoostPercent(
                gpu.Handle, new PrivateVoltageBoostPercentV1((uint)Math.Clamp(value, 0, 100)));
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void RestoreOverclockDefaults()
    {
        if (_gpu is not { } gpu) return;

        TrySetClockOffsets(0, 0);

        var thermal = ReadTemperatureLimit(gpu);
        if (thermal.Ok) TrySetTemperatureLimit(thermal.Default);

        var voltage = ReadVoltageBoost(gpu);
        if (voltage.Ok && voltage.Percent != 0) TrySetVoltage(0);

        TryRestorePowerLimitDefault();
    }

    private static (bool CoreOk, bool MemOk, int Core, int CoreMin, int CoreMax, int Mem, int MemMin, int MemMax)
        ReadClockOffsets(PhysicalGPU gpu)
    {
        try
        {
            IPerformanceStates20Info info = NvAPIWrapper.Native.GPUApi.GetPerformanceStates20(gpu.Handle);
            if (!info.Clocks.TryGetValue(OverclockState, out IPerformanceStates20ClockEntry[]? clocks))
            {
                return default;
            }

            IPerformanceStates20ClockEntry? core = clocks.FirstOrDefault(c => c.DomainId == PublicClockDomain.Graphics);
            IPerformanceStates20ClockEntry? memory = clocks.FirstOrDefault(c => c.DomainId == PublicClockDomain.Memory);

            (int coreValue, int coreMin, int coreMax) = ReadDelta(core, FallbackCoreOffsetMinMhz, FallbackCoreOffsetMaxMhz);
            (int memValue, int memMin, int memMax) = ReadDelta(memory, FallbackMemoryOffsetMinMhz, FallbackMemoryOffsetMaxMhz);

            return (core is not null, memory is not null, coreValue, coreMin, coreMax, memValue, memMin, memMax);
        }
        catch
        {
            return default;
        }
    }

    /// <summary>Convertit un delta NVAPI (kHz) en MHz, avec repli sur une plage prudente quand le
    /// pilote ne renvoie pas de plage exploitable.</summary>
    private static (int Value, int Min, int Max) ReadDelta(
        IPerformanceStates20ClockEntry? entry, int fallbackMin, int fallbackMax)
    {
        if (entry is null) return (0, fallbackMin, fallbackMax);

        PerformanceStates20ParameterDelta delta = entry.FrequencyDeltaInkHz;
        int min = delta.DeltaRange.Minimum / 1000;
        int max = delta.DeltaRange.Maximum / 1000;
        if (max <= min)
        {
            min = fallbackMin;
            max = fallbackMax;
        }

        return (delta.DeltaValue / 1000, min, max);
    }

    private static (bool Ok, int Current, int Min, int Max, int Default) ReadTemperatureLimit(PhysicalGPU gpu)
    {
        try
        {
            GPUThermalLimitInfo? info = gpu.PerformanceControl.ThermalLimitInformation.FirstOrDefault();
            if (info is null) return (false, 0, 0, 0, 0);

            GPUThermalLimitPolicy? policy = gpu.PerformanceControl.ThermalLimitPolicies.FirstOrDefault();
            int current = policy?.TargetTemperature ?? info.DefaultTemperature;

            if (info.MaximumTemperature <= info.MinimumTemperature) return (false, 0, 0, 0, 0);

            return (true, current, info.MinimumTemperature, info.MaximumTemperature, info.DefaultTemperature);
        }
        catch
        {
            return (false, 0, 0, 0, 0);
        }
    }

    private static (bool Ok, int Percent) ReadVoltageBoost(PhysicalGPU gpu)
    {
        try
        {
            return (true, (int)NvAPIWrapper.Native.GPUApi.GetCoreVoltageBoostPercent(gpu.Handle).Percent);
        }
        catch
        {
            return (false, 0);
        }
    }

    public bool TrySetFanPercent(int coolerId, int percent)
    {
        if (_gpu is not { } gpu) return false;

        try
        {
            gpu.CoolerInformation.SetCoolerSettings(coolerId, Math.Clamp(percent, 0, 100));
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool TryRestoreFanAuto()
    {
        if (_gpu is not { } gpu) return false;

        try
        {
            gpu.CoolerInformation.RestoreCoolerSettingsToDefault();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        _gpu = null;
        try { NVIDIA.Unload(); } catch { /* best-effort */ }
    }
}
