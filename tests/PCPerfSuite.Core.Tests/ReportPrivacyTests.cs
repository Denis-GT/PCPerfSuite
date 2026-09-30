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
    public void Le_profil_d_un_autre_compte_est_masque_aussi()
    {
        // App élevée sous le compte Admin, lancée depuis le profil de la personne devant l'écran.
        string masked = ReportPrivacy.MaskUserFolders(@"lance C:\Users\alice\Downloads\PCPerfSuite.exe",
            @"C:\Users\Admin\AppData\Local", @"C:\Users\Admin");

        Assert.Equal(@"lance C:\Users\(compte)\Downloads\PCPerfSuite.exe", masked);
    }

    [Theory]
    [InlineData(@"C:\Users\jdupontx\notes.txt")]
    [InlineData(@"C:\Users\jdupont.martin\notes.txt")]
    public void Un_nom_de_compte_plus_long_n_est_pas_coupe_en_deux(string path)
    {
        // Masqué en entier comme autre profil, jamais en « %USERPROFILE%x » qui laisserait deviner le nom.
        Assert.Equal(@"C:\Users\(compte)\notes.txt", ReportPrivacy.MaskUserFolders(path, LocalAppData, UserProfile));
    }

    [Fact]
    public void Un_nom_de_compte_avec_un_point_est_masque_en_entier()
    {
        Assert.Equal(@"%LOCALAPPDATA%\PCPerfSuite\erreurs.log",
            ReportPrivacy.MaskUserFolders(@"C:\Users\d.gatev\AppData\Local\PCPerfSuite\erreurs.log",
                @"C:\Users\d.gatev\AppData\Local", @"C:\Users\d.gatev"));
    }

    [Fact]
    public void Sans_dossier_connu_le_texte_ne_change_pas()
    {
        Assert.Equal(@"C:\Program Files\PCPerfSuite", ReportPrivacy.MaskUserFolders(@"C:\Program Files\PCPerfSuite", "", ""));
    }
}
