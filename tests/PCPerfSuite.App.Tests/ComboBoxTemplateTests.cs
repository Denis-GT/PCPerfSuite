using System.IO;
using System.Text.RegularExpressions;

namespace PCPerfSuite.App.Tests;

/// <summary>
/// Le gabarit de ComboBox du thème n'applique DisplayMemberPath qu'à la liste déroulée : la case fermée affiche alors
/// ToString() de l'objet (un record y étale toute sa structure). Une ComboBox doit passer par un ItemTemplate
/// (LabelOptionTemplate de Styles/Theme.xaml).
/// </summary>
public class ComboBoxTemplateTests
{
    private static readonly Regex ComboBoxTag = new(@"<ComboBox\b[^>]*>", RegexOptions.Singleline);

    private static string ViewsDirectory()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string views = Path.Combine(dir.FullName, "src", "PCPerfSuite.App", "Views");
            if (Directory.Exists(views)) return views;
        }

        throw new DirectoryNotFoundException("src/PCPerfSuite.App/Views introuvable depuis le dossier des tests");
    }

    [Fact]
    public void Aucune_ComboBox_n_utilise_DisplayMemberPath()
    {
        List<string> offenders = Directory.EnumerateFiles(ViewsDirectory(), "*.xaml", SearchOption.AllDirectories)
            .SelectMany(file => ComboBoxTag.Matches(File.ReadAllText(file))
                .Where(tag => tag.Value.Contains("DisplayMemberPath", StringComparison.Ordinal))
                .Select(tag => $"{Path.GetFileName(file)} : {tag.Value.Split('\n')[0].Trim()}"))
            .ToList();

        Assert.Empty(offenders);
    }
}
