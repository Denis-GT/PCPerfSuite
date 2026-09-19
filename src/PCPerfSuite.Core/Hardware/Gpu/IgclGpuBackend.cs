using System.Runtime.InteropServices;
using System.Text;
using N = PCPerfSuite.Core.Hardware.Gpu.IgclNative;

namespace PCPerfSuite.Core.Hardware.Gpu;

/// <summary>
/// Contrôle des Intel Arc via IGCL : décalage de fréquence cœur, tension, vitesse mémoire, limites de
/// puissance et de température, ventilateurs — les réglages de la page "Performance" de l'Intel
/// Graphics Software. Seules les Arc dédiées (Alchemist, Battlemage) les exposent ; les iGPU Intel
/// répondent "overclocking non supporté" et restent donc non disponibles.
///
/// Deux générations d'API cohabitent : les fonctions "…V2" (pilotes récents, Battlemage), dont l'unité
/// est annoncée par ctlOverclockGetProperties, et les fonctions d'origine, à unités fixes (MHz, mV,
/// GT/s, mW, °C). Pour chaque réglage on retient celle que le pilote accepte, puis tout est converti
/// dans une unité commune avant d'être présenté comme chez les autres marques.
///
/// Intel exige que l'utilisateur accepte une renonciation de garantie avant tout overclock
/// (ctlOverclockWaiverSet) : tant que ce n'est pas fait, les écritures d'overclock sont refusées ici.
/// </summary>
internal sealed class IgclGpuBackend : IGpuTuningBackend
{
    private delegate uint Getter(IntPtr device, out double value);
    private delegate uint Setter(IntPtr device, double value);

    private enum Kind { Unknown, Frequency, Rate, Voltage, Power, Temperature, Percent }

    /// <summary>Un réglage IGCL résolu : sa plage (ctl_oc_control_info_t) et la paire de fonctions
    /// retenue, avec l'unité dans laquelle elles lisent/écrivent.</summary>
    private sealed class Control
    {
        public required N.OcControlInfo Info { get; init; }
        public required Getter Get { get; init; }
        public required Setter Set { get; init; }
        public required int ValueUnits { get; init; }

        public double Min => ToCanonical(Info.Min, Info.Units);
        public double Max => ToCanonical(Info.Max, Info.Units);
        public double Default => ToCanonical(Info.Default, Info.Units);

        public bool TryRead(IntPtr device, out double canonical)
        {
            canonical = 0;
            try
            {
                if (Get(device, out double raw) != N.Success) return false;
                canonical = ToCanonical(raw, ValueUnits);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public bool TryWrite(IntPtr device, double canonical)
        {
            try
            {
                double clamped = Max > Min ? Math.Clamp(canonical, Min, Max) : canonical;
                return Set(device, FromCanonical(clamped, ValueUnits)) == N.Success;
            }
            catch
            {
                return false;
            }
        }
    }

    private IntPtr _api;
    private IntPtr _device;
    private string _name = "Intel Arc";

    private Control? _core;
    private Control? _memory;
    private string _memoryUnit = "MT/s";
    private Control? _voltage;
    private Control? _power;
    private Control? _temperature;

    private readonly List<IntPtr> _fans = new();

    /// <summary>Dernière consigne par ventilateur : l'onglet Ventilateurs la renvoie à chaque relevé,
    /// inutile de la réécrire dans le pilote quand elle n'a pas changé.</summary>
    private readonly Dictionary<int, int> _lastFanPercent = new();

    private bool _waiverSet;

    public GpuVendor Vendor => GpuVendor.Intel;

    public bool RequiresOverclockWaiver => true;

    public bool TryAcceptOverclockWaiver()
    {
        if (_device == IntPtr.Zero) return false;

        try
        {
            _waiverSet = N.ctlOverclockWaiverSet(_device) == N.Success;
        }
        catch
        {
            _waiverSet = false;
        }

        return _waiverSet;
    }

    public bool TryInitialize()
    {
        try
        {
            var args = new N.InitArgs
            {
                Size = (uint)Marshal.SizeOf<N.InitArgs>(),
                Version = 0,
                AppVersion = N.ApiVersion,
                Flags = N.InitFlagUseLevelZero,
            };

            if (N.ctlInit(ref args, out _api) != N.Success || _api == IntPtr.Zero) return false;
            if (!SelectDevice()) return false;

            ResolveControls();
            ResolveFans();

            return _core is not null || _memory is not null || _voltage is not null
                   || _power is not null || _temperature is not null || _fans.Count > 0;
        }
        catch
        {
            // DllNotFoundException (pas de pilote Intel) ou pilote trop ancien.
            return false;
        }
    }

    /// <summary>Retient l'adaptateur Intel qui annonce l'overclocking, en préférant une carte dédiée
    /// (un PC peut avoir un iGPU Intel et une Arc).</summary>
    private unsafe bool SelectDevice()
    {
        uint count = 0;
        if (N.ctlEnumerateDevices(_api, ref count, null) != N.Success || count == 0) return false;

        var devices = new IntPtr[count];
        if (N.ctlEnumerateDevices(_api, ref count, devices) != N.Success) return false;

        IntPtr luid = Marshal.AllocHGlobal(8);
        try
        {
            bool bestIsDiscrete = false;

            foreach (IntPtr device in devices.Take((int)count))
            {
                if (device == IntPtr.Zero) continue;

                var properties = new N.DeviceAdapterProperties
                {
                    Size = (uint)sizeof(N.DeviceAdapterProperties),
                    Version = 2,
                    DeviceId = luid,
                    DeviceIdSize = 8,
                };

                if (N.ctlGetDeviceProperties(device, ref properties) != N.Success) continue;
                if (properties.DeviceType != N.DeviceTypeGraphics || properties.PciVendorId != 0x8086) continue;

                var oc = ReadOcProperties(device);
                if (oc is not { Supported: not 0 }) continue;

                bool discrete = (properties.GraphicsAdapterProperties & N.AdapterFlagIntegrated) == 0;
                if (_device != IntPtr.Zero && (bestIsDiscrete || !discrete)) continue;

                _device = device;
                bestIsDiscrete = discrete;
                _name = ReadName(properties.Name, 100) ?? _name;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(luid);
        }

        return _device != IntPtr.Zero;
    }

    private static unsafe string? ReadName(byte* name, int length)
    {
        int end = 0;
        while (end < length && name[end] != 0) end++;
        string value = Encoding.UTF8.GetString(name, end).Trim();
        return value.Length > 0 ? value : null;
    }

    private static N.OcProperties? ReadOcProperties(IntPtr device)
    {
        var properties = new N.OcProperties { Size = (uint)Marshal.SizeOf<N.OcProperties>(), Version = 1 };
        try
        {
            return N.ctlOverclockGetProperties(device, ref properties) == N.Success ? properties : null;
        }
        catch
        {
            return null;
        }
    }

    private void ResolveControls()
    {
        if (ReadOcProperties(_device) is not { } oc) return;

        _core = Resolve(oc.GpuFrequencyOffset,
            N.ctlOverclockGpuFrequencyOffsetGetV2, N.ctlOverclockGpuFrequencyOffsetSetV2,
            N.ctlOverclockGpuFrequencyOffsetGet, N.ctlOverclockGpuFrequencyOffsetSet, N.UnitsFrequencyMhz);

        // Battlemage : limite de vitesse mémoire (V2) ; Alchemist : décalage de fréquence VRAM (V1),
        // souvent verrouillé par le pilote — il reste alors masqué.
        _memory = Resolve(oc.VramMemSpeedLimit,
            N.ctlOverclockVramMemSpeedLimitGetV2, N.ctlOverclockVramMemSpeedLimitSetV2, null, null, 0);
        if (_memory is not null)
        {
            _memoryUnit = "Mbps";
        }
        else
        {
            _memory = Resolve(oc.VramFrequencyOffset, null, null,
                N.ctlOverclockVramFrequencyOffsetGet, N.ctlOverclockVramFrequencyOffsetSet, N.UnitsOperationsGts);
            _memoryUnit = "MT/s";
        }

        _voltage = Resolve(oc.GpuVoltageOffset,
            N.ctlOverclockGpuMaxVoltageOffsetGetV2, N.ctlOverclockGpuMaxVoltageOffsetSetV2,
            N.ctlOverclockGpuVoltageOffsetGet, N.ctlOverclockGpuVoltageOffsetSet, N.UnitsVoltageMillivolts);

        _power = Resolve(oc.PowerLimit,
            N.ctlOverclockPowerLimitGetV2, N.ctlOverclockPowerLimitSetV2,
            N.ctlOverclockPowerLimitGet, N.ctlOverclockPowerLimitSet, N.UnitsPowerMilliwatts);

        _temperature = Resolve(oc.TemperatureLimit,
            N.ctlOverclockTemperatureLimitGetV2, N.ctlOverclockTemperatureLimitSetV2,
            N.ctlOverclockTemperatureLimitGet, N.ctlOverclockTemperatureLimitSet, N.UnitsTemperatureCelsius);
        if (_temperature is not null && KindOf(_temperature.ValueUnits) != Kind.Temperature) _temperature = null;
    }

    /// <summary>Retient la V2 si le pilote y répond (unité = celle des propriétés), sinon la V1 (unité
    /// fixe). Écarté si l'unité de la valeur ne correspond pas à celle de la plage annoncée.</summary>
    private Control? Resolve(N.OcControlInfo info, Getter? getV2, Setter? setV2, Getter? getV1, Setter? setV1, int v1Units)
    {
        if (!info.IsSupported || KindOf(info.Units) == Kind.Unknown) return null;

        if (getV2 is not null && setV2 is not null && Probe(getV2))
        {
            return new Control { Info = info, Get = getV2, Set = setV2, ValueUnits = info.Units };
        }

        if (getV1 is not null && setV1 is not null && Probe(getV1) && KindOf(v1Units) == KindOf(info.Units))
        {
            return new Control { Info = info, Get = getV1, Set = setV1, ValueUnits = v1Units };
        }

        return null;
    }

    private bool Probe(Getter getter)
    {
        try
        {
            return getter(_device, out _) == N.Success;
        }
        catch
        {
            // EntryPointNotFoundException : fonction absente de ce pilote.
            return false;
        }
    }

    private void ResolveFans()
    {
        try
        {
            uint count = 0;
            if (N.ctlEnumFans(_device, ref count, null) != N.Success || count == 0) return;

            var fans = new IntPtr[count];
            if (N.ctlEnumFans(_device, ref count, fans) != N.Success) return;

            foreach (IntPtr fan in fans.Take((int)count))
            {
                var properties = new N.FanProperties { Size = (uint)Marshal.SizeOf<N.FanProperties>() };
                if (fan != IntPtr.Zero && N.ctlFanGetProperties(fan, ref properties) == N.Success && properties.CanControl != 0)
                {
                    _fans.Add(fan);
                }
            }
        }
        catch
        {
            _fans.Clear();
        }
    }

    // --- Unités ------------------------------------------------------------------------------------

    private static Kind KindOf(int units) => units switch
    {
        N.UnitsFrequencyMhz => Kind.Frequency,
        N.UnitsOperationsGts or N.UnitsOperationsMts or N.UnitsMemSpeedGbps => Kind.Rate,
        3 or N.UnitsVoltageMillivolts => Kind.Voltage,
        N.UnitsPowerWatts or N.UnitsPowerMilliwatts => Kind.Power,
        N.UnitsTemperatureCelsius => Kind.Temperature,
        N.UnitsPercent => Kind.Percent,
        _ => Kind.Unknown,
    };

    /// <summary>Unité commune : MHz, MT/s (ou Mbps), mV, W, °C, %.</summary>
    private static double ToCanonical(double value, int units) => units switch
    {
        N.UnitsOperationsGts or N.UnitsMemSpeedGbps => value * 1000,
        3 => value * 1000, // volts → mV
        N.UnitsPowerMilliwatts => value / 1000,
        _ => value,
    };

    private static double FromCanonical(double value, int units) => units switch
    {
        N.UnitsOperationsGts or N.UnitsMemSpeedGbps => value / 1000,
        3 => value / 1000,
        N.UnitsPowerMilliwatts => value * 1000,
        _ => value,
    };

    // --- Instantanés -------------------------------------------------------------------------------

    public GpuControlSnapshot? GetSnapshot()
    {
        if (_device == IntPtr.Zero) return null;

        var power = ReadPower();
        return new GpuControlSnapshot
        {
            Name = _name,
            Vendor = GpuVendor.Intel,
            PowerLimitSupported = power.Ok,
            PowerLimitPercent = power.Percent,
            PowerLimitMinPercent = power.Min,
            PowerLimitMaxPercent = power.Max,
            PowerLimitDefaultPercent = 100,
            Fans = _fans.Select((_, i) => new GpuFanInfo { CoolerId = i }).ToList(),
        };
    }

    /// <summary>Limite de puissance présentée en % de la valeur d'usine, quelle que soit l'unité du
    /// pilote (mW, W, ou déjà en %).</summary>
    private (bool Ok, float Percent, float Min, float Max) ReadPower()
    {
        if (_power is null || !_power.TryRead(_device, out double value)) return (false, 100, 50, 100);

        return (true, PowerToPercent(value), PowerToPercent(_power.Min), PowerToPercent(_power.Max));
    }

    private float PowerToPercent(double canonical)
    {
        if (_power is null) return 100;
        if (_power.Info.Relative != 0) return (float)(100 + canonical - _power.Default);
        return _power.Default > 0 ? (float)(canonical / _power.Default * 100) : 100;
    }

    private double PercentToPower(float percent)
    {
        if (_power is null) return 0;
        if (_power.Info.Relative != 0) return _power.Default + percent - 100;
        return _power.Default * percent / 100;
    }

    public bool TrySetPowerLimitPercent(float percent)
        => _power is not null && _waiverSet && _power.TryWrite(_device, PercentToPower(percent));

    public bool TryRestorePowerLimitDefault()
        => _power is not null && _waiverSet && _power.TryWrite(_device, _power.Default);

    public GpuOverclockSnapshot? GetOverclock()
    {
        if (_device == IntPtr.Zero) return null;

        var core = ReadOffset(_core);
        var memory = ReadOffset(_memory);

        bool voltageOk = false;
        double voltage = 0;
        if (_voltage is not null) voltageOk = _voltage.TryRead(_device, out voltage);

        bool temperatureOk = false;
        double temperature = 0;
        if (_temperature is not null) temperatureOk = _temperature.TryRead(_device, out temperature);

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
            MemoryOffsetUnit = _memoryUnit,

            TemperatureLimitSupported = temperatureOk && _temperature!.Max > _temperature.Min,
            TemperatureLimitC = (int)Math.Round(temperature),
            TemperatureLimitMinC = (int)Math.Round(_temperature?.Min ?? 0),
            TemperatureLimitMaxC = (int)Math.Round(_temperature?.Max ?? 0),
            TemperatureLimitDefaultC = (int)Math.Round(_temperature?.Default ?? 0),

            VoltageSupported = voltageOk,
            Voltage = (int)Math.Round(voltage),
            VoltageMin = (int)Math.Round(_voltage?.Min ?? 0),
            VoltageMax = (int)Math.Round(_voltage?.Max ?? 0),
            VoltageDefault = (int)Math.Round(_voltage?.Default ?? 0),
            VoltageUnit = _voltage is not null && KindOf(_voltage.ValueUnits) == Kind.Percent
                ? GpuVoltageUnit.Percent
                : GpuVoltageUnit.Millivolts,
            VoltageIsOffset = true,
        };
    }

    /// <summary>Décalage par rapport à la valeur d'usine : identique à la valeur brute pour un réglage
    /// déjà relatif (usine = 0), et valable aussi pour une limite absolue (vitesse mémoire V2).</summary>
    private (bool Ok, int Value, int Min, int Max) ReadOffset(Control? control)
    {
        if (control is null || control.Max <= control.Min || !control.TryRead(_device, out double value))
        {
            return (false, 0, 0, 0);
        }

        double factory = control.Default;
        return (true, (int)Math.Round(value - factory), (int)Math.Round(control.Min - factory), (int)Math.Round(control.Max - factory));
    }

    public GpuPerformanceLimit? GetActiveLimit() => null;

    public bool TrySetClockOffsets(int coreMhz, int memoryMhz)
    {
        if (!_waiverSet || (_core is null && _memory is null)) return false;

        bool ok = true;
        if (_core is not null) ok &= _core.TryWrite(_device, _core.Default + coreMhz);
        if (_memory is not null) ok &= _memory.TryWrite(_device, _memory.Default + memoryMhz);
        return ok;
    }

    public bool TrySetTemperatureLimit(int celsius)
        => _temperature is not null && _waiverSet && _temperature.TryWrite(_device, celsius);

    public bool TrySetVoltage(int value)
        => _voltage is not null && _waiverSet && _voltage.TryWrite(_device, value);

    public void RestoreOverclockDefaults()
    {
        if (_device == IntPtr.Zero) return;

        try
        {
            if (N.ctlOverclockResetToDefault(_device) == N.Success) return;
        }
        catch
        {
            // Pilote sans ctlOverclockResetToDefault : on remet chaque réglage à sa valeur d'usine.
        }

        if (!_waiverSet) return;
        _core?.TryWrite(_device, _core.Default);
        _memory?.TryWrite(_device, _memory.Default);
        _voltage?.TryWrite(_device, _voltage.Default);
        _temperature?.TryWrite(_device, _temperature.Default);
        _power?.TryWrite(_device, _power.Default);
    }

    public bool TrySetFanPercent(int coolerId, int percent)
    {
        if (coolerId < 0 || coolerId >= _fans.Count) return false;
        if (_lastFanPercent.TryGetValue(coolerId, out int last) && last == percent) return true;

        try
        {
            var speed = new N.FanSpeed
            {
                Size = (uint)Marshal.SizeOf<N.FanSpeed>(),
                Speed = Math.Clamp(percent, 0, 100),
                Units = N.FanSpeedUnitsPercent,
            };

            bool ok = N.ctlFanSetFixedSpeedMode(_fans[coolerId], ref speed) == N.Success;
            if (ok) _lastFanPercent[coolerId] = percent;
            else _lastFanPercent.Remove(coolerId);
            return ok;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Rend tous les ventilateurs au pilote : IGCL n'a qu'un mode par défaut par ventilateur,
    /// et l'onglet Ventilateurs demande "auto" pour la carte entière (comme NVAPI).</summary>
    public bool TryRestoreFanAuto()
    {
        _lastFanPercent.Clear();
        if (_fans.Count == 0) return false;

        try
        {
            return _fans.All(fan => N.ctlFanSetDefaultMode(fan) == N.Success);
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        _fans.Clear();
        _device = IntPtr.Zero;

        if (_api == IntPtr.Zero) return;

        try { N.ctlClose(_api); } catch { /* best-effort */ }
        _api = IntPtr.Zero;
    }
}
