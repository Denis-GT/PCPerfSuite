using System.IO.Compression;
using System.Text;
using PCPerfSuite.Core.Installations;

namespace PCPerfSuite.Core.Tests;

/// <summary>
/// Extraction des archives téléchargées : une entrée qui sortirait du dossier choisi (« zip-slip ») fait refuser toute
/// l'archive avant la moindre écriture, la taille décompressée est bornée, et l'installeur d'une archive n'en sort que
/// s'il est seul à porter le nom attendu.
/// </summary>
public class SafeZipExtractorTests
{
    private static string MakeZip(TempDirectory temp, params (string Name, string Content)[] entries)
    {
        string path = temp.File($"{Guid.NewGuid():N}.zip");
        using (ZipArchive archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            foreach ((string name, string content) in entries)
            {
                ZipArchiveEntry entry = archive.CreateEntry(name);
                using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
                writer.Write(content);
            }
        }

        return path;
    }

    private static string Folder(TempDirectory temp, string name)
    {
        string folder = Path.Combine(temp.Root, name);
        Directory.CreateDirectory(folder);
        return folder;
    }

    [Fact]
    public void Une_archive_ordinaire_est_extraite_avec_ses_dossiers()
    {
        using var temp = new TempDirectory();
        string zip = MakeZip(temp, ("outil.exe", "MZ"), ("lisezmoi.txt", "bonjour"), ("donnees/profil.ini", "a=1"), ("vide/", ""));
        string destination = Folder(temp, "dest");

        ZipExtractionResult result = SafeZipExtractor.ExtractAll(zip, destination, maxTotalBytes: 1024 * 1024);

        Assert.True(result.Succeeded, result.Error);
        Assert.True(File.Exists(Path.Combine(destination, "outil.exe")));
        Assert.Equal("a=1", File.ReadAllText(Path.Combine(destination, "donnees", "profil.ini")));
        Assert.True(Directory.Exists(Path.Combine(destination, "vide")));
    }

    [Theory]
    [InlineData("../evil.exe")]
    [InlineData("..\\evil.exe")]
    [InlineData("dossier/../../evil.exe")]
    [InlineData("dossier\\..\\..\\evil.exe")]
    [InlineData("/evil.exe")]
    [InlineData("\\evil.exe")]
    [InlineData("C:/evil.exe")]
    [InlineData("C:\\Windows\\evil.exe")]
    [InlineData("outil.exe:flux")]
    [InlineData("dossier/.. ./evil.exe")]
    [InlineData("dossier/.../evil.exe")]
    [InlineData("CON")]
    [InlineData("dossier/nul.txt")]
    [InlineData("dossier//evil.exe")]
    public void Une_entree_qui_sortirait_du_dossier_fait_refuser_toute_l_archive(string dangerous)
    {
        using var temp = new TempDirectory();
        string zip = MakeZip(temp, ("ok.txt", "x"), (dangerous, "MZ piégé"));
        string destination = Folder(temp, Path.Combine("a", "b", "dest"));

        ZipExtractionResult result = SafeZipExtractor.ExtractAll(zip, destination, maxTotalBytes: 1024 * 1024);

        Assert.False(result.Succeeded);
        Assert.Contains("dangereux", result.Error);
        Assert.Equal(DownloadFailureKind.Mismatch, result.Failure);

        // Rien n'a été écrit, ni dehors ni dedans : l'archive est jugée en entier avant la première écriture.
        Assert.Empty(Directory.EnumerateFileSystemEntries(destination));
        Assert.False(File.Exists(Path.Combine(temp.Root, "a", "b", "evil.exe")));
        Assert.False(File.Exists(Path.Combine(temp.Root, "a", "evil.exe")));
    }

    [Fact]
    public void L_extraction_dit_ou_elle_en_est_et_s_arrete_quand_on_l_annule()
    {
        using var temp = new TempDirectory();
        string zip = MakeZip(temp, Enumerable.Range(0, 5).Select(i => ($"f{i}.txt", "x")).ToArray());
        var reports = new List<string>();

        ZipExtractionResult done = SafeZipExtractor.ExtractAll(zip, Folder(temp, "a"), 1024 * 1024, progress: new SynchronousProgress(reports.Add));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        ZipExtractionResult stopped = SafeZipExtractor.ExtractAll(zip, Folder(temp, "b"), 1024 * 1024, cancellationToken: cancelled.Token);

        Assert.True(done.Succeeded);
        Assert.Equal("Extraction… 5 / 5 fichiers", reports[^1]);
        Assert.False(stopped.Succeeded);
        Assert.Contains("annulée", stopped.Error);
        Assert.Equal(PCPerfSuite.Core.Installations.DownloadFailureKind.Other, stopped.Failure);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(temp.Root, "b")));
    }

    /// <summary>Progression rappelée tout de suite, sur le même fil (la classe Progress de .NET passerait par le pool).</summary>
    private sealed class SynchronousProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }

    [Fact]
    public void Une_archive_qui_depasse_la_taille_annoncee_est_refusee()
    {
        using var temp = new TempDirectory();
        string zip = MakeZip(temp, ("gros.bin", new string('a', 50_000)));
        string destination = Folder(temp, "dest");

        ZipExtractionResult result = SafeZipExtractor.ExtractAll(zip, destination, maxTotalBytes: 10_000);

        Assert.False(result.Succeeded);
        Assert.Contains("dépasserait", result.Error);
    }

    [Fact]
    public void Une_archive_avec_trop_d_entrees_est_refusee()
    {
        using var temp = new TempDirectory();
        string zip = MakeZip(temp, Enumerable.Range(0, 20).Select(i => ($"f{i}.txt", "x")).ToArray());

        ZipExtractionResult result = SafeZipExtractor.ExtractAll(zip, Folder(temp, "dest"), maxTotalBytes: 1024 * 1024, maxEntries: 10);

        Assert.False(result.Succeeded);
        Assert.Contains("plus de 10", result.Error);
    }

    [Fact]
    public void Un_fichier_qui_n_est_pas_une_archive_est_refuse_sans_lever()
    {
        using var temp = new TempDirectory();
        string notZip = temp.File("faux.zip");
        File.WriteAllText(notZip, "ceci n'est pas un zip");

        ZipExtractionResult result = SafeZipExtractor.ExtractAll(notZip, Folder(temp, "dest"), maxTotalBytes: 1024);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void L_installeur_attendu_sort_seul_de_son_archive()
    {
        using var temp = new TempDirectory();
        string zip = MakeZip(temp, ("RTSSSetup737.exe", "MZ"), ("downloaded_from_www.guru3d.com.txt", "texte"));
        string destination = Folder(temp, "installeur");

        ZipExtractionResult result = SafeZipExtractor.ExtractSingle(zip, "RTSSSetup*.exe", destination, maxBytes: 1024);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(Path.Combine(destination, "RTSSSetup737.exe"), result.ExtractedPath);
        Assert.Single(Directory.EnumerateFiles(destination));
    }

    [Fact]
    public void L_installeur_d_un_sous_dossier_est_pose_a_la_racine_du_dossier_choisi()
    {
        using var temp = new TempDirectory();
        string zip = MakeZip(temp, ("sous/dossier/MSIAfterburnerSetup466.exe", "MZ"));
        string destination = Folder(temp, "installeur");

        ZipExtractionResult result = SafeZipExtractor.ExtractSingle(zip, "MSIAfterburnerSetup*.exe", destination, maxBytes: 1024);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(Path.Combine(destination, "MSIAfterburnerSetup466.exe"), result.ExtractedPath);
    }

    [Fact]
    public void Aucun_ou_plusieurs_installeurs_au_nom_attendu_font_refuser_l_archive()
    {
        using var temp = new TempDirectory();
        string none = MakeZip(temp, ("autre.exe", "MZ"));
        string two = MakeZip(temp, ("RTSSSetup737.exe", "MZ"), ("copie/RTSSSetup738.exe", "MZ"));

        ZipExtractionResult missing = SafeZipExtractor.ExtractSingle(none, "RTSSSetup*.exe", Folder(temp, "a"), maxBytes: 1024);
        ZipExtractionResult ambiguous = SafeZipExtractor.ExtractSingle(two, "RTSSSetup*.exe", Folder(temp, "b"), maxBytes: 1024);

        Assert.False(missing.Succeeded);
        Assert.False(ambiguous.Succeeded);
        Assert.Contains("plusieurs", ambiguous.Error);
    }

    [Fact]
    public void Un_installeur_au_chemin_dangereux_est_refuse()
    {
        using var temp = new TempDirectory();
        string zip = MakeZip(temp, ("../RTSSSetup737.exe", "MZ"));

        ZipExtractionResult result = SafeZipExtractor.ExtractSingle(zip, "RTSSSetup*.exe", Folder(temp, "a"), maxBytes: 1024);

        Assert.False(result.Succeeded);
        Assert.Contains("dangereux", result.Error);
    }

    [Theory]
    [InlineData("RTSSSetup*.exe", "RTSSSetup737.exe", true)]
    [InlineData("RTSSSetup*.exe", "rtsssetup737.EXE", true)]
    [InlineData("RTSSSetup*.exe", "RTSSSetup.exe", true)]
    [InlineData("RTSSSetup*.exe", "RTSSSetup737.exe.txt", false)]
    [InlineData("RTSSSetup*.exe", "XRTSSSetup737.exe", false)]
    [InlineData("*.exe", "a.exe", true)]
    [InlineData("a*b*c", "abc", true)]
    [InlineData("a*b*c", "aXbYc", true)]
    [InlineData("a*b*c", "acb", false)]
    [InlineData("outil.exe", "outil.exe", true)]
    [InlineData("outil.exe", "outil.ex", false)]
    public void Le_motif_ne_connait_que_l_etoile(string pattern, string name, bool expected)
    {
        Assert.Equal(expected, Wildcard.IsMatch(pattern, name));
    }
}
