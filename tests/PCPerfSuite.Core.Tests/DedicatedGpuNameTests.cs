using PCPerfSuite.Core.Hardware;
using PCPerfSuite.Core.Hardware.Gpu;

namespace PCPerfSuite.Core.Tests;

/// <summary>Carte dédiée ou GPU intégré, d'après le nom que Windows donne à la carte.</summary>
public class DedicatedGpuNameTests
{
    [Theory]
    [InlineData("NVIDIA GeForce RTX 4070 Laptop GPU")]
    [InlineData("NVIDIA GeForce GTX 1060 6GB")]
    [InlineData("NVIDIA RTX A2000 12GB")]
    public void Nvidia_IsDedicated(string name)
        => Assert.Equal(GpuVendor.Nvidia, DedicatedGpuName.VendorOf(name));

    [Theory]
    [InlineData("AMD Radeon RX 7900 XTX")]
    [InlineData("AMD Radeon RX 9070 XT")]
    [InlineData("AMD Radeon RX 6700M")]
    [InlineData("AMD Radeon(TM) RX 6800")]
    [InlineData("Radeon RX 580 Series")]
    [InlineData("Radeon RX Vega")]
    [InlineData("Radeon RX Vega M GH Graphics")]
    [InlineData("AMD Radeon PRO W7900")]
    [InlineData("AMD Radeon Pro 5500M")]
    public void RadeonRxAndPro_AreDedicated(string name)
        => Assert.Equal(GpuVendor.Amd, DedicatedGpuName.VendorOf(name));

    [Theory]
    [InlineData("Intel(R) Arc(TM) A770 Graphics")]
    [InlineData("Intel(R) Arc(TM) A380 Graphics")]
    [InlineData("Intel(R) Arc(TM) A370M Graphics")]
    [InlineData("Intel(R) Arc(TM) B580 Graphics")]
    [InlineData("Intel(R) Arc(TM) Pro A40 Graphics")]
    [InlineData("Intel(R) Arc(TM) Pro A60M Graphics")]
    [InlineData("Intel(R) Arc(TM) Pro B60 Graphics")]
    [InlineData("Intel® Arc™ A750 Graphics")]
    [InlineData("Intel Arc B580")]
    public void ArcWithADiscreteModel_IsDedicated(string name)
        => Assert.Equal(GpuVendor.Intel, DedicatedGpuName.VendorOf(name));

    [Theory]
    [InlineData("Intel(R) Arc(TM) Graphics")] // Meteor Lake, Arrow Lake
    [InlineData("Intel(R) Arc(TM) Pro Graphics")] // Meteor Lake des stations de travail mobiles
    [InlineData("Intel(R) Arc(TM) 140V GPU (16GB)")] // Lunar Lake
    [InlineData("Intel(R) Arc(TM) 130V GPU (8GB)")]
    [InlineData("Intel(R) Arc(TM) 140T GPU (16GB)")] // Arrow Lake-H
    [InlineData("Intel(R) Arc(TM) B390 GPU")] // Panther Lake
    [InlineData("Intel(R) Arc(TM) Pro B390 GPU")]
    [InlineData("Intel(R) Arc(TM) Graphics B390")]
    [InlineData("Intel(R) Graphics")]
    [InlineData("Intel(R) UHD Graphics 770")]
    [InlineData("Intel(R) Iris(R) Xe Graphics")]
    public void IntelIntegrated_IsNotDedicated(string name)
        => Assert.Null(DedicatedGpuName.VendorOf(name));

    [Theory]
    [InlineData("AMD Radeon(TM) Graphics")]
    [InlineData("AMD Radeon Graphics")]
    [InlineData("AMD Radeon 780M Graphics")]
    [InlineData("AMD Radeon 780M")]
    [InlineData("AMD Radeon 890M")]
    [InlineData("AMD Radeon(TM) 890M Graphics")]
    [InlineData("AMD Radeon(TM) 8060S Graphics")]
    [InlineData("AMD Radeon(TM) Vega 8 Graphics")]
    [InlineData("AMD Radeon(TM) RX Vega 11 Graphics")] // Ryzen 5 2400G
    [InlineData("AMD Radeon RX Vega 10 Graphics")]
    public void AmdIntegrated_IsNotDedicated(string name)
        => Assert.Null(DedicatedGpuName.VendorOf(name));

    [Theory]
    [InlineData("Microsoft Basic Display Adapter")]
    [InlineData("Parsec Virtual Display Adapter")]
    [InlineData("")]
    public void VirtualOrUnknown_IsNotDedicated(string name)
        => Assert.Null(DedicatedGpuName.VendorOf(name));

    [Fact]
    public void LaptopWithArcIgpuListedFirst_FindsTheNvidiaCard()
    {
        string[] names = ["Intel(R) Arc(TM) Graphics", "NVIDIA GeForce RTX 4060 Laptop GPU"];

        Assert.Equal(new[] { GpuVendor.Nvidia }, DedicatedGpuName.VendorsOf(names));
    }

    [Fact]
    public void IntegratedOnly_FindsNoDedicatedCard()
        => Assert.Empty(DedicatedGpuName.VendorsOf(["Intel(R) Arc(TM) 140V GPU (16GB)", "AMD Radeon 780M Graphics"]));

    [Fact]
    public void SeveralCards_KeepWindowsOrderWithoutDuplicates()
    {
        string[] names = ["AMD Radeon RX 7900 XTX", "AMD Radeon(TM) Graphics", "NVIDIA GeForce RTX 4090", "AMD Radeon RX 7900 XTX"];

        Assert.Equal(new[] { GpuVendor.Amd, GpuVendor.Nvidia }, DedicatedGpuName.VendorsOf(names));
    }
}
