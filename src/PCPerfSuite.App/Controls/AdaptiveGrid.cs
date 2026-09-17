using System.Windows;
using System.Windows.Controls;

namespace PCPerfSuite.App.Controls;

/// <summary>
/// Grille à colonnes égales dont le nombre suit la largeur disponible : autant de colonnes que possible
/// sans passer sous <see cref="MinItemWidth"/>, puis les éléments s'étirent pour remplir la ligne. Évite le
/// bord droit en dents de scie d'un WrapPanel à largeur fixe.
/// </summary>
public sealed class AdaptiveGrid : Panel
{
    public static readonly DependencyProperty MinItemWidthProperty = DependencyProperty.Register(
        nameof(MinItemWidth), typeof(double), typeof(AdaptiveGrid),
        new FrameworkPropertyMetadata(280d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing), typeof(double), typeof(AdaptiveGrid),
        new FrameworkPropertyMetadata(14d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double MinItemWidth
    {
        get => (double)GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }

    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    private int ColumnsFor(double width)
    {
        if (double.IsInfinity(width) || width <= 0) return Math.Max(1, InternalChildren.Count);
        return Math.Max(1, (int)((width + Spacing) / (MinItemWidth + Spacing)));
    }

    private double CellWidth(double width, int columns)
        => double.IsInfinity(width) ? MinItemWidth : Math.Max(0, (width - Spacing * (columns - 1)) / columns);

    protected override Size MeasureOverride(Size availableSize)
    {
        int columns = ColumnsFor(availableSize.Width);
        double cellWidth = CellWidth(availableSize.Width, columns);

        double height = 0, rowHeight = 0;
        int visible = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(cellWidth, double.PositiveInfinity));
            if (child.Visibility == Visibility.Collapsed) continue;

            if (visible > 0 && visible % columns == 0)
            {
                height += rowHeight + Spacing;
                rowHeight = 0;
            }
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            visible++;
        }
        height += rowHeight;

        double width = double.IsInfinity(availableSize.Width)
            ? Math.Min(visible, columns) * (cellWidth + Spacing) - (visible > 0 ? Spacing : 0)
            : availableSize.Width;
        return new Size(Math.Max(0, width), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        int columns = ColumnsFor(finalSize.Width);
        double cellWidth = CellWidth(finalSize.Width, columns);

        // Hauteur de chaque ligne = la plus grande tuile de la ligne, pour aligner les bas de tuiles.
        var visibleChildren = InternalChildren.Cast<UIElement>().Where(c => c.Visibility != Visibility.Collapsed).ToList();
        double y = 0;
        for (int row = 0; row * columns < visibleChildren.Count; row++)
        {
            var rowChildren = visibleChildren.Skip(row * columns).Take(columns).ToList();
            double rowHeight = rowChildren.Max(c => c.DesiredSize.Height);
            for (int i = 0; i < rowChildren.Count; i++)
            {
                rowChildren[i].Arrange(new Rect(i * (cellWidth + Spacing), y, cellWidth, rowHeight));
            }
            y += rowHeight + Spacing;
        }

        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed) child.Arrange(new Rect());
        }
        return finalSize;
    }
}
