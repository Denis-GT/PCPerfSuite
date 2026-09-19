using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PCPerfSuite.Core.Hardware;

/// <summary>
/// Batterie(s) lues directement auprès du pilote Windows (IOCTL_BATTERY_QUERY_INFORMATION / _STATUS), la
/// source qu'utilisent BatteryInfoView ou GHelper. WMI (root\wmi) donne le même état mais pas toujours la
/// capacité nominale (BatteryStaticData échoue sur bien des portables), indispensable à l'état de santé.
/// Sur un PC fixe, aucune batterie n'est énumérée et <see cref="Read"/> renvoie null.
/// </summary>
internal sealed class BatteryReader : IDisposable
{
    private static readonly Guid BatteryClassGuid = new("72631E54-78A4-11D0-BCF7-00AA00B7B32A");

    private const uint IoctlBatteryQueryTag = 0x294040;
    private const uint IoctlBatteryQueryInformation = 0x294044;
    private const uint IoctlBatteryQueryStatus = 0x29404C;

    private const uint BatteryCapacityRelative = 0x40000000;
    private const uint BatteryPowerOnLine = 0x1;
    private const uint BatteryDischarging = 0x2;
    private const uint BatteryCharging = 0x4;
    private const uint BatteryUnknownValue = 0xFFFFFFFF;
    private const int BatteryUnknownRate = unchecked((int)0x80000000);

    private sealed class Device
    {
        public required SafeFileHandle Handle { get; init; }
        public required uint Tag { get; init; }
        public string? Name { get; init; }
        public string? Manufacturer { get; init; }
    }

    private List<Device>? _devices;

    /// <summary>Énumération refaite au plus toutes les 30 s quand aucune batterie n'a été trouvée : une batterie
    /// ne se branche pas en cours de route sur un fixe, inutile de réinterroger SetupAPI à chaque relevé.</summary>
    private DateTime _nextEnumerationUtc = DateTime.MinValue;

    public BatterySnapshot? Read()
    {
        if (_devices is null || _devices.Count == 0)
        {
            if (DateTime.UtcNow < _nextEnumerationUtc) return null;
            Close();
            _devices = Enumerate();
            _nextEnumerationUtc = DateTime.UtcNow.AddSeconds(30);
            if (_devices.Count == 0) return null;
        }

        var batteries = new List<(Device Device, BatteryInformation Info, BatteryStatus Status)>();
        foreach (Device device in _devices)
        {
            if (!TryQueryInformation(device, out BatteryInformation info) || !TryQueryStatus(device, out BatteryStatus status))
            {
                // Tag périmé (batterie retirée, pilote rechargé) : on repart d'une énumération complète au relevé suivant.
                Close();
                _nextEnumerationUtc = DateTime.MinValue;
                return null;
            }
            batteries.Add((device, info, status));
        }

        return Aggregate(batteries);
    }

    private static BatterySnapshot Aggregate(List<(Device Device, BatteryInformation Info, BatteryStatus Status)> batteries)
    {
        // Capacités en mWh seulement si TOUTES les batteries parlent en mWh : additionner des mWh et des unités
        // relatives donnerait un nombre sans signification.
        bool absolute = batteries.All(b => (b.Info.Capabilities & BatteryCapacityRelative) == 0);

        uint powerState = 0;
        double? remaining = absolute ? 0 : null;
        double? full = absolute ? 0 : null;
        double? design = absolute ? 0 : null;
        double? rate = 0;
        double? voltage = null;
        int? cycles = null;

        foreach ((_, BatteryInformation info, BatteryStatus status) in batteries)
        {
            powerState |= status.PowerState;
            remaining = Add(remaining, status.Capacity);
            full = Add(full, info.FullChargedCapacity);
            design = Add(design, info.DesignedCapacity);
            rate = status.Rate == BatteryUnknownRate || rate is null ? null : rate + status.Rate;
            if (voltage is null && status.Voltage is not (0 or BatteryUnknownValue)) voltage = status.Voltage;
            if (info.CycleCount > 0) cycles = Math.Max(cycles ?? 0, (int)info.CycleCount);
        }

        // Débit non renseigné par le pilote : 0 sans charge ni décharge (batterie pleine sur secteur), sinon inconnu.
        bool charging = (powerState & BatteryCharging) != 0;
        bool discharging = (powerState & BatteryDischarging) != 0;
        if (rate is 0 && (charging || discharging)) rate = null;

        (Device first, BatteryInformation firstInfo, _) = batteries[0];
        return new BatterySnapshot
        {
            PowerOnline = (powerState & BatteryPowerOnLine) != 0,
            Charging = charging,
            Discharging = discharging,
            RemainingMWh = remaining,
            FullChargeMWh = full,
            DesignMWh = design,
            VoltageMv = voltage,
            RateMw = rate,
            CycleCount = cycles,
            Name = first.Name,
            Manufacturer = first.Manufacturer,
            Chemistry = Encoding.ASCII.GetString(firstInfo.Chemistry).TrimEnd('\0', ' '),
            BatteryCount = batteries.Count,
        };

        static double? Add(double? total, uint value)
            => total is { } t && value is not (0 or BatteryUnknownValue) ? t + value : null;
    }

    private static List<Device> Enumerate()
    {
        var devices = new List<Device>();
        Guid classGuid = BatteryClassGuid;
        IntPtr set = SetupDiGetClassDevs(ref classGuid, null, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
        if (set == InvalidHandleValue) return devices;

        try
        {
            for (uint index = 0; ; index++)
            {
                var data = new SpDeviceInterfaceData { CbSize = Marshal.SizeOf<SpDeviceInterfaceData>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref classGuid, index, ref data)) break;

                string? path = GetDevicePath(set, ref data);
                if (path is null) continue;

                SafeFileHandle handle = CreateFile(path, GenericRead | GenericWrite, FileShareRead | FileShareWrite,
                    IntPtr.Zero, OpenExisting, FileAttributeNormal, IntPtr.Zero);
                if (handle.IsInvalid)
                {
                    handle.Dispose();
                    continue;
                }

                // Tag 0 : emplacement sans batterie (portable à deux baies dont une vide).
                uint timeout = 0;
                if (!DeviceIoControl(handle, IoctlBatteryQueryTag, ref timeout, sizeof(uint), out uint tag, sizeof(uint), out _, IntPtr.Zero)
                    || tag == 0)
                {
                    handle.Dispose();
                    continue;
                }

                devices.Add(new Device
                {
                    Handle = handle,
                    Tag = tag,
                    Name = QueryString(handle, tag, BatteryQueryInformationLevel.DeviceName),
                    Manufacturer = QueryString(handle, tag, BatteryQueryInformationLevel.ManufactureName),
                });
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }

        return devices;
    }

    private static string? GetDevicePath(IntPtr set, ref SpDeviceInterfaceData data)
    {
        SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out int required, IntPtr.Zero);
        if (required <= 0) return null;

        IntPtr buffer = Marshal.AllocHGlobal(required);
        try
        {
            // cbSize de SP_DEVICE_INTERFACE_DETAIL_DATA_W : 8 en 64 bits (alignement), 6 en 32 bits.
            Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
            if (!SetupDiGetDeviceInterfaceDetail(set, ref data, buffer, required, out _, IntPtr.Zero)) return null;
            return Marshal.PtrToStringUni(buffer + 4);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool TryQueryInformation(Device device, out BatteryInformation info)
    {
        var query = new BatteryQueryInformation { BatteryTag = device.Tag, InformationLevel = BatteryQueryInformationLevel.Information };
        return DeviceIoControl(device.Handle, IoctlBatteryQueryInformation, ref query, Marshal.SizeOf<BatteryQueryInformation>(),
            out info, Marshal.SizeOf<BatteryInformation>(), out _, IntPtr.Zero);
    }

    private static bool TryQueryStatus(Device device, out BatteryStatus status)
    {
        var wait = new BatteryWaitStatus { BatteryTag = device.Tag };
        return DeviceIoControl(device.Handle, IoctlBatteryQueryStatus, ref wait, Marshal.SizeOf<BatteryWaitStatus>(),
            out status, Marshal.SizeOf<BatteryStatus>(), out _, IntPtr.Zero);
    }

    private static string? QueryString(SafeFileHandle handle, uint tag, BatteryQueryInformationLevel level)
    {
        var query = new BatteryQueryInformation { BatteryTag = tag, InformationLevel = level };
        var buffer = new byte[512];
        if (!DeviceIoControl(handle, IoctlBatteryQueryInformation, ref query, Marshal.SizeOf<BatteryQueryInformation>(),
                buffer, buffer.Length, out int returned, IntPtr.Zero) || returned <= 0)
        {
            return null;
        }

        string value = Encoding.Unicode.GetString(buffer, 0, returned).TrimEnd('\0').Trim();
        return value.Length > 0 ? value : null;
    }

    private void Close()
    {
        if (_devices is null) return;
        foreach (Device device in _devices) device.Handle.Dispose();
        _devices = null;
    }

    public void Dispose() => Close();

    // ----- Interop -----

    private enum BatteryQueryInformationLevel
    {
        Information = 0,
        DeviceName = 4,
        ManufactureName = 6,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BatteryQueryInformation
    {
        public uint BatteryTag;
        public BatteryQueryInformationLevel InformationLevel;
        public int AtRate;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BatteryInformation
    {
        public uint Capabilities;
        public byte Technology;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)] public byte[] Reserved;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public byte[] Chemistry;
        public uint DesignedCapacity;
        public uint FullChargedCapacity;
        public uint DefaultAlert1;
        public uint DefaultAlert2;
        public uint CriticalBias;
        public uint CycleCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BatteryWaitStatus
    {
        public uint BatteryTag;
        public uint Timeout;
        public uint PowerState;
        public uint LowCapacity;
        public uint HighCapacity;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BatteryStatus
    {
        public uint PowerState;
        public uint Capacity;
        public uint Voltage;
        public int Rate;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        public int CbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    private const int DigcfPresent = 0x2;
    private const int DigcfDeviceInterface = 0x10;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x1;
    private const uint FileShareWrite = 0x2;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x80;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, IntPtr hwndParent, int flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid,
        uint memberIndex, ref SpDeviceInterfaceData deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet, ref SpDeviceInterfaceData deviceInterfaceData,
        IntPtr deviceInterfaceDetailData, int deviceInterfaceDetailDataSize, out int requiredSize, IntPtr deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint ioControlCode, ref uint inBuffer, int inBufferSize,
        out uint outBuffer, int outBufferSize, out int bytesReturned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint ioControlCode, ref BatteryQueryInformation inBuffer, int inBufferSize,
        out BatteryInformation outBuffer, int outBufferSize, out int bytesReturned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint ioControlCode, ref BatteryQueryInformation inBuffer, int inBufferSize,
        [Out] byte[] outBuffer, int outBufferSize, out int bytesReturned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint ioControlCode, ref BatteryWaitStatus inBuffer, int inBufferSize,
        out BatteryStatus outBuffer, int outBufferSize, out int bytesReturned, IntPtr overlapped);
}
