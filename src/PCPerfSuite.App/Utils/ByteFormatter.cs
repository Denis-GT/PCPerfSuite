namespace PCPerfSuite.App.Utils;

public static class ByteFormatter
{
    private static readonly string[] Units = { "o", "Ko", "Mo", "Go", "To" };

    public static string Format(long bytes)
    {
        (string value, string unit) = Split(bytes);
        return $"{value} {unit}";
    }

    /// <summary>Valeur mise à l'échelle et unité séparées, pour les affichages qui ne les stylent pas pareil.</summary>
    /// <param name="fixedDecimal">Toujours une décimale au-delà de l'octet ("12,0" plutôt que "12") : pour une valeur qui
    /// change sans cesse, comme un débit, la largeur du texte ne saute plus d'un relevé à l'autre.</param>
    public static (string Value, string Unit) Split(double bytes, bool fixedDecimal = false)
    {
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return (unit == 0 ? $"{value:0}" : fixedDecimal ? $"{value:0.0}" : $"{value:0.#}", Units[unit]);
    }

    public static string FormatRate(double? bytesPerSecond)
        => bytesPerSecond is { } v ? $"{Format((long)Math.Round(Math.Max(0, v)))}/s" : "--";
}
