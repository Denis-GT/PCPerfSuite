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
    public void Le_diagnostic_de_memoire_de_Windows_est_trouve_dans_System32()
    {
        ToolInstallState state = ToolDetection.Detect(ToolCatalog.Find("mdsched")!);

        Assert.True(state.IsPresent);
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "MdSched.exe"), state.ExecutablePath, ignoreCase: true);
    }

    [Fact]
    public void Une_inscription_d_applications_installees_est_reconnue_par_sa_cle_ou_son_nom()
    {
        var entries = new[]
        {
            new ToolDetection.UninstallEntry("{1234}", "Autre logiciel", "1.0", null, null),
            new ToolDetection.UninstallEntry("Afterburner", "MSI Afterburner 4.6.6", "4.6.6", null, null),
            new ToolDetection.UninstallEntry("Display Driver Uninstaller", "Display Driver Uninstaller", " 18.1.5.2 ", null, null),
        };

        Assert.Equal("4.6.6", ToolDetection.DetectUninstallEntry(ToolCatalog.Find("afterburner")!, entries).Version);
        Assert.Equal("18.1.5.2", ToolDetection.DetectUninstallEntry(Ddu, entries).Version);
        Assert.False(ToolDetection.DetectUninstallEntry(ToolCatalog.Find("openrgb")!, entries).IsPresent);
    }

    [Fact]
    public void La_detection_groupee_rend_un_etat_par_outil_dans_l_ordre()
    {
        IReadOnlyList<ToolInstallState> states = ToolDetection.DetectAll(ToolCatalog.All);

        Assert.Equal(ToolCatalog.All.Count, states.Count);
        Assert.True(states[ToolCatalog.All.ToList().FindIndex(t => t.Id == "mdsched")].IsPresent);
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
        // Identifiant inventé : même si le garde-fou régressait, winget n'installerait rien.
        ToolDefinition unsigned = ToolCatalog.Find("7-zip")! with { WingetId = "PCPerfSuite.Test.Inexistant" };

        ToolActionOutcome outcome = WingetFallback.Install(unsigned);

        Assert.False(outcome.Succeeded);
        Assert.Equal("winget n'est pas proposé pour cet outil.", outcome.Message);
    }

    // Garde-fous de ToolboxActions, sans réseau : chacun refuse avant tout téléchargement.

    private static ToolRelease ReleaseOf(string id) => new ToolCatalogStore(ToolCatalog.All).ReleaseOf(id)!;

    [Fact]
    public void Un_outil_non_signe_ne_se_lance_jamais_meme_installe()
    {
        ToolActionOutcome outcome = ToolboxActions.Launch(ToolCatalog.Find("openrgb")!,
            new ToolInstallState(true, "1.0", @"C:\chemin\inexistant\OpenRGB.exe", false));

        Assert.False(outcome.Succeeded);
        Assert.Contains("n'est pas signé", outcome.Message);
    }

    [Fact]
    public void Un_outil_portable_dont_l_exe_n_a_plus_sa_signature_ne_se_lance_pas()
    {
        using var temp = new TempDirectory();
        string exe = temp.File("cpuz_x64.exe");
        File.WriteAllBytes(exe, "MZ"u8.ToArray().Concat(new byte[2048]).ToArray());

        ToolActionOutcome outcome = ToolboxActions.Launch(CpuZ, new ToolInstallState(true, "3.01", exe, true, temp.Root));

        Assert.False(outcome.Succeeded);
        Assert.Equal(DownloadFailureKind.Mismatch, outcome.Failure);
        Assert.Contains("réinstalle", outcome.Message);
    }

    [Fact]
    public async Task Chaque_action_refuse_un_outil_qui_n_est_pas_du_bon_type()
    {
        ToolDefinition prime95 = ToolCatalog.Find("prime95")!;

        ToolActionOutcome install = await ToolboxActions.RunInstallerAsync(prime95, ReleaseOf("prime95"), null, CancellationToken.None);
        ToolActionOutcome portable = await ToolboxActions.InstallPortableAsync(Ddu, ReleaseOf("ddu"), null, CancellationToken.None);

        Assert.False(install.Succeeded);
        Assert.Contains("ne s'installe pas", install.Message);
        Assert.False(portable.Succeeded);
        Assert.Contains("portable", portable.Message);
    }

    [Fact]
    public async Task Sans_version_verifiee_rien_n_est_telecharge_et_la_page_officielle_est_proposee()
    {
        ToolActionOutcome none = await ToolboxActions.RunInstallerAsync(Ddu, null, null, CancellationToken.None);
        ToolActionOutcome other = await ToolboxActions.DownloadForUserAsync(Ddu, ReleaseOf("cpu-z"), null, CancellationToken.None);
        ToolActionOutcome portable = await ToolboxActions.InstallPortableAsync(CpuZ, null, null, CancellationToken.None);

        Assert.All(new[] { none, other, portable }, outcome =>
        {
            Assert.False(outcome.Succeeded);
            Assert.Equal(DownloadFailureKind.LinkUnavailable, outcome.Failure);
            Assert.Contains("page officielle", outcome.Message);
        });
    }

    [Fact]
    public void Le_message_ne_promet_une_signature_que_si_elle_a_ete_verifiee()
    {
        Assert.Equal("empreinte et signature de Wagnardsoft vérifiées", ToolboxActions.VerifiedDescription(Ddu));
        Assert.Equal("empreinte vérifiée", ToolboxActions.VerifiedDescription(CpuZ));
        Assert.Equal("empreinte vérifiée", ToolboxActions.VerifiedDescription(ToolCatalog.Find("rtss")!));
        Assert.Equal("empreinte vérifiée", ToolboxActions.VerifiedDescription(ToolCatalog.Find("prime95")!));
    }

    [Fact]
    public void La_mise_a_jour_reprend_les_reglages_mais_jamais_les_programmes()
    {
        using var temp = new TempDirectory();
        string old = Path.Combine(temp.Root, "9.9.2");
        string fresh = Path.Combine(temp.Root, "9.9.3");
        Directory.CreateDirectory(Path.Combine(old, "Smart", "disque1"));
        Directory.CreateDirectory(fresh);
        File.WriteAllText(Path.Combine(old, "DiskInfo.ini"), "mes réglages");
        File.WriteAllText(Path.Combine(old, "Smart", "disque1", "historique.csv"), "températures");
        File.WriteAllText(Path.Combine(old, "DiskInfo64.exe"), "ancien programme");
        File.WriteAllText(Path.Combine(old, "ancienne.dll"), "ancienne bibliothèque");
        File.WriteAllText(Path.Combine(old, "LisezMoi.txt"), "ancien texte");
        File.WriteAllText(Path.Combine(fresh, "LisezMoi.txt"), "nouveau texte");

        int kept = ToolboxActions.CarryOverSettings(old, fresh);

        Assert.Equal(2, kept);
        Assert.Equal("mes réglages", File.ReadAllText(Path.Combine(fresh, "DiskInfo.ini")));
        Assert.True(File.Exists(Path.Combine(fresh, "Smart", "disque1", "historique.csv")));
        Assert.False(File.Exists(Path.Combine(fresh, "DiskInfo64.exe")));
        Assert.False(File.Exists(Path.Combine(fresh, "ancienne.dll")));
        Assert.Equal("nouveau texte", File.ReadAllText(Path.Combine(fresh, "LisezMoi.txt")));
    }

    [Fact]
    public void Un_fichier_absent_n_est_jamais_lance_ni_ouvert()
    {
        Assert.False(UnelevatedLauncher.TryLaunch(@"C:\chemin\inexistant\outil.exe", "", out string? error));
        Assert.Contains("introuvable", error);
    }

    [Fact]
    public void L_app_elevee_ne_depose_rien_dans_un_dossier_de_Windows_ni_par_une_jonction()
    {
        using var temp = new TempDirectory();

        Assert.NotNull(UserDownloads.RefusalReason(Environment.SystemDirectory));
        Assert.NotNull(UserDownloads.RefusalReason(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)));
        Assert.NotNull(UserDownloads.RefusalReason(Path.Combine(temp.Root, "absent")));
        Assert.Null(UserDownloads.RefusalReason(temp.Root));

        string target = Path.Combine(temp.Root, "cible");
        Directory.CreateDirectory(target);
        string link = Path.Combine(temp.Root, "lien");
        Assert.True(Junctions.TryCreate(link, target), "mklink /J ne demande aucun droit particulier : la jonction doit exister.");
        Assert.Contains("lien", UserDownloads.RefusalReason(link));
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
