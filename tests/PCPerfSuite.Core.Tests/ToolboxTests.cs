using PCPerfSuite.Core.Installations;
using PCPerfSuite.Core.SystemChanges;

namespace PCPerfSuite.Core.Tests;

/// <summary>
/// Ce qui entoure les téléchargements de la Boîte à outils : versions comparées sans rien supposer, outils portables
/// retrouvés dans leur dossier, noms de fichiers libres dans Téléchargements, repli winget borné, registre des
/// modifications et ligne du diagnostic. Sans réseau, sans toucher au vrai %ProgramData%.
/// </summary>
public class ToolboxTests
{
    private static ToolDefinition CpuZ => ToolCatalog.Find("cpu-z")!;
    private static ToolDefinition Ddu => ToolCatalog.Find("ddu")!;

    [Theory]
    [InlineData("3.02", "3.01", 1)]
    [InlineData("3.01", "3.01", 0)]
    [InlineData("2.10", "2.9", 1)]
    [InlineData("7.3.7.0", "7.3.7", 0)]
    [InlineData("17.1.5.0", "17.1.5", 0)]
    [InlineData("0.8.7.9547b", "0.8.7.9547", 0)]
    [InlineData("7.3.6 Beta 2", "7.3.7", -1)]
    [InlineData("30.19b20", "30.19b19", 1)]
    public void Les_versions_se_comparent_par_blocs_de_chiffres(string a, string b, int expected)
    {
        Assert.Equal(expected, ToolVersion.Compare(a, b));
    }

    [Theory]
    [InlineData(null, "3.01")]
    [InlineData("", "3.01")]
    [InlineData("version inconnue", "23.200")]
    public void Sans_chiffres_on_ne_compare_pas_et_on_ne_propose_pas_de_mise_a_jour(string? installed, string candidate)
    {
        Assert.Null(ToolVersion.Compare(candidate, installed));
        Assert.False(ToolVersion.IsNewer(candidate, installed));
    }

    [Fact]
    public void La_version_portable_la_plus_recente_dont_l_exe_existe_est_retenue()
    {
        using var temp = new TempDirectory();
        string tool = Path.Combine(temp.Root, "cpu-z");
        foreach (string version in new[] { "2.99", "3.01", ".partiel-1234", "9.99" })
        {
            Directory.CreateDirectory(Path.Combine(tool, version));
        }

        File.WriteAllText(Path.Combine(tool, "2.99", "cpuz_x64.exe"), "MZ");
        File.WriteAllText(Path.Combine(tool, "3.01", "cpuz_x64.exe"), "MZ");
        File.WriteAllText(Path.Combine(tool, ".partiel-1234", "cpuz_x64.exe"), "MZ");
        // 9.99 sans exe : un dossier vidé à moitié n'est pas un outil prêt.

        ToolInstallState state = ToolDetection.DetectPortable(CpuZ, tool);

        Assert.True(state.IsPresent);
        Assert.True(state.IsPortable);
        Assert.Equal("3.01", state.Version);
        Assert.Equal(Path.Combine(tool, "3.01", "cpuz_x64.exe"), state.ExecutablePath);
    }

    [Fact]
    public void Sans_dossier_l_outil_portable_est_absent()
    {
        Assert.False(ToolDetection.DetectPortable(CpuZ, null).IsPresent);
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\DDU\\Display Driver Uninstaller.exe\",0", "C:\\Program Files\\DDU\\Display Driver Uninstaller.exe")]
    [InlineData("C:\\Outils\\outil.exe,1", "C:\\Outils\\outil.exe")]
    [InlineData("C:\\Outils\\outil.exe", "C:\\Outils\\outil.exe")]
    [InlineData("  ", null)]
    [InlineData(null, null)]
    public void Le_chemin_de_l_icone_inscrite_perd_ses_guillemets_et_son_numero(string? icon, string? expected)
    {
        Assert.Equal(expected, ToolDetection.PathOfIcon(icon));
    }

    [Fact]
    public void L_exe_installe_est_cherche_dans_le_dossier_d_installation()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(temp.File("Display Driver Uninstaller.exe"), "MZ");

        Assert.Equal(temp.File("Display Driver Uninstaller.exe"), ToolDetection.InstalledExecutable(Ddu, $"\"{temp.Root}\"", null));
        Assert.Equal(temp.File("Display Driver Uninstaller.exe"),
            ToolDetection.InstalledExecutable(Ddu, null, $"\"{temp.File("Display Driver Uninstaller.exe")}\",0"));
        // L'icône d'un autre programme n'est pas l'exe de l'outil.
        Assert.Null(ToolDetection.InstalledExecutable(Ddu, null, temp.File("uninstall.exe")));
    }

    [Fact]
    public void La_detection_de_chaque_outil_ne_leve_jamais()
    {
        foreach (ToolDefinition tool in ToolCatalog.All) Assert.NotNull(ToolDetection.Detect(tool));
    }

    [Fact]
    public void Un_nom_deja_pris_dans_Telechargements_recoit_un_numero()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(temp.File("outil.zip"), "");
        File.WriteAllText(temp.File("outil (2).zip"), "");

        Assert.Equal(temp.File("outil (3).zip"), UserDownloads.UniquePath(temp.Root, "outil.zip"));
        Assert.Equal(temp.File("autre.zip"), UserDownloads.UniquePath(temp.Root, "autre.zip"));
    }

    [Theory]
    [InlineData("CPUID.CPU-Z", true)]
    [InlineData("Guru3D.RTSS", true)]
    [InlineData("--source msstore", false)]
    [InlineData("a b", false)]
    [InlineData("x\"&calc", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Un_identifiant_winget_ne_peut_pas_passer_pour_une_option(string? id, bool expected)
    {
        Assert.Equal(expected, WingetFallback.IsValidId(id));
    }

    [Fact]
    public void Winget_installe_exactement_l_identifiant_depuis_sa_source()
    {
        Assert.Equal("install --id Guru3D.RTSS --exact --source winget", WingetFallback.ArgumentsFor("Guru3D.RTSS"));
    }

    [Fact]
    public void Winget_n_est_jamais_propose_pour_un_outil_non_signe()
    {
        ToolActionOutcome outcome = WingetFallback.Install(ToolCatalog.Find("7-zip")!);

        Assert.False(outcome.Succeeded);
    }

    // Registre des modifications

    private static ToolboxChanges Changes(Dictionary<string, ToolInstallState> states, List<ToolInstallRecord>? records = null,
        Func<ToolDefinition, ToolActionOutcome>? remove = null, List<string>? removed = null)
        => new(ToolCatalog.All,
            tool => states.TryGetValue(tool.Id, out ToolInstallState? state) ? state : ToolInstallState.Absent,
            () => records ?? new List<ToolInstallRecord>(),
            remove ?? (tool =>
            {
                removed?.Add(tool.Id);
                states.Remove(tool.Id);
                return new ToolActionOutcome(true, "supprimé");
            }));

    [Fact]
    public void Sans_outil_depose_rien_n_est_a_rendre()
    {
        ToolboxChanges changes = Changes(new Dictionary<string, ToolInstallState>());

        Assert.False(changes.HasChanges);
        Assert.Equal(SystemRestoreStatus.NothingToRestore, changes.RestoreAll().Status);
    }

    [Fact]
    public void Les_portables_et_les_installes_sont_listes_les_installes_comme_non_restaurables()
    {
        var states = new Dictionary<string, ToolInstallState>
        {
            ["cpu-z"] = new(true, "3.01", @"C:\ProgramData\PCPerfSuite\Tools\cpu-z\3.01\cpuz_x64.exe", true, @"C:\ProgramData\PCPerfSuite\Tools\cpu-z\3.01"),
            ["ddu"] = new(true, "18.1.5.3", null, false),
        };
        var records = new List<ToolInstallRecord> { new() { Id = "ddu", Version = "18.1.5.3" } };

        IReadOnlyList<SystemChange> described = Changes(states, records).Describe();

        Assert.Equal(2, described.Count);
        Assert.Contains(described, c => c.Title.StartsWith("CPU-Z 3.01") && c.CanRestore);
        Assert.Contains(described, c => c.Title.StartsWith("DDU") && !c.CanRestore && c.Detail.Contains("Applications"));
    }

    [Fact]
    public void Un_outil_installe_par_l_app_puis_desinstalle_n_est_plus_liste()
    {
        var records = new List<ToolInstallRecord> { new() { Id = "ddu", Version = "18.1.5.3" } };

        Assert.Empty(Changes(new Dictionary<string, ToolInstallState>(), records).Describe());
    }

    [Fact]
    public void Un_outil_installe_par_l_utilisateur_lui_meme_n_est_pas_liste()
    {
        var states = new Dictionary<string, ToolInstallState> { ["ddu"] = new(true, "18.1.5.3", null, false) };

        Assert.Empty(Changes(states).Describe());
    }

    [Fact]
    public void Tout_retablir_supprime_les_portables_et_dit_ce_qui_reste_installe()
    {
        var states = new Dictionary<string, ToolInstallState>
        {
            ["cpu-z"] = new(true, "3.01", "x", true, "dossier"),
            ["occt"] = new(true, "17.1.5.0", "y", true, "dossier"),
            ["ddu"] = new(true, "18.1.5.3", null, false),
        };
        var records = new List<ToolInstallRecord> { new() { Id = "ddu" } };
        var removed = new List<string>();

        SystemRestoreResult result = Changes(states, records, removed: removed).RestoreAll();

        Assert.Equal(new[] { "cpu-z", "occt" }, removed);
        Assert.Equal(SystemRestoreStatus.Partial, result.Status);
        Assert.Single(result.NotRestored, c => c.Title.StartsWith("DDU"));
    }

    [Fact]
    public void Un_portable_qui_ne_se_supprime_pas_est_signale_sans_arreter_les_autres()
    {
        var states = new Dictionary<string, ToolInstallState>
        {
            ["cpu-z"] = new(true, "3.01", "x", true, "dossier"),
            ["occt"] = new(true, "17.1.5.0", "y", true, "dossier"),
        };

        SystemRestoreResult result = Changes(states, remove: tool => tool.Id == "cpu-z"
            ? new ToolActionOutcome(false, "CPU-Z est ouvert.")
            : throw new IOException("disque")).RestoreAll();

        Assert.Equal(SystemRestoreStatus.Failed, result.Status);
        Assert.Equal(2, result.NotRestored.Count);
        Assert.Contains("CPU-Z est ouvert.", result.Message);
    }

    // Ligne du diagnostic

    private static ToolCatalogStatus EmbeddedStatus() => new ToolCatalogStore(ToolCatalog.All).Status;

    [Fact]
    public void Le_diagnostic_dit_d_ou_vient_le_catalogue_et_que_le_catalogue_en_ligne_attend_sa_cle()
    {
        var row = ToolboxRowProvider.BuildRow(EmbeddedStatus(), onlineEnabled: false, ToolCatalog.All, folder: null, hasWinget: null, portables: null);

        Assert.Equal(ToolboxRowProvider.RowTitle, row.Title);
        Assert.Contains("intégré", row.Status);
        Assert.Contains("pas encore activé", row.Detail);
        Assert.Contains("Expérimental", row.Detail);
        Assert.True(row.IsSupported);
    }

    [Fact]
    public void Un_dossier_securise_refuse_est_un_probleme_un_dossier_absent_non()
    {
        ToolCatalogStatus status = EmbeddedStatus();

        var refused = ToolboxRowProvider.BuildRow(status, true, ToolCatalog.All,
            new SecureFolderResult(null, "appartient à un autre compte"), hasWinget: true, portables: new[] { "CPU-Z 3.01" });
        var absent = ToolboxRowProvider.BuildRow(status, true, ToolCatalog.All,
            new SecureFolderResult(null, "pas encore créé") { IsAbsent = true }, hasWinget: false, portables: Array.Empty<string>());

        Assert.False(refused.IsSupported);
        Assert.Contains("CPU-Z 3.01", refused.Detail);
        Assert.Contains("winget : présent", refused.Detail);
        Assert.True(absent.IsSupported);
        Assert.Contains("winget : absent", absent.Detail);
    }

    [Fact]
    public void Un_catalogue_en_ligne_refuse_pour_sa_signature_est_un_probleme()
    {
        ToolCatalogStatus status = EmbeddedStatus() with { OnlineMessage = "catalogue en ligne refusé (signature invalide)", OnlineRefusalIsSuspicious = true };

        var row = ToolboxRowProvider.BuildRow(status, true, ToolCatalog.All, null, null, null);

        Assert.False(row.IsSupported);
        Assert.Contains("signature invalide", row.Detail);
    }
}
