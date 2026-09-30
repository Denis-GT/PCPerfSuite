using PCPerfSuite.Core.Compatibility;

namespace PCPerfSuite.Core.Tests;

/// <summary>Le rapport copié ne montre pas le nom du compte Windows, caché dans les chemins du profil.</summary>
public class ReportPrivacyTests
{
    private const string LocalAppData = @"C:\Users\jdupont\AppData\Local";
    private const string UserProfile = @"C:\Users\jdupont";

    [Fact]
    public void Le_dossier_local_devient_LOCALAPPDATA_et_le_reste_du_profil_USERPROFILE()
    {
        string text = @"Détail dans C:\Users\jdupont\AppData\Local\PCPerfSuite\erreurs.log. "
                      + @"La tâche lance C:\Users\jdupont\Downloads\PCPerfSuite\PCPerfSuite.exe.";

        string masked = ReportPrivacy.MaskUserFolders(text, LocalAppData, UserProfile);

        Assert.Equal(@"Détail dans %LOCALAPPDATA%\PCPerfSuite\erreurs.log. "
                     + @"La tâche lance %USERPROFILE%\Downloads\PCPerfSuite\PCPerfSuite.exe.", masked);
        Assert.DoesNotContain("jdupont", masked);
    }

    [Fact]
    public void La_casse_du_chemin_n_empeche_pas_le_masquage()
    {
        string masked = ReportPrivacy.MaskUserFolders(@"c:\users\JDUPONT\appdata\local\x", LocalAppData, UserProfile);

        Assert.Equal(@"%LOCALAPPDATA%\x", masked);
    }

    [Fact]
    public void Sans_dossier_connu_le_texte_ne_change_pas()
    {
        Assert.Equal(@"C:\Program Files\PCPerfSuite", ReportPrivacy.MaskUserFolders(@"C:\Program Files\PCPerfSuite", "", ""));
    }
}
