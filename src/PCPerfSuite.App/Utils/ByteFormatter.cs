namespace PCPerfSuite.App.Utils;

public static class ByteFormatter
{
    private static readonly string[] Units = { "o", "Ko", "Mo", "Go", "To" };

    public static string Format(long bytes)
    {
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{value:0} {Units[unit]}" : $"{value:0.#} {Units[unit]}";
    }
}
