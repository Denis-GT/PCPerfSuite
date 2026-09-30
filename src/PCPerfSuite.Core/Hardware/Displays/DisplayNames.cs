namespace PCPerfSuite.Core.Hardware.Displays;

/// <summary>Noms lisibles des écrans et de leurs sorties, isolés ici pour être testés sans écran.</summary>
public static class DisplayNames
{
    /// <summary>Nom d'un écran dans une liste : son nom convivial, sinon « Écran 2 (1920×1080) » ; des écrans
    /// dupliqués donnent « A + B (dupliqués) ». <paramref name="number"/> est son numéro dans la liste (1 pour le
    /// premier), celui qu'affiche « Identifier ».</summary>
    public static string Label(DisplayMonitor monitor, int number)
    {
        string fallback = $"Écran {number} ({monitor.Bounds.Width}×{monitor.Bounds.Height})";
        List<string> names = monitor.Targets
            .Select(t => t.FriendlyName?.Trim())
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .ToList();

        if (!monitor.IsCloned) return names.Count > 0 ? names[0] : fallback;

        IEnumerable<string> parts = monitor.Targets.Select(t => string.IsNullOrWhiteSpace(t.FriendlyName)
            ? "écran sans nom"
            : t.FriendlyName.Trim());
        return $"{string.Join(" + ", parts)} (dupliqués)";
    }

    /// <summary>Sortie en clair, pour le diagnostic.</summary>
    public static string Describe(DisplayOutputKind output) => output switch
    {
        DisplayOutputKind.Vga => "VGA",
        DisplayOutputKind.Dvi => "DVI",
        DisplayOutputKind.Hdmi => "HDMI",
        DisplayOutputKind.DisplayPort => "DisplayPort",
        DisplayOutputKind.UsbDisplayPort => "DisplayPort par USB-C",
        DisplayOutputKind.Internal => "écran interne",
        DisplayOutputKind.Wireless => "sans fil (Miracast)",
        DisplayOutputKind.Indirect => "indirecte (DisplayLink, écran virtuel)",
        _ => "autre sortie",
    };

    /// <summary>DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY vers <see cref="DisplayOutputKind"/>. Les dalles internes
    /// arrivent en LVDS, eDP, UDI intégré ou « interne » selon le pilote.</summary>
    public static DisplayOutputKind FromOutputTechnology(uint technology) => technology switch
    {
        0 => DisplayOutputKind.Vga,
        4 => DisplayOutputKind.Dvi,
        5 => DisplayOutputKind.Hdmi,
        6 or 11 or 13 or 0x80000000 => DisplayOutputKind.Internal,
        10 => DisplayOutputKind.DisplayPort,
        15 => DisplayOutputKind.Wireless,
        16 or 17 => DisplayOutputKind.Indirect,
        18 => DisplayOutputKind.UsbDisplayPort,
        _ => DisplayOutputKind.Other,
    };

    /// <summary>
    /// Code PNP du fabricant (« DEL », « SAM ») depuis l'identifiant EDID que rend DisplayConfig. L'EDID le stocke en
    /// gros-boutiste (octets 8-9) et Windows le rend tel quel dans un UINT16 : on inverse les octets, puis trois lettres
    /// de 5 bits (1 = A). Null si une lettre est hors de A-Z (écran sans EDID ou identifiant nul).
    /// </summary>
    public static string? DecodeManufacturer(ushort edidManufacturerId)
    {
        int value = ((edidManufacturerId & 0xFF) << 8) | (edidManufacturerId >> 8);
        Span<char> letters = stackalloc char[3];
        for (int i = 0; i < 3; i++)
        {
            int code = (value >> (10 - 5 * i)) & 0x1F;
            if (code is < 1 or > 26) return null;
            letters[i] = (char)('A' + code - 1);
        }
        return new string(letters);
    }
}
