using System.Reflection;
using System.Text.RegularExpressions;
using PCPerfSuite.App.ViewModels;

namespace PCPerfSuite.App.Tests;

/// <summary>Le menu de la barre latérale : des clés stables et uniques, des sections dans l'ordre, et chaque page
/// soit livrée, soit « bientôt disponible ».</summary>
public class NavigationMenuTests
{
    private static IReadOnlyList<string> AllKeys()
        => typeof(PageKeys).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral)
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

    /// <summary>Un ViewModel quelconque pour chaque page qui n'a pas de texte d'attente, comme MainViewModel.</summary>
    private static Dictionary<string, object> DeliveredPages()
        => NavigationMenu.Pages.Where(p => !ComingSoonPages.Has(p.Key)).ToDictionary(p => p.Key, _ => new object());

    [Fact]
    public void Keys_AreUniqueKebabCase()
    {
        IReadOnlyList<string> keys = AllKeys();

        Assert.Equal(keys.Count, keys.Distinct().Count());
        Assert.All(keys, key => Assert.Matches(new Regex("^[a-z]+(-[a-z]+)*$"), key));
    }

    [Fact]
    public void Menu_ListsEveryKeyOnce_SettingsApart()
    {
        IEnumerable<string> expected = AllKeys().Where(k => k != PageKeys.Settings).OrderBy(k => k);

        Assert.Equal(expected, NavigationMenu.Pages.Select(p => p.Key).OrderBy(k => k));
        Assert.Equal(PageKeys.Settings, NavigationMenu.Settings.Key);
        Assert.Equal(NavSection.Footer, NavigationMenu.Settings.Section);
    }

    [Fact]
    public void Menu_SectionsFollowDisplayOrder_SoEachHeaderAppearsOnce()
    {
        IReadOnlyList<NavSection> sections = NavigationMenu.Pages.Select(p => p.Section).ToList();

        Assert.Equal(sections.OrderBy(s => s), sections);
        Assert.DoesNotContain(NavSection.Footer, sections);
    }

    [Fact]
    public void Build_GroupsEntriesUnderContiguousHeaders()
    {
        IReadOnlyList<NavEntry> entries = NavigationMenu.Build(DeliveredPages(), isLaptop: true);

        // Chaque en-tête forme un seul bloc : la vue groupée n'affichera jamais deux fois la même section.
        IEnumerable<string> blocks = entries.Select(e => e.SectionTitle)
            .Where((title, i) => i == 0 || title != entries[i - 1].SectionTitle);
        Assert.Equal(new[] { "Surveiller", "Régler", "Diagnostiquer", "Outils" }, blocks);
    }

    [Fact]
    public void Titles_FitTheSidebar()
    {
        Assert.All(NavigationMenu.Pages, p =>
        {
            Assert.False(string.IsNullOrWhiteSpace(p.Title));
            Assert.True(p.Title.Length <= NavigationMenu.MaxTitleLength, $"Titre trop long : « {p.Title} »");
            Assert.False(string.IsNullOrEmpty(p.Icon));
        });
    }

    [Fact]
    public void Build_LaptopOnlyPage_HiddenOnDesktop_ShownOnLaptop()
    {
        Assert.DoesNotContain(NavigationMenu.Build(DeliveredPages(), isLaptop: false), e => e.Key == PageKeys.LaptopGpu);
        Assert.Contains(NavigationMenu.Build(DeliveredPages(), isLaptop: true), e => e.Key == PageKeys.LaptopGpu);
    }

    [Fact]
    public void Build_PageWithoutViewModel_IsComingSoon()
    {
        IReadOnlyList<NavEntry> entries = NavigationMenu.Build(DeliveredPages(), isLaptop: false);

        NavEntry profiles = Assert.Single(entries, e => e.Key == PageKeys.Profiles);
        Assert.True(profiles.IsComingSoon);
        Assert.Equal("Profils", profiles.ComingSoon!.Title);
        Assert.NotEmpty(profiles.ComingSoon.Bullets);

        Assert.False(Assert.Single(entries, e => e.Key == PageKeys.Monitoring).IsComingSoon);
    }

    [Fact]
    public void Build_DeliveredPage_UsesItsViewModel()
    {
        Dictionary<string, object> pages = DeliveredPages();
        var profiles = new object();
        pages[PageKeys.Profiles] = profiles;

        NavEntry entry = Assert.Single(NavigationMenu.Build(pages, isLaptop: false), e => e.Key == PageKeys.Profiles);

        Assert.Same(profiles, entry.ViewModel);
        Assert.False(entry.IsComingSoon);
    }

    [Fact]
    public void Build_UnknownKey_Throws()
    {
        Dictionary<string, object> pages = DeliveredPages();
        pages["page-inconnue"] = new object();

        Assert.Throws<ArgumentException>(() => NavigationMenu.Build(pages, isLaptop: false));
    }

    [Fact]
    public void Build_DeliveredPageMissing_Throws()
    {
        // Une page livrée (sans texte d'attente) dont le ViewModel manque disparaîtrait sans un mot : erreur visible.
        Dictionary<string, object> pages = DeliveredPages();
        pages.Remove(PageKeys.Monitoring);

        Assert.Throws<InvalidOperationException>(() => NavigationMenu.Build(pages, isLaptop: false));
    }
}
