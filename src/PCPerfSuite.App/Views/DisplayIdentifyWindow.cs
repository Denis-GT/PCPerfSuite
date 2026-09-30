using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PCPerfSuite.App.Interop;
using PCPerfSuite.Core.Hardware.Displays;

namespace PCPerfSuite.App.Views;

/// <summary>
/// « Identifier » : un grand numéro au centre d'un écran pendant quelques secondes, celui de la liste de l'onglet
/// Overlay. Traversante pour la souris et jamais active, comme l'overlay : elle ne dérange pas un jeu en cours.
/// </summary>
public sealed class DisplayIdentifyWindow : Window
{
    private static readonly TimeSpan Duration = TimeSpan.FromSeconds(3);

    /// <summary>Au plus deux replacements après un changement d'échelle : WPF redimensionne la fenêtre quand elle arrive
    /// sur un écran d'un autre DPI, ce qui décale son centre.</summary>
    private const int MaxDpiReplacements = 2;

    private readonly DisplayMonitor _monitor;
    private readonly DispatcherTimer _timer;
    private IntPtr _hwnd;
    private int _dpiReplacements;

    private DisplayIdentifyWindow(DisplayMonitor monitor, int number)
    {
        _monitor = monitor;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        IsHitTestVisible = false;
        Focusable = false;
        Title = "PCPerfSuite";

        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xD9, 0x10, 0x14, 0x1C)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x4C, 0x8D, 0xF6)),
            BorderThickness = new Thickness(3),
            CornerRadius = new CornerRadius(24),
            Padding = new Thickness(56, 16, 56, 24),
            Child = new TextBlock
            {
                Text = number.ToString(),
                FontSize = 160,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
            },
        };

        ClickThroughWindow.Apply(this);
        SourceInitialized += (_, _) => _hwnd = new WindowInteropHelper(this).Handle;
        Loaded += (_, _) => Center();
        DpiChanged += (_, _) =>
        {
            if (++_dpiReplacements <= MaxDpiReplacements) Dispatcher.BeginInvoke(Center, DispatcherPriority.Loaded);
        };

        _timer = new DispatcherTimer { Interval = Duration };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            Close();
        };
    }

    public static void ShowOn(DisplayMonitor monitor, int number)
    {
        try
        {
            var window = new DisplayIdentifyWindow(monitor, number);
            window.Show();
            window._timer.Start();
        }
        catch
        {
            // Un numéro qui ne s'affiche pas ne change rien au réglage.
        }
    }

    private void Center()
    {
        PixelRect bounds = _monitor.Bounds;
        int width = (int)Math.Round(ActualWidth * _monitor.Scale);
        int height = (int)Math.Round(ActualHeight * _monitor.Scale);
        WindowPlacement.MoveTo(_hwnd, new PixelPoint(bounds.Left + (bounds.Width - width) / 2, bounds.Top + (bounds.Height - height) / 2));
    }
}
