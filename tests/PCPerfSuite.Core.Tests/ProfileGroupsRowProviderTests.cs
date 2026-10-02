using PCPerfSuite.Core.Compatibility;
using PCPerfSuite.Core.Profiles;
using Xunit;

namespace PCPerfSuite.Core.Tests;

public class ProfileGroupsRowProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Sans_groupe_la_ligne_le_dit_et_reste_marquee_experimentale()
    {
        var provider = new ProfileGroupsRowProvider(() => new ProfileGroupsSettings(), new TuningLease(new ManualClock(Now)),
            () => new ProfileGroupsStatus(null, null, null));

        CompatibilityRow row = Assert.Single(provider.GetRows());

        Assert.Equal("aucun groupe", row.Status);
        Assert.Contains("Aucun groupe appliqué.", row.Detail);
        Assert.Contains("Bail de réglage libre.", row.Detail);
        Assert.Contains("Expérimental", row.Detail);
        Assert.True(row.IsSupported);
    }

    [Fact]
    public void Groupe_actif_conformite_rapport_et_bail_sont_repris()
    {
        var settings = new ProfileGroupsSettings();
        var group = new ProfileGroup { Name = "Jeu" };
        settings.Groups.Add(group);
        settings.Groups.Add(new ProfileGroup { Name = "Calme" });
        settings.Active = new ProfileGroupActiveState { GroupId = group.Id, SinceUtc = Now, MadeStartupState = false };

        var lease = new TuningLease(new ManualClock(Now));
        using TuningLeaseHandle handle = lease.TryAcquire("bench", "le bench", "mesure en cours").Handle!;

        var provider = new ProfileGroupsRowProvider(() => settings, lease,
            () => new ProfileGroupsStatus("Conforme", null, $"Groupe « Jeu » appliqué.{Environment.NewLine}Processeur : rien à changer."));

        CompatibilityRow row = Assert.Single(provider.GetRows());

        Assert.Equal("2 groupes", row.Status);
        Assert.Contains("Groupe actif : « Jeu », sans en faire l'état de démarrage ; Conforme.", row.Detail);
        Assert.Contains("Dernier rapport : Groupe « Jeu » appliqué. Processeur : rien à changer.", row.Detail);
        Assert.Contains("Réglages pilotés par le bench", row.Detail);
    }

    [Fact]
    public void Un_groupe_suspendu_marque_la_ligne_en_probleme()
    {
        var settings = new ProfileGroupsSettings();
        var group = new ProfileGroup { Name = "OC" };
        settings.Groups.Add(group);
        settings.Suspensions[group.Id] = new ProfileGroupSuspension { SinceUtc = Now, Cause = "écran bleu" };

        var provider = new ProfileGroupsRowProvider(() => settings, new TuningLease(new ManualClock(Now)),
            () => new ProfileGroupsStatus(null, null, null));

        CompatibilityRow row = Assert.Single(provider.GetRows());

        Assert.False(row.IsSupported);
        Assert.Contains("Suspendu(s) après un incident : « OC » (écran bleu).", row.Detail);
    }
}
