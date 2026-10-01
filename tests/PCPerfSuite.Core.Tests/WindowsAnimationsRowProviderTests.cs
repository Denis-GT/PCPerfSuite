using PCPerfSuite.Core.Compatibility;
using PCPerfSuite.Core.PowerSettings.Animations;
using PCPerfSuite.Core.SystemInfo;

namespace PCPerfSuite.Core.Tests;

/// <summary>La ligne « Animations et effets » du diagnostic : valeurs lues, réglages modifiés, compte visé en donnée
/// personnelle.</summary>
public class WindowsAnimationsRowProviderTests
{
    private static AnimationSnapshot Snapshot(string? unavailable = null, Func<AnimationSetting, AnimationReading>? reading = null)
        => new(WindowsAnimationCatalog.All.Select(s => reading?.Invoke(s) ?? new AnimationReading(s, true, null)).ToList(), unavailable);

    [Fact]
    public void Pas_encore_lu()
    {
        CompatibilityRow row = Assert.Single(WindowsAnimationsRowProvider.BuildRows(null, null, null));

        Assert.Equal("Pas encore lu", row.Status);
        Assert.True(row.IsSupported);
    }

    [Fact]
    public void Les_valeurs_lues_et_les_modifications_sont_dans_le_detail()
    {
        AnimationSnapshot snapshot = Snapshot(reading: s => s.Key == "menu-animation"
            ? new AnimationReading(s, false, true)
            : new AnimationReading(s, true, null));

        IReadOnlyList<CompatibilityRow> rows = WindowsAnimationsRowProvider.BuildRows(snapshot, @"PC\denis", @"PC\denis");

        CompatibilityRow main = rows[0];
        Assert.Equal("1 réglage modifié par PCPerfSuite", main.Status);
        Assert.Contains("Fondu ou glissement des menus : désactivé (origine : activé)", main.Detail);
        Assert.Contains("Expérimental", main.Detail);
        Assert.True(main.IsSupported);
        Assert.False(main.IsPersonal);

        CompatibilityRow account = rows[1];
        Assert.Equal(WindowsAnimationsRowProvider.AccountRowTitle, account.Title);
        Assert.Equal(@"PC\denis", account.Status);
        Assert.True(account.IsPersonal);
    }

    [Fact]
    public void Sous_un_autre_compte_la_ligne_est_un_probleme_qui_dit_pourquoi()
    {
        string reason = SessionUser.DescribeOtherProfileSetting(@"PC\admin", @"PC\denis");

        IReadOnlyList<CompatibilityRow> rows = WindowsAnimationsRowProvider.BuildRows(Snapshot(reason), @"PC\admin", @"PC\denis");

        Assert.False(rows[0].IsSupported);
        Assert.Equal("Grisé : autre compte", rows[0].Status);
        // Les noms de compte ne vont que dans la ligne personnelle, masquée dans le rapport copié.
        Assert.False(rows[0].IsPersonal);
        Assert.DoesNotContain(@"PC\admin", rows[0].Detail);
        Assert.DoesNotContain(@"PC\denis", rows[0].Detail);
        Assert.Contains(@"PC\denis", rows[1].Detail);
        Assert.True(rows[1].IsPersonal);
    }

    [Fact]
    public void Un_reglage_illisible_est_signale()
    {
        AnimationSnapshot snapshot = Snapshot(reading: s => new AnimationReading(s, s.Key == "transparency" ? null : true, null));

        CompatibilityRow main = WindowsAnimationsRowProvider.BuildRows(snapshot, null, null).Single();

        Assert.False(main.IsSupported);
        Assert.Contains("Effets de transparence : illisible", main.Detail);
    }
}
