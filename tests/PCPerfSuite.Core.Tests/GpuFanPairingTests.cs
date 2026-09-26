using PCPerfSuite.Core.Hardware;
using PCPerfSuite.Core.Hardware.Fans;
using PCPerfSuite.Core.Overlay;

namespace PCPerfSuite.Core.Tests;

public class GpuFanPairingTests
{
    private static FanReading Gpu(string hardwareId, int channel, float rpm)
        => new()
        {
            HardwareName = "GPU", SensorName = $"GPU Fan {channel}", SensorId = $"{hardwareId}/fan/{channel}",
            PercentControlSensorId = $"{hardwareId}/control/{channel}",
            Group = SensorGroup.Gpu, Category = FanCategory.Gpu, Channel = channel, HardwareId = hardwareId, Rpm = rpm,
        };

    private static FanReading Board(int channel)
        => new()
        {
            HardwareName = "Nuvoton NCT6798D", SensorName = $"Fan #{channel + 1}", SensorId = $"/lpc/nct6798d/0/fan/{channel}",
            PercentControlSensorId = $"/lpc/nct6798d/0/control/{channel}",
            Group = SensorGroup.Motherboard, Channel = channel, HardwareId = "/lpc/nct6798d/0",
        };

    [Fact]
    public void Les_coolers_sont_rapproches_par_numero_de_canal()
    {
        // Le PC du signalement : NVAPI expose les coolers 1 à 3, la bibliothèque /gpu-nvidia/0/fan/1 à 3.
        FanReading[] gpu = { Gpu("/gpu-nvidia/0", 1, 1100), Gpu("/gpu-nvidia/0", 2, 1200), Gpu("/gpu-nvidia/0", 3, 1300) };

        GpuFanPairing pairing = GpuFanPairing.Pair(new[] { 1, 2, 3 }, GpuVendor.Nvidia, gpu);

        Assert.Equal(new float?[] { 1100, 1200, 1300 }, pairing.Pairs.Select(p => p.Reading?.Rpm));
        Assert.Equal(new[] { 1, 2, 3 }, pairing.Pairs.Select(p => p.CoolerId));
    }

    [Fact]
    public void Chaque_cooler_recoit_sa_propre_lecture()
    {
        FanReading[] gpu = { Gpu("/gpu-nvidia/0", 3, 1300), Gpu("/gpu-nvidia/0", 1, 1100), Gpu("/gpu-nvidia/0", 2, 1200) };

        GpuFanPairing pairing = GpuFanPairing.Pair(new[] { 3, 1, 2 }, GpuVendor.Nvidia, gpu);

        Assert.Equal(new float?[] { 1100, 1200, 1300 }, pairing.Pairs.Select(p => p.Reading?.Rpm));
    }

    [Fact]
    public void Sans_canal_commun_le_rapprochement_se_fait_par_position()
    {
        // ADLX numérote à partir de 0, la bibliothèque à partir de 1 : même nombre de ventilateurs, donc dans l'ordre.
        FanReading[] gpu = { Gpu("/gpu-amd/0", 1, 900), Gpu("/gpu-amd/0", 2, 950) };

        GpuFanPairing pairing = GpuFanPairing.Pair(new[] { 0, 1 }, GpuVendor.Amd, gpu);

        Assert.Equal(new float?[] { 900, 950 }, pairing.Pairs.Select(p => p.Reading?.Rpm));
    }

    [Fact]
    public void Un_rapprochement_incertain_est_abandonne()
    {
        // 3 coolers pour 2 lectures et un seul canal en commun : mieux vaut « -- » que la vitesse d'un autre ventilateur.
        FanReading[] gpu = { Gpu("/gpu-nvidia/0", 1, 1100), Gpu("/gpu-nvidia/0", 2, 1200) };

        GpuFanPairing pairing = GpuFanPairing.Pair(new[] { 1, 2, 3 }, GpuVendor.Nvidia, gpu);

        Assert.Equal(new float?[] { 1100, 1200, null }, pairing.Pairs.Select(p => p.Reading?.Rpm));
    }

    [Fact]
    public void Toutes_les_lectures_de_la_carte_pilotee_sont_ecartees_de_la_liste()
    {
        FanReading[] gpu = { Gpu("/gpu-nvidia/0", 1, 1100), Gpu("/gpu-nvidia/0", 2, 1200), Gpu("/gpu-nvidia/0", 3, 1300) };
        FanReading board = Board(0);

        GpuFanPairing pairing = GpuFanPairing.Pair(new[] { 1, 2, 3 }, GpuVendor.Nvidia, gpu.Append(board).ToArray());

        Assert.Equal(3, pairing.Replaced.Count);
        Assert.DoesNotContain(board, pairing.Replaced);
    }

    [Fact]
    public void Seule_la_premiere_carte_de_la_marque_est_ecartee()
    {
        FanReading first = Gpu("/gpu-nvidia/0", 1, 1100);
        FanReading second = Gpu("/gpu-nvidia/1", 1, 800);

        GpuFanPairing pairing = GpuFanPairing.Pair(new[] { 1 }, GpuVendor.Nvidia, new[] { first, second });

        Assert.Equal(new[] { first }, pairing.Replaced);
        Assert.Same(first, pairing.Pairs.Single().Reading);
    }

    [Fact]
    public void Une_carte_d_une_autre_marque_n_est_pas_touchee()
    {
        FanReading integrated = Gpu("/gpu-intel-integrated/0", 0, 0);

        GpuFanPairing pairing = GpuFanPairing.Pair(new[] { 1 }, GpuVendor.Nvidia, new[] { integrated });

        Assert.Empty(pairing.Replaced);
        Assert.Null(pairing.Pairs.Single().Reading);
    }

    [Fact]
    public void Les_gpu_intel_integres_et_dedies_sont_reconnus()
    {
        FanReading discrete = Gpu("/gpu-intel-discrete/0", 0, 700);

        GpuFanPairing pairing = GpuFanPairing.Pair(new[] { 0 }, GpuVendor.Intel, new[] { discrete });

        Assert.Same(discrete, pairing.Pairs.Single().Reading);
    }

    [Fact]
    public void Sans_cooler_expose_par_le_constructeur_rien_n_est_ecarte()
    {
        // Ancienne Radeon : l'API n'expose aucun cooler, les commandes de la bibliothèque restent la seule voie de pilotage.
        FanReading[] gpu = { Gpu("/gpu-amd/0", 0, 900) };

        GpuFanPairing pairing = GpuFanPairing.Pair(Array.Empty<int>(), GpuVendor.Amd, gpu);

        Assert.Empty(pairing.Pairs);
        Assert.Empty(pairing.Replaced);
    }

    [Fact]
    public void Sans_marque_connue_rien_n_est_ecarte()
    {
        GpuFanPairing pairing = GpuFanPairing.Pair(new[] { 1 }, vendor: null, new[] { Gpu("/gpu-nvidia/0", 1, 1100) });

        Assert.Empty(pairing.Replaced);
    }
}
