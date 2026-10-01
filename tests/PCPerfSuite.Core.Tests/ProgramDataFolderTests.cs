using System.Diagnostics;
using System.Security.AccessControl;
using PCPerfSuite.Core.Installations;

namespace PCPerfSuite.Core.Tests;

/// <summary>
/// Le dossier sécurisé %ProgramData%\PCPerfSuite : seuls les administrateurs, SYSTEM et TrustedInstaller peuvent en
/// être propriétaires ou y écrire, et une jonction n'est jamais suivie. Les règles d'accès sont jugées sur des
/// descriptions SDDL ; rien n'est créé dans le vrai %ProgramData%.
/// </summary>
public class ProgramDataFolderTests
{
    private static string? Judge(string sddl)
    {
        var security = new DirectorySecurity();
        security.SetSecurityDescriptorSddlForm(sddl);
        return ProgramDataFolder.DescribeUntrusted(security);
    }

    [Fact]
    public void La_liste_d_acces_creee_par_l_app_est_sure()
    {
        Assert.Null(Judge(ProgramDataFolder.SecureSddl));
    }

    [Fact]
    public void Un_dossier_appartenant_a_SYSTEM_ou_TrustedInstaller_est_sur()
    {
        Assert.Null(Judge("O:SYG:SYD:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)"));
        Assert.Null(Judge("O:S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464G:SYD:PAI(A;OICI;FA;;;BA)"));
    }

    [Fact]
    public void Un_dossier_prepare_par_un_compte_standard_est_refuse()
    {
        // Propriétaire : Utilisateurs (BU), comme un dossier créé dans %ProgramData% par n'importe quelle session.
        Assert.Contains("appartient", Judge("O:BUG:BUD:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)"));
    }

    [Theory]
    [InlineData("(A;OICI;FA;;;BU)")]
    [InlineData("(A;OICI;0x1301bf;;;AU)")]
    [InlineData("(A;OICI;GA;;;WD)")]
    [InlineData("(A;OICI;GW;;;IU)")]
    [InlineData("(A;CI;0x4;;;BU)")]
    [InlineData("(A;OICIIO;FA;;;CO)")]
    [InlineData("(A;;WD;;;BU)")]
    public void Un_droit_d_ecriture_donne_a_un_autre_compte_fait_refuser_le_dossier(string ace)
    {
        Assert.Contains("modifier", Judge($"O:BAG:BAD:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA){ace}"));
    }

    [Theory]
    [InlineData("(A;OICI;0x1200a9;;;WD)")]
    [InlineData("(D;OICI;FA;;;BU)")]
    public void Lecture_pour_tous_ou_refus_explicite_ne_genent_pas(string ace)
    {
        Assert.Null(Judge($"O:BAG:BAD:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA){ace}"));
    }

    [Theory]
    [InlineData("Tools", true)]
    [InlineData("cpu-z", true)]
    [InlineData("17.1.5.0", true)]
    [InlineData("..", false)]
    [InlineData(".", false)]
    [InlineData("a\\b", false)]
    [InlineData("a/b", false)]
    [InlineData("C:", false)]
    [InlineData("nom.", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Seuls_des_noms_simples_servent_de_sous_dossiers(string? name, bool expected)
    {
        Assert.Equal(expected, ProgramDataFolder.IsPlainName(name));
    }

    [Fact]
    public void Un_segment_dangereux_est_refuse_avant_toute_creation()
    {
        using var temp = new TempDirectory();
        string root = Path.Combine(temp.Root, "PCPerfSuite");

        SecureFolderResult result = ProgramDataFolder.TryEnsure(root, new[] { "Tools", ".." });

        Assert.False(result.IsReady);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void Une_jonction_a_la_place_du_dossier_est_refusee()
    {
        using var temp = new TempDirectory();
        string target = Path.Combine(temp.Root, "ailleurs");
        Directory.CreateDirectory(target);
        string root = Path.Combine(temp.Root, "PCPerfSuite");
        if (!TryCreateJunction(root, target)) return;

        SecureFolderResult result = ProgramDataFolder.TryEnsure(root, new[] { "Tools" });

        Assert.False(result.IsReady);
        Assert.Contains("lien", result.Error);
        Assert.False(Directory.Exists(Path.Combine(target, "Tools")));
    }

    [Fact]
    public void La_creation_ne_leve_jamais_meme_sans_droits_administrateur()
    {
        using var temp = new TempDirectory();

        // Sans élévation, le propriétaire « Administrateurs » ne peut pas être posé : refus, avec sa raison, jamais une
        // exception. Élevé, le dossier est créé et vérifié.
        SecureFolderResult result = ProgramDataFolder.TryEnsure(Path.Combine(temp.Root, "PCPerfSuite"), new[] { "Tools", "cpu-z" });

        Assert.True(result.IsReady || !string.IsNullOrEmpty(result.Error));
    }

    [Fact]
    public void L_etat_du_vrai_dossier_se_lit_sans_rien_creer()
    {
        bool existed = Directory.Exists(ProgramDataFolder.RootPath);

        SecureFolderResult result = ProgramDataFolder.Inspect();

        Assert.Equal(existed, Directory.Exists(ProgramDataFolder.RootPath));
        if (!existed) Assert.True(result.IsAbsent);
    }

    /// <summary>Une jonction se crée sans droits particuliers (mklink /J) ; si cmd échoue, le test n'a rien à vérifier.</summary>
    private static bool TryCreateJunction(string link, string target)
    {
        try
        {
            using Process? process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            process?.WaitForExit(10_000);
            return Directory.Exists(link) && (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return false;
        }
    }
}
