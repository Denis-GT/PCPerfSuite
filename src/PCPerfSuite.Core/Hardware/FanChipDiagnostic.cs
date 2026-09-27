using System.Reflection;
using System.Text;
using LibreHardwareMonitor.Hardware;

namespace PCPerfSuite.Core.Hardware;

/// <summary>
/// Journal de l'état de la puce des ventilateurs (Super I/O) à des moments clés : au démarrage, avant toute écriture
/// de l'app, et à la fermeture, juste après le retour des ventilateurs au BIOS. Comparer une fermeture avec le
/// démarrage qui la suit dit si l'état laissé par l'app est le bon, ou si un autre logiciel (Armoury Crate…) le
/// change ensuite. Sert de rapport de bug pour les cartes mères où les ventilateurs s'emballent après la fermeture.
/// Best-effort (règle 2) : ne lève jamais, et ne fait rien sans puce Super I/O.
/// </summary>
internal static class FanChipDiagnostic
{
    /// <summary>Au-delà, le journal repart de zéro : quelques dizaines de démarrages et fermetures.</summary>
    private const long MaxFileBytes = 2 * 1024 * 1024;

    private static readonly object FileGate = new();

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PCPerfSuite", "diagnostic-ventilateurs.log");

    public static void Record(IComputer computer, string moment)
    {
        try
        {
            var text = new StringBuilder();
            foreach (IHardware hardware in computer.Hardware)
            {
                foreach (IHardware sub in hardware.SubHardware)
                {
                    if (sub.HardwareType != HardwareType.SuperIO) continue;
                    AppendChip(text, sub);
                }
            }

            if (text.Length == 0) return;

            lock (FileGate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > MaxFileBytes) info.Delete();

                File.AppendAllText(FilePath,
                    $"===== {DateTime.Now:yyyy-MM-dd HH:mm:ss} — {moment} ====={Environment.NewLine}{text}{Environment.NewLine}");
            }
        }
        catch
        {
            // Un diagnostic ne doit jamais gêner le démarrage ni la fermeture.
        }
    }

    private static void AppendChip(StringBuilder text, IHardware chip)
    {
        text.AppendLine($"{chip.Name} ({chip.Identifier})");

        foreach (ISensor sensor in chip.Sensors.Where(s => s.SensorType == SensorType.Control))
        {
            text.AppendLine($"  commande {sensor.Index} « {sensor.Name} » : mode {sensor.Control?.ControlMode}, " +
                            $"sortie {sensor.Value?.ToString("0") ?? "?"} %");
        }

        AppendSavedDefaults(text, chip);
        text.AppendLine(chip.GetReport());
    }

    /// <summary>
    /// L'état « d'origine » que LibreHardwareMonitor a mémorisé pour chaque ventilateur à sa première prise en main,
    /// et qu'il réécrit pour le rendre au BIOS. Ces champs sont internes à la bibliothèque : lus par réflexion,
    /// uniquement pour ce diagnostic, et absents sans rien casser si une version future les renomme.
    /// </summary>
    private static void AppendSavedDefaults(StringBuilder text, IHardware chip)
    {
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

        object? superIo = chip.GetType().GetField("_superIO", Private)?.GetValue(chip);
        if (superIo is null) return;

        Type type = superIo.GetType();
        var pending = type.GetField("_restoreDefaultFanControlRequired", Private)?.GetValue(superIo) as bool[];
        var modes = type.GetField("_initialFanControlMode", Private)?.GetValue(superIo) as byte[];
        var pwms = type.GetField("_initialFanPwmCommand", Private)?.GetValue(superIo) as byte[];
        if (pending is null || modes is null || pwms is null) return;

        for (int i = 0; i < pending.Length && i < modes.Length && i < pwms.Length; i++)
        {
            text.AppendLine($"  état d'origine mémorisé {i} : à restaurer {pending[i]}, mode 0x{modes[i]:X2}, consigne 0x{pwms[i]:X2}");
        }
    }
}
