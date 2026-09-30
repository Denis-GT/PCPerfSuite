namespace PCPerfSuite.Core.Compatibility;

/// <summary>
/// Ce qu'un rapport copié ne doit pas contenir. Le nom du compte Windows se glisse dans tous les chemins du profil
/// (journal des erreurs, témoin ADLX, exe lancé depuis Téléchargements, message d'une exception…) : le rapport les
/// écrit avec %LOCALAPPDATA% et %USERPROFILE% à la place. L'écran, lui, garde le vrai chemin.
/// </summary>
public static class ReportPrivacy
{
    public static string MaskUserFolders(string text)
        => MaskUserFolders(text,
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>LocalAppData d'abord : il est dans le profil, et masquer le profil d'abord laisserait
    /// « %USERPROFILE%\AppData\Local » au lieu de « %LOCALAPPDATA% ».</summary>
    public static string MaskUserFolders(string text, string localAppData, string userProfile)
    {
        string masked = Replace(text, localAppData, "%LOCALAPPDATA%");
        return Replace(masked, userProfile, "%USERPROFILE%");
    }

    private static string Replace(string text, string folder, string variable)
    {
        string trimmed = folder.TrimEnd('\\', '/');
        return trimmed.Length == 0 ? text : text.Replace(trimmed, variable, StringComparison.OrdinalIgnoreCase);
    }
}
