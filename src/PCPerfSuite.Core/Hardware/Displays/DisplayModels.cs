namespace PCPerfSuite.Core.Hardware.Displays;

/// <summary>Rectangle en pixels physiques du bureau virtuel (l'app est PerMonitorV2) : un écran à gauche ou au-dessus
/// du principal a des coordonnées négatives.</summary>
public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

/// <summary>Point en pixels physiques du bureau virtuel.</summary>
public readonly record struct PixelPoint(int X, int Y);

/// <summary>Type de sortie vidéo d'un écran, d'après DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.</summary>
public enum DisplayOutputKind
{
    Other,
    Vga,
    Dvi,
    Hdmi,
    DisplayPort,
    UsbDisplayPort,
    Internal,
    Wireless,
    Indirect,
}

/// <summary>
/// Un écran physique branché, tel que le décrit Windows (un chemin actif de QueryDisplayConfig). Plusieurs cibles
/// partagent un même <see cref="DisplayMonitor"/> quand les écrans sont dupliqués. Chaque champ peut manquer : un écran
/// forcé sans EDID, un dock ou un adaptateur ne donnent souvent ni nom ni identifiants.
/// </summary>
/// <param name="FriendlyName">Nom convivial (« DELL U2720Q »), vide chez certains écrans sans EDID.</param>
/// <param name="DevicePath">Chemin du périphérique moniteur (\\?\DISPLAY#DEL4123#…), l'identifiant le plus stable.</param>
/// <param name="EdidManufacturerId">Identifiant fabricant EDID tel que le rend Windows (octets inversés).</param>
/// <param name="ManufacturerCode">Code PNP du fabricant décodé (« DEL », « SAM »).</param>
/// <param name="EdidProductCodeId">Code produit EDID.</param>
/// <param name="AdapterLuid">LUID de l'adaptateur qui pilote l'écran (change à chaque démarrage).</param>
/// <param name="AdapterDevicePath">Chemin du périphérique de l'adaptateur (\\?\PCI#VEN_10DE&amp;DEV_…).</param>
/// <param name="RefreshHz">Fréquence de rafraîchissement du chemin, null si Windows ne la donne pas.</param>
public sealed record DisplayTarget(
    string? FriendlyName,
    string? DevicePath,
    ushort? EdidManufacturerId,
    string? ManufacturerCode,
    ushort? EdidProductCodeId,
    DisplayOutputKind Output,
    uint ConnectorInstance,
    ulong AdapterLuid,
    string? AdapterDevicePath,
    double? RefreshHz)
{
    public bool HasEdid => EdidManufacturerId is not null && EdidProductCodeId is not null;
}

/// <summary>
/// Un écran du bureau (un HMONITOR) : ses bornes, son échelle et les écrans physiques qui l'affichent.
/// </summary>
/// <param name="Handle">HMONITOR, valable jusqu'au prochain changement de configuration : relire après.</param>
/// <param name="GdiDeviceName">« \\.\DISPLAYn » : sert au rapprochement, jamais à l'enregistrement (instable).</param>
/// <param name="Bounds">Écran entier en pixels physiques (rcMonitor).</param>
/// <param name="WorkArea">Écran sans la barre des tâches, en pixels physiques (rcWork).</param>
/// <param name="Dpi">DPI effectif (96 = 100 %). 96 quand Windows ne le donne pas, voir <paramref name="DpiIsKnown"/>.</param>
/// <param name="AdapterName">Nom du GPU qui pilote l'écran (« NVIDIA GeForce RTX 4070 »), null s'il n'a pas été lu.</param>
/// <param name="Targets">Écrans physiques, plusieurs quand ils sont dupliqués, aucun si CCD n'a pas pu être lu.</param>
public sealed record DisplayMonitor(
    IntPtr Handle,
    string GdiDeviceName,
    PixelRect Bounds,
    PixelRect WorkArea,
    int Dpi,
    bool DpiIsKnown,
    bool IsPrimary,
    string? AdapterName,
    IReadOnlyList<DisplayTarget> Targets)
{
    public double Scale => Dpi / 96.0;

    public bool IsCloned => Targets.Count > 1;

    /// <summary>Fréquence du premier écran physique (celle de l'écran dupliqué le plus lent n'est pas donnée ici).</summary>
    public double? RefreshHz => Targets.Count > 0 ? Targets[0].RefreshHz : null;
}

/// <summary>Écrans lus à un instant donné, dans l'ordre affiché à l'utilisateur (principal d'abord, puis de gauche à
/// droite). <paramref name="Problem"/> dit en clair ce qui n'a pas pu être lu (null si tout va bien).</summary>
public sealed record DisplayTopologySnapshot(IReadOnlyList<DisplayMonitor> Monitors, string? Problem)
{
    public static DisplayTopologySnapshot Empty { get; } = new(Array.Empty<DisplayMonitor>(), null);

    public DisplayMonitor? Primary => Monitors.FirstOrDefault(m => m.IsPrimary) ?? Monitors.FirstOrDefault();
}
