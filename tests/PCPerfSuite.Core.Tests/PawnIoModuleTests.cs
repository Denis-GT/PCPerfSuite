using System.Text;
using PCPerfSuite.Core.Compatibility;
using PCPerfSuite.Core.Hardware.Cpu;

namespace PCPerfSuite.Core.Tests;

/// <summary>Fonctions d'un module PawnIO relevées dans son contenu, choix du module à charger et diagnostic.</summary>
public class PawnIoModuleTests
{
    private static byte[] Blob(string text) => Encoding.ASCII.GetBytes(text);

    [Fact]
    public void Parse_FindsEachIoctlOnce_InOrder()
    {
        byte[] blob = Blob("\0\0ioctl_read_msr\0junk ioctl_write_msr\0ioctl_read_msr\0");

        Assert.Equal(["ioctl_read_msr", "ioctl_write_msr"], PawnIoModuleFunctions.Parse(blob));
    }

    [Fact]
    public void Parse_StopsAtTheFirstByteOutsideTheName()
    {
        byte[] blob = Blob("ioctl_send_smu_command\u0001ioctl_Read ioctl_x9-y");

        // « ioctl_Read » n'a aucun caractère autorisé après le préfixe (majuscule) : ignoré.
        Assert.Equal(["ioctl_send_smu_command", "ioctl_x9"], PawnIoModuleFunctions.Parse(blob));
    }

    [Fact]
    public void Parse_IgnoresAPrefixGluedToAPrecedingName_AndABarePrefix()
    {
        byte[] blob = Blob("xioctl_hidden ioctl_ ioctl_ok");

        Assert.Equal(["ioctl_ok"], PawnIoModuleFunctions.Parse(blob));
    }

    [Fact]
    public void Parse_EmptyBlob_HasNoFunction()
        => Assert.Empty(PawnIoModuleFunctions.Parse(ReadOnlySpan<byte>.Empty));

    [Fact]
    public void LibreHardwareMonitorIntelMsr_OnlyReads()
    {
        // Le constat qui motive le module livré : l'IntelMSR de LHM 0.9.6 n'écrit pas les MSR.
        PawnIoModuleCandidate? lhm = PawnIoDriver.ReadLibreHardwareMonitorModule("IntelMSR");

        Assert.NotNull(lhm);
        Assert.Equal(["ioctl_read_msr"], lhm.Info.Functions);
        Assert.Equal(PawnIoModuleSource.LibreHardwareMonitor, lhm.Info.Source);
    }

    [Fact]
    public void LibreHardwareMonitorRyzenSmu_SendsSmuCommands()
    {
        PawnIoModuleCandidate? lhm = PawnIoDriver.ReadLibreHardwareMonitorModule("RyzenSMU");

        Assert.NotNull(lhm);
        Assert.Contains("ioctl_send_smu_command", lhm.Info.Functions);
    }

    [Fact]
    public void ShippedIntelMsr_IsTheReleaseFile_AndWrites()
    {
        // Le fichier livré dans PawnIO\ à côté de l'exe (et des tests) : celui de release_0_2_11.zip, octet pour octet.
        string path = ShippedPawnIoModules.PathFor("IntelMSR");
        Assert.True(File.Exists(path), $"{path} manque dans la sortie : vérifier le Content de PCPerfSuite.Core.csproj.");

        byte[] blob = File.ReadAllBytes(path);
        Assert.Equal("d6ed85d65ab17a22f813ef98207d6d537155ee2ded5976a21cb48413c9b92e5f", PawnIoModuleFunctions.Sha256(blob));
        Assert.Equal(ShippedPawnIoModules.ExpectedSha256["IntelMSR"], PawnIoModuleFunctions.Sha256(blob));
        Assert.Equal(["ioctl_read_msr", "ioctl_write_msr"], PawnIoModuleFunctions.Parse(blob).Order());

        PawnIoModuleCandidate? shipped = ShippedPawnIoModules.TryRead("IntelMSR");
        Assert.NotNull(shipped);
        Assert.True(shipped.Info.IsOfficialCopy);
        Assert.Equal("0.2.11", shipped.Info.Version);
    }

    [Fact]
    public void Candidates_ShippedFirst_ThenLibreHardwareMonitor()
    {
        IReadOnlyList<PawnIoModuleCandidate> candidates = PawnIoDriver.ReadCandidates("IntelMSR");

        Assert.Equal([PawnIoModuleSource.PCPerfSuite, PawnIoModuleSource.LibreHardwareMonitor],
            candidates.Select(c => c.Info.Source));
    }

    [Fact]
    public void Candidates_ModuleNotShipped_ComesFromLibreHardwareMonitorOnly()
    {
        IReadOnlyList<PawnIoModuleCandidate> candidates = PawnIoDriver.ReadCandidates("RyzenSMU");

        Assert.Equal([PawnIoModuleSource.LibreHardwareMonitor], candidates.Select(c => c.Info.Source));
    }

    private static PawnIoModuleCandidate Candidate(PawnIoModuleSource source, params string[] functions)
        => new(new PawnIoModuleInfo("IntelMSR", source, "1", functions, "sha", source == PawnIoModuleSource.PCPerfSuite ? true : null), []);

    [Fact]
    public void Order_PrefersTheShippedModule()
    {
        var lhm = Candidate(PawnIoModuleSource.LibreHardwareMonitor, "ioctl_read_msr", "ioctl_write_msr");
        var shipped = Candidate(PawnIoModuleSource.PCPerfSuite, "ioctl_read_msr", "ioctl_write_msr");

        Assert.Same(shipped, PawnIoModuleChoice.Order([lhm, shipped], "ioctl_write_msr")[0]);
    }

    [Fact]
    public void Order_ShippedModuleWithoutThePreferredFunction_GoesAfter()
    {
        // Fichier livré remplacé par une version qui ne sait que lire : celui qui écrit passe devant.
        var shipped = Candidate(PawnIoModuleSource.PCPerfSuite, "ioctl_read_msr");
        var lhm = Candidate(PawnIoModuleSource.LibreHardwareMonitor, "ioctl_read_msr", "ioctl_write_msr");

        Assert.Equal([lhm, shipped], PawnIoModuleChoice.Order([shipped, lhm], "ioctl_write_msr"));
    }

    [Fact]
    public void Order_ShippedModuleMissing_KeepsLibreHardwareMonitor()
    {
        var lhm = Candidate(PawnIoModuleSource.LibreHardwareMonitor, "ioctl_read_msr");

        Assert.Equal([lhm], PawnIoModuleChoice.Order([lhm], "ioctl_write_msr"));
    }

    [Fact]
    public void ReadOnlyModule_ReasonBlamesTheModule()
    {
        var lhm = Candidate(PawnIoModuleSource.LibreHardwareMonitor, "ioctl_read_msr").Info with { Version = "0.9.6" };

        string? reason = IntelPowerLimitBackend.DescribeModuleWriteRefusal(lhm);

        Assert.NotNull(reason);
        Assert.Contains("LibreHardwareMonitorLib 0.9.6", reason);
        Assert.Contains("ne sait que lire", reason);
        Assert.Contains(@"PawnIO\IntelMSR.bin", reason);
        Assert.DoesNotContain("BIOS", reason);
    }

    [Fact]
    public void ReadOnlyModule_RefusedShippedModule_SaysWhy()
    {
        var lhm = Candidate(PawnIoModuleSource.LibreHardwareMonitor, "ioctl_read_msr").Info
            with { Note = "Le module IntelMSR livré avec PCPerfSuite a été refusé par PawnIO (code 0xD0000001)." };

        Assert.Contains("refusé par PawnIO", IntelPowerLimitBackend.DescribeModuleWriteRefusal(lhm));
    }

    [Fact]
    public void WritingModule_HasNoRefusal()
        => Assert.Null(IntelPowerLimitBackend.DescribeModuleWriteRefusal(
            Candidate(PawnIoModuleSource.PCPerfSuite, "ioctl_read_msr", "ioctl_write_msr").Info));

    [Fact]
    public void Diagnostic_ListsLoadedModuleFunctions_AndFlagsAReadOnlyIntelMsr()
    {
        var readOnly = Candidate(PawnIoModuleSource.LibreHardwareMonitor, "ioctl_read_msr").Info;

        CompatibilityRow row = Assert.Single(PawnIoModulesRowProvider.BuildRows([readOnly], [], true, null));

        Assert.Equal("Modules PawnIO · IntelMSR", row.Title);
        Assert.Equal("Lecture seule", row.Status);
        Assert.False(row.IsSupported);
        Assert.Contains("ioctl_read_msr", row.Detail);
    }

    [Fact]
    public void Diagnostic_ShippedModuleNotLoaded_IsDescribedFromItsFile()
    {
        var shipped = Candidate(PawnIoModuleSource.PCPerfSuite, "ioctl_read_msr", "ioctl_write_msr").Info;

        CompatibilityRow row = Assert.Single(PawnIoModulesRowProvider.BuildRows([], [shipped], false, "Le pilote PawnIO n'est pas installé."));

        Assert.Equal("Livré, non chargé", row.Status);
        Assert.Contains("ioctl_write_msr", row.Detail);
        Assert.Contains("n'est pas installé", row.Detail);
    }

    [Fact]
    public void Diagnostic_NothingAtAll_SaysWhy()
    {
        CompatibilityRow row = Assert.Single(PawnIoModulesRowProvider.BuildRows([], [], false, "PawnIO n'existe que pour les processeurs x64."));

        Assert.Equal("Aucun module chargé", row.Status);
        Assert.Contains("x64", row.Detail);
    }
}
