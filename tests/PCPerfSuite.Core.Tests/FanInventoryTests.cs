using PCPerfSuite.Core.Hardware;
using PCPerfSuite.Core.Hardware.Fans;
using PCPerfSuite.Core.Overlay;

namespace PCPerfSuite.Core.Tests;

public class FanInventoryTests
{
    private static FanReading Board(string chip, int channel, bool named)
        => new()
        {
            HardwareName = chip, SensorName = named ? "CPU Fan" : $"Fan #{channel + 1}", SensorId = $"/lpc/x/fan/{channel}",
            Group = SensorGroup.Motherboard, Channel = channel, HardwareId = "/lpc/x", NameFromHardware = named,
            Category = named ? FanCategory.Cpu : FanCategory.Unidentified,
        };

    private static FanReading Gpu(int channel)
        => new()
        {
            HardwareName = "NVIDIA GeForce", SensorName = $"GPU Fan {channel}", SensorId = $"/gpu-nvidia/0/fan/{channel}",
            Group = SensorGroup.Gpu, Category = FanCategory.Gpu, Channel = channel, HardwareId = "/gpu-nvidia/0",
        };

    private static FanReading Laptop(string key)
        => new()
        {
            HardwareName = "Lenovo", SensorName = "Ventilateur CPU", SensorId = $"laptop/lenovo/{key}",
            Group = SensorGroup.Motherboard, HardwareId = "laptop/lenovo", NameFromHardware = true, Category = FanCategory.Cpu,
        };

    [Fact]
    public void La_carte_du_signalement_a_sept_canaux_sans_nom_et_trois_ventilateurs_gpu()
    {
        var fans = Enumerable.Range(0, 7).Select(i => Board("Nuvoton NCT6798D", i, named: false))
            .Concat(new[] { Gpu(1), Gpu(2), Gpu(3) }).ToList();

        FanInventory inventory = FanInventory.Of(fans);

        Assert.Equal(7, inventory.BoardFans);
        Assert.Equal(0, inventory.BoardFansNamed);
        Assert.Equal(new[] { "Nuvoton NCT6798D" }, inventory.ChipsWithoutNames);
        Assert.Equal(3, inventory.GpuFans);
        Assert.Equal(0, inventory.LaptopFans);
    }

    [Fact]
    public void Une_carte_dont_les_noms_sont_connus_les_compte_tous()
    {
        FanInventory inventory = FanInventory.Of(Enumerable.Range(0, 5).Select(i => Board("ITE IT8688E", i, named: true)).ToList());

        Assert.Equal(5, inventory.BoardFans);
        Assert.Equal(5, inventory.BoardFansNamed);
        Assert.Empty(inventory.ChipsWithoutNames);
    }

    [Fact]
    public void Deux_puces_sans_nom_sont_citees_une_fois_chacune()
    {
        var fans = new[] { Board("Nuvoton NCT6798D", 0, false), Board("Nuvoton NCT6798D", 1, false), Board("ITE IT8790E", 0, false) };

        Assert.Equal(new[] { "Nuvoton NCT6798D", "ITE IT8790E" }, FanInventory.Of(fans).ChipsWithoutNames);
    }

    [Fact]
    public void Les_ventilateurs_de_portable_ne_comptent_pas_comme_ceux_de_la_carte_mere()
    {
        FanInventory inventory = FanInventory.Of(new[] { Laptop("cpu"), Laptop("gpu") });

        Assert.Equal(2, inventory.LaptopFans);
        Assert.Equal(0, inventory.BoardFans);
        Assert.Equal(0, inventory.GpuFans);
    }

    [Fact]
    public void Sans_ventilateur_tout_est_a_zero()
    {
        FanInventory inventory = FanInventory.Of(Array.Empty<FanReading>());

        Assert.Equal(0, inventory.BoardFans + inventory.GpuFans + inventory.LaptopFans);
        Assert.Empty(inventory.ChipsWithoutNames);
    }
}
