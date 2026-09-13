using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using PCPerfSuite.Core.Hardware;

namespace PCPerfSuite.App.Converters;

public sealed class DiskHealthStatusToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string key = value switch
        {
            DiskHealthStatus.Healthy => "AccentBrush2",
            DiskHealthStatus.Warning => "WarnBrush",
            DiskHealthStatus.Unhealthy => "DangerBrush",
            _ => "TextSecondaryBrush",
        };
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
