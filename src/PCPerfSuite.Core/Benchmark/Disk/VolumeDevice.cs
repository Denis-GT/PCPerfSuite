using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PCPerfSuite.Core.Benchmark.Disk;

/// <summary>Ce que Windows dit du périphérique derrière un volume : tailles de secteur, numéro de disque physique
/// (\\.\PhysicalDriveN, le DeviceId de MSFT_PhysicalDisk), pénalité de recherche (disque à plateaux). Null = non lu, et
/// <see cref="Problem"/> dit pourquoi.</summary>
public sealed record VolumeDeviceInfo(int? LogicalSectorBytes, int? PhysicalSectorBytes, int? DeviceNumber, bool? IncursSeekPenalty, string? Problem)
{
    /// <summary>Secteur à respecter pour les E/S sans cache : le physique, sinon le logique, sinon 4 Ko.</summary>
    public int SectorBytes => PhysicalSectorBytes ?? LogicalSectorBytes ?? VolumeDevice.DefaultSectorBytes;
}

/// <summary>
/// Lecture du périphérique d'un volume par <c>IOCTL_STORAGE_QUERY_PROPERTY</c> (alignement d'accès, pénalité de
/// recherche) et <c>IOCTL_STORAGE_GET_DEVICE_NUMBER</c> sur <c>\\.\X:</c>, ouvert sans droit d'accès (ces IOCTL n'en
/// demandent aucun : pas besoin d'administrateur). Décodage en logique pure sur les octets rendus. Best-effort : rien ne
/// lève, un repli de 4 096 octets est pris pour le secteur.
/// </summary>
public static class VolumeDevice
{
    public const int DefaultSectorBytes = 4096;

    private const uint IoctlStorageQueryProperty = 0x2D1400;
    private const uint IoctlStorageGetDeviceNumber = 0x2D1080;
    private const int StorageAccessAlignmentProperty = 6;
    private const int StorageDeviceSeekPenaltyProperty = 7;
    private const uint FileShareReadWrite = 0x1 | 0x2;
    private const uint OpenExisting = 3;

    public static VolumeDeviceInfo Read(string driveLetter)
    {
        string? letter = NormalizeLetter(driveLetter);
        if (letter is null) return new VolumeDeviceInfo(null, null, null, null, "lettre de lecteur invalide");

        try
        {
            using SafeFileHandle handle = CreateFileW($@"\\.\{letter}", 0, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                return new VolumeDeviceInfo(null, null, null, null, $"volume {letter} inaccessible (erreur Windows {Marshal.GetLastWin32Error()})");
            }

            var problems = new List<string>();
            (int? logical, int? physical) = (null, null);
            if (QueryProperty(handle, StorageAccessAlignmentProperty, 64, out byte[] alignment)) (logical, physical) = ParseAccessAlignment(alignment);
            else problems.Add("alignement non lu");

            bool? seekPenalty = null;
            if (QueryProperty(handle, StorageDeviceSeekPenaltyProperty, 16, out byte[] penalty)) seekPenalty = ParseSeekPenalty(penalty);
            else problems.Add("pénalité de recherche non lue");

            int? deviceNumber = null;
            var number = new byte[12];
            if (DeviceIoControl(handle, IoctlStorageGetDeviceNumber, null, 0, number, number.Length, out uint returned, IntPtr.Zero) && returned >= 12)
            {
                deviceNumber = ParseDeviceNumber(number);
            }
            else
            {
                problems.Add("numéro de disque non lu");
            }

            return new VolumeDeviceInfo(logical, physical, deviceNumber, seekPenalty, problems.Count == 0 ? null : string.Join(", ", problems));
        }
        catch (Exception ex)
        {
            return new VolumeDeviceInfo(null, null, null, null, $"lecture impossible ({ex.GetType().Name})");
        }
    }

    /// <summary>« C: » depuis « C », « c: », « C:\ » ; null pour autre chose.</summary>
    public static string? NormalizeLetter(string? driveLetter)
    {
        if (string.IsNullOrWhiteSpace(driveLetter)) return null;
        string trimmed = driveLetter.Trim().TrimEnd('\\', '/');
        if (trimmed.Length == 1 && char.IsAsciiLetter(trimmed[0])) return char.ToUpperInvariant(trimmed[0]) + ":";
        if (trimmed.Length == 2 && char.IsAsciiLetter(trimmed[0]) && trimmed[1] == ':') return char.ToUpperInvariant(trimmed[0]) + ":";
        return null;
    }

    /// <summary>STORAGE_ACCESS_ALIGNMENT_DESCRIPTOR : Version @0, Size @4, BytesPerCacheLine @8, BytesOffsetForCacheAlignment
    /// @12, BytesPerLogicalSector @16, BytesPerPhysicalSector @20. Une taille nulle ou non puissance de deux est ignorée.</summary>
    public static (int? Logical, int? Physical) ParseAccessAlignment(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < 24) return (null, null);
        return (AsSector(BinaryPrimitives.ReadUInt32LittleEndian(buffer[16..])), AsSector(BinaryPrimitives.ReadUInt32LittleEndian(buffer[20..])));
    }

    /// <summary>DEVICE_SEEK_PENALTY_DESCRIPTOR : Version @0, Size @4, IncursSeekPenalty (BOOLEAN) @8.</summary>
    public static bool? ParseSeekPenalty(ReadOnlySpan<byte> buffer) => buffer.Length < 9 ? null : buffer[8] != 0;

    /// <summary>STORAGE_DEVICE_NUMBER : DeviceType @0, DeviceNumber @4, PartitionNumber @8.</summary>
    public static int? ParseDeviceNumber(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < 8) return null;
        uint number = BinaryPrimitives.ReadUInt32LittleEndian(buffer[4..]);
        return number == uint.MaxValue ? null : (int)Math.Min(number, int.MaxValue);
    }

    private static int? AsSector(uint value) => value is >= 512 and <= 65536 && (value & (value - 1)) == 0 ? (int)value : null;

    private static bool QueryProperty(SafeFileHandle handle, int propertyId, int outputSize, out byte[] output)
    {
        // STORAGE_PROPERTY_QUERY : PropertyId @0, QueryType @4 (0 = PropertyStandardQuery), AdditionalParameters @8.
        var query = new byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(query, propertyId);
        output = new byte[outputSize];
        return DeviceIoControl(handle, IoctlStorageQueryProperty, query, query.Length, output, output.Length, out uint returned, IntPtr.Zero) && returned >= 8;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint ioControlCode, byte[]? inBuffer, int inBufferSize,
        byte[] outBuffer, int outBufferSize, out uint bytesReturned, IntPtr overlapped);
}
