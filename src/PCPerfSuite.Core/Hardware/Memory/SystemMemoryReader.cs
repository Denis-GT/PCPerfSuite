using System.Runtime.InteropServices;

namespace PCPerfSuite.Core.Hardware.Memory;

/// <summary>
/// Utilisation de la mémoire lue directement auprès de Windows, par GlobalMemoryStatusEx — la source
/// qu'utilise le Gestionnaire des tâches. Elle ne demande aucun privilège, ne dépend d'aucun pilote,
/// d'aucun SMBus et d'aucune marque : elle répond sur n'importe quel PC sous Windows.
///
/// Elle existe ici parce que LibreHardwareMonitor, seule source de la RAM jusqu'ici, pouvait laisser
/// toutes les valeurs vides sur certaines machines. Une donnée que le Gestionnaire des tâches affiche
/// sans difficulté ne doit jamais s'afficher « N/D » dans PCPerfSuite.
/// https://learn.microsoft.com/en-us/windows/win32/api/sysinfoapi/nf-sysinfoapi-globalmemorystatusex
/// </summary>
internal static class SystemMemoryReader
{
    private const double BytesPerGb = 1024d * 1024d * 1024d;

    /// <summary>Lecture best-effort : renvoie null plutôt que de lever, y compris hors Windows.</summary>
    public static Reading? Read()
    {
        try
        {
            var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (!GlobalMemoryStatusEx(ref status)) return null;

            // ullTotalPhys est la mémoire VISIBLE par Windows, pas la capacité des barrettes : sur un PC à
            // graphique intégré, plusieurs centaines de Mo sont réservés au GPU et n'y figurent pas. C'est
            // exactement ce que montre le Gestionnaire des tâches, donc ce qu'on veut afficher.
            double total = status.ullTotalPhys / BytesPerGb;
            double available = status.ullAvailPhys / BytesPerGb;

            return new Reading
            {
                TotalGb = (float)total,
                AvailableGb = (float)available,
                UsedGb = (float)Math.Max(total - available, 0),
                LoadPercent = Math.Clamp(status.dwMemoryLoad, 0u, 100u),
                VirtualTotalGb = (float)(status.ullTotalPageFile / BytesPerGb),
                VirtualUsedGb = (float)(Math.Max(status.ullTotalPageFile - status.ullAvailPageFile, 0) / BytesPerGb),
            };
        }
        catch (Exception)
        {
            // DllNotFoundException, EntryPointNotFoundException : l'app ne tourne pas sous Windows. Aucun
            // intérêt à propager, l'appelant a déjà prévu l'absence de valeur.
            return null;
        }
    }

    public sealed class Reading
    {
        public float? UsedGb { get; init; }
        public float? AvailableGb { get; init; }
        public float? TotalGb { get; init; }
        public float? LoadPercent { get; init; }
        public float? VirtualUsedGb { get; init; }
        public float? VirtualTotalGb { get; init; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);
}
