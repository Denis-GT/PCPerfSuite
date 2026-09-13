using System.Windows.Controls;
using PCPerfSuite.App.ViewModels;
using PCPerfSuite.Core.Hardware;

namespace PCPerfSuite.App.Views;

public partial class MonitoringView : UserControl
{
    private MonitoringViewModel? _vm;

    public MonitoringView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (_vm is not null) _vm.SnapshotUpdated -= OnSnapshotUpdated;
            _vm = e.NewValue as MonitoringViewModel;
            if (_vm is not null) _vm.SnapshotUpdated += OnSnapshotUpdated;
        };
    }

    private void OnSnapshotUpdated(HardwareSnapshot snapshot)
    {
        CpuSparkline.Push(snapshot.Cpu.LoadPercent ?? 0);
        if (snapshot.Gpu is { } gpu) GpuSparkline.Push(gpu.LoadPercent ?? 0);
        MemSparkline.Push(snapshot.Memory.LoadPercent ?? 0);
    }
}
