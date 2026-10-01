using PCPerfSuite.Core.Safety.Events;

namespace PCPerfSuite.Core.Tests;

/// <summary>Classement des événements du journal Système et qualification d'une opération interrompue.</summary>
public class IncidentClassifierTests
{
    private static readonly DateTimeOffset T = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset EntryBoot = T.AddHours(-3);

    private static DateTimeOffset At(double minutes) => T.AddMinutes(minutes);

    private static SystemEventRecord Boot(double minutes) => new(SystemEventKind.BootStarted, 12, At(minutes));
    private static SystemEventRecord Clean(double minutes) => new(SystemEventKind.CleanShutdown, 13, At(minutes));
    private static SystemEventRecord K41(double minutes, ulong bugcheck = 0, ulong button = 0)
        => new(SystemEventKind.KernelPower41, 41, At(minutes)) { BugcheckCode = bugcheck, PowerButtonTimestamp = button };
    private static SystemEventRecord E6008(double minutes) => new(SystemEventKind.UnexpectedShutdown, 6008, At(minutes));
    private static SystemEventRecord E1001(double minutes, string code = "0x0000009F") => new(SystemEventKind.BugCheck, 1001, At(minutes)) { Detail = code };
    private static SystemEventRecord Tdr(double minutes) => new(SystemEventKind.DisplayDriverReset, 4101, At(minutes)) { Detail = "nvlddmkm" };
    private static SystemEventRecord Whea(double minutes, int id) => new(SystemEventKind.HardwareError, id, At(minutes));

    private static IncidentQualification QualifyAfterReboot(params SystemEventRecord[] events)
        => IncidentClassifier.Qualify(T, EntryBoot, currentBootUtc: At(20), events, nowUtc: At(30));

    [Fact]
    public void Kernel41WithoutCodeNorButton_IsPowerLoss()
    {
        IncidentQualification result = QualifyAfterReboot(Boot(20), K41(20.5), E6008(20.6));

        Assert.Equal(IncidentQualificationKind.PowerLoss, result.Kind);
        Incident incident = Assert.Single(result.Incidents);
        Assert.Equal(IncidentKind.PowerLoss, incident.Kind);
        Assert.Equal(At(20), result.RestartedUtc);
        Assert.Contains("arrêt brutal", result.Summary);
    }

    [Fact]
    public void Kernel41WithThePowerButtonHeld_IsForcedShutdown()
    {
        IncidentQualification result = QualifyAfterReboot(Boot(20), K41(20.5, button: 133_000_000_000));

        Assert.Equal(IncidentQualificationKind.ForcedShutdown, result.Kind);
    }

    [Fact]
    public void Kernel41WithCodeAnd1001OfTheSameBoot_IsASingleBlueScreen()
    {
        IncidentQualification result = QualifyAfterReboot(Boot(20), K41(20.5, bugcheck: 0x9F), E6008(20.6), E1001(21));

        Assert.Equal(IncidentQualificationKind.BlueScreen, result.Kind);
        Incident incident = Assert.Single(result.Incidents);
        Assert.Equal(IncidentKind.BlueScreen, incident.Kind);
        Assert.Contains("0x0000009F", incident.Evidence);
    }

    [Fact]
    public void Only1001_IsBlueScreen()
    {
        Assert.Equal(IncidentQualificationKind.BlueScreen, QualifyAfterReboot(Boot(20), E1001(21)).Kind);
    }

    [Fact]
    public void Only6008_IsUnexpectedShutdown()
    {
        Assert.Equal(IncidentQualificationKind.UnexpectedShutdown, QualifyAfterReboot(Boot(20), E6008(20.5)).Kind);
    }

    [Fact]
    public void CleanShutdownBeforeTheReboot_IsCleanShutdown()
    {
        IncidentQualification result = QualifyAfterReboot(Clean(10), Boot(20));

        Assert.Equal(IncidentQualificationKind.CleanShutdown, result.Kind);
        Assert.Empty(result.Incidents);
    }

    [Fact]
    public void RebootWithoutAnyRecord_IsUnknown()
    {
        Assert.Equal(IncidentQualificationKind.Unknown, QualifyAfterReboot(Boot(20)).Kind);
    }

    [Fact]
    public void SameBoot_IsInterrupted_WithoutAnyFalsePowerLoss()
    {
        // L'app a été tuée ; une coupure sans rapport a eu lieu avant l'opération, une autre n'existe pas encore.
        SystemEventRecord[] events = [K41(-120), Boot(-119)];

        IncidentQualification result = IncidentClassifier.Qualify(T, EntryBoot, currentBootUtc: EntryBoot.AddSeconds(30), events, At(5));

        Assert.Equal(IncidentQualificationKind.Interrupted, result.Kind);
        Assert.Empty(result.Incidents);
    }

    [Fact]
    public void SameBoot_WithACleanShutdown_IsACleanShutdown_FastStartup()
    {
        // Démarrage rapide : Windows « arrêté » sans vrai redémarrage, l'heure de démarrage ne change pas.
        IncidentQualification result = IncidentClassifier.Qualify(T, EntryBoot, EntryBoot,
            [new SystemEventRecord(SystemEventKind.CleanShutdown, 109, At(10))], At(600));

        Assert.Equal(IncidentQualificationKind.CleanShutdown, result.Kind);
    }

    [Fact]
    public void SameBoot_KeepsATdrThatHappenedDuringTheOperation()
    {
        IncidentQualification result = IncidentClassifier.Qualify(T, EntryBoot, EntryBoot, [Tdr(2)], At(5));

        Assert.Equal(IncidentQualificationKind.Interrupted, result.Kind);
        Assert.True(result.HasIncident(IncidentKind.DisplayDriverReset));
        Assert.Contains("TDR", result.Summary);
    }

    [Fact]
    public void SameBoot_WithUnreadableLogs_IsStillInterrupted()
    {
        IncidentQualification result = IncidentClassifier.Qualify(T, EntryBoot, EntryBoot, events: null, At(5));

        Assert.Equal(IncidentQualificationKind.Interrupted, result.Kind);
    }

    [Fact]
    public void Reboot_WithUnreadableLogs_IsUnknown()
    {
        IncidentQualification result = IncidentClassifier.Qualify(T, EntryBoot, At(20), events: null, At(30));

        Assert.Equal(IncidentQualificationKind.Unknown, result.Kind);
        Assert.Contains("illisible", result.Summary);
    }

    [Fact]
    public void MissingEntryBoot_UsesTheCurrentBootTime()
    {
        IncidentQualification sameSession = IncidentClassifier.Qualify(T, entryBootUtc: null, currentBootUtc: EntryBoot, [], At(5));
        IncidentQualification rebooted = IncidentClassifier.Qualify(T, entryBootUtc: null, currentBootUtc: At(20), [Boot(20), K41(20.5)], At(30));

        Assert.Equal(IncidentQualificationKind.Interrupted, sameSession.Kind);
        Assert.Equal(IncidentQualificationKind.PowerLoss, rebooted.Kind);
    }

    [Fact]
    public void ACrashDuringALaterSession_IsNotBlamedOnTheOperation()
    {
        // Redémarrage propre à 20 min, puis coupure pendant la session suivante, journalisée au démarrage de 200 min.
        IncidentQualification result = IncidentClassifier.Qualify(T, EntryBoot, currentBootUtc: At(200),
            [Clean(10), Boot(20), Boot(200), K41(200.5)], At(210));

        Assert.Equal(IncidentQualificationKind.CleanShutdown, result.Kind);
        Assert.DoesNotContain(result.Incidents, incident => incident.Kind == IncidentKind.PowerLoss);
    }

    [Fact]
    public void TdrDuringTheOperation_AndFatalWheaAtTheReboot_AreBothKept()
    {
        IncidentQualification result = QualifyAfterReboot(Tdr(5), Boot(20), K41(20.5, bugcheck: 0x124), Whea(20.7, 18));

        Assert.Equal(IncidentQualificationKind.BlueScreen, result.Kind);
        Assert.True(result.HasIncident(IncidentKind.DisplayDriverReset));
        Assert.True(result.HasIncident(IncidentKind.HardwareError));
        Assert.Contains("pendant l'opération", result.Summary);
    }

    [Fact]
    public void EventsBeforeTheOperation_AreIgnored()
    {
        IncidentQualification result = QualifyAfterReboot(Tdr(-5), Whea(-3, 19), Boot(20), E6008(20.5));

        Assert.Equal(IncidentQualificationKind.UnexpectedShutdown, result.Kind);
        Assert.Single(result.Incidents);
    }

    [Fact]
    public void NoBootMarker_FallsBackOnTheCurrentBoot()
    {
        IncidentQualification result = QualifyAfterReboot(K41(20.5));

        Assert.Equal(IncidentQualificationKind.PowerLoss, result.Kind);
        Assert.Equal(At(20), result.RestartedUtc);
    }

    [Fact]
    public void Precedence_BlueScreenWinsOverEveryOtherShutdown()
    {
        Assert.True(IncidentQualificationKind.BlueScreen > IncidentQualificationKind.PowerLoss);
        Assert.True(IncidentQualificationKind.PowerLoss > IncidentQualificationKind.ForcedShutdown);
        Assert.True(IncidentQualificationKind.ForcedShutdown > IncidentQualificationKind.UnexpectedShutdown);
        Assert.True(IncidentQualificationKind.UnexpectedShutdown > IncidentQualificationKind.CleanShutdown);
        Assert.True(IncidentQualificationKind.CleanShutdown > IncidentQualificationKind.Interrupted);
    }

    [Fact]
    public void ClassifyAll_GroupsEachShutdownAndCountsLiveIncidents()
    {
        IReadOnlyList<Incident> incidents = IncidentClassifier.ClassifyAll(
        [
            K41(0), E6008(0.2),                    // arrêt brutal
            K41(600, bugcheck: 0x9F), E1001(601),  // écran bleu, un autre jour
            E6008(1200),                           // arrêt inattendu seul
            Tdr(50), Tdr(60), Whea(70, 17),
            new SystemEventRecord(SystemEventKind.DiskError, 153, At(80)) { Detail = "disque 1" },
            Boot(0.1), Clean(500),
        ]);

        Assert.Equal(1, incidents.Count(i => i.Kind == IncidentKind.PowerLoss));
        Assert.Equal(1, incidents.Count(i => i.Kind == IncidentKind.BlueScreen));
        Assert.Equal(1, incidents.Count(i => i.Kind == IncidentKind.UnexpectedShutdown));
        Assert.Equal(2, incidents.Count(i => i.Kind == IncidentKind.DisplayDriverReset));
        Assert.Equal(1, incidents.Count(i => i.Kind == IncidentKind.HardwareError));
        Assert.Contains(incidents, i => i.Kind == IncidentKind.DiskError && i.Evidence.Contains("disque 1"));
    }
}
