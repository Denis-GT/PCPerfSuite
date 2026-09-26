using PCPerfSuite.Core.Hardware;
using PCPerfSuite.Core.Hardware.Fans;
using PCPerfSuite.Core.Hardware.LaptopFans;

namespace PCPerfSuite.Core.Tests;

public class FanIdentificationTests
{
    // Noms relevés dans LibreHardwareMonitorLib 0.9.6 (tables de cartes mères et pilotes graphiques).

    [Theory]
    [InlineData("CPU Fan")]
    [InlineData("CPU Fan #1")]
    [InlineData("CPU Optional Fan")]
    [InlineData("CPU_OPT")]
    [InlineData("Radiator Fan 1")]
    public void Un_nom_de_processeur_ou_de_radiateur_est_classe_processeur(string name)
        => Assert.Equal(FanCategory.Cpu, FanIdentification.Categorize(name, onGpu: false));

    [Theory]
    [InlineData("AIO Pump")]
    [InlineData("AIO_PUMP")]
    [InlineData("Water Pump")]
    [InlineData("Water Pump+")]
    [InlineData("W_PUMP+")]
    [InlineData("CPU Pump Fan")]
    [InlineData("Pump Fan #1")]
    public void Un_nom_de_pompe_est_classe_pompe(string name)
        => Assert.Equal(FanCategory.Pump, FanIdentification.Categorize(name, onGpu: false));

    [Theory]
    [InlineData("System Fan #5 / Pump")]
    [InlineData("CPU Fan #2 / Pump")]
    public void Un_connecteur_mixte_ventilateur_ou_pompe_est_traite_en_pompe(string name)
        => Assert.Equal(FanCategory.Pump, FanIdentification.Categorize(name, onGpu: false));

    [Theory]
    [InlineData("Chassis Fan 1")]
    [InlineData("Chassis Fan #2")]
    [InlineData("CHA_FAN1")]
    [InlineData("System Fan")]
    [InlineData("System Fan #7")]
    [InlineData("Auxiliary Fan #2")]
    [InlineData("High Amp Fan")]
    [InlineData("Rear Fan")]
    public void Un_nom_de_boitier_est_classe_boitier(string name)
        => Assert.Equal(FanCategory.Case, FanIdentification.Categorize(name, onGpu: false));

    [Theory]
    [InlineData("Fan #1")]
    [InlineData("Fan 6")]
    [InlineData("Fan Control #3")]
    [InlineData("Fan")]
    [InlineData("Ventilateur 2")]
    [InlineData("")]
    [InlineData("   ")]
    public void Un_simple_numero_de_canal_reste_non_identifie(string name)
        => Assert.Equal(FanCategory.Unidentified, FanIdentification.Categorize(name, onGpu: false));

    [Theory]
    [InlineData("Chipset Fan")]
    [InlineData("VRM Fan")]
    [InlineData("Channel 1")] // « cha » ne doit pas reconnaître n'importe quel mot qui commence par ces lettres
    public void Un_vrai_nom_sans_rapport_est_classe_autres(string name)
        => Assert.Equal(FanCategory.Other, FanIdentification.Categorize(name, onGpu: false));

    [Fact]
    public void Un_ventilateur_de_carte_graphique_est_toujours_classe_gpu()
    {
        Assert.Equal(FanCategory.Gpu, FanIdentification.Categorize("GPU Fan 1", onGpu: true));
        Assert.Equal(FanCategory.Gpu, FanIdentification.Categorize("Fan #2", onGpu: true));
    }

    [Fact]
    public void Le_role_annonce_par_un_portable_prime_sur_le_nom()
    {
        Assert.Equal(FanCategory.Cpu, FanIdentification.Categorize("Ventilateur CPU", onGpu: false, LaptopFanRole.Cpu));
        Assert.Equal(FanCategory.Gpu, FanIdentification.Categorize("Ventilateur GPU", onGpu: false, LaptopFanRole.Gpu));
        Assert.Equal(FanCategory.Other, FanIdentification.Categorize("Ventilateur central", onGpu: false, LaptopFanRole.Other));
        Assert.Equal(FanCategory.Unidentified, FanIdentification.Categorize("Ventilateur 1", onGpu: false, LaptopFanRole.Other));
    }

    private static FanLabelInput Unnamed(int channel, FanCategory category = FanCategory.Unidentified, string chip = "/lpc/nct6798d/0")
        => new(category, $"Fan #{channel + 1}", NameFromHardware: false, channel, chip);

    private static FanLabelInput Named(string name, int channel, FanCategory category, string chip = "/lpc/nct6798d/0")
        => new(category, name, NameFromHardware: true, channel, chip);

    [Fact]
    public void Sans_nom_un_canal_porte_le_numero_de_sa_puce()
    {
        // La carte du signalement : 7 canaux sans nom (ASUS TUF GAMING B760-PLUS WIFI, Nuvoton NCT6798D).
        FanLabelInput[] fans = Enumerable.Range(0, 7).Select(i => Unnamed(i)).ToArray();

        Assert.Equal(
            new[] { "Ventilateur 1", "Ventilateur 2", "Ventilateur 3", "Ventilateur 4", "Ventilateur 5", "Ventilateur 6", "Ventilateur 7" },
            FanIdentification.AssignLabels(fans));
    }

    [Fact]
    public void Un_nom_donne_par_le_materiel_est_repris_tel_quel()
    {
        var fans = new[]
        {
            Named("CPU Fan", 1, FanCategory.Cpu),
            Named("AIO Pump", 5, FanCategory.Pump),
            Named("Chassis Fan 1", 0, FanCategory.Case),
        };

        Assert.Equal(new[] { "CPU Fan", "AIO Pump", "Chassis Fan 1" }, FanIdentification.AssignLabels(fans));
    }

    [Fact]
    public void Les_ventilateurs_gpu_sont_numerotes_dans_leur_categorie()
    {
        // NVIDIA numérote ses canaux à partir de 1 (/gpu-nvidia/0/fan/1..3) : le libellé, lui, part de 1 quand même.
        var fans = new FanLabelInput[]
        {
            new(FanCategory.Gpu, "GPU Fan 1", false, 1, "/gpu-nvidia/0"),
            new(FanCategory.Gpu, "GPU Fan 2", false, 2, "/gpu-nvidia/0"),
            new(FanCategory.Gpu, "GPU Fan 3", false, 3, "/gpu-nvidia/0"),
        };

        Assert.Equal(new[] { "GPU 1", "GPU 2", "GPU 3" }, FanIdentification.AssignLabels(fans));
    }

    [Fact]
    public void Un_ventilateur_seul_dans_sa_categorie_n_a_pas_de_numero()
    {
        var fans = new[] { new FanLabelInput(FanCategory.Gpu, "GPU Fan", false, 0, "/gpu-amd/0") };

        Assert.Equal(new[] { "GPU" }, FanIdentification.AssignLabels(fans));
    }

    [Fact]
    public void Le_rang_dans_la_categorie_suit_l_ordre_des_canaux_pas_celui_de_lecture()
    {
        var fans = new[]
        {
            Unnamed(3, FanCategory.Case),
            Unnamed(1, FanCategory.Case),
            Unnamed(2, FanCategory.Case),
        };

        Assert.Equal(new[] { "Boîtier 3", "Boîtier 1", "Boîtier 2" }, FanIdentification.AssignLabels(fans));
    }

    [Fact]
    public void Un_libelle_ne_depend_pas_des_ventilateurs_des_autres_categories()
    {
        FanLabelInput[] board = Enumerable.Range(0, 3).Select(i => Unnamed(i)).ToArray();
        FanLabelInput gpu = new(FanCategory.Gpu, "GPU Fan 1", false, 1, "/gpu-nvidia/0");

        IReadOnlyList<string> alone = FanIdentification.AssignLabels(board);
        IReadOnlyList<string> withGpu = FanIdentification.AssignLabels(board.Append(gpu).ToArray());

        Assert.Equal(alone, withGpu.Take(3));
    }

    [Fact]
    public void Deux_puces_qui_numerotent_chacune_a_partir_de_un_retombent_sur_le_rang()
    {
        var fans = new[]
        {
            Unnamed(0, chip: "/lpc/nct6798d/0"),
            Unnamed(0, chip: "/lpc/it8688e/0"),
        };

        Assert.Equal(new[] { "Ventilateur 1", "Ventilateur 2" }, FanIdentification.AssignLabels(fans));
    }

    [Fact]
    public void Deux_libelles_identiques_sont_numerotes()
    {
        var fans = new[]
        {
            Named("CPU Fan", 1, FanCategory.Cpu, chip: "/lpc/nct6798d/0"),
            Named("CPU Fan", 1, FanCategory.Cpu, chip: "/lpc/it8688e/0"),
        };

        Assert.Equal(new[] { "CPU Fan (1)", "CPU Fan (2)" }, FanIdentification.AssignLabels(fans));
    }

    [Fact]
    public void Un_portable_sans_canal_prend_le_numero_du_nom()
    {
        var fans = new[]
        {
            new FanLabelInput(FanCategory.Unidentified, "Ventilateur 1", false, null, "laptop/msi"),
            new FanLabelInput(FanCategory.Unidentified, "Ventilateur 2", false, null, "laptop/msi"),
        };

        Assert.Equal(new[] { "Ventilateur 1", "Ventilateur 2" }, FanIdentification.AssignLabels(fans));
    }

    [Fact]
    public void Les_lectures_recoivent_leur_libelle_sans_perdre_le_nom_brut()
    {
        var readings = new List<FanReading>
        {
            new()
            {
                HardwareName = "Nuvoton NCT6798D", SensorName = "Fan #6", SensorId = "/lpc/nct6798d/0/fan/5",
                Category = FanCategory.Unidentified, Channel = 5, HardwareId = "/lpc/nct6798d/0",
            },
        };

        IReadOnlyList<FanReading> labeled = FanIdentification.Label(readings);

        Assert.Equal("Ventilateur 6", labeled[0].Label);
        Assert.Equal("Fan #6", labeled[0].SensorName);
        Assert.Equal("Fan #6", readings[0].Label); // l'original n'est pas modifié : sans passage, le libellé est le nom brut
    }
}
