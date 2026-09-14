using System.Windows;
using System.Windows.Controls;
using PCPerfSuite.App.Controls;
using PCPerfSuite.App.ViewModels;

namespace PCPerfSuite.App.Views;

public partial class GpuControlView : UserControl
{
    public GpuControlView()
    {
        InitializeComponent();
        AddHandler(FanCurveEditor.EditingCompletedEvent, new RoutedEventHandler(OnCurveEditingCompleted));
    }

    private void OnCurveEditingCompleted(object sender, RoutedEventArgs e)
    {
        if (DataContext is GpuControlViewModel vm)
        {
            vm.NotifyPointsEdited();
        }
    }
}
