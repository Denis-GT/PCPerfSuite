using System.Windows;
using System.Windows.Controls;
using PCPerfSuite.App.Controls;
using PCPerfSuite.App.ViewModels;

namespace PCPerfSuite.App.Views;

public partial class FanCurvesView : UserControl
{
    public FanCurvesView()
    {
        InitializeComponent();
        AddHandler(FanCurveEditor.EditingCompletedEvent, new RoutedEventHandler(OnCurveEditingCompleted));
    }

    private void OnCurveEditingCompleted(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement { DataContext: FanControlItemViewModel item })
        {
            item.NotifyPointsEdited();
        }
    }
}
