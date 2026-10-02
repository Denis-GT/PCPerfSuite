using PCPerfSuite.Core.Hardware;
using PCPerfSuite.Core.PowerSettings;
using PCPerfSuite.Core.Profiles;
using PCPerfSuite.Core.Safety;
using static PCPerfSuite.Core.Tests.ProfileGroupTestData;

namespace PCPerfSuite.Core.Tests;

/// <summary>
/// Un OC fait de puissance ou de tension seules (décalages à zéro) est un OC partout : générateur, reprise au lancement
/// (et TDR en session), blocage d'un groupe qui partage l'OC d'un groupe suspendu.
/// </summary>
public sealed class GpuRaisedProfileTests
{
    private static GpuOverclockProfile PowerAndVoltage()
        => new() { PowerLimitPercent = 120, VoltageValue = 50, VoltageUnit = GpuVoltageUnit.Millivolts };

    private static GpuControlSettings Saved(GpuOverclockProfile oc) => new()
    {
        ApplyOverclockAtStartup = true,
        CoreClockOffsetMhz = oc.CoreClockOffsetMhz,
        MemoryClockOffsetMhz = oc.MemoryClockOffsetMhz,
        PowerLimitPercent = oc.PowerLimitPercent,
        TemperatureLimitC = oc.TemperatureLimitC,
        VoltageValue = oc.VoltageValue,
        VoltageUnit = oc.VoltageUnit,
    };

    [Theory]
    [InlineData(150, 0, null, null, true)]
    [InlineData(0, 500, null, null, true)]
    [InlineData(0, 0, 120f, null, true)]
    [InlineData(0, 0, null, 50, true)]
    [InlineData(0, 0, 100f, 0, false)]
    [InlineData(-100, 0, 90f, null, false)]
    public void Un_profil_est_releve_par_ses_decalages_sa_puissance_ou_sa_tension(int core, int memory, float? power, int? voltage, bool raised)
    {
        var profile = new GpuOverclockProfile
        {
            CoreClockOffsetMhz = core, MemoryClockOffsetMhz = memory, PowerLimitPercent = power, VoltageValue = voltage,
        };

        Assert.Equal(raised, GpuOverclockRaise.IsRaisedProfile(profile));
    }

    [Fact]
    public void Un_oc_de_puissance_et_de_tension_est_celui_de_l_onglet()
    {
        GpuOverclockProfile oc = PowerAndVoltage();

        Assert.True(ProfileGroupStartupCheck.MatchesSavedGpu(GpuPart(oc), Saved(oc)));
    }

    [Fact]
    public void Une_autre_puissance_dans_l_onglet_n_est_plus_l_oc_du_groupe()
    {
        GpuOverclockProfile oc = PowerAndVoltage();
        GpuControlSettings saved = Saved(oc);
        saved.PowerLimitPercent = 110;

        Assert.False(ProfileGroupStartupCheck.MatchesSavedGpu(GpuPart(oc), saved));
    }

    [Fact]
    public void Apres_un_ecran_bleu_un_oc_de_puissance_seule_decoche_la_case_gpu()
    {
        GpuOverclockProfile oc = PowerAndVoltage();
        var settings = new AppSettings { Gpu = Saved(oc) };
        settings.ProfileGroups.Groups.Add(new ProfileGroup { Id = "g1", Name = "Jeu exigeant (auto)", Gpu = GpuPart(oc) });
        var handler = new ProfileGroupRecoveryHandler(mutate => mutate(settings));
        var entry = new SessionJournalEntry(Guid.NewGuid(), ProfileGroupProbation.Component, ProfileGroupProbation.ApplyAction,
            new Dictionary<string, string> { ["groupe"] = "g1", ["gpu-oc"] = "oui", ["watts"] = "non", ["etat-demarrage"] = "non" },
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 4242, SessionEntryState.InProgress, null, DateTimeOffset.UnixEpoch);

        handler.Handle([new RecoveredEntry(entry, new Safety.Events.IncidentQualification(
            Safety.Events.IncidentQualificationKind.BlueScreen, [], "écran bleu"))]);

        Assert.False(settings.Gpu.ApplyOverclockAtStartup);
    }

    [Fact]
    public void Un_groupe_qui_partage_un_oc_de_puissance_avec_un_groupe_suspendu_n_est_pas_pose()
    {
        GpuOverclockProfile oc = PowerAndVoltage();
        var heavy = new ProfileGroup { Id = "heavy", Name = "heavy", Usage = ProfileGroupUsage.HeavyGaming, Gpu = GpuPart(oc) };
        var light = new ProfileGroup { Id = "light", Name = "light", Usage = ProfileGroupUsage.LightGaming, Gpu = GpuPart(PowerAndVoltage()) };
        var store = new ProfileGroupsSettings
        {
            Groups = { heavy, light },
            Suspensions = { ["heavy"] = new ProfileGroupSuspension { Cause = "écran bleu" } },
        };

        Assert.Same(heavy, UsageGroupResolver.SuspendedBy(store, light));
    }

    [Fact]
    public void Une_autre_tension_n_est_pas_le_meme_oc()
    {
        var heavy = new ProfileGroup { Id = "heavy", Name = "heavy", Gpu = GpuPart(PowerAndVoltage()) };
        GpuOverclockProfile lower = PowerAndVoltage();
        lower.VoltageValue = 20;
        var light = new ProfileGroup { Id = "light", Name = "light", Gpu = GpuPart(lower) };
        var store = new ProfileGroupsSettings
        {
            Groups = { heavy, light },
            Suspensions = { ["heavy"] = new ProfileGroupSuspension { Cause = "écran bleu" } },
        };

        Assert.Null(UsageGroupResolver.SuspendedBy(store, light));
    }
}
