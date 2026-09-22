using System.Security;
using Microsoft.Win32;

namespace PCPerfSuite.Core.PowerSettings;

internal static class RegistryHelper
{
    /// <summary>
    /// Lecture best-effort pour une clé absente ou illisible : rend alors <paramref name="defaultValue"/>.
    ///
    /// Une lecture REFUSÉE fait exception et continue de lever. Les appelants ne traitent pas l'absence
    /// comme une inconnue : plusieurs traduisent null en un état affirmé — « ce réglage est désactivé ».
    /// Sur un poste où une stratégie de groupe verrouille la clé, avaler le refus afficherait un
    /// interrupteur sur « off » pour un réglage en réalité actif, et l'utilisateur découvrirait le
    /// problème en essayant de le changer. L'exception, elle, fait afficher « état inconnu » et sa cause.
    /// </summary>
    public static int? ReadDword(RegistryHive hive, string subKey, string valueName, int? defaultValue = null)
    {
        try
        {
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using RegistryKey? key = baseKey.OpenSubKey(subKey, writable: false);
            object? value = key?.GetValue(valueName);
            if (value is int i) return i;
            return defaultValue;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
        {
            throw new InvalidOperationException(
                $"Lecture refusée dans {subKey}. Cette clé est probablement verrouillée par une stratégie de "
                + "groupe ou un antivirus : l'état réel de ce réglage ne peut pas être lu.", ex);
        }
        catch (Exception)
        {
            return defaultValue;
        }
    }

    /// <summary>
    /// Écrit une valeur DWORD. Volontairement la seule méthode de ce fichier qui lève : c'est une action
    /// demandée par l'utilisateur, et un échec silencieux lui laisserait croire que son réglage est pris en
    /// compte alors que rien n'a changé. La règle « best-effort » vaut pour la lecture des capteurs, pas
    /// pour une écriture explicite.
    ///
    /// Le message est reformulé pour nommer la cause : ces clés sont souvent verrouillées par une stratégie
    /// de groupe ou un antivirus, et « L'accès au chemin est refusé » n'apprend rien à personne.
    /// </summary>
    public static void WriteDword(RegistryHive hive, string subKey, string valueName, int value)
    {
        try
        {
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using RegistryKey key = baseKey.CreateSubKey(subKey, writable: true)
                ?? throw new InvalidOperationException($"Impossible d'ouvrir ou de créer la clé {subKey}.");
            key.SetValue(valueName, value, RegistryValueKind.DWord);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
        {
            throw new InvalidOperationException(
                $"Écriture refusée dans {subKey}. Cette clé est probablement verrouillée par une stratégie de "
                + "groupe ou un antivirus, à moins que PCPerfSuite ne doive être relancée en administrateur.", ex);
        }
    }
}
