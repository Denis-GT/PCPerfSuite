using NvAPIWrapper;
using NvAPIWrapper.GPU;
using NvAPIWrapper.Native.Exceptions;
using NvAPIWrapper.Native.GPU.Structures;

namespace PCPerfSuite.Core.Hardware;

/// <summary>
/// Contrôle GPU via NVAPI (NvAPIWrapper.Net) : limite de puissance et ventilateurs.
///
/// Le décalage d'horloge cœur/mémoire façon MSI Afterburner n'est PAS supporté ici. L'API NVAPI que
/// cette librairie expose pour l'overclocking (ClockBoostTable / ClockBoostLock / CoreVoltageBoost)
/// est documentée "Pascal only" (GTX 10xx) — vérifié dans les commentaires XML du package. Depuis
/// Turing, NVIDIA pilote l'overclocking via une API de points tension/fréquence non documentée que
/// ni cette librairie ni son fork le plus actif n'implémentent ; l'utiliser à l'aveugle sur une RTX
/// 50xx (structures privées versionnées, jamais testées sur ce matériel) serait plus dangereux
/// qu'utile. Limite de puissance et ventilateurs, eux, passent par une API NVAPI générique à toutes
/// les générations récentes.
/// </summary>
public sealed class GpuControlService : IDisposable
{
    private PhysicalGPU? _gpu;
    private bool _initialized;

    /// <summary>Initialise NVAPI et repère le premier GPU NVIDIA. Ne lève jamais — retourne false si
    /// NVAPI est indisponible (pas de GPU NVIDIA, pilote absent, etc.).</summary>
    public bool TryInitialize()
    {
        if (_initialized) return _gpu is not null;
        _initialized = true;

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

        try
        {
            GPUPowerLimitInfo? info = gpu.PerformanceControl.PowerLimitInformation.FirstOrDefault();
            GPUPowerLimitPolicy? policy = gpu.PerformanceControl.PowerLimitPolicies.FirstOrDefault();

            List<GpuFanInfo> fans = gpu.CoolerInformation.Coolers.Select(c => new GpuFanInfo
            {
                CoolerId = c.CoolerId,
                CurrentLevelPercent = c.CurrentLevel,
                CurrentRpm = c.CurrentFanSpeedInRPM,
                MinLevelPercent = c.CurrentMinimumLevel,
                MaxLevelPercent = c.CurrentMaximumLevel,
            }).ToList();

            return new GpuControlSnapshot
            {
                Name = gpu.FullName,
                PowerLimitPercent = policy?.PowerTargetInPercent ?? 100,
                PowerLimitMinPercent = info?.MinimumPowerInPercent ?? 50,
                PowerLimitMaxPercent = info?.MaximumPowerInPercent ?? 100,
                PowerLimitDefaultPercent = info?.DefaultPowerInPercent ?? 100,
                Fans = fans,
            };
        }
        catch (NVIDIAApiException)
        {
            return null;
        }
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
        catch (NVIDIAApiException)
        {
            return false;
        }
    }

    public bool TryRestorePowerLimitDefault()
    {
        if (_gpu is not { } gpu) return false;

        GPUPowerLimitInfo? info = gpu.PerformanceControl.PowerLimitInformation.FirstOrDefault();
        return info is not null && TrySetPowerLimitPercent(info.DefaultPowerInPercent);
    }

    public bool TrySetFanPercent(int coolerId, int percent)
    {
        if (_gpu is not { } gpu) return false;

        try
        {
            gpu.CoolerInformation.SetCoolerSettings(coolerId, Math.Clamp(percent, 0, 100));
            return true;
        }
        catch (NVIDIAApiException)
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
        catch (NVIDIAApiException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (!_initialized) return;

        if (_gpu is not null)
        {
            TryRestorePowerLimitDefault();
            TryRestoreFanAuto();
        }

        try { NVIDIA.Unload(); } catch { /* best-effort */ }
    }
}
