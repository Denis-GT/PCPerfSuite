namespace PCPerfSuite.Core.Overlay;

/// <summary>Constantes du header de la mémoire partagée de RTSS (RTSSSharedMemory.h du SDK RTSS), communes
/// à l'écriture de l'OSD (RtssOsdClient) et à la lecture des FPS (RtssFrameStatsReader).</summary>
internal static class RtssSharedMemory
{
    public const string MappingName = "RTSSSharedMemoryV2";
    public const uint Signature = 0x52545353; // multi-char constant 'RTSS' tel qu'utilisé par RTSS lui-même
    public const uint MinVersion = 0x00020000; // v2.0 : première version avec arrOSD[]/arrApp[]
}
