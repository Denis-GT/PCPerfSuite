using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace PCPerfSuite.Core.Overlay;

/// <summary>Rendu d'une application 3D accrochée par RTSS. Null quand RTSS n'a pas encore de mesure.</summary>
public readonly record struct RtssFrameStats(double? Fps, double? FrameTimeMs);

/// <summary>
/// Lit, en lecture seule et dans la même mémoire partagée que RtssOsdClient, les compteurs que RTSS publie
/// pour chaque application 3D qu'il a accrochée (tableau arrApp[] de RTSSSharedMemory.h), et retient celle
/// du processus au premier plan — le jeu en cours, en pratique. Offsets et tailles du tableau relus depuis
/// le header, comme pour l'OSD.
/// </summary>
public static class RtssFrameStatsReader
{
    private const long HeaderAppEntrySizeOffset = 8;
    private const long HeaderAppArrOffsetOffset = 12;
    private const long HeaderAppArrSizeOffset = 16;

    // RTSS_SHARED_MEMORY_APP_ENTRY : DWORD dwProcessID, char szName[MAX_PATH], DWORD dwFlags, puis les compteurs.
    private const long EntryProcessIdOffset = 0;
    private const long EntryTime0Offset = 268;      // ms
    private const long EntryTime1Offset = 272;      // ms
    private const long EntryFramesOffset = 276;
    private const long EntryFrameTimeOffset = 280;  // microsecondes
    private const uint MinEntrySize = 284;

    /// <summary>Ne lève jamais (RTSS absent est un cas normal) : null si RTSS n'est pas lancé ou n'a pas
    /// accroché l'application au premier plan.</summary>
    public static RtssFrameStats? TryReadForeground()
    {
        try
        {
            uint foregroundPid = GetForegroundProcessId();
            if (foregroundPid == 0) return null;

            using MemoryMappedFile mmf = MemoryMappedFile.OpenExisting(RtssSharedMemory.MappingName, MemoryMappedFileRights.Read);
            using MemoryMappedViewAccessor accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

            if (accessor.ReadUInt32(0) != RtssSharedMemory.Signature || accessor.ReadUInt32(4) < RtssSharedMemory.MinVersion) return null;

            uint entrySize = accessor.ReadUInt32(HeaderAppEntrySizeOffset);
            uint arrOffset = accessor.ReadUInt32(HeaderAppArrOffsetOffset);
            uint arrSize = accessor.ReadUInt32(HeaderAppArrSizeOffset);
            if (entrySize < MinEntrySize) return null;

            for (uint i = 0; i < arrSize; i++)
            {
                long entry = arrOffset + (long)i * entrySize;
                if (entry + MinEntrySize > accessor.Capacity) break;
                if (accessor.ReadUInt32(entry + EntryProcessIdOffset) != foregroundPid) continue;

                uint time0 = accessor.ReadUInt32(entry + EntryTime0Offset);
                uint time1 = accessor.ReadUInt32(entry + EntryTime1Offset);
                uint frames = accessor.ReadUInt32(entry + EntryFramesOffset);
                uint frameTimeUs = accessor.ReadUInt32(entry + EntryFrameTimeOffset);

                double? fps = time1 > time0 ? 1000.0 * frames / (time1 - time0) : null;
                double? frameTimeMs = frameTimeUs > 0 ? frameTimeUs / 1000.0 : null;
                return new RtssFrameStats(fps, frameTimeMs);
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static uint GetForegroundProcessId()
    {
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return 0;

        GetWindowThreadProcessId(hwnd, out uint processId);
        return processId;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}
