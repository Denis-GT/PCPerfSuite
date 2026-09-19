using System.Management;

namespace PCPerfSuite.Core.Hardware.LaptopFans;

/// <summary>
/// Portables HP Omen / Victus : interface BIOS WMI de HP (classe hpqBIntM), commande 0x20008 de type 0x2D qui
/// renvoie la vitesse de chaque ventilateur en centaines de RPM (octet 0 : CPU, octet 1 : GPU). Protocole
/// repris du pilote Linux hp-wmi et d'OmenHwCtl/OmenMon. Les HP sans cette commande (EliteBook...) répondent
/// par un code d'erreur : ils sont alors signalés non pris en charge.
/// </summary>
internal sealed class HpFanProvider : ILaptopFanProvider, IDisposable
{
    private const uint GameCommand = 0x20008;
    private const uint FanLevelQuery = 0x2D;

    /// <summary>"SECU" : signature que le BIOS HP attend en tête de chaque requête.</summary>
    private static readonly byte[] Signature = { 0x53, 0x45, 0x43, 0x55 };

    private ManagementObject? _bios;

    public string Vendor => "HP";
    public bool IsVerified => false;

    public bool TryDetect()
    {
        _bios = WmiMethods.FirstInstance("hpqBIntM");
        return _bios is not null && QueryLevels() is not null;
    }

    public IReadOnlyList<LaptopFanReading> Read()
    {
        byte[]? levels = QueryLevels();
        return new[]
        {
            new LaptopFanReading { Key = "cpu", Name = "Ventilateur CPU", Role = LaptopFanRole.Cpu, Rpm = levels is null ? null : levels[0] * 100 },
            new LaptopFanReading { Key = "gpu", Name = "Ventilateur GPU", Role = LaptopFanRole.Gpu, Rpm = levels is null ? null : levels[1] * 100 },
        };
    }

    /// <summary>Octets de réponse, ou null si le BIOS refuse la commande.</summary>
    private byte[]? QueryLevels()
    {
        using ManagementObject input = WmiMethods.CreateInstance("hpqBDataIn");
        input["Sign"] = Signature;
        WmiMethods.Set(input, "Command", GameCommand);
        WmiMethods.Set(input, "CommandType", FanLevelQuery);
        WmiMethods.Set(input, "Size", 4);
        input["hpqBData"] = new byte[4];

        // hpqBIOSInt128 : la variante dont le tampon de réponse fait 128 octets.
        using ManagementBaseObject result = WmiMethods.Invoke(_bios!, "hpqBIOSInt128", ("InData", input));
        if (result["OutData"] is not ManagementBaseObject output) return null;

        using (output)
        {
            if (Convert.ToUInt32(output["rwReturnCode"]) != 0) return null;
            return output["Data"] is byte[] { Length: >= 2 } data ? data : null;
        }
    }

    public void Dispose() => _bios?.Dispose();
}
