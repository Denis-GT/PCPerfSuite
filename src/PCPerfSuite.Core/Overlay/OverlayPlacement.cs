using PCPerfSuite.Core.Hardware.Displays;

namespace PCPerfSuite.Core.Overlay;

/// <summary>
/// Position de l'overlay fenêtre sur un écran, en pixels physiques, isolée ici pour être testée sans écran.
///
/// Tout est ramené aux pixels de l'écran cible : la taille de la fenêtre et les marges sont en DIP (unités WPF), donc
/// multipliées par l'échelle de CET écran, et le résultat est posé par SetWindowPos. Left/Top en DIP seraient faux sur
/// un écran d'une autre échelle : WPF les convertit avec le DPI de l'écran où la fenêtre se trouve encore
/// (dotnet/wpf #4127).
/// </summary>
public static class OverlayPlacement
{
    /// <summary>Coin haut-gauche de la fenêtre. L'écran entier est visé (pas la zone de travail) : en jeu, la barre des
    /// tâches est masquée. La fenêtre reste dans l'écran ; plus grande que lui, elle se colle en haut à gauche.</summary>
    /// <param name="bounds">Écran cible en pixels physiques (négatifs pour un écran à gauche ou au-dessus du principal).</param>
    /// <param name="scale">Échelle de l'écran cible (1,5 = 150 %).</param>
    public static PixelPoint Compute(OverlayAnchor anchor, double marginXDip, double marginYDip, PixelRect bounds, double scale,
        double widthDip, double heightDip)
    {
        if (!(scale > 0) || double.IsInfinity(scale)) scale = 1;

        int width = ToPixels(widthDip, scale);
        int height = ToPixels(heightDip, scale);
        int marginX = ToPixels(marginXDip, scale);
        int marginY = ToPixels(marginYDip, scale);

        int x = anchor switch
        {
            OverlayAnchor.TopLeft or OverlayAnchor.MiddleLeft or OverlayAnchor.BottomLeft => bounds.Left + marginX,
            OverlayAnchor.TopCenter or OverlayAnchor.MiddleCenter or OverlayAnchor.BottomCenter => bounds.Left + (bounds.Width - width) / 2,
            _ => bounds.Right - width - marginX,
        };

        int y = anchor switch
        {
            OverlayAnchor.TopLeft or OverlayAnchor.TopCenter or OverlayAnchor.TopRight => bounds.Top + marginY,
            OverlayAnchor.MiddleLeft or OverlayAnchor.MiddleCenter or OverlayAnchor.MiddleRight => bounds.Top + (bounds.Height - height) / 2,
            _ => bounds.Bottom - height - marginY,
        };

        return new PixelPoint(Keep(x, bounds.Left, bounds.Right - width), Keep(y, bounds.Top, bounds.Bottom - height));
    }

    private static int ToPixels(double dip, double scale)
        => double.IsFinite(dip) && dip > 0 ? (int)Math.Round(dip * scale, MidpointRounding.AwayFromZero) : 0;

    /// <summary>Borne dans [min, max] ; si la fenêtre ne tient pas (max &lt; min), elle se colle à min.</summary>
    private static int Keep(int value, int min, int max) => max < min ? min : Math.Clamp(value, min, max);
}
