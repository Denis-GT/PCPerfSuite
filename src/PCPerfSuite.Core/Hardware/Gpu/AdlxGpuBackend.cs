using N = PCPerfSuite.Core.Hardware.Gpu.AdlxNative;

namespace PCPerfSuite.Core.Hardware.Gpu;

/// <summary>
/// Contrôle des Radeon via ADLX, les mêmes réglages que la page "Réglage" d'AMD Adrenalin : fréquence
/// max du cœur, tension, fréquence max de la VRAM, limite de puissance et ventilateur. Couvre les
/// Radeon RDNA (RX 5000 à RX 9000) ; les GCN/Vega et la plupart des GPU intégrés n'exposent pas ces
/// réglages et restent donc "non disponibles".
///
/// Adrenalin raisonne en valeurs absolues (fréquence max 2 600 MHz, tension 1 150 mV) jusqu'à RDNA 3,
/// puis en décalages à partir de RDNA 4 (RX 9000). Pour garder les mêmes curseurs que chez NVIDIA, on
/// présente toujours un décalage par rapport à la valeur d'usine : décalage = valeur − défaut. Les
/// valeurs d'usine viennent des interfaces "…2_1"/"…1" quand le pilote les fournit ; sinon on retient
/// la valeur lue au démarrage (ou 0 quand la plage entoure 0, signe d'un réglage déjà en décalage).
///
/// ADLX n'expose ni limite de température ni raison du bridage : ces blocs sont signalés non
/// disponibles plutôt que simulés.
/// </summary>
internal sealed class AdlxGpuBackend : IGpuTuningBackend
{
    private IntPtr _system;
    private IntPtr _tuning;
    private IntPtr _gpu;
    private IntPtr _gfx;
    private IntPtr _gfxDefaults;
    private IntPtr _vram;
    private IntPtr _vramDefaults;
    private IntPtr _power;
    private IntPtr _powerDefaults;
    private IntPtr _fan;

    private string _name = "GPU AMD";

    private int _coreDefault;
    private int _memoryDefault;
    private int _voltageDefault;
    private int _powerDefault;

    /// <summary>Courbe de ventilation d'origine, relevée au démarrage : ADLX n'a pas de mode "auto" à
    /// réactiver, on rend donc au pilote la courbe qu'il avait.</summary>
    private List<(int Temperature, int Speed)>? _originalFanStates;

    /// <summary>Dernière consigne envoyée : l'onglet Ventilateurs la renvoie à chaque relevé, inutile de
    /// réécrire toute la courbe dans le pilote quand elle n'a pas changé.</summary>
    private int? _lastFanPercent;

    public GpuVendor Vendor => GpuVendor.Amd;

    public bool RequiresOverclockWaiver => false;

    public bool TryAcceptOverclockWaiver() => true;

    public bool TryInitialize()
    {
        try
        {
            _system = N.TryInitialize();
            if (_system == IntPtr.Zero) return false;

            _tuning = N.GetObject(_system, N.SystemIface.GetGPUTuningServices);
            if (_tuning == IntPtr.Zero || !SelectGpu()) return false;

            _name = N.GetString(_gpu, N.Gpu.Name) ?? _name;

            if (N.IsSupportedForGpu(_tuning, N.TuningServices.IsSupportedManualGfxTuning, _gpu))
            {
                (_gfx, _gfxDefaults) = GetTuning(N.TuningServices.GetManualGfxTuning,
                    "IADLXManualGraphicsTuning2", "IADLXManualGraphicsTuning2_1");
            }

            if (N.IsSupportedForGpu(_tuning, N.TuningServices.IsSupportedManualVramTuning, _gpu))
            {
                (_vram, _vramDefaults) = GetTuning(N.TuningServices.GetManualVramTuning,
                    "IADLXManualVRAMTuning2", "IADLXManualVRAMTuning2_1");
            }

            if (N.IsSupportedForGpu(_tuning, N.TuningServices.IsSupportedManualPowerTuning, _gpu))
            {
                (_power, _powerDefaults) = GetTuning(N.TuningServices.GetManualPowerTuning,
                    "IADLXManualPowerTuning", "IADLXManualPowerTuning1");
            }

            if (N.IsSupportedForGpu(_tuning, N.TuningServices.IsSupportedManualFanTuning, _gpu))
            {
                (_fan, _) = GetTuning(N.TuningServices.GetManualFanTuning, "IADLXManualFanTuning", null);
            }

            if (_gfx == IntPtr.Zero && _vram == IntPtr.Zero && _power == IntPtr.Zero && _fan == IntPtr.Zero)
            {
                return false;
            }

            CaptureDefaults();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Retient le GPU à piloter : le premier GPU dédié qui accepte au moins un réglage manuel,
    /// à défaut un GPU intégré qui en accepte (certaines APU récentes).</summary>
    private bool SelectGpu()
    {
        IntPtr list = N.GetObject(_system, N.SystemIface.GetGPUs);
        if (list == IntPtr.Zero) return false;

        try
        {
            IntPtr best = IntPtr.Zero;
            bool bestIsDiscrete = false;

            uint count = N.GetSize(list, N.GpuList.Size);
            for (uint i = 0; i < count; i++)
            {
                IntPtr gpu = N.GetAt(list, N.GpuList.AtGpu, i);
                if (gpu == IntPtr.Zero) continue;

                bool tunable = N.IsSupportedForGpu(_tuning, N.TuningServices.IsSupportedManualGfxTuning, gpu)
                               || N.IsSupportedForGpu(_tuning, N.TuningServices.IsSupportedManualPowerTuning, gpu)
                               || N.IsSupportedForGpu(_tuning, N.TuningServices.IsSupportedManualVramTuning, gpu)
                               || N.IsSupportedForGpu(_tuning, N.TuningServices.IsSupportedManualFanTuning, gpu);
                bool discrete = N.TryGetInt(gpu, N.Gpu.Type, out int type) && type == N.GpuTypeDiscrete;

                if (tunable && (best == IntPtr.Zero || (discrete && !bestIsDiscrete)))
                {
                    N.Release(best);
                    best = gpu;
                    bestIsDiscrete = discrete;
                }
                else
                {
                    N.Release(gpu);
                }
            }

            _gpu = best;
            return best != IntPtr.Zero;
        }
        finally
        {
            N.Release(list);
        }
    }

    /// <summary>Récupère une interface de réglage et, si le pilote la fournit, son extension qui donne
    /// les valeurs d'usine.</summary>
    private (IntPtr Main, IntPtr Defaults) GetTuning(int getterSlot, string interfaceId, string? defaultsInterfaceId)
    {
        IntPtr raw = N.GetObjectForGpu(_tuning, getterSlot, _gpu);
        if (raw == IntPtr.Zero) return (IntPtr.Zero, IntPtr.Zero);

        try
        {
            IntPtr main = N.QueryInterface(raw, interfaceId);
            IntPtr defaults = main != IntPtr.Zero && defaultsInterfaceId is not null
                ? N.QueryInterface(raw, defaultsInterfaceId)
                : IntPtr.Zero;
            return (main, defaults);
        }
        finally
        {
            N.Release(raw);
        }
    }

    private void CaptureDefaults()
    {
        _coreDefault = ReadDefault(_gfx, _gfxDefaults, N.GfxTuning2.GetMaxFrequencyRange,
            N.GfxTuning2.GetMaxFrequency, N.GfxTuning2.GetMaxFrequencyDefault);
        _voltageDefault = ReadDefault(_gfx, _gfxDefaults, N.GfxTuning2.GetVoltageRange,
            N.GfxTuning2.GetVoltage, N.GfxTuning2.GetVoltageDefault);
        _memoryDefault = ReadDefault(_vram, _vramDefaults, N.VramTuning2.GetMaxFrequencyRange,
            N.VramTuning2.GetMaxFrequency, N.VramTuning2.GetMaxFrequencyDefault);
        _powerDefault = ReadDefault(_power, _powerDefaults, N.PowerTuning.GetPowerLimitRange,
            N.PowerTuning.GetPowerLimit, N.PowerTuning.GetPowerLimitDefault);

        if (_fan != IntPtr.Zero) _originalFanStates = ReadFanStates();
    }

    private static int ReadDefault(IntPtr tuning, IntPtr defaults, int rangeSlot, int valueSlot, int defaultSlot)
    {
        if (tuning == IntPtr.Zero) return 0;
        if (defaults != IntPtr.Zero && N.TryGetInt(defaults, defaultSlot, out int factory)) return factory;

        // Plage autour de 0 : le réglage est déjà un décalage (RDNA 4), l'usine vaut 0.
        if (N.TryGetRange(tuning, rangeSlot, out N.IntRange range) && range.Min <= 0 && range.Max >= 0) return 0;

        return N.TryGetInt(tuning, valueSlot, out int current) ? current : 0;
    }

    public GpuControlSnapshot? GetSnapshot()
    {
        if (_gpu == IntPtr.Zero) return null;

        try
        {
            var power = ReadPower();

            return new GpuControlSnapshot
            {
                Name = _name,
                Vendor = GpuVendor.Amd,
                PowerLimitSupported = power.Ok,
                PowerLimitPercent = power.Percent,
                PowerLimitMinPercent = power.Min,
                PowerLimitMaxPercent = power.Max,
                PowerLimitDefaultPercent = 100,
                Fans = _fan != IntPtr.Zero ? new[] { new GpuFanInfo { CoolerId = 0 } } : Array.Empty<GpuFanInfo>(),
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>La limite de puissance ADLX est un écart en % autour de la valeur d'usine (ex. −10 à +15) :
    /// on l'affiche en % absolu (90 à 115 %) comme les autres marques.</summary>
    private (bool Ok, float Percent, float Min, float Max) ReadPower()
    {
        if (_power == IntPtr.Zero
            || !N.TryGetRange(_power, N.PowerTuning.GetPowerLimitRange, out N.IntRange range)
            || !N.TryGetInt(_power, N.PowerTuning.GetPowerLimit, out int current))
        {
            return (false, 100, 50, 100);
        }

        return (true, ToPercent(current), ToPercent(range.Min), ToPercent(range.Max));
    }

    private float ToPercent(int raw) => 100 + raw - _powerDefault;

    public bool TrySetPowerLimitPercent(float percent)
    {
        if (_power == IntPtr.Zero || !N.TryGetRange(_power, N.PowerTuning.GetPowerLimitRange, out N.IntRange range)) return false;

        int raw = Math.Clamp((int)Math.Round(percent - 100) + _powerDefault, range.Min, range.Max);
        return N.TrySetInt(_power, N.PowerTuning.SetPowerLimit, raw);
    }

    public bool TryRestorePowerLimitDefault()
        => _power != IntPtr.Zero && N.TrySetInt(_power, N.PowerTuning.SetPowerLimit, _powerDefault);

    public GpuOverclockSnapshot? GetOverclock()
    {
        if (_gpu == IntPtr.Zero) return null;

        try
        {
            var core = ReadOffset(_gfx, N.GfxTuning2.GetMaxFrequencyRange, N.GfxTuning2.GetMaxFrequency, _coreDefault);
            var memory = ReadOffset(_vram, N.VramTuning2.GetMaxFrequencyRange, N.VramTuning2.GetMaxFrequency, _memoryDefault);
            var voltage = ReadVoltage();

            return new GpuOverclockSnapshot
            {
                CoreOffsetSupported = core.Ok,
                CoreOffsetMhz = core.Value,
                CoreOffsetMinMhz = core.Min,
                CoreOffsetMaxMhz = core.Max,
                MemoryOffsetSupported = memory.Ok,
                MemoryOffsetMhz = memory.Value,
                MemoryOffsetMinMhz = memory.Min,
                MemoryOffsetMaxMhz = memory.Max,

                TemperatureLimitSupported = false,

                VoltageSupported = voltage.Ok,
                Voltage = voltage.Value,
                VoltageMin = voltage.Min,
                VoltageMax = voltage.Max,
                VoltageDefault = _voltageDefault,
                VoltageUnit = GpuVoltageUnit.Millivolts,
                VoltageIsOffset = voltage.IsOffset,
            };
        }
        catch
        {
            return null;
        }
    }

    private static (bool Ok, int Value, int Min, int Max) ReadOffset(IntPtr tuning, int rangeSlot, int valueSlot, int factory)
    {
        if (tuning == IntPtr.Zero
            || !N.TryGetRange(tuning, rangeSlot, out N.IntRange range)
            || !N.TryGetInt(tuning, valueSlot, out int current))
        {
            return (false, 0, 0, 0);
        }

        return (true, current - factory, range.Min - factory, range.Max - factory);
    }

    /// <summary>Tension brute du pilote : absolue jusqu'à RDNA 3 (plage type 700–1 150 mV), en décalage à
    /// partir de RDNA 4 (plage autour de 0). Affichée telle quelle avec son unité, sans conversion.</summary>
    private (bool Ok, int Value, int Min, int Max, bool IsOffset) ReadVoltage()
    {
        if (_gfx == IntPtr.Zero
            || !N.TryGetRange(_gfx, N.GfxTuning2.GetVoltageRange, out N.IntRange range)
            || !N.TryGetInt(_gfx, N.GfxTuning2.GetVoltage, out int current))
        {
            return (false, 0, 0, 0, false);
        }

        return (true, current, range.Min, range.Max, range.Min <= 0);
    }

    public GpuPerformanceLimit? GetActiveLimit() => null;

    public bool TrySetClockOffsets(int coreMhz, int memoryMhz)
    {
        bool ok = true;

        if (_gfx != IntPtr.Zero)
        {
            ok &= TrySetOffset(_gfx, N.GfxTuning2.GetMaxFrequencyRange, N.GfxTuning2.SetMaxFrequency, _coreDefault, coreMhz);
        }

        if (_vram != IntPtr.Zero)
        {
            ok &= TrySetOffset(_vram, N.VramTuning2.GetMaxFrequencyRange, N.VramTuning2.SetMaxFrequency, _memoryDefault, memoryMhz);
        }

        return ok && (_gfx != IntPtr.Zero || _vram != IntPtr.Zero);
    }

    private static bool TrySetOffset(IntPtr tuning, int rangeSlot, int setSlot, int factory, int offset)
    {
        if (!N.TryGetRange(tuning, rangeSlot, out N.IntRange range)) return false;
        return N.TrySetInt(tuning, setSlot, Math.Clamp(factory + offset, range.Min, range.Max));
    }

    public bool TrySetTemperatureLimit(int celsius) => false;

    public bool TrySetVoltage(int value)
    {
        if (_gfx == IntPtr.Zero || !N.TryGetRange(_gfx, N.GfxTuning2.GetVoltageRange, out N.IntRange range)) return false;
        return N.TrySetInt(_gfx, N.GfxTuning2.SetVoltage, Math.Clamp(value, range.Min, range.Max));
    }

    /// <summary>Réinitialisation d'usine d'Adrenalin : horloges, tension, puissance et ventilateur d'un coup.</summary>
    public void RestoreOverclockDefaults()
    {
        if (_gpu == IntPtr.Zero) return;

        try
        {
            if (!N.CallForGpu(_tuning, N.TuningServices.ResetToFactory, _gpu))
            {
                TrySetClockOffsets(0, 0);
                if (_gfx != IntPtr.Zero) TrySetVoltage(_voltageDefault);
                TryRestorePowerLimitDefault();
            }

            _lastFanPercent = null;
        }
        catch
        {
            // best-effort
        }
    }

    public bool TrySetFanPercent(int coolerId, int percent)
    {
        if (_fan == IntPtr.Zero) return false;
        if (_lastFanPercent == percent) return true;

        try
        {
            if (!N.TryGetTwoRanges(_fan, N.FanTuning.GetFanTuningRanges, out N.IntRange speedRange, out _)) return false;
            int speed = speedRange.Max > speedRange.Min ? Math.Clamp(percent, speedRange.Min, speedRange.Max) : percent;

            // Vitesse fixe = tous les points de la courbe du pilote à la même vitesse.
            bool ok = WriteFanStates(_ => speed);
            _lastFanPercent = ok ? percent : null;
            return ok;
        }
        catch
        {
            return false;
        }
    }

    public bool TryRestoreFanAuto()
    {
        _lastFanPercent = null;
        if (_fan == IntPtr.Zero || _originalFanStates is not { Count: > 0 } original) return false;

        try
        {
            return WriteFanStates(i => i < original.Count ? original[i].Speed : original[^1].Speed);
        }
        catch
        {
            return false;
        }
    }

    private List<(int Temperature, int Speed)>? ReadFanStates()
    {
        IntPtr list = N.GetObject(_fan, N.FanTuning.GetFanTuningStates);
        if (list == IntPtr.Zero) return null;

        try
        {
            var states = new List<(int, int)>();
            uint count = N.GetSize(list, N.FanStateList.Size);
            for (uint i = 0; i < count; i++)
            {
                IntPtr state = N.GetAt(list, N.FanStateList.AtState, i);
                if (state == IntPtr.Zero) continue;

                N.TryGetInt(state, N.FanState.GetTemperature, out int temperature);
                N.TryGetInt(state, N.FanState.GetFanSpeed, out int speed);
                states.Add((temperature, speed));
                N.Release(state);
            }

            return states;
        }
        finally
        {
            N.Release(list);
        }
    }

    /// <summary>Réécrit la vitesse de chaque point de la courbe du pilote (les températures restent
    /// celles du pilote), après validation par ADLX.</summary>
    private bool WriteFanStates(Func<int, int> speedAt)
    {
        IntPtr list = N.GetObject(_fan, N.FanTuning.GetFanTuningStates);
        if (list == IntPtr.Zero) return false;

        try
        {
            uint count = N.GetSize(list, N.FanStateList.Size);
            for (uint i = 0; i < count; i++)
            {
                IntPtr state = N.GetAt(list, N.FanStateList.AtState, i);
                if (state == IntPtr.Zero) continue;

                N.TrySetInt(state, N.FanState.SetFanSpeed, speedAt((int)i));
                N.Release(state);
            }

            return N.IsValidWithList(_fan, N.FanTuning.IsValidFanTuningStates, list)
                   && N.CallWithObject(_fan, N.FanTuning.SetFanTuningStates, list);
        }
        finally
        {
            N.Release(list);
        }
    }

    public void Dispose()
    {
        try
        {
            foreach (IntPtr iface in new[] { _fan, _power, _powerDefaults, _vram, _vramDefaults, _gfx, _gfxDefaults, _gpu, _tuning })
            {
                N.Release(iface);
            }

            _fan = _power = _powerDefaults = _vram = _vramDefaults = _gfx = _gfxDefaults = _gpu = _tuning = IntPtr.Zero;

            if (_system != IntPtr.Zero)
            {
                _system = IntPtr.Zero;
                N.Terminate();
            }
        }
        catch
        {
            // best-effort
        }
    }
}
