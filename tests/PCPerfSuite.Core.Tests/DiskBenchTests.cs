using System.Buffers.Binary;
using PCPerfSuite.Core.Benchmark;
using PCPerfSuite.Core.Benchmark.Disk;
using PCPerfSuite.Core.Benchmark.Protocol;
using PCPerfSuite.Core.Benchmark.Worker;
using PCPerfSuite.Core.Compatibility;
using PCPerfSuite.Core.Hardware;

namespace PCPerfSuite.Core.Tests;

public class DiskBenchPlanTests
{
    private const long Mo = 1L << 20;

    [Fact]
    public void Un_ssd_passe_huit_phases_lecture_puis_ecriture_par_profil()
    {
        DiskBenchPlan plan = DiskBenchPlan.Create(1024 * Mo, 4096, 5, isRotational: false);

        Assert.Equal(
            ["seq1m-q8.lecture", "seq1m-q8.ecriture", "seq1m-q1.lecture", "seq1m-q1.ecriture", "alea4k-q32.lecture", "alea4k-q32.ecriture", "alea4k-q1.lecture", "alea4k-q1.ecriture"],
            plan.Phases.Select(p => p.MeasurementKey));
        Assert.Equal([8, 8, 1, 1, 32, 32, 1, 1], plan.Phases.Select(p => p.QueueDepth));
        Assert.All(plan.Phases.Take(4), p => Assert.Equal((int)Mo, p.BlockBytes));
        Assert.All(plan.Phases.Skip(4), p => Assert.Equal(4096, p.BlockBytes));
        Assert.All(plan.Phases.Skip(4), p => Assert.True(p.IsRandom && p.ReportsIops));
        Assert.All(plan.Phases.Take(4), p => Assert.False(p.IsRandom || p.ReportsIops));
        Assert.Equal(1024 * Mo, plan.PrefillBytes);
        Assert.Equal(4 * 1024 * Mo, plan.WriteBudgetBytes);
        Assert.Equal(4 * 1024 * Mo, plan.PlannedWriteBytes);
    }

    [Fact]
    public void Un_disque_a_plateaux_ne_passe_que_les_files_de_1_et_ecrit_deux_fois_et_demie_la_taille()
    {
        DiskBenchPlan plan = DiskBenchPlan.Create(1024 * Mo, 4096, 5, isRotational: true);

        Assert.Equal(["seq1m-q1.lecture", "seq1m-q1.ecriture", "alea4k-q1.lecture", "alea4k-q1.ecriture"], plan.Phases.Select(p => p.MeasurementKey));
        Assert.Equal(2560 * Mo, plan.PlannedWriteBytes);
        Assert.True(plan.IsRotational);
    }

    [Fact]
    public void La_taille_est_alignee_au_mo_et_les_blocs_au_secteur()
    {
        DiskBenchPlan plan = DiskBenchPlan.Create(300 * Mo + 12345, 8192, 5, false);

        Assert.Equal(300 * Mo, plan.FileBytes);
        Assert.All(plan.Phases.Skip(4), p => Assert.Equal(8192, p.BlockBytes));
        Assert.All(plan.Phases, p => Assert.Equal(0, p.MaxBytes % p.BlockBytes));
    }

    [Fact]
    public void Un_budget_plus_court_reduit_les_ecritures_en_gardant_le_preremplissage()
    {
        DiskBenchPlan plan = DiskBenchPlan.Create(1024 * Mo, 4096, 5, false, writeBudgetBytes: 2 * 1024 * Mo);

        Assert.Equal(1024 * Mo, plan.PrefillBytes);
        Assert.InRange(plan.PlannedWriteBytes, 2 * 1024 * Mo - 8 * Mo, 2 * 1024 * Mo);
        DiskPhase seq = plan.Phases.First(p => p.ProfileKey == "seq1m-q8" && p.Operation == DiskIoOperation.Write);
        DiskPhase rnd = plan.Phases.First(p => p.ProfileKey == "alea4k-q32" && p.Operation == DiskIoOperation.Write);
        Assert.InRange(seq.MaxBytes, 340 * Mo, 342 * Mo); // 1 Go × 1/3
        Assert.InRange(rnd.MaxBytes, 170 * Mo, 171 * Mo); // 0,5 Go × 1/3
    }

    [Fact]
    public void Les_parametres_invalides_sont_refuses()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DiskBenchPlan.Create(4 * Mo, 4096, 5, false));
        Assert.Throws<ArgumentOutOfRangeException>(() => DiskBenchPlan.Create(64 * Mo, 3000, 5, false));
        Assert.Throws<ArgumentOutOfRangeException>(() => DiskBenchPlan.Create(64 * Mo, 4096, 0, false));
        Assert.Throws<ArgumentOutOfRangeException>(() => DiskBenchPlan.Create(64 * Mo, 4096, 5, false, writeBudgetBytes: 64 * Mo));
    }

    [Fact]
    public void Une_chute_de_debit_sur_le_dernier_tiers_signale_un_cache_epuise()
    {
        Assert.True(DiskBenchPlan.LooksLikeCacheExhaustion([2000, 2000, 2000, 1900, 500, 400, 400]));
        Assert.False(DiskBenchPlan.LooksLikeCacheExhaustion([2000, 1950, 2000, 1980, 1900, 2000]));
        Assert.False(DiskBenchPlan.LooksLikeCacheExhaustion([2000, 100]));
        Assert.False(DiskBenchPlan.LooksLikeCacheExhaustion([]));
    }

    [Fact]
    public void La_cle_et_le_libelle_d_une_phase_sont_stables()
    {
        var phase = new DiskPhase("alea4k-q32", "Aléatoire 4 Ko, file 32", DiskIoOperation.Write, 4096, 32, true, 5, 0);

        Assert.Equal("alea4k-q32.ecriture", phase.MeasurementKey);
        Assert.Equal("Aléatoire 4 Ko, file 32, écriture", phase.Label);
    }
}

public class DiskTestFileTests
{
    private const long Mo = 1L << 20;

    [Fact]
    public void L_espace_libre_exige_est_la_taille_plus_dix_pour_cent_et_512_mo()
    {
        Assert.Equal((long)(1024 * Mo * 1.1) + 512 * Mo, DiskTestFile.RequiredFreeBytes(1024 * Mo));
    }

    [Fact]
    public void Le_dossier_d_un_volume_secondaire_est_a_sa_racine()
    {
        Assert.Equal(@"D:\PCPerfSuite.Bench", DiskTestFile.FolderOnVolume("D:"));
        Assert.Equal("test-disque.bin", DiskTestFile.FileName);
    }

    [Fact]
    public void La_taille_se_regle_par_pas_de_256_mo_entre_256_mo_et_8_go()
    {
        Assert.Equal(256 * Mo, DiskTestFile.ClampFileBytes(0));
        Assert.Equal(256 * Mo, DiskTestFile.ClampFileBytes(300 * Mo));
        Assert.Equal(1024 * Mo, DiskTestFile.ClampFileBytes(1024 * Mo));
        Assert.Equal(8192 * Mo, DiskTestFile.ClampFileBytes(20_000 * Mo));
    }

    [Fact]
    public void Un_volume_ecarte_ou_trop_plein_est_refuse_avec_la_raison()
    {
        BenchVolume network = Volume(DriveType.Network, "NTFS", free: 100_000 * Mo);
        BenchVolume full = Volume(DriveType.Fixed, "NTFS", free: 1000 * Mo);

        DiskTestFilePlacement refused = DiskTestFile.Prepare(network, 1024 * Mo, _ => null);
        DiskTestFilePlacement tooFull = DiskTestFile.Prepare(full, 1024 * Mo, _ => null);

        Assert.False(refused.IsReady);
        Assert.Contains("réseau", refused.Problem!.Reason);
        Assert.False(tooFull.IsReady);
        Assert.Contains("espace libre", tooFull.Problem!.Reason);
        Assert.Contains("1000 Mo libres", tooFull.Problem.Reason);
    }

    [Fact]
    public void L_espace_libre_est_relu_au_lancement_et_pas_pris_de_l_inventaire()
    {
        // Inventaire de l'ouverture de la page : 100 Go libres ; depuis, le volume s'est rempli.
        BenchVolume volume = Volume(DriveType.Fixed, "NTFS", free: 100_000 * Mo);

        DiskTestFilePlacement placement = DiskTestFile.Prepare(volume, 4096 * Mo, _ => 4300 * Mo);

        Assert.False(placement.IsReady);
        Assert.Contains("4300 Mo libres", placement.Problem!.Reason);
    }

    [Fact]
    public void Un_volume_absent_n_est_pas_un_fichier_supprime()
    {
        string? missing = Enumerable.Range('D', 23).Select(c => $"{(char)c}:").FirstOrDefault(l => !Directory.Exists(l + "\\"));
        if (missing is null) return; // toutes les lettres prises : rien à vérifier sur ce PC

        Assert.False(DiskTestFile.TryDeleteStale(Path.Combine(DiskTestFile.FolderOnVolume(missing), DiskTestFile.FileName), out string? error));
        Assert.Contains("volume absent", error);
        Assert.False(DiskTestFile.TryDeleteOnVolume(missing, isSystem: false, out _));
    }

    [Fact]
    public void Un_fichier_de_test_restant_est_supprime_mais_jamais_un_lien()
    {
        using var temp = new TempDirectory();
        string path = temp.File(DiskTestFile.FileName);
        File.WriteAllText(path, "reste d'un test interrompu");

        Assert.True(DiskTestFile.TryDeleteStale(path, out string? error));
        Assert.Null(error);
        Assert.False(File.Exists(path));
        Assert.True(DiskTestFile.TryDeleteStale(path, out _));
    }

    private static BenchVolume Volume(DriveType type, string format, long free)
        => new("Z:", null, format, type, 200_000 * Mo, free, false, new VolumeDeviceInfo(512, 4096, 1, false, null), null, null,
            BenchVolumeRules.Judge(type, true, format));
}

public class BenchVolumeRulesTests
{
    [Theory]
    [InlineData(DriveType.Fixed, "NTFS")]
    [InlineData(DriveType.Fixed, "ReFS")]
    [InlineData(DriveType.Removable, "exFAT")]
    [InlineData(DriveType.Removable, "NTFS")]
    public void Les_volumes_locaux_ntfs_refs_exfat_sont_eligibles(DriveType type, string format)
    {
        Assert.Null(BenchVolumeRules.Judge(type, true, format));
    }

    [Fact]
    public void Les_autres_sont_ecartes_avec_la_raison()
    {
        Assert.Contains("réseau", BenchVolumeRules.Judge(DriveType.Network, true, "NTFS")!.Reason);
        Assert.Contains("optique", BenchVolumeRules.Judge(DriveType.CDRom, true, "UDF")!.Reason);
        Assert.Contains("FAT32", BenchVolumeRules.Judge(DriveType.Removable, true, "FAT32")!.Reason);
        Assert.Contains("non prêt", BenchVolumeRules.Judge(DriveType.Removable, false, "")!.Reason);
        Assert.Equal(UnavailableCause.UnsupportedModel, BenchVolumeRules.Judge(DriveType.Fixed, true, "Btrfs")!.Cause);
        Assert.True(BenchVolumeRules.SupportsAcl("NTFS"));
        Assert.False(BenchVolumeRules.SupportsAcl("exFAT"));
    }

    [Fact]
    public void La_description_d_un_volume_dit_l_essentiel()
    {
        var disk = new PhysicalDiskInfo("0", "WD_BLACK SN770", 17, 4, 4096, 512);
        var volume = new BenchVolume("C:", "Windows", "NTFS", DriveType.Fixed, 1_000_000_000_000, 412_000_000_000, true,
            new VolumeDeviceInfo(512, 4096, 0, false, null), disk, true, null);

        Assert.Equal("C: Windows · système · NVMe SSD · 412 Go libres sur 1000", volume.Describe());
        Assert.False(volume.IsRotational);
        Assert.Equal(4096, volume.SectorBytes);
        Assert.True(volume.IsEligible);
    }

    [Fact]
    public void L_inventaire_de_ce_pc_ne_plante_pas_et_connait_le_volume_systeme()
    {
        IReadOnlyList<BenchVolume> volumes = BenchVolumeReader.Read();

        BenchVolume system = Assert.Single(volumes, v => v.IsSystem);
        Assert.Equal(volumes[0], system);
        Assert.True(system.TotalBytes > 0);
    }
}

public class VolumeDeviceTests
{
    [Fact]
    public void Les_descripteurs_sont_decodes_a_leur_offset()
    {
        var alignment = new byte[28];
        BinaryPrimitives.WriteUInt32LittleEndian(alignment.AsSpan(16), 512);
        BinaryPrimitives.WriteUInt32LittleEndian(alignment.AsSpan(20), 4096);
        var penalty = new byte[12];
        penalty[8] = 1;
        var number = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(number.AsSpan(4), 2);

        Assert.Equal((512, 4096), VolumeDevice.ParseAccessAlignment(alignment));
        Assert.True(VolumeDevice.ParseSeekPenalty(penalty));
        Assert.Equal(2, VolumeDevice.ParseDeviceNumber(number));
    }

    [Fact]
    public void Une_valeur_absurde_ou_un_tampon_court_rendent_null()
    {
        var odd = new byte[28];
        BinaryPrimitives.WriteUInt32LittleEndian(odd.AsSpan(16), 3000);
        BinaryPrimitives.WriteUInt32LittleEndian(odd.AsSpan(20), 0);
        var unknown = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(unknown.AsSpan(4), uint.MaxValue);

        Assert.Equal((null, null), VolumeDevice.ParseAccessAlignment(odd));
        Assert.Equal((null, null), VolumeDevice.ParseAccessAlignment(new byte[8]));
        Assert.Null(VolumeDevice.ParseSeekPenalty(new byte[4]));
        Assert.Null(VolumeDevice.ParseDeviceNumber(unknown));
        Assert.Equal(VolumeDevice.DefaultSectorBytes, new VolumeDeviceInfo(null, null, null, null, "rien").SectorBytes);
        Assert.Equal(512, new VolumeDeviceInfo(512, null, null, null, null).SectorBytes);
    }

    [Theory]
    [InlineData("c", "C:")]
    [InlineData("D:", "D:")]
    [InlineData(@"E:\", "E:")]
    [InlineData(@"\\serveur\part", null)]
    [InlineData("", null)]
    public void La_lettre_est_normalisee(string input, string? expected)
    {
        Assert.Equal(expected, VolumeDevice.NormalizeLetter(input));
    }

    [Fact]
    public void Le_volume_systeme_de_ce_pc_rend_un_secteur_ou_une_raison()
    {
        string letter = VolumeDevice.NormalizeLetter(Path.GetPathRoot(Environment.SystemDirectory))!;

        VolumeDeviceInfo info = VolumeDevice.Read(letter);

        Assert.True(info.PhysicalSectorBytes is not null || info.Problem is not null);
        Assert.InRange(info.SectorBytes, 512, 65536);
    }
}

public class PhysicalDiskLabelsTests
{
    [Fact]
    public void Les_codes_wmi_ont_leur_libelle()
    {
        Assert.Equal("NVMe", PhysicalDiskLabels.BusType(17));
        Assert.Equal("SATA", PhysicalDiskLabels.BusType(11));
        Assert.Equal("USB", PhysicalDiskLabels.BusType(7));
        Assert.Equal("inconnu", PhysicalDiskLabels.BusType(null));
        Assert.Equal("HDD", PhysicalDiskLabels.MediaType(3));
        Assert.Equal("SSD", PhysicalDiskLabels.MediaType(4));
        Assert.True(new PhysicalDiskInfo("0", null, 11, 3, null, null).IsRotational);
        Assert.Null(new PhysicalDiskInfo("0", null, 7, 0, null, null).IsRotational);
        Assert.Equal("USB (média non dit)", new PhysicalDiskInfo("0", null, 7, 0, null, null).Describe());
    }
}

/// <summary>De vraies E/S sans cache sur un petit fichier du dossier temporaire (NTFS) : c'est le chemin d'ouverture,
/// d'alignement, de files d'attente et de suppression qu'on vérifie, pas la vitesse du disque.</summary>
public class DiskBenchRunnerTests
{
    private const long Mo = 1L << 20;

    private static BenchJobRequest Request(string path, bool rotational = false, long size = 16 * Mo) => new()
    {
        Kind = BenchTestKinds.Key(BenchTestKind.Disk),
        Disk = new DiskJobParameters { Path = path, FileSizeBytes = size, SectorBytes = 4096, PhaseSeconds = 0.15, IsRotational = rotational },
    };

    [Fact]
    public void Un_test_complet_rend_toutes_les_mesures_et_supprime_le_fichier()
    {
        using var temp = new TempDirectory();
        string path = temp.File(DiskTestFile.FileName);
        var progress = new List<BenchProgress>();

        BenchJobResult result = new DiskBenchRunner().Run(Request(path), progress.Add, CancellationToken.None);

        Assert.True(result.Succeeded, result.Error);
        Assert.False(File.Exists(path));
        string[] keys = result.Measurements.Select(m => m.Key).ToArray();
        Assert.Equal(DiskBenchPlan.PrefillKey, keys[0]);
        Assert.Contains("seq1m-q8.lecture", keys);
        Assert.Contains("seq1m-q8.ecriture", keys);
        Assert.Contains("alea4k-q32.lecture", keys);
        Assert.Contains("alea4k-q32.lecture.iops", keys);
        Assert.Contains("alea4k-q1.ecriture.iops", keys);
        Assert.Equal(1 + 8 + 4, keys.Length);
        Assert.All(result.Measurements, m => Assert.True(m.Median > 0, m.Key));
        Assert.All(result.Measurements.Where(m => m.Key.EndsWith(DiskBenchRunner.IopsSuffix)), m => Assert.Equal(DiskBenchRunner.IopsUnit, m.Unit));
        Assert.Equal("16", result.Notes["fichier-mo"]);
        Assert.Equal("oui", result.Notes["fichier-supprime"]);
        Assert.InRange(long.Parse(result.Notes["ecrit-mo"]), 16, 64);
        Assert.NotEmpty(progress);
        Assert.Contains(progress, p => p.Value is > 0);
    }

    [Fact]
    public void Un_disque_a_plateaux_passe_les_files_de_1_seulement()
    {
        using var temp = new TempDirectory();
        string path = temp.File(DiskTestFile.FileName);

        BenchJobResult result = new DiskBenchRunner().Run(Request(path, rotational: true), null, CancellationToken.None);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(1 + 4 + 2, result.Measurements.Count);
        Assert.DoesNotContain(result.Measurements, m => m.Key.Contains("q8") || m.Key.Contains("q32"));
    }

    [Fact]
    public void Un_fichier_deja_present_est_refuse_et_laisse_en_place()
    {
        using var temp = new TempDirectory();
        string path = temp.File(DiskTestFile.FileName);
        File.WriteAllText(path, "x");

        BenchJobResult result = new DiskBenchRunner().Run(Request(path), null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("déjà là", result.Error);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Une_annulation_arrete_le_test_et_supprime_le_fichier()
    {
        using var temp = new TempDirectory();
        string path = temp.File(DiskTestFile.FileName);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));
        BenchJobRequest request = Request(path, size: 64 * Mo);
        request.Disk!.PhaseSeconds = 5;

        BenchJobResult result = new DiskBenchRunner().Run(request, null, cancel.Token);

        Assert.False(result.Succeeded);
        Assert.Equal(BenchJobDispatcher.CancelledError, result.Error);
        Assert.False(File.Exists(path));
        Assert.InRange(result.DurationSeconds, 0, 10);
    }

    [Fact]
    public void Arreter_pendant_la_derniere_phase_n_est_pas_un_test_reussi()
    {
        using var temp = new TempDirectory();
        string path = temp.File(DiskTestFile.FileName);
        string lastPhase = DiskBenchPlan.Create(16 * Mo, 4096, 0.15, isRotational: false).Phases[^1].Label;
        using var cancel = new CancellationTokenSource();

        BenchJobResult result = new DiskBenchRunner().Run(Request(path), p => { if (p.Phase == lastPhase) cancel.Cancel(); }, cancel.Token);

        Assert.False(result.Succeeded);
        Assert.Equal(BenchJobDispatcher.CancelledError, result.Error);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Les_tranches_d_une_phase_disque_ne_rendent_pas_la_mesure_instable()
    {
        using var temp = new TempDirectory();

        BenchJobResult result = new DiskBenchRunner().Run(Request(temp.File(DiskTestFile.FileName)), null, CancellationToken.None);

        Assert.True(result.Succeeded, result.Error);
        Assert.All(result.Measurements, m => Assert.False(m.IsUnstable, m.Key));
    }

    [Fact]
    public void Un_dossier_devenu_une_jonction_est_refuse_par_le_worker()
    {
        using var temp = new TempDirectory();
        string target = Directory.CreateDirectory(temp.File("ailleurs")).FullName;
        string junction = temp.File("PCPerfSuite.Bench");
        using (var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junction}\" \"{target}\"")
               { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!)
        {
            mklink.WaitForExit(10_000);
        }
        if (!Directory.Exists(junction)) return; // jonction impossible à créer ici : rien à vérifier

        Assert.True(DiskTestFile.FolderIsLink(junction));
        Assert.False(DiskTestFile.FolderIsLink(target));

        BenchJobResult result = new DiskBenchRunner().Run(Request(Path.Combine(junction, DiskTestFile.FileName)), null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("lien", result.Error);
        Assert.Empty(Directory.GetFiles(target));
    }

    [Fact]
    public void Des_parametres_invalides_sont_refuses_sans_rien_creer()
    {
        using var temp = new TempDirectory();
        string path = temp.File(DiskTestFile.FileName);
        BenchJobRequest tooSmall = Request(path, size: 2 * Mo);
        var noPath = new BenchJobRequest { Kind = "disque", Disk = new DiskJobParameters { FileSizeBytes = 64 * Mo } };

        Assert.Contains("Au moins", new DiskBenchRunner().Run(tooSmall, null, CancellationToken.None).Error);
        Assert.Contains("chemin", new DiskBenchRunner().Run(noPath, null, CancellationToken.None).Error);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Le_distributeur_connait_le_test_disque()
    {
        using var temp = new TempDirectory();
        BenchJobResult result = BenchJobDispatcher.Run(Request(temp.File(DiskTestFile.FileName)), null, CancellationToken.None);

        Assert.True(result.Succeeded, result.Error);
    }
}
