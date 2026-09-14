using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Text;

namespace PCPerfSuite.Core.Overlay;

/// <summary>
/// Pousse du texte personnalisé dans l'overlay de RTSS (RivaTuner Statistics Server) via sa mémoire
/// partagée nommée "RTSSSharedMemoryV2" — le mécanisme qu'utilisent MSI Afterburner (dont l'overlay
/// EST RTSS), CapFrameX, HWiNFO, etc. RTSS gère lui-même le hook DirectX/Vulkan/OpenGL à l'intérieur
/// du jeu (y compris en plein écran exclusif) ; PCPerfSuite ne fait qu'écrire dans un créneau de
/// mémoire partagée que RTSS relit et dessine — aucune injection dans un process de jeu ici.
///
/// Layout de structure tiré du header officiel RTSSSharedMemory.h du SDK RTSS. Les offsets/tailles
/// (dwOSDArrOffset, dwOSDEntrySize...) sont toujours relus depuis la mémoire partagée plutôt que
/// supposés fixes, pour rester compatible si RTSS fait évoluer sa structure. Note : dans le header
/// officiel, les commentaires de dwAppEntrySize/dwOSDEntrySize sont inversés (bug connu et documenté
/// de RTSS) — ce sont les noms de champs qui sont fiables, pas leurs commentaires.
/// </summary>
public sealed class RtssOsdClient : IDisposable
{
    private const uint ExtendedTextVersion = 0x00020007; // v2.7 : szOSDEx (4096 caractères)

    private const long HeaderOsdEntrySizeOffset = 20;
    private const long HeaderOsdArrOffsetOffset = 24;
    private const long HeaderOsdArrSizeOffset = 28;
    private const long HeaderOsdFrameOffset = 32;

    private const int SzOsdSize = 256;
    private const int SzOsdOwnerSize = 256;
    private const int SzOsdExSize = 4096;

    // RTSS lit ces chaînes en ANSI (le client de référence RTSSSharedMemoryNET les marshale avec
    // Marshal.StringToHGlobalAnsi) : en ASCII, "°" devenait "?". .NET 8 n'embarque pas les code pages
    // Windows sans ce fournisseur.
    private static readonly Encoding AnsiEncoding = CreateAnsiEncoding();

    private readonly string _ownerName;
    private uint _claimedSlot;

    public RtssOsdClient(string ownerName)
    {
        if (string.IsNullOrWhiteSpace(ownerName)) throw new ArgumentException("Nom de propriétaire requis.", nameof(ownerName));
        _ownerName = ownerName[..FittingLength(ownerName, SzOsdOwnerSize - 1)];
    }

    /// <summary>Écrit le texte dans notre créneau OSD (le réclame si besoin) et force RTSS à
    /// rafraîchir l'affichage. Ne lève jamais — retourne false si RTSS n'est pas lancé/disponible,
    /// ou si les 8 créneaux sont déjà occupés par d'autres applications.</summary>
    public bool TryUpdate(string text)
    {
        try
        {
            using MemoryMappedFile mmf = MemoryMappedFile.OpenExisting(RtssSharedMemory.MappingName);
            using MemoryMappedViewAccessor accessor = mmf.CreateViewAccessor();

            RtssHeader? header = ReadHeader(accessor);
            if (header is null) return false;

            bool useExtended = header.Value.Version >= ExtendedTextVersion;

            for (uint i = 1; i < header.Value.OsdArrSize; i++)
            {
                long entryOffset = header.Value.OsdArrOffset + (long)i * header.Value.OsdEntrySize;
                long ownerOffset = entryOffset + SzOsdSize;
                string owner = ReadFixedString(accessor, ownerOffset, SzOsdOwnerSize);

                bool isFree = owner.Length == 0;
                bool isOurs = owner == _ownerName;
                if (!isFree && !isOurs) continue;

                if (isFree)
                {
                    WriteFixedString(accessor, ownerOffset, SzOsdOwnerSize, _ownerName);
                }

                long textOffset = useExtended ? ownerOffset + SzOsdOwnerSize : entryOffset;
                WriteFixedString(accessor, textOffset, useExtended ? SzOsdExSize : SzOsdSize, text);

                accessor.Write(HeaderOsdFrameOffset, accessor.ReadUInt32(HeaderOsdFrameOffset) + 1);

                _claimedSlot = i;
                return true;
            }

            _claimedSlot = 0;
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Libère notre créneau OSD (le vide) — à appeler quand l'overlay est désactivé ou à la
    /// fermeture de l'app, pour ne pas laisser un texte périmé affiché dans les jeux.</summary>
    public void Release()
    {
        if (_claimedSlot == 0) return;

        try
        {
            using MemoryMappedFile mmf = MemoryMappedFile.OpenExisting(RtssSharedMemory.MappingName);
            using MemoryMappedViewAccessor accessor = mmf.CreateViewAccessor();

            RtssHeader? header = ReadHeader(accessor);
            if (header is null) return;

            long entryOffset = header.Value.OsdArrOffset + (long)_claimedSlot * header.Value.OsdEntrySize;
            long ownerOffset = entryOffset + SzOsdSize;

            if (ReadFixedString(accessor, ownerOffset, SzOsdOwnerSize) == _ownerName)
            {
                WriteFixedString(accessor, entryOffset, SzOsdSize, "");
                WriteFixedString(accessor, ownerOffset, SzOsdOwnerSize, "");
                if (header.Value.Version >= ExtendedTextVersion)
                {
                    WriteFixedString(accessor, ownerOffset + SzOsdOwnerSize, SzOsdExSize, "");
                }
                accessor.Write(HeaderOsdFrameOffset, accessor.ReadUInt32(HeaderOsdFrameOffset) + 1);
            }
        }
        catch { /* best-effort */ }
        finally
        {
            _claimedSlot = 0;
        }
    }

    public void Dispose() => Release();

    private readonly record struct RtssHeader(uint Version, uint OsdEntrySize, long OsdArrOffset, uint OsdArrSize);

    private static RtssHeader? ReadHeader(MemoryMappedViewAccessor accessor)
    {
        uint signature = accessor.ReadUInt32(0);
        uint version = accessor.ReadUInt32(4);
        if (signature != RtssSharedMemory.Signature || version < RtssSharedMemory.MinVersion) return null;

        uint osdEntrySize = accessor.ReadUInt32(HeaderOsdEntrySizeOffset);
        uint osdArrOffset = accessor.ReadUInt32(HeaderOsdArrOffsetOffset);
        uint osdArrSize = accessor.ReadUInt32(HeaderOsdArrSizeOffset);
        if (osdEntrySize == 0 || osdArrSize == 0) return null;

        return new RtssHeader(version, osdEntrySize, osdArrOffset, osdArrSize);
    }

    private static string ReadFixedString(MemoryMappedViewAccessor accessor, long offset, int maxSize)
    {
        var buffer = new byte[maxSize];
        accessor.ReadArray(offset, buffer, 0, maxSize);
        int len = Array.IndexOf(buffer, (byte)0);
        if (len < 0) len = maxSize;
        return AnsiEncoding.GetString(buffer, 0, len);
    }

    private static void WriteFixedString(MemoryMappedViewAccessor accessor, long offset, int maxSize, string value)
    {
        var buffer = new byte[maxSize];
        AnsiEncoding.GetBytes(value, 0, FittingLength(value, maxSize - 1), buffer, 0);
        accessor.WriteArray(offset, buffer, 0, maxSize);
    }

    /// <summary>Plus long préfixe de value dont l'encodage ANSI tient dans maxBytes, sans couper un caractère
    /// sur deux octets (code pages asiatiques) ni une paire de substitution.</summary>
    private static int FittingLength(string value, int maxBytes)
    {
        if (AnsiEncoding.GetByteCount(value) <= maxBytes) return value.Length;

        int low = 0;
        int high = value.Length;
        while (low < high)
        {
            int mid = (low + high + 1) / 2;
            if (AnsiEncoding.GetByteCount(value.AsSpan(0, mid)) <= maxBytes) low = mid;
            else high = mid - 1;
        }

        if (low > 0 && char.IsHighSurrogate(value[low - 1])) low--;
        return low;
    }

    private static Encoding CreateAnsiEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage);
    }
}
