using System.Runtime.InteropServices;

namespace PCPerfSuite.Core.Hardware.Gpu;

/// <summary>
/// Déclarations P/Invoke d'IGCL (Intel Graphics Control Library), l'API qu'Intel livre avec le pilote
/// Arc (ControlLib.dll dans System32) et qu'utilisent l'Intel Graphics Software et Arc OC Tool.
///
/// Les structures reprennent octet pour octet celles de include/igcl_api.h (dépôt
/// intel/drivers.gpu.control-library) ; les "bool" C++ y font 1 octet, d'où des champs byte.
/// Les fonctions "…V2" n'existent que dans les pilotes récents (Battlemage) : un appel peut lever
/// EntryPointNotFoundException, que le backend traite comme "pas de V2, on tente la V1".
/// </summary>
internal static class IgclNative
{
    private const string Dll = "ControlLib.dll";

    public const uint Success = 0;

    // ctl_units_t
    public const int UnitsFrequencyMhz = 0;
    public const int UnitsOperationsGts = 1;
    public const int UnitsOperationsMts = 2;
    public const int UnitsPowerWatts = 4;
    public const int UnitsTemperatureCelsius = 5;
    public const int UnitsPowerMilliwatts = 10;
    public const int UnitsPercent = 11;
    public const int UnitsMemSpeedGbps = 12;
    public const int UnitsVoltageMillivolts = 13;

    public const uint DeviceTypeGraphics = 1;
    public const uint AdapterFlagIntegrated = 1;
    public const uint InitFlagUseLevelZero = 1;
    public const int FanSpeedUnitsPercent = 1;

    /// <summary>CTL_MAKE_VERSION(1, 1) — version de l'API visée par ces déclarations.</summary>
    public const uint ApiVersion = (1u << 16) | 1u;

    [StructLayout(LayoutKind.Sequential)]
    public struct InitArgs
    {
        public uint Size;
        public byte Version;
        public uint AppVersion;
        public uint Flags;
        public uint SupportedVersion;
        public Guid ApplicationUid;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct OcControlInfo
    {
        public byte Supported;
        public byte Relative;
        public byte Reference;
        public int Units;
        public double Min;
        public double Max;
        public double Step;
        public double Default;
        public double ReferenceValue;

        public bool IsSupported => Supported != 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct OcProperties
    {
        public uint Size;
        public byte Version;
        public byte Supported;
        public OcControlInfo GpuFrequencyOffset;
        public OcControlInfo GpuVoltageOffset;
        public OcControlInfo VramFrequencyOffset;
        public OcControlInfo VramVoltageOffset;
        public OcControlInfo PowerLimit;
        public OcControlInfo TemperatureLimit;
        public OcControlInfo VramMemSpeedLimit;
        public OcControlInfo GpuVfCurveVoltageLimit;
        public OcControlInfo GpuVfCurveFrequencyLimit;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct DeviceAdapterProperties
    {
        public uint Size;
        public byte Version;
        public IntPtr DeviceId;
        public uint DeviceIdSize;
        public uint DeviceType;
        public uint SupportedSubfunctionFlags;
        public ulong DriverVersion;
        public ulong FirmwareMajor;
        public ulong FirmwareMinor;
        public ulong FirmwareBuild;
        public uint PciVendorId;
        public uint PciDeviceId;
        public uint RevId;
        public uint NumEusPerSubSlice;
        public uint NumSubSlicesPerSlice;
        public uint NumSlices;
        public fixed byte Name[100];
        public uint GraphicsAdapterProperties;
        public uint Frequency;
        public ushort PciSubsysId;
        public ushort PciSubsysVendorId;
        public byte Bus;
        public byte Device;
        public byte Function;
        public uint NumXeCores;
        public fixed byte Reserved[108];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FanSpeed
    {
        public uint Size;
        public byte Version;
        public int Speed;
        public int Units;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FanProperties
    {
        public uint Size;
        public byte Version;
        public byte CanControl;
        public uint SupportedModes;
        public uint SupportedUnits;
        public int MaxRpm;
        public int MaxPoints;
    }

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern uint ctlInit(ref InitArgs initDesc, out IntPtr apiHandle);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern uint ctlClose(IntPtr apiHandle);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern uint ctlEnumerateDevices(IntPtr apiHandle, ref uint count, [Out] IntPtr[]? devices);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern uint ctlGetDeviceProperties(IntPtr device, ref DeviceAdapterProperties properties);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern uint ctlOverclockGetProperties(IntPtr device, ref OcProperties properties);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern uint ctlOverclockWaiverSet(IntPtr device);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern uint ctlOverclockResetToDefault(IntPtr device);

    // --- Fréquence cœur ---
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern uint ctlOverclockGpuFrequencyOffsetGetV2(IntPtr device, out double value);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern uint ctlOverclockGpuFrequencyOffsetSetV2(IntPtr device, double value);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern uint ctlOverclockGpuFrequencyOffsetGet(IntPtr device, out double value);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern uint ctlOverclockGpuFrequencyOffsetSet(IntPtr device, double value);

    // --- Tension cœur ---
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern uint ctlOverclockGpuMaxVoltageOffsetGetV2(IntPtr device, out double value);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern uint ctlOverclockGpuMaxVoltageOffsetSetV2(IntPtr device, double value);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern uint ctlOverclockGpuVoltageOffsetGet(IntPtr device, out double value);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern uint ctlOverclockGpuVoltageOffsetSet(IntPtr device, double value);

    // --- Mémoire ---
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern uint ctlOverclockVramMemSpeedLimitGetV2(IntPtr device, out double value);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern uint ctlOverclockVramMemSpeedLimitSetV2(IntPtr device, double value);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern uint ctlOverclockVramFrequencyOffsetGet(IntPtr device, out double value);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern uint ctlOverclockVramFrequencyOffsetSet(IntPtr device, double value);

    // --- Limite de puissance ---
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern uint ctlOverclockPowerLimitGetV2(IntPtr device, out double value);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern uint ctlOverclockPowerLimitSetV2(IntPtr device, double value);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern uint ctlOverclockPowerLimitGet(IntPtr device, out double value);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern uint ctlOverclockPowerLimitSet(IntPtr device, double value);

    // --- Limite de température ---
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern uint ctlOverclockTemperatureLimitGetV2(IntPtr device, out double value);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern uint ctlOverclockTemperatureLimitSetV2(IntPtr device, double value);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern uint ctlOverclockTemperatureLimitGet(IntPtr device, out double value);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern uint ctlOverclockTemperatureLimitSet(IntPtr device, double value);

    // --- Ventilateurs ---
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern uint ctlEnumFans(IntPtr device, ref uint count, [Out] IntPtr[]? fans);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern uint ctlFanGetProperties(IntPtr fan, ref FanProperties properties);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern uint ctlFanSetDefaultMode(IntPtr fan);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern uint ctlFanSetFixedSpeedMode(IntPtr fan, ref FanSpeed speed);
}
