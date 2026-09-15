using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace PCPerfSuite.Core.Overlay;

/// <summary>Rendu d'une application 3D accrochée par RTSS. Null quand RTSS n'a pas encore de mesure.</summary>
public readonly record struct RtssFrameStats(
    double? Fps,
    double? FrameTimeMs,
    double? AverageFps,
    double? OnePercentLowFps,
    double? PointOnePercentLowFps,
    int SampleCount);

/// <summary>
/// Lit, en lecture seule et dans la même mémoire partagée que RtssOsdClient, les compteurs que RTSS publie
/// pour chaque application 3D qu'il a accrochée (tableau arrApp[] de RTSSSharedMemory.h), et retient celle
/// du processus au premier plan — le jeu en cours, en pratique. Offsets et tailles du tableau relus depuis
/// le header, comme pour l'OSD.
///
/// En plus du FPS instantané, RTSS tient un historique circulaire des 1024 derniers temps de frame
/// (dwStatFrameTimeBuf) : c'est lui qui permet de calculer le FPS moyen et les 1% / 0.1% low, exactement
/// comme le font les outils de benchmark, sans échantillonner nous-mêmes image par image.
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

    // Champs "v2.5 et plus" : historique circulaire des temps de frame, en microsecondes.
    private const long EntryFrameTimeBufOffset = 924;
    private const int FrameTimeBufLength = 1024;
    private const uint EntrySizeWithFrameTimeBuf = 5028;

    // Champs "v2.13 et plus" : 1% / 0.1% low calculés par RTSS lui-même pendant un enregistrement de
    // statistiques. Servent de repli quand l'historique de temps de frame est vide.
    private const long EntryFramerate1PercentLowOffset = 9176;
    private const long EntryFramerate01PercentLowOffset = 9180;
    private const uint EntrySizeWithPercentileLows = 9184;

    /// <summary>Un temps de frame au-delà d'une seconde n'est pas une image de jeu : case jamais écrite,
    /// ou application en pause. On l'ignore pour ne pas fausser les moyennes.</summary>
    private const uint MaxPlausibleFrameTimeUs = 1_000_000;

    /// <summary>Nombre minimal d'images pour qu'un centile ait un sens : 100 images pour un 1% low,
    /// 1000 pour un 0.1% low. En-dessous, la valeur s'affiche "--" plutôt que d'inventer un chiffre.</summary>
    private const int MinSamplesFor1Percent = 100;
    private const int MinSamplesFor01Percent = 1000;

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
                if (entry + entrySize > accessor.Capacity) break;
                if (accessor.ReadUInt32(entry + EntryProcessIdOffset) != foregroundPid) continue;

                uint time0 = accessor.ReadUInt32(entry + EntryTime0Offset);
                uint time1 = accessor.ReadUInt32(entry + EntryTime1Offset);
                uint frames = accessor.ReadUInt32(entry + EntryFramesOffset);
                uint frameTimeUs = accessor.ReadUInt32(entry + EntryFrameTimeOffset);

                double? fps = time1 > time0 ? 1000.0 * frames / (time1 - time0) : null;
                double? frameTimeMs = frameTimeUs > 0 ? frameTimeUs / 1000.0 : null;

                FrameTimeStats stats = ReadFrameTimeStats(accessor, entry, entrySize, fps);

                return new RtssFrameStats(
                    fps, frameTimeMs, stats.AverageFps, stats.OnePercentLowFps, stats.PointOnePercentLowFps, stats.SampleCount);
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private readonly record struct FrameTimeStats(
        double? AverageFps, double? OnePercentLowFps, double? PointOnePercentLowFps, int SampleCount);

    private static FrameTimeStats ReadFrameTimeStats(
        MemoryMappedViewAccessor accessor, long entry, uint entrySize, double? instantFps)
    {
        if (entrySize < EntrySizeWithFrameTimeBuf) return default;

        var raw = new uint[FrameTimeBufLength];
        accessor.ReadArray(entry + EntryFrameTimeBufOffset, raw, 0, FrameTimeBufLength);

        // L'historique est circulaire : tant que le jeu vient de démarrer, une partie des cases vaut
        // encore zéro. On ne garde que les mesures plausibles, sans se soucier de leur ordre (un
        // centile ne dépend pas de la chronologie).
        var samples = new List<uint>(FrameTimeBufLength);
        double totalUs = 0;
        foreach (uint value in raw)
        {
            if (value == 0 || value >= MaxPlausibleFrameTimeUs) continue;
            samples.Add(value);
            totalUs += value;
        }

        if (samples.Count < 8)
        {
            // Historique vide (RTSS ancien, ou jeu qui vient d'être accroché) : on se rabat sur les
            // centiles que RTSS calcule lui-même pendant un enregistrement de statistiques.
            return ReadRtssComputedLows(accessor, entry, entrySize, instantFps);
        }

        samples.Sort();
        double averageFps = samples.Count * 1_000_000.0 / totalUs;

        return new FrameTimeStats(
            averageFps,
            PercentileFps(samples, 0.99, MinSamplesFor1Percent),
            PercentileFps(samples, 0.999, MinSamplesFor01Percent),
            samples.Count);
    }

    /// <summary>FPS correspondant au centile haut des temps de frame : le "1% low" au sens des bancs
    /// d'essai, c'est-à-dire la vitesse atteinte par le pourcent d'images le plus lent.</summary>
    private static double? PercentileFps(List<uint> sortedFrameTimesUs, double quantile, int minSamples)
    {
        if (sortedFrameTimesUs.Count < minSamples) return null;

        int index = (int)Math.Ceiling(quantile * sortedFrameTimesUs.Count) - 1;
        index = Math.Clamp(index, 0, sortedFrameTimesUs.Count - 1);

        uint frameTimeUs = sortedFrameTimesUs[index];
        return frameTimeUs > 0 ? 1_000_000.0 / frameTimeUs : null;
    }

    private static FrameTimeStats ReadRtssComputedLows(
        MemoryMappedViewAccessor accessor, long entry, uint entrySize, double? instantFps)
    {
        if (entrySize < EntrySizeWithPercentileLows) return default;

        double? low1 = PlausibleFps(accessor.ReadUInt32(entry + EntryFramerate1PercentLowOffset), instantFps);
        double? low01 = PlausibleFps(accessor.ReadUInt32(entry + EntryFramerate01PercentLowOffset), instantFps);

        return new FrameTimeStats(null, low1, low01, 0);
    }

    /// <summary>Le header ne documente pas l'échelle de ces deux compteurs : on ne les affiche que
    /// s'ils ressemblent vraiment à un FPS (et pas à un FPS multiplié par 1000).</summary>
    private static double? PlausibleFps(uint value, double? instantFps)
    {
        if (value is 0 or > 1000) return null;
        if (instantFps is { } fps && value > fps * 1.5) return null;
        return value;
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
