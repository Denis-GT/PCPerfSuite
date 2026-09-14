using System.Windows.Controls;

namespace PCPerfSuite.App.Controls;

/// <summary>Liste à cocher des métriques du catalogue, groupée par catégorie — partagée par l'onglet Overlay
/// et "Mes métriques" (DataContext : MetricSelectionViewModel).</summary>
public partial class MetricPicker : UserControl
{
    public MetricPicker() => InitializeComponent();
}
