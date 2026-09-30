using PCPerfSuite.Core.Hardware.Displays;

namespace PCPerfSuite.Core.Tests;

/// <summary>Écrans factices pour les tests du service des écrans et de l'overlay.</summary>
internal static class DisplayTestData
{
    /// <summary>Identifiant fabricant « DEL » tel que le rend DisplayConfig (octets inversés).</summary>
    public const ushort Dell = 0xAC10;

    public static DisplayTarget Target(string? name, string? path, ushort? manufacturer = Dell, ushort? product = 0x4123,
        DisplayOutputKind output = DisplayOutputKind.DisplayPort, double? hz = 60)
        => new(name, path, manufacturer, manufacturer is { } m ? DisplayNames.DecodeManufacturer(m) : null, product, output, 0,
            0x1234, @"\\?\PCI#VEN_10DE&DEV_2786", hz);

    public static DisplayMonitor Monitor(int handle, PixelRect bounds, int dpi = 96, bool primary = false, params DisplayTarget[] targets)
        => new(new IntPtr(handle), $@"\\.\DISPLAY{handle}", bounds, bounds, dpi, true, primary, "NVIDIA GeForce RTX 4070", targets);

    public static DisplayTopologySnapshot Snapshot(params DisplayMonitor[] monitors) => new(monitors, null);

    public static readonly IReadOnlyDictionary<string, string> NoSerials = new Dictionary<string, string>();
}
