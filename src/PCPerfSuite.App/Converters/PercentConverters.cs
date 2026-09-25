using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

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

/// <summary>Visible quand la valeur liée == ConverterParameter (comparaison de chaînes) — sert à garder
/// plusieurs vues instanciées en permanence et à juste basculer laquelle est visible (voir MainWindow),
/// plutôt qu'un ContentControl qui détruirait/recréerait la vue à chaque changement d'onglet.</summary>
public sealed class StringEqualsToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.Equals(value as string, parameter as string, StringComparison.Ordinal)
            ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Fond bleu plus ou moins transparent derrière une valeur, pour repérer d'un coup d'œil les gros
/// consommateurs dans une longue liste. Reçoit une intensité de 0 à 1 (null si la valeur est absente) et
/// choisit parmi <see cref="Steps"/> pinceaux figés une fois pour toutes : ce converter est appelé pour chaque
/// cellule visible à chaque relevé, y allouer un pinceau serait du gaspillage pur. L'échelle (linéaire,
/// racine, logarithme) est décidée par celui qui fournit l'intensité, pas ici.</summary>
public sealed class IntensityToBlueBrushConverter : IValueConverter
{
    private const int Steps = 16;

    /// <summary>Opacité la plus faible et la plus forte : assez pour se voir dès la première marche, pas assez
    /// pour gêner la lecture du texte à la dernière.</summary>
    private const byte MinAlpha = 0x0C;
    private const byte MaxAlpha = 0x66;

    private static readonly SolidColorBrush None = Frozen(Colors.Transparent);
    private static readonly SolidColorBrush[] Levels = BuildLevels();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // Une valeur absente n'est pas une valeur nulle : rien à teinter.
        if (value is not double intensity || double.IsNaN(intensity)) return None;

        int level = (int)Math.Round(Math.Clamp(intensity, 0, 1) * (Steps - 1));
        return Levels[level];
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static SolidColorBrush[] BuildLevels()
    {
        // Le bleu d'accentuation du thème (AccentColor, #0A84FF), à des opacités croissantes.
        var levels = new SolidColorBrush[Steps];
        for (int i = 0; i < Steps; i++)
        {
            byte alpha = (byte)(MinAlpha + (MaxAlpha - MinAlpha) * i / (Steps - 1));
            levels[i] = Frozen(Color.FromArgb(alpha, 0x0A, 0x84, 0xFF));
        }
        return levels;
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

/// <summary>Atténue une ligne dont le processus vient de se terminer : elle reste affichée le temps que le
/// pointeur quitte la liste, pour que rien ne remonte d'un cran sous le curseur.</summary>
public sealed class BoolToDimOpacityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? 0.45 : 1.0;

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
