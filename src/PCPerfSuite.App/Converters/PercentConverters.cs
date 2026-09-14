using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace PCPerfSuite.App.Converters;

/// <summary>Convertit un pourcentage 0-100 (ou null) en largeur "étoile" pour un ColumnDefinition — sert à dessiner
/// une barre de charge sans dépendre du template interne de ProgressBar (qu'on ne peut pas fiabiliser à l'oeil).</summary>
public sealed class PercentToStarConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double percent = ToPercent(value);
        return new GridLength(percent, GridUnitType.Star);
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    internal static double ToPercent(object? value)
    {
        double raw = value switch
        {
            double d => d,
            float f => f,
            int i => i,
            _ => 0,
        };
        return Math.Clamp(raw, 0, 100);
    }
}

public sealed class PercentRemainderToStarConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double percent = PercentToStarConverter.ToPercent(value);
        return new GridLength(Math.Max(0.01, 100 - percent), GridUnitType.Star);
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Déballe un nombre nullable (float?/double?) vers un double non-nullable, 0 si absent —
/// pour les DependencyProperty numériques (ex: MeterBar.Value) qui n'acceptent pas null.</summary>
public sealed class NullableNumberToDoubleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch { double d => d, float f => (double)f, int i => (double)i, _ => 0d };

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class ZeroCountToVisibilityConverter : IValueConverter
{
    /// <summary>Visible quand la collection est vide (int count == 0) — pour un message "rien à afficher".</summary>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int i && i == 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && !b;

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && !b;
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility.Visible;
}

/// <summary>Inverse de BoolToVisibilityConverter : affiche l'élément quand la valeur est fausse —
/// pour les messages "cette fonction n'est pas disponible sur ta carte".</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Formate une valeur nullable avec une unité, ou affiche "--" si absente (capteur non disponible sur cette config).</summary>
public sealed class NullableMetricConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null) return "--";
        string unit = parameter as string ?? "";
        double d = value switch { double dd => dd, float f => f, _ => 0 };
        return $"{d:0.#}{unit}";
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
