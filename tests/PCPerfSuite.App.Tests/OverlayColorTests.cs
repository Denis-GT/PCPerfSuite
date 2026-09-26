using PCPerfSuite.App.Metrics;
using PCPerfSuite.App.Overlay;
using PCPerfSuite.App.ViewModels;
using PCPerfSuite.Core.PowerSettings;

namespace PCPerfSuite.App.Tests;

public class OverlayColorTests
{
    /// <summary>Défauts de l'overlay avant cet assombrissement : ce que d'anciens settings.json ont pu enregistrer.</summary>
    private static readonly Dictionary<string, string> PreviousDefaults = new()
    {
        ["cpu"] = "#2FA3E0", ["gpu"] = "#4CB85F", ["ram"] = "#9A62E0", ["mb"] = "#E0932B", ["storage"] = "#E0B030",
        ["net"] = "#25B5A0", ["game"] = "#E0587A", ["sys"] = "#8C97B3", ["power"] = "#E0C030",
    };

    private static MetricCategory Category(string key) => MetricCatalog.Categories.Single(c => c.Key == key);

    private static OverlayAppearanceViewModel Appearance(OverlayAppearanceSettings settings)
        => new(settings, onChanged: () => { }, onLayoutChanged: () => { });

    [Fact]
    public void DefaultCategoryColors_AreDarkerThanTheirPredecessors()
    {
        foreach (MetricCategory category in MetricCatalog.Categories)
        {
            double before = OverlayPalette.ToHsv(OverlayPalette.ToColor(PreviousDefaults[category.Key])).Value;
            double after = OverlayPalette.ToHsv(OverlayPalette.ToColor(category.OverlayColor)).Value;

            Assert.True(after < before, $"{category.Key} : {category.OverlayColor} n'est pas plus sombre que {PreviousDefaults[category.Key]}");
        }
    }

    [Fact]
    public void Palette_ContainsEveryCategoryDefault()
    {
        // La couleur par défaut d'une catégorie doit rester l'une des pastilles du sélecteur.
        foreach (MetricCategory category in MetricCatalog.Categories)
        {
            Assert.Contains(category.OverlayColor, OverlayPalette.Colors, StringComparer.OrdinalIgnoreCase);
        }

        Assert.Equal(OverlayPalette.Colors.Count, OverlayPalette.Colors.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Resolve_WithNothingSaved_GivesTheCurrentDefault()
    {
        foreach (MetricCategory category in MetricCatalog.Categories)
        {
            Assert.Equal(category.OverlayColor, OverlayColorDefaults.Resolve(category, null));
            Assert.Equal(category.OverlayColor, OverlayColorDefaults.Resolve(category, "  "));
        }
    }

    [Fact]
    public void Resolve_MigratesEveryFormerDefault()
    {
        foreach (MetricCategory category in MetricCatalog.Categories)
        {
            // Défaut d'avant l'assombrissement précédent, puis défaut de l'assombrissement précédent.
            Assert.Equal(category.OverlayColor, OverlayColorDefaults.Resolve(category, category.DefaultColor));
            Assert.Equal(category.OverlayColor, OverlayColorDefaults.Resolve(category, PreviousDefaults[category.Key]));
        }
    }

    [Theory]
    [InlineData("#2fa3e0")]
    [InlineData("2FA3E0")]
    [InlineData("#FF2FA3E0")]
    public void Resolve_RecognisesAFormerDefaultWhateverItsSpelling(string saved)
        => Assert.Equal(Category("cpu").OverlayColor, OverlayColorDefaults.Resolve(Category("cpu"), saved));

    [Fact]
    public void Resolve_KeepsAColorTheUserChose()
    {
        foreach (MetricCategory category in MetricCatalog.Categories)
        {
            Assert.Equal("#123456", OverlayColorDefaults.Resolve(category, "#123456"));
        }
    }

    [Fact]
    public void Resolve_KeepsAnUnreadableValueRatherThanGuessing()
        => Assert.Equal("pas une couleur", OverlayColorDefaults.Resolve(Category("cpu"), "pas une couleur"));

    [Fact]
    public void Appearance_MigratesFormerDefaultsButNotChosenColors()
    {
        var settings = new OverlayAppearanceSettings
        {
            CategoryColors = new Dictionary<string, string>
            {
                ["cpu"] = PreviousDefaults["cpu"], // ancien défaut, jamais touché
                ["gpu"] = "#123456",               // choisie par l'utilisateur
            },
        };

        OverlayAppearanceViewModel appearance = Appearance(settings);

        Assert.Equal(Category("cpu").OverlayColor, appearance.CategoryColors.Single(s => s.Key == "cpu").ColorHex);
        Assert.Equal("#123456", appearance.CategoryColors.Single(s => s.Key == "gpu").ColorHex);
        Assert.Equal(Category("ram").OverlayColor, appearance.CategoryColors.Single(s => s.Key == "ram").ColorHex);
    }

    [Fact]
    public void WriteTo_SavesOnlyTheColorsTheUserChanged()
    {
        OverlayAppearanceViewModel appearance = Appearance(new OverlayAppearanceSettings
        {
            CategoryColors = new Dictionary<string, string> { ["cpu"] = PreviousDefaults["cpu"], ["gpu"] = "#123456" },
        });

        var written = new OverlayAppearanceSettings();
        appearance.WriteTo(written);

        Assert.Equal(new Dictionary<string, string> { ["gpu"] = "#123456" }, written.CategoryColors);
    }

    [Fact]
    public void WriteTo_AfterResettingColors_SavesNone()
    {
        OverlayAppearanceViewModel appearance = Appearance(new OverlayAppearanceSettings
        {
            CategoryColors = new Dictionary<string, string> { ["gpu"] = "#123456" },
        });

        appearance.ResetColorsCommand.Execute(null);

        var written = new OverlayAppearanceSettings();
        appearance.WriteTo(written);
        Assert.Empty(written.CategoryColors);
    }
}
