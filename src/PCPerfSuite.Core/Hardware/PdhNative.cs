using System.Runtime.InteropServices;

namespace PCPerfSuite.Core.Hardware;

/// <summary>Appels PDH (compteurs de performances Windows) partagés par <see cref="PdhCounterSampler"/> et
/// <see cref="Cpu.CoreActivityReader"/>.</summary>
internal static class PdhNative
{
    public const uint FmtDouble = 0x00000200;

    /// <summary>Sans ce drapeau, PDH ramène à 100 toute valeur au-dessus : « % Processor Performance » dépasse 100 en
    /// turbo, et c'est justement ce qu'on veut voir.</summary>
    public const uint FmtNoCap100 = 0x00008000;

    public const uint MoreData = 0x800007D2;

    /// <summary>PDH_CSTATUS_VALID_DATA et PDH_CSTATUS_NEW_DATA : les deux seuls états d'une valeur utilisable.</summary>
    public static bool IsValid(uint status) => status is 0 or 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct FmtCounterValue
    {
        public uint CStatus;
        public double DoubleValue;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    public static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    public static extern uint PdhAddEnglishCounterW(IntPtr query, string fullCounterPath, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    public static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll")]
    public static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, IntPtr type, out FmtCounterValue value);

    /// <summary>Toutes les instances d'un compteur à joker. Appelée une première fois avec un tampon nul pour en
    /// connaître la taille (<see cref="MoreData"/>), puis avec le tampon.</summary>
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    public static extern uint PdhGetFormattedCounterArrayW(
        IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr itemBuffer);

    [DllImport("pdh.dll")]
    public static extern uint PdhCloseQuery(IntPtr query);
}
