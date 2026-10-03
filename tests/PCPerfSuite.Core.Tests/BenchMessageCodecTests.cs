using PCPerfSuite.Core.Benchmark;
using PCPerfSuite.Core.Benchmark.Protocol;
using Xunit;

namespace PCPerfSuite.Core.Tests;

public class BenchMessageCodecTests
{
    [Fact]
    public void Un_resultat_fait_l_aller_retour_sans_rien_perdre()
    {
        var result = new BenchJobResult { JobId = "j1", Kind = "cpu-mono", Succeeded = true, DurationSeconds = 12.5 };
        result.Measurements.Add(BenchMeasurement.From("entier.rafale", "Entier", "Mops/s", [300, 310, 305]));
        result.Notes["threads"] = "1";

        string line = BenchMessageCodec.Encode(BenchMessage.ResultOf(result));
        BenchMessage? decoded = BenchMessageCodec.TryDecode(line, out string? problem);

        Assert.Null(problem);
        Assert.NotNull(decoded);
        Assert.Equal(BenchMessageTypes.Result, decoded.Type);
        Assert.Equal("j1", decoded.JobId);
        Assert.NotNull(decoded.Result);
        Assert.True(decoded.Result.Succeeded);
        BenchMeasurement measurement = Assert.Single(decoded.Result.Measurements);
        Assert.Equal(305, measurement.Median);
        Assert.Equal([300, 310, 305], measurement.Values);
        Assert.Equal("1", decoded.Result.Notes["threads"]);
    }

    [Fact]
    public void Une_demande_fait_l_aller_retour_avec_ses_parametres()
    {
        var request = new BenchJobRequest
        {
            Id = "abc",
            Kind = BenchTestKinds.Key(BenchTestKind.CpuMulti),
            Cpu = new CpuJobParameters { ThreadCount = 20, SustainedSeconds = 0, Threads = [new LogicalProcessorTarget { Group = 0, Index = 3, CpuSetId = 259 }] },
        };

        BenchMessage? decoded = BenchMessageCodec.TryDecode(BenchMessageCodec.Encode(BenchMessage.Start(request)), out _);

        Assert.NotNull(decoded?.Job?.Cpu);
        Assert.Equal("cpu-multi", decoded.Job.Kind);
        Assert.Equal(20, decoded.Job.Cpu.ThreadCount);
        Assert.Equal(0, decoded.Job.Cpu.SustainedSeconds);
        Assert.Equal(259u, Assert.Single(decoded.Job.Cpu.Threads!).CpuSetId);
    }

    [Fact]
    public void Le_message_tient_sur_une_ligne_meme_avec_un_saut_de_ligne_dans_le_texte()
    {
        string line = BenchMessageCodec.Encode(BenchMessage.ErrorOf("j", "ligne 1\nligne 2"));

        Assert.DoesNotContain('\n', line);
        Assert.Equal("ligne 1\nligne 2", BenchMessageCodec.TryDecode(line, out _)!.Error);
    }

    [Fact]
    public void Une_autre_version_de_protocole_est_refusee()
    {
        BenchMessage? decoded = BenchMessageCodec.TryDecode("""{"v":2,"type":"battement"}""", out string? problem);

        Assert.Null(decoded);
        Assert.Contains("version de protocole 2", problem);
    }

    [Fact]
    public void Une_ligne_sans_version_est_refusee_au_lieu_de_passer_pour_le_protocole_courant()
    {
        BenchMessage? decoded = BenchMessageCodec.TryDecode("""{"type":"battement"}""", out string? problem);

        Assert.Null(decoded);
        Assert.Equal("version de protocole absente", problem);
        Assert.Equal(BenchVersion.Protocol, new BenchMessage().Version);
    }

    [Fact]
    public void Un_champ_inconnu_est_garde_et_une_ligne_abimee_refusee()
    {
        BenchMessage? decoded = BenchMessageCodec.TryDecode("""{"v":1,"type":"battement","futur":{"x":1}}""", out string? problem);
        Assert.Null(problem);
        Assert.True(decoded!.Extra!.ContainsKey("futur"));

        Assert.Null(BenchMessageCodec.TryDecode("{\"v\":1,\"type\":", out problem));
        Assert.Contains("JSON invalide", problem);

        Assert.Null(BenchMessageCodec.TryDecode("", out problem));
        Assert.Equal("ligne vide", problem);

        Assert.Null(BenchMessageCodec.TryDecode("""{"v":1}""", out problem));
        Assert.Equal("type absent", problem);
    }

    [Fact]
    public void Le_bonjour_porte_le_pid_et_la_version_du_bench()
    {
        BenchMessage? decoded = BenchMessageCodec.TryDecode(BenchMessageCodec.Encode(BenchMessage.Hello(4242)), out _);

        Assert.Equal(4242, decoded!.ProcessId);
        Assert.Equal(BenchVersion.Bench, decoded.BenchVersion);
    }
}
