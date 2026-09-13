using Microsoft.Win32;

namespace PCPerfSuite.Core.PowerSettings;

internal static class RegistryHelper
{
    public static int? ReadDword(RegistryHive hive, string subKey, string valueName, int? defaultValue = null)
    {
        using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using RegistryKey? key = baseKey.OpenSubKey(subKey, writable: false);
        object? value = key?.GetValue(valueName);
        if (value is int i) return i;
        return defaultValue;
    }

    public static void WriteDword(RegistryHive hive, string subKey, string valueName, int value)
    {
        using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using RegistryKey key = baseKey.CreateSubKey(subKey, writable: true)
            ?? throw new InvalidOperationException($"Impossible d'ouvrir/créer la clé {subKey}");
        key.SetValue(valueName, value, RegistryValueKind.DWord);
    }
}
