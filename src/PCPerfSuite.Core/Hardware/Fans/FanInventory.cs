using PCPerfSuite.Core.Overlay;

namespace PCPerfSuite.Core.Hardware.Fans;

/// <summary>
/// Compte les ventilateurs lus d'après leur origine, pour le diagnostic « Compatibilité de ce PC » : combien viennent
/// de la carte mère, du GPU ou d'un portable, et combien de ceux de la carte mère portent un vrai nom. C'est ce qui
/// permet de distinguer, dans un signalement, « la carte mère ne nomme pas ses connecteurs » (modèle absent de la
/// table de la bibliothèque de capteurs) de « aucun ventilateur lu ».
/// </summary>
public sealed class FanInventory
{
    /// <summary>Préfixe de <see cref="FanReading.HardwareId"/> des ventilateurs lus via l'interface d'un constructeur de portables.</summary>
    public const string LaptopHardwarePrefix = "laptop/";

    /// <summary>Ventilateurs de la carte mère : puce Super I/O, ou contrôleur embarqué vu par la bibliothèque de capteurs.</summary>
    public int BoardFans { get; init; }

    /// <summary>Parmi eux, ceux dont la carte mère a donné un vrai nom (« CPU Fan »), pas seulement un numéro de canal.</summary>
    public int BoardFansNamed { get; init; }

    /// <summary>Puces dont au moins un ventilateur n'a pas de nom, sans doublon (« Nuvoton NCT6798D »).</summary>
    public IReadOnlyList<string> ChipsWithoutNames { get; init; } = Array.Empty<string>();

    /// <summary>Ventilateurs d'une carte graphique lus par la bibliothèque de capteurs.</summary>
    public int GpuFans { get; init; }

    /// <summary>Ventilateurs de portable lus via l'interface du constructeur, en lecture seule.</summary>
    public int LaptopFans { get; init; }

    public static FanInventory Of(IReadOnlyList<FanReading> fans)
    {
        var board = new List<FanReading>();
        int gpu = 0;
        int laptop = 0;

        foreach (FanReading fan in fans)
        {
            if (fan.HardwareId?.StartsWith(LaptopHardwarePrefix, StringComparison.Ordinal) == true) laptop++;
            else if (fan.Category == FanCategory.Gpu || fan.Group == SensorGroup.Gpu) gpu++;
            else board.Add(fan);
        }

        return new FanInventory
        {
            BoardFans = board.Count,
            BoardFansNamed = board.Count(f => f.NameFromHardware),
            ChipsWithoutNames = board.Where(f => !f.NameFromHardware).Select(f => f.HardwareName).Distinct().ToList(),
            GpuFans = gpu,
            LaptopFans = laptop,
        };
    }
}
