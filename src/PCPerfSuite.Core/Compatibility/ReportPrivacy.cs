using System.Text.RegularExpressions;

namespace PCPerfSuite.Core.Compatibility;

/// <summary>
/// Ce qu'un rapport copié ne doit pas contenir. Le nom du compte Windows se glisse dans tous les chemins du profil
/// (journal des erreurs, témoin ADLX, exe lancé depuis Téléchargements, message d'une exception…) : le rapport les
/// écrit avec %LOCALAPPDATA% et %USERPROFILE% à la place, et tout autre profil (app lancée sous un autre compte, voir
/// SessionUser.IsOtherProfile) en « (compte) ». L'écran, lui, garde le vrai chemin.
/// </summary>
public static class ReportPrivacy
{
    public const string OtherAccount = "(compte)";

    public static string MaskUserFolders(string text)
        => MaskUserFolders(text,
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>LocalAppData d'abord : il est dans le profil, et masquer le profil d'abord laisserait
    /// « %USERPROFILE%\AppData\Local » au lieu de « %LOCALAPPDATA% ». Puis tout autre dossier du parent des profils
    /// (C:\Users\autre) : l'app élevée tourne parfois sous un autre compte que celui devant l'écran.</summary>
    public static string MaskUserFolders(string text, string localAppData, string userProfile)
    {
        string masked = ReplaceFolder(text, localAppData, "%LOCALAPPDATA%");
        masked = ReplaceFolder(masked, userProfile, "%USERPROFILE%");

        string profile = userProfile.TrimEnd('\\', '/');
        string? profilesRoot = profile.Length == 0 ? null : Path.GetDirectoryName(profile);
        if (string.IsNullOrEmpty(profilesRoot)) return masked;

        string root = profilesRoot.TrimEnd('\\');
        return Regex.Replace(masked, Regex.Escape(root) + @"\\[^\\/:*?""<>|\r\n]+" + SegmentEnd,
            _ => $"{root}\\{OtherAccount}", RegexOptions.IgnoreCase);
    }

    /// <summary>Le dossier s'arrête là : « C:\Users\bob » ne doit mordre ni dans « C:\Users\bobby » ni dans
    /// « C:\Users\bob.martin » (un point fait partie de bien des noms de compte).</summary>
    private const string SegmentEnd = @"(?=[\\/\s""';)\]]|$)";

    private static string ReplaceFolder(string text, string folder, string variable)
    {
        string trimmed = folder.TrimEnd('\\', '/');
        return trimmed.Length == 0
            ? text
            : Regex.Replace(text, Regex.Escape(trimmed) + SegmentEnd, variable.Replace("$", "$$"), RegexOptions.IgnoreCase);
    }
}
