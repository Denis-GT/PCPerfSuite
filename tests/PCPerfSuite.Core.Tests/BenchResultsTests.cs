using PCPerfSuite.Core.Benchmark;
using PCPerfSuite.Core.Benchmark.Protocol;
using PCPerfSuite.Core.Benchmark.Results;
using PCPerfSuite.Core.Benchmark.Session;
using PCPerfSuite.Core.Compatibility;

namespace PCPerfSuite.Core.Tests;

internal static class BenchResultsTestData
{
    public static readonly DateTimeOffset T0 = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    public static BenchTestOutcome Outcome(BenchTestKind kind, bool succeeded = true, bool comparable = true, bool mismatch = false, BenchStopReason? stop = null)
    {
        var request = new BenchJobRequest { Kind = BenchTestKinds.Key(kind) };
        var result = new BenchJobResult { JobId = request.Id, Kind = request.Kind, Succeeded = succeeded, IsComparable = comparable, ChecksumMismatch = mismatch, DurationSeconds = 23 };
        if (!succeeded) result.Error = "arrêté";
        result.Measurements.Add(BenchMeasurement.From("entier.rafale", "Entier (rafale)", "Mops/s", [325, 327, 323]));
        result.Measurements.Add(BenchMeasurement.From("flottant.rafale", "Flottant (rafale)", "GFLOPS", [48, 49, 47]));
        result.Measurements.Add(BenchMeasurement.From("branches.rafale", "Branchements (rafale)", "Mops/s", [395, 398, 392]));
        result.Notes["threads"] = "1";
        var series = new SensorRecording(T0.UtcDateTime);
        series.Add(BenchSnapshots.At(0, cpuTemp: 50));
        series.Add(BenchSnapshots.At(1, cpuTemp: 60, power: true));
        return new BenchTestOutcome(kind, request, result, T0, T0.AddSeconds(23), series.Series, series.Cadence(T0.AddSeconds(23).UtcDateTime), series.Throttle, 60, stop,
            stop is null ? null : "détail", true, null);
    }

    public static BenchSessionOutcome Session(params BenchTestOutcome[] tests)
        => new(T0, T0.AddSeconds(60), tests, null, ["EcoQoS : désactivé"], ["worker lancé"], tests.FirstOrDefault(t => t.StopReason is not null)?.StopReason, false);

    public static BenchContext Context() => BenchContextReader.Read(new BenchContextSources { LastSnapshot = () => null });

    public static BenchPreconditionReport Preconditions(bool onBattery = false)
    {
        var window = new BackgroundLoadWindow();
        for (int s = 0; s <= 10; s++) window.Note(T0.AddSeconds(s), 1);
        return BenchPreconditions.Evaluate(new BenchPreconditionInputs(!onBattery, onBattery ? 80 : null, window, null, null, null, null));
    }

    public static BenchThermalLimits Thermal => new(98, "test", TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
}

public class BenchSessionResultTests
{
    [Fact]
    public void Une_session_se_convertit_avec_ses_points_ses_series_et_son_contexte()
    {
        BenchSessionOutcome outcome = BenchResultsTestData.Session(BenchResultsTestData.Outcome(BenchTestKind.CpuMono));

        BenchSessionResult result = BenchSessionResult.From(outcome, BenchResultsTestData.Context(), BenchResultsTestData.Preconditions(), BenchResultsTestData.Thermal);

        Assert.Equal(BenchSessionResult.FormatVersion, result.Version);
        Assert.Equal(BenchVersion.Bench, result.BenchVersion);
        BenchTestResult test = Assert.Single(result.Tests);
        Assert.Equal("cpu-mono", test.Kind);
        Assert.Equal("Processeur, un cœur", test.Title);
        Assert.Equal(1000, test.Points); // exactement les références provisoires
        Assert.True(test.Succeeded);
        Assert.False(test.IsUnstable);
        Assert.Equal(3, test.Measurements.Count);
        Assert.Contains(test.Series, s => s.Key == "cpu-temp" && s.Values.Count == 2);
        Assert.Equal(1, test.ThrottlePowerLimit);
        Assert.Equal(60, test.MaxCpuTempC);
        Assert.True(result.IsRepresentative);
        Assert.Contains("98 °C", result.ThermalLimit);
        Assert.NotEmpty(result.Context.Cpu);
        Assert.Equal(["EcoQoS : désactivé"], result.WorkerNotes);
        Assert.Contains("1 test", result.Summary());
        Assert.Contains("pts", result.Summary());
    }

    [Fact]
    public void Sans_chemin_comparable_ou_avec_une_erreur_de_calcul_il_n_y_a_pas_de_points()
    {
        BenchTestResult fallback = BenchTestResult.From(BenchResultsTestData.Outcome(BenchTestKind.CpuMono, comparable: false));
        BenchTestResult corrupted = BenchTestResult.From(BenchResultsTestData.Outcome(BenchTestKind.CpuMono, mismatch: true));
        BenchTestResult failed = BenchTestResult.From(BenchResultsTestData.Outcome(BenchTestKind.CpuMono, succeeded: false, stop: BenchStopReason.Thermal));

        Assert.Null(fallback.Points);
        Assert.Null(corrupted.Points);
        Assert.Null(failed.Points);
        Assert.Equal("Thermal", failed.StopReason);
        Assert.Equal("détail", failed.StopDetail);
    }

    [Fact]
    public void Le_json_fait_l_aller_retour_et_garde_les_champs_inconnus()
    {
        BenchSessionResult original = BenchSessionResult.From(BenchResultsTestData.Session(BenchResultsTestData.Outcome(BenchTestKind.RamLatency)),
            BenchResultsTestData.Context(), BenchResultsTestData.Preconditions(onBattery: true), BenchResultsTestData.Thermal);
        string json = BenchResultStore.Serialize(original).Replace("\"Cancelled\":", "\"futur\":{\"x\":1},\"Cancelled\":");

        BenchSessionResult? parsed = BenchResultStore.TryParse(json, out string? problem);

        Assert.Null(problem);
        Assert.NotNull(parsed);
        Assert.Equal(original.Id, parsed.Id);
        Assert.Equal(original.StartedUtc, parsed.StartedUtc);
        Assert.False(parsed.IsRepresentative);
        Assert.True(parsed.OnBattery);
        Assert.True(parsed.Extra!.ContainsKey("futur"));
        BenchTestResult test = Assert.Single(parsed.Tests);
        Assert.Equal(original.Tests[0].Points, test.Points);
        Assert.Equal(3, test.Measurements.Count);
        Assert.Equal(original.Tests[0].Series[0].Values, test.Series[0].Values);
    }

    [Fact]
    public void Un_document_abime_ou_sans_version_est_refuse_sans_lever()
    {
        Assert.Null(BenchResultStore.TryParse("{\"v\":1,", out string? problem));
        Assert.Contains("JSON invalide", problem);
        Assert.Null(BenchResultStore.TryParse("{\"v\":0}", out problem));
        Assert.Equal("version absente", problem);
        Assert.Null(BenchResultStore.TryParse("null", out problem));

        BenchSessionResult? future = BenchResultStore.TryParse("{\"v\":2,\"Id\":\"abc\",\"Tests\":null}", out problem);
        Assert.NotNull(future);
        Assert.Equal(2, future.Version);
        Assert.Empty(future.Tests);
    }

    [Fact]
    public void Le_depot_ecrit_un_fichier_par_session_et_relit_la_plus_recente_en_premier()
    {
        using var temp = new TempDirectory();
        var store = new BenchResultStore(() => Path.Combine(temp.Root, "bench"));
        BenchSessionResult older = BenchSessionResult.From(BenchResultsTestData.Session(BenchResultsTestData.Outcome(BenchTestKind.CpuMono)),
            BenchResultsTestData.Context(), BenchResultsTestData.Preconditions(), BenchResultsTestData.Thermal);
        BenchSessionResult newer = BenchSessionResult.From(BenchResultsTestData.Session(BenchResultsTestData.Outcome(BenchTestKind.CpuMulti)),
            BenchResultsTestData.Context(), BenchResultsTestData.Preconditions(), BenchResultsTestData.Thermal);
        newer.StartedUtc = older.StartedUtc.AddHours(1);

        string? first = store.Save(older, out string? error1);
        string? second = store.Save(newer, out string? error2);
        File.WriteAllText(Path.Combine(store.Folder, BenchResultStore.FilePrefix + "zzz-corrompu.json"), "{pas du json");

        Assert.NotNull(first);
        Assert.Null(error1);
        Assert.NotNull(second);
        Assert.Null(error2);
        Assert.StartsWith(BenchResultStore.FilePrefix, Path.GetFileName(first));
        IReadOnlyList<BenchSessionResult> loaded = store.LoadAll(out int unreadable);
        Assert.Equal(1, unreadable);
        Assert.Equal([newer.Id, older.Id], loaded.Select(r => r.Id));
        Assert.Empty(new BenchResultStore(() => Path.Combine(temp.Root, "absent")).LoadAll(out int none));
        Assert.Equal(0, none);
    }
}

public class BenchRowProviderTests
{
    [Fact]
    public void Sans_ouverture_de_la_page_la_ligne_dit_pas_encore_lu()
    {
        using var temp = new TempDirectory();
        var provider = new BenchRowProvider(() => null, new BenchResultStore(() => temp.Root));

        IReadOnlyList<CompatibilityRow> rows = provider.GetRows();

        Assert.Equal("Bench", provider.Title);
        Assert.Equal("Pas encore lu", rows[0].Status);
        Assert.Equal("Pas encore lu", rows[1].Status);
    }

    [Fact]
    public async Task La_ligne_compte_les_tests_disponibles_et_relit_la_derniere_session()
    {
        using var temp = new TempDirectory();
        var store = new BenchResultStore(() => Path.Combine(temp.Root, "bench"));
        store.Save(BenchSessionResult.From(BenchResultsTestData.Session(BenchResultsTestData.Outcome(BenchTestKind.CpuMono)),
            BenchResultsTestData.Context(), BenchResultsTestData.Preconditions(), BenchResultsTestData.Thermal), out _);
        var perTest = BenchTestKinds.All.ToDictionary(k => k, k => k == BenchTestKind.Disk
            ? new Unavailable(UnavailableCause.HardwareOrDriver, "aucun volume éligible")
            : (Unavailable?)null);
        var status = new BenchDiagnosticStatus(perTest, null, "98 °C tenus 10 s (test)", 0, ["sur secteur"]);
        var provider = new BenchRowProvider(() => status, store);

        await provider.RefreshAsync(CancellationToken.None);
        IReadOnlyList<CompatibilityRow> rows = provider.GetRows();

        Assert.Contains("4 test(s) sur 5", rows[0].Status);
        Assert.Contains("Disque : N/D (aucun volume éligible)", rows[0].Detail);
        Assert.Contains("98 °C", rows[0].Detail);
        Assert.True(rows[0].IsSupported);
        Assert.Equal("Enregistrée", rows[1].Status);
        Assert.Contains("pts", rows[1].Detail);
    }
}
