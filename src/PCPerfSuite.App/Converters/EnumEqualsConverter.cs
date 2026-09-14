using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace PCPerfSuite.App.Converters;

/// <summary>Pont enum ↔ RadioButton.IsChecked : compare la valeur bindée au nom passé en
/// ConverterParameter, et reconstruit l'enum à partir de ce nom quand on coche le bouton.</summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value?.ToString() == parameter as string;

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => value is true && parameter is string s ? Enum.Parse(targetType, s) : Binding.DoNothing;
}

/// <summary>Même comparaison qu'EnumEqualsConverter, mais vers une Visibility — pour n'afficher une
/// section (ex: réglages "Manuel") que quand l'enum bindé vaut la valeur passée en paramètre.</summary>
public sealed class EnumEqualsVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value?.ToString() == parameter as string ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
