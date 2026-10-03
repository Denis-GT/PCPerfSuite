using PCPerfSuite.Core.Benchmark;
using PCPerfSuite.Core.Benchmark.Session;
using PCPerfSuite.Core.Compatibility;
using PCPerfSuite.Core.Hardware;
using PCPerfSuite.Core.Hardware.Cpu;
using PCPerfSuite.Core.Hardware.Cpu.Throttle;
using PCPerfSuite.Core.Hardware.Fans;
using PCPerfSuite.Core.Safety;
using PCPerfSuite.Core.Safety.Events;

namespace PCPerfSuite.Core.Tests;

internal static class BenchSnapshots
{
    public static readonly DateTime T0 = new(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);

    public static HardwareSnapshot At(double seconds, float? cpuTemp = 60, float? cpuPower = 65, float? fanRpm = 1200, bool cpuRead = true,
        bool? thermal = null, bool? power = null, double? batteryPercent = null, bool? powerOnline = null)
    {
        var fans = new List<FanReading>();
        if (fanRpm is not null)
        {
            fans.Add(new FanReading { HardwareName = "Carte mère", SensorName = "CPU Fan", SensorId = "/fan/0", Category = FanCategory.Cpu, Rpm = fanRpm });
        }
        BatterySnapshot? battery = batteryPercent is { } p
            ? new BatterySnapshot { PowerOnline = powerOnline ?? false, RemainingMWh = p * 10, FullChargeMWh = 1000 }
            : null;
        return new HardwareSnapshot
        {
            CapturedAtUtc = T0.AddSeconds(seconds),
            CapturedTimestamp = T0.AddSeconds(seconds).Ticks, // échelle de ManualClock.GetTimestamp
            Cpu = new CpuSnapshot { PackageTempC = cpuTemp, PowerWatts = cpuPower, MaxClockMhz = 4800, LoadPercent = 95 },
            Fans = fans,
            Battery = battery,
            GroupsRead = cpuRead ? [SensorGroup.Cpu, SensorGroup.CpuLoad] : [SensorGroup.CpuLoad],
            CpuThrottle = thermal is null && power is null ? null : new CpuThrottleReading { Source = CpuThrottleSource.WindowsCounters, Thermal = thermal, PowerLimit = power },
        };
    }
}

public class BackgroundLoadWindowTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Dix_secondes_sous_cinq_pour_cent_font_une_machine_calme()
    {
        var window = new BackgroundLoadWindow();
        for (int s = 0; s <= 10; s++) window.Note(T0.AddSeconds(s), 2);

        Assert.True(window.IsQuiet);
        Assert.Contains("machine calme", window.Describe());
    }

    [Fact]
    public void Un_historique_trop_court_ou_une_charge_de_fond_ne_sont_pas_calmes()
    {
        var shortWindow = new BackgroundLoadWindow();
        shortWindow.Note(T0, 1);
        shortWindow.Note(T0.AddSeconds(3), 1);
        Assert.False(shortWindow.IsQuiet);
        Assert.Contains("trop court", shortWindow.Describe());

        var busy = new BackgroundLoadWindow();
        for (int s = 0; s <= 10; s++) busy.Note(T0.AddSeconds(s), s % 2 == 0 ? 1 : 20);
        Assert.False(busy.IsQuiet);
        Assert.Contains("arrière-plan", busy.Describe());
        Assert.Equal(20, busy.MaxPercent);
    }

    [Fact]
    public void Les_vieux_echantillons_sortent_de_la_fenetre_et_les_absents_sont_ignores()
    {
        var window = new BackgroundLoadWindow();
        window.Note(T0, 90);
        window.Note(T0.AddSeconds(5), null);
        for (int s = 20; s <= 30; s++) window.Note(T0.AddSeconds(s), 1);

        Assert.True(window.IsQuiet);
        Assert.Equal("charge de fond non lue", new BackgroundLoadWindow().Describe());
    }
}

public class BenchPreconditionsTests
{
    private static BackgroundLoadWindow Quiet()
    {
        var window = new BackgroundLoadWindow();
        for (int s = 0; s <= 10; s++) window.Note(DateTimeOffset.UnixEpoch.AddSeconds(s), 1);
        return window;
    }

    [Fact]
    public void Sur_secteur_tout_est_disponible_et_representatif()
    {
        BenchPreconditionReport report = BenchPreconditions.Evaluate(new BenchPreconditionInputs(true, null, Quiet(), null, null, null, null));

        Assert.True(report.IsRepresentative);
        Assert.False(report.OnBattery);
        Assert.All(BenchTestKinds.All, kind => Assert.True(report.IsAvailable(kind)));
        Assert.Contains("sur secteur", report.Notes);
    }

    [Fact]
    public void Sur_batterie_le_bench_part_mais_n_est_pas_representatif_et_s_arrete_sous_30_pour_cent()
    {
        BenchPreconditionReport okay = BenchPreconditions.Evaluate(new BenchPreconditionInputs(false, 55, Quiet(), null, null, null, null));
        BenchPreconditionReport low = BenchPreconditions.Evaluate(new BenchPreconditionInputs(false, 20, Quiet(), null, null, null, null));

        Assert.True(okay.AnyAvailable);
        Assert.False(okay.IsRepresentative);
        Assert.Contains(okay.Notes, n => n.Contains("non représentatif"));
        Assert.False(low.AnyAvailable);
        Assert.Contains("20 %", low.For(BenchTestKind.CpuMono)!.Reason);
    }

    [Fact]
    public void Sans_worker_rien_ne_part_et_les_raisons_propres_s_appliquent_par_test()
    {
        var ram = new Unavailable(UnavailableCause.HardwareOrDriver, "pas assez de RAM libre");
        var disk = new Unavailable(UnavailableCause.MissingRights, "dossier refusé");
        BenchPreconditionReport perTest = BenchPreconditions.Evaluate(new BenchPreconditionInputs(true, null, Quiet(), null, ram, null, disk));
        BenchPreconditionReport noWorker = BenchPreconditions.Evaluate(new BenchPreconditionInputs(true, null, Quiet(), "hôte dotnet", null, null, null));

        Assert.True(perTest.IsAvailable(BenchTestKind.CpuMulti));
        Assert.True(perTest.IsAvailable(BenchTestKind.RamLatency));
        Assert.Same(ram, perTest.For(BenchTestKind.RamBandwidth));
        Assert.Same(disk, perTest.For(BenchTestKind.Disk));
        Assert.False(noWorker.AnyAvailable);
        Assert.Contains("hôte dotnet", noWorker.For(BenchTestKind.Disk)!.Reason);
    }
}

public class BenchThermalPolicyTests
{
    private static CpuPlatform Cpu(CpuVendor vendor, string name, int family = 0, int model = 0)
        => new() { Vendor = vendor, Name = name, Family = family, Model = model };

    [Fact]
    public void Intel_prend_son_tjmax_lu_sinon_un_seuil_prudent()
    {
        Assert.Equal(100, BenchThermalPolicy.Resolve(Cpu(CpuVendor.Intel, "i5-14600K"), null, 100).CpuThresholdC);
        Assert.Equal(BenchThermalPolicy.DefaultThresholdC, BenchThermalPolicy.Resolve(Cpu(CpuVendor.Intel, "i5"), null, null).CpuThresholdC);
        Assert.Equal(BenchThermalPolicy.DefaultThresholdC, BenchThermalPolicy.Resolve(Cpu(CpuVendor.Intel, "i5"), null, 250).CpuThresholdC);
    }

    [Fact]
    public void Amd_distingue_zen4_zen5_x3d_et_le_reste()
    {
        BenchThermalLimits zen4 = BenchThermalPolicy.Resolve(Cpu(CpuVendor.Amd, "Ryzen 7 7700", 0x19, 0x61), null, null);
        BenchThermalLimits zen5 = BenchThermalPolicy.Resolve(Cpu(CpuVendor.Amd, "Ryzen 9 9950X", 0x1A, 0x44), null, null);
        BenchThermalLimits x3d = BenchThermalPolicy.Resolve(Cpu(CpuVendor.Amd, "Ryzen 7 7800X3D", 0x19, 0x61), null, null);
        BenchThermalLimits zen3 = BenchThermalPolicy.Resolve(Cpu(CpuVendor.Amd, "Ryzen 5 5600X", 0x19, 0x21), null, null);
        BenchThermalLimits laptop = BenchThermalPolicy.Resolve(Cpu(CpuVendor.Amd, "Ryzen 7 7840HS", 0x19, 0x74), null, 95);

        Assert.Equal(BenchThermalPolicy.Zen4Or5ThresholdC, zen4.CpuThresholdC);
        Assert.Equal(BenchThermalPolicy.Zen4Or5ThresholdC, zen5.CpuThresholdC);
        Assert.Equal(BenchThermalPolicy.X3DThresholdC, x3d.CpuThresholdC);
        Assert.Equal(BenchThermalPolicy.DefaultThresholdC, zen3.CpuThresholdC);
        Assert.Equal(BenchThermalPolicy.Zen4Or5ThresholdC, laptop.CpuThresholdC);
        Assert.Contains("X3D", x3d.Source);
    }

    [Fact]
    public void Un_x3d_zen5_garde_le_seuil_des_zen5_qui_tournent_a_95_degres()
    {
        BenchThermalLimits zen5X3d = BenchThermalPolicy.Resolve(Cpu(CpuVendor.Amd, "AMD Ryzen 7 9800X3D 8-Core", 0x1A, 0x44), null, null);
        BenchThermalLimits zen3X3d = BenchThermalPolicy.Resolve(Cpu(CpuVendor.Amd, "AMD Ryzen 7 5800X3D 8-Core", 0x19, 0x21), null, null);

        Assert.Equal(BenchThermalPolicy.Zen4Or5ThresholdC, zen5X3d.CpuThresholdC);
        Assert.Contains("X3D Zen 5", zen5X3d.Source);
        Assert.Equal(BenchThermalPolicy.X3DThresholdC, zen3X3d.CpuThresholdC);
    }

    [Fact]
    public void Un_x3d_se_reconnait_aussi_a_ses_deux_tailles_de_l3()
    {
        (byte[] cpuSets, byte[] caches) = CpuSetBuffers.DualCcdX3D();
        CpuTopology topology = CpuTopology.Build(CpuTopology.ParseCpuSets(cpuSets), CpuTopology.ParseCaches(caches))!;

        BenchThermalLimits limits = BenchThermalPolicy.Resolve(Cpu(CpuVendor.Amd, "AMD Ryzen 9 7950X3D 16-Core", 0x19, 0x61), topology, null);

        Assert.Equal(BenchThermalPolicy.X3DThresholdC, limits.CpuThresholdC);
        Assert.Equal(BenchThermalPolicy.Delay, limits.Delay);
        ThermalGuard guard = limits.CreateGuard();
        Assert.Equal(BenchThermalLimits.SensorName, Assert.Single(guard.Limits).Sensor);
    }

    [Fact]
    public void Un_processeur_inconnu_prend_le_seuil_prudent()
    {
        Assert.Equal(BenchThermalPolicy.DefaultThresholdC, BenchThermalPolicy.Resolve(null, null, null).CpuThresholdC);
        Assert.Contains("98 °C tenus 10 s", BenchThermalPolicy.Resolve(null, null, null).Describe());
    }
}

public class BenchSafetyMonitorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly BenchThermalLimits Limits = new(98, "test", TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));

    private static BenchSafetySample Sample(double seconds, float? temp = 70, float? fan = 1200, bool fanKnown = true, double? battery = null, bool onBattery = false)
        => new(T0.AddSeconds(seconds), temp, fan, fanKnown, battery, onBattery);

    [Fact]
    public void Le_seuil_tenu_dix_secondes_arrete_pas_une_pointe()
    {
        var monitor = new BenchSafetyMonitor(Limits);

        Assert.Null(monitor.Note(Sample(0, temp: 99)).Stop);
        Assert.True(monitor.Note(Sample(5, temp: 99)).Heating);
        Assert.Null(monitor.Note(Sample(6, temp: 80)).Stop);
        Assert.Null(monitor.Note(Sample(7, temp: 99)).Stop);
        BenchSafetyVerdict tripped = monitor.Note(Sample(17, temp: 99));

        Assert.Equal(BenchStopReason.Thermal, tripped.Stop);
        Assert.Contains("99 °C", tripped.Detail);
        Assert.Equal(99, monitor.MaxCpuTempC);
    }

    [Fact]
    public void La_temperature_maximale_est_celle_du_test_en_cours()
    {
        var monitor = new BenchSafetyMonitor(Limits);
        monitor.Note(Sample(0, temp: 95)); // test processeur

        monitor.Reset(); // test suivant
        monitor.Note(Sample(1, temp: 55));

        Assert.Equal(55, monitor.MaxCpuTempC);
    }

    [Fact]
    public void Un_ventilateur_cpu_vu_tourner_puis_a_zero_sous_charge_arrete_apres_dix_secondes()
    {
        var monitor = new BenchSafetyMonitor(Limits);

        Assert.Null(monitor.Note(Sample(0, fan: 1100)).Stop);
        Assert.Null(monitor.Note(Sample(1, fan: 0)).Stop);
        Assert.Null(monitor.Note(Sample(6, fan: 0)).Stop);
        Assert.Equal(BenchStopReason.FanStopped, monitor.Note(Sample(11, fan: 0)).Stop);
    }

    [Fact]
    public void Un_ventilateur_cpu_jamais_vu_tourner_pendant_le_test_ne_declenche_rien()
    {
        // Connecteur CPU_FAN vide (AIO branché ailleurs) ou ventilateur arrêté par le BIOS à froid : 0 dès le début.
        var monitor = new BenchSafetyMonitor(Limits);
        for (int s = 0; s <= 30; s += 5) Assert.Null(monitor.Note(Sample(s, fan: 0)).Stop);

        // Vu tourner au test précédent ne compte pas : chaque test repart de zéro.
        var next = new BenchSafetyMonitor(Limits);
        next.Note(Sample(0, fan: 1200));
        next.Reset();
        for (int s = 1; s <= 30; s += 5) Assert.Null(next.Note(Sample(s, fan: 0)).Stop);
    }

    [Fact]
    public void Un_ventilateur_non_identifie_ou_hors_charge_ne_declenche_rien()
    {
        var monitor = new BenchSafetyMonitor(Limits);
        monitor.Note(Sample(0, fan: 0, fanKnown: false));
        Assert.Null(monitor.Note(Sample(12, fan: 0, fanKnown: false)).Stop);

        var idle = new BenchSafetyMonitor(Limits);
        idle.Note(Sample(0, fan: 0), underLoad: false);
        Assert.Null(idle.Note(Sample(12, fan: 0), underLoad: false).Stop);
        Assert.Null(idle.Note(Sample(13, fan: null)).Stop);
    }

    [Fact]
    public void La_batterie_sous_trente_pour_cent_arrete_sur_batterie_seulement()
    {
        var monitor = new BenchSafetyMonitor(Limits);

        Assert.Null(monitor.Note(Sample(0, battery: 20, onBattery: false)).Stop);
        Assert.Equal(BenchStopReason.BatteryLow, monitor.Note(Sample(1, battery: 20, onBattery: true)).Stop);
    }

    [Fact]
    public void Plus_aucun_relevé_pendant_dix_secondes_est_une_perte()
    {
        var monitor = new BenchSafetyMonitor(Limits);
        Assert.Null(new BenchSafetyMonitor(Limits).NoteNoReading(T0.AddSeconds(30)).Stop);

        monitor.Note(Sample(0));
        Assert.Null(monitor.NoteNoReading(T0.AddSeconds(5)).Stop);
        Assert.Equal(BenchStopReason.SensorsLost, monitor.NoteNoReading(T0.AddSeconds(10)).Stop);
    }

    [Fact]
    public void Un_releve_sans_temperature_ni_batterie_ne_declenche_rien()
    {
        var monitor = new BenchSafetyMonitor(Limits);
        for (int s = 0; s < 30; s += 5) Assert.Null(monitor.Note(Sample(s, temp: null, fan: null, fanKnown: false)).Stop);
        Assert.Equal("arrêt de sécurité thermique", BenchSafetyMonitor.Label(BenchStopReason.Thermal));
    }

    [Fact]
    public void L_extraction_d_un_releve_prend_le_paquet_le_ventilateur_cpu_et_la_batterie()
    {
        HardwareSnapshot snapshot = BenchSnapshots.At(0, cpuTemp: 72, fanRpm: 900, batteryPercent: 45, powerOnline: false);

        BenchSafetySample sample = BenchSafetySample.From(snapshot, T0);

        Assert.Equal(72, sample.CpuTempC);
        Assert.Equal(900, sample.CpuFanRpm);
        Assert.True(sample.CpuFanIdentified);
        Assert.Equal(45, sample.BatteryPercent);
        Assert.True(sample.OnBattery);
        Assert.False(BenchSafetySample.From(BenchSnapshots.At(0, fanRpm: null), T0).CpuFanIdentified);
    }

    [Fact]
    public void Une_batterie_en_unites_relatives_donne_quand_meme_son_pourcentage()
    {
        // Pilote sans mWh (certains portables) : pas de RemainingMWh, mais le pourcentage relatif suffit au seuil.
        var relative = new BatterySnapshot { PowerOnline = false, IsCapacityRelative = true, RelativeChargePercent = 22 };

        Assert.Equal(22, BenchSafetySample.BatteryPercentOf(relative));
        Assert.Null(BenchSafetySample.BatteryPercentOf(new BatterySnapshot()));
        Assert.Null(BenchSafetySample.BatteryPercentOf(null));

        var monitor = new BenchSafetyMonitor(Limits);
        BenchSafetySample sample = BenchSafetySample.From(new HardwareSnapshot { CapturedAtUtc = T0.UtcDateTime, Battery = relative }, T0);
        Assert.Equal(BenchStopReason.BatteryLow, monitor.Note(sample).Stop);
    }
}

public class MonotonicClockTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Un_changement_d_heure_de_Windows_ne_deplace_ni_l_heure_ni_les_releves()
    {
        var time = new ManualClock(T0);
        var clock = new MonotonicClock(time);

        time.Now = T0.AddSeconds(3);
        Assert.Equal(T0.AddSeconds(3), clock.Now());

        time.WallShift = TimeSpan.FromSeconds(15); // recalage vers l'avant
        Assert.Equal(T0.AddSeconds(3), clock.Now());
        time.WallShift = TimeSpan.FromMinutes(-5); // l'heure recule
        Assert.Equal(T0.AddSeconds(3), clock.Now());
        Assert.Equal(T0.AddSeconds(1), clock.At(T0.AddSeconds(1).UtcTicks));
    }

    [Fact]
    public void Un_recul_de_l_heure_ne_retarde_pas_l_arret_thermique()
    {
        var time = new ManualClock(T0);
        var clock = new MonotonicClock(time);
        var monitor = new BenchSafetyMonitor(new BenchThermalLimits(95, "test", TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10)));

        Assert.Null(monitor.Note(new BenchSafetySample(clock.Now(), 96, 1200, true, null, false)).Stop);
        time.WallShift = TimeSpan.FromMinutes(-1);
        time.Now = T0.AddSeconds(11);

        Assert.Equal(BenchStopReason.Thermal, monitor.Note(new BenchSafetySample(clock.Now(), 96, 1200, true, null, false)).Stop);
    }
}

public class IdleReturnTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void On_attend_le_retour_a_trois_degres_de_la_base()
    {
        var idle = new IdleReturn(45, T0);

        Assert.False(idle.Check(T0.AddSeconds(5), 60).Done);
        Assert.False(idle.Check(T0.AddSeconds(20), 49).Done);
        IdleReturnState done = idle.Check(T0.AddSeconds(30), 48);
        Assert.True(done.Done);
        Assert.Contains("48 °C", done.Reason);
        Assert.Equal(48, idle.TargetC);
    }

    [Fact]
    public void Soixante_secondes_au_plus_meme_sans_retour()
    {
        var idle = new IdleReturn(45, T0);

        Assert.False(idle.Check(T0.AddSeconds(59), 70).Done);
        IdleReturnState done = idle.Check(T0.AddSeconds(60), 70);
        Assert.True(done.Done);
        Assert.Contains("60 s", done.Reason);
    }

    [Fact]
    public void Sans_temperature_une_pause_fixe()
    {
        var idle = new IdleReturn(null, T0);

        Assert.False(idle.Check(T0.AddSeconds(5), null).Done);
        Assert.True(idle.Check(T0.AddSeconds(10), null).Done);
        Assert.Null(idle.TargetC);
    }
}

public class SensorRecordingTests
{
    [Fact]
    public void Les_releves_resservis_sont_dedoublonnes_et_les_series_a_un_hertz()
    {
        var recording = new SensorRecording(BenchSnapshots.T0);
        HardwareSnapshot same = BenchSnapshots.At(0.25, cpuTemp: 60);

        Assert.True(recording.Add(same));
        Assert.False(recording.Add(same));
        Assert.True(recording.Add(BenchSnapshots.At(0.5, cpuTemp: 61)));
        Assert.True(recording.Add(BenchSnapshots.At(0.75, cpuTemp: 62)));
        Assert.True(recording.Add(BenchSnapshots.At(1.25, cpuTemp: 70)));
        Assert.True(recording.Add(BenchSnapshots.At(3.0, cpuTemp: 75, cpuRead: false)));

        Assert.Equal(5, recording.Snapshots);
        Assert.Equal(1, recording.Duplicates);
        Assert.Equal(1, recording.CpuGroupMisses);
        SensorSeries temps = recording["cpu-temp"];
        Assert.Equal([62f, 70f, 75f], temps.Points.Select(p => p.Value));
        Assert.Equal(75, temps.Max);
        Assert.Equal(3, recording["ventilateur-cpu"].Points.Count);
        Assert.Empty(recording["gpu-temp"].Points);
    }

    [Fact]
    public void La_cadence_obtenue_dit_l_intervalle_moyen_et_le_plus_grand_trou()
    {
        var recording = new SensorRecording(BenchSnapshots.T0);
        recording.Add(BenchSnapshots.At(0));
        recording.Add(BenchSnapshots.At(0.25));
        recording.Add(BenchSnapshots.At(0.5));
        recording.Add(BenchSnapshots.At(2.5));

        RecordingCadence cadence = recording.Cadence(BenchSnapshots.T0.AddSeconds(3));

        Assert.Equal(4, cadence.Snapshots);
        Assert.Equal(3, cadence.Seconds);
        Assert.Equal(2000, cadence.MaxGapMs);
        Assert.InRange(cadence.MeanIntervalMs!.Value, 833, 834);
        Assert.Contains("trou max 2,0 s", cadence.Describe().Replace("2.0", "2,0"));
        Assert.Equal("aucun relevé pendant le test", new SensorRecording(BenchSnapshots.T0).Cadence(BenchSnapshots.T0).Describe());
    }

    [Fact]
    public void Le_bridage_est_compte_par_cause()
    {
        var recording = new SensorRecording(BenchSnapshots.T0);
        recording.Add(BenchSnapshots.At(0, thermal: false, power: true));
        recording.Add(BenchSnapshots.At(1, thermal: true, power: true));
        recording.Add(BenchSnapshots.At(2));

        ThrottleTally tally = recording.Throttle;

        Assert.Equal(2, tally.Snapshots);
        Assert.Equal(1, tally.Thermal);
        Assert.Equal(2, tally.PowerLimit);
        Assert.True(tally.Any);
        Assert.Contains("puissance 2", tally.Describe());
        Assert.Equal("bridage non lu", new SensorRecording(BenchSnapshots.T0).Throttle.Describe());
    }
}

public class PowerModeReaderTests
{
    [Fact]
    public void Les_superpositions_de_windows_ont_leur_libelle()
    {
        Assert.Equal("Équilibré", PowerModeReader.OverlayLabel(Guid.Empty));
        Assert.Equal("Meilleures performances", PowerModeReader.OverlayLabel(PowerModeReader.BestPerformance));
        Assert.Equal("Meilleure efficacité énergétique", PowerModeReader.OverlayLabel(PowerModeReader.BestPowerEfficiency));
        Assert.Equal("mode d'alimentation non lu", PowerModeReader.OverlayLabel(null));
        Assert.StartsWith("mode inconnu", PowerModeReader.OverlayLabel(Guid.NewGuid()));
    }

    [Fact]
    public void La_lecture_sur_ce_pc_ne_plante_pas()
    {
        PowerModeReading reading = PowerModeReader.Read();

        Assert.NotEmpty(reading.OverlayLabel);
        Assert.NotEmpty(reading.Describe());
    }
}

public class BenchContextReaderTests
{
    [Fact]
    public void Le_contexte_de_ce_pc_se_lit_sans_planter_et_dit_ce_qui_manque()
    {
        BenchContext context = BenchContextReader.Read(new BenchContextSources { CpuPowerLimits = () => throw new InvalidOperationException("boum") });

        Assert.NotEmpty(context.CpuName);
        Assert.True(context.LogicalProcessors > 0);
        Assert.NotEmpty(context.Windows);
        Assert.NotEmpty(context.Runtime);
        Assert.Null(context.VendorMode);
        Assert.NotNull(context.VendorModeUnavailable);
        Assert.Contains("#4", context.PcieLinkUnavailable!.Reason);
        Assert.Contains(context.Problems, p => p.StartsWith("limites CPU"));
        Assert.Null(context.CpuSustainedWatts);
    }

    [Fact]
    public void La_priorite_de_l_app_est_relevee_puis_rendue()
    {
        using System.Diagnostics.Process current = System.Diagnostics.Process.GetCurrentProcess();
        System.Diagnostics.ProcessPriorityClass before = current.PriorityClass;

        using (ProcessPriorityScope scope = ProcessPriorityScope.Raise(System.Diagnostics.ProcessPriorityClass.AboveNormal))
        {
            Assert.NotEmpty(scope.Note);
            current.Refresh();
            Assert.Equal(System.Diagnostics.ProcessPriorityClass.AboveNormal, current.PriorityClass);
        }

        current.Refresh();
        Assert.Equal(before, current.PriorityClass);
    }
}

public class BenchRecoveryHandlerTests
{
    private static RecoveredEntry Entry(string action, IncidentQualificationKind kind, Dictionary<string, string>? values = null)
    {
        var entry = new SessionJournalEntry(Guid.NewGuid(), BenchVersion.Requester, action, values ?? new Dictionary<string, string>(),
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 4242, SessionEntryState.InProgress, null, DateTimeOffset.UnixEpoch);
        return new RecoveredEntry(entry, new IncidentQualification(kind, [], IncidentClassifier.Label(kind)));
    }

    [Fact]
    public void Un_test_interrompu_est_note_et_le_fichier_disque_supprime_sur_son_volume()
    {
        var deleted = new List<(string Letter, bool System)>();
        var handler = new BenchRecoveryHandler((letter, system) => { deleted.Add((letter, system)); return (true, null); });

        string? note = handler.Handle(
        [
            Entry("cpu-mono", IncidentQualificationKind.Interrupted),
            Entry("disque", IncidentQualificationKind.BlueScreen, new Dictionary<string, string> { ["volume"] = "D", ["systeme"] = "non", ["taille-mo"] = "1024" }),
        ]);

        Assert.NotNull(note);
        Assert.Contains("bench interrompu : processeur, un cœur", note);
        Assert.Contains("arrêt brutal pendant le bench", note);
        Assert.Contains("disque", note);
        Assert.Contains("fichier de test disque supprimé", note);
        Assert.DoesNotContain(@"\", note);
        Assert.Equal(("D:", false), Assert.Single(deleted));
        Assert.Equal(RecoveryStage.Bench, handler.Stage);
        Assert.Equal(["bench"], handler.Components);
    }

    [Fact]
    public void Le_volume_systeme_est_reconnu_et_une_suppression_ratee_est_dite()
    {
        var handler = new BenchRecoveryHandler((_, _) => (false, "fichier verrouillé"));

        string? note = handler.Handle([Entry("disque", IncidentQualificationKind.CleanShutdown, new Dictionary<string, string> { ["volume"] = "c", ["systeme"] = "oui" })]);

        Assert.Contains("non supprimé (fichier verrouillé)", note);
        Assert.StartsWith("bench interrompu", note);
    }

    [Fact]
    public void Sans_volume_ou_sans_entree_rien_n_est_supprime()
    {
        int calls = 0;
        var handler = new BenchRecoveryHandler((_, _) => { calls++; return (true, null); });

        Assert.Null(handler.Handle([]));
        Assert.NotNull(handler.Handle([Entry("disque", IncidentQualificationKind.Unknown)]));
        Assert.NotNull(handler.Handle([Entry("disque", IncidentQualificationKind.Unknown, new Dictionary<string, string> { ["volume"] = "[masqué]" })]));
        Assert.Equal(0, calls);
        Assert.True(BenchRecoveryHandler.IsBrutal(IncidentQualificationKind.PowerLoss));
        Assert.False(BenchRecoveryHandler.IsBrutal(IncidentQualificationKind.Interrupted));
    }

    [Fact]
    public void Le_fichier_de_test_d_un_volume_se_supprime_vraiment()
    {
        using var temp = new TempDirectory();
        string folder = Path.Combine(temp.Root, "PCPerfSuite.Bench");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, Core.Benchmark.Disk.DiskTestFile.FileName);
        File.WriteAllText(path, "reste");

        Assert.True(Core.Benchmark.Disk.DiskTestFile.TryDeleteStale(path, out _));
        Assert.False(File.Exists(path));
    }
}
