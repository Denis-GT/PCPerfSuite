using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PCPerfSuite.Core.Hardware.Cpu;

public enum CpuVendor
{
    Other,
    Intel,
    Amd,
    Qualcomm,
}

/// <summary>Ce que l'on sait du processeur, lu une fois au démarrage.</summary>
public sealed class CpuPlatform
{
    public required CpuVendor Vendor { get; init; }
    public required string Name { get; init; }

    /// <summary>Famille et modèle CPUID "affichés" (famille étendue et modèle étendu déjà ajoutés), comme
    /// Windows les donne dans son identifiant : 0x19 / 0x50 pour un Ryzen 5000 mobile. 0 sur ARM.</summary>
    public int Family { get; init; }
    public int Model { get; init; }

    /// <summary>Processus x64 : seule architecture sur laquelle le pilote PawnIO et ses modules existent.</summary>
    public bool IsX64 { get; init; }

    /// <summary>Machine sur batterie possible (portable, tablette) : les réglages Windows y ont une valeur
    /// "sur secteur" et une valeur "sur batterie".</summary>
    public bool HasBattery { get; init; }

    /// <summary>Cœurs de plusieurs classes d'efficacité (P-cores/E-cores Intel 12e gén.+, cœurs "prime" des
    /// Snapdragon X…) : Windows expose alors des réglages propres aux cœurs les plus performants.</summary>
    public bool IsHybrid { get; init; }

    public string VendorLabel => Vendor switch
    {
        CpuVendor.Intel => "Intel",
        CpuVendor.Amd => "AMD",
        CpuVendor.Qualcomm => "Qualcomm Snapdragon",
        _ => "Autre fabricant",
    };
}

/// <summary>
/// Identifie le processeur sans pilote : tout vient du registre (que Windows remplit depuis CPUID, ou depuis
/// les tables firmware sur ARM), de l'état batterie et de la topologie des cœurs.
/// </summary>
public static partial class CpuPlatformDetector
{
    private const string CentralProcessorKey = @"HARDWARE\DESCRIPTION\System\CentralProcessor\0";

    public static CpuPlatform Detect()
    {
        string vendorId = "";
        string name = "";
        string identifier = "";

        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(CentralProcessorKey);
            vendorId = (key?.GetValue("VendorIdentifier") as string)?.Trim() ?? "";
            name = (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? "";
            identifier = (key?.GetValue("Identifier") as string)?.Trim() ?? "";
        }
        catch
        {
            // Registre illisible : on retombe sur "autre fabricant", toutes les fonctions avancées seront N/D.
        }

        CpuVendor vendor = vendorId switch
        {
            "GenuineIntel" => CpuVendor.Intel,
            "AuthenticAMD" => CpuVendor.Amd,
            _ when vendorId.Contains("Qualcomm", StringComparison.OrdinalIgnoreCase)
                   || name.Contains("Snapdragon", StringComparison.OrdinalIgnoreCase) => CpuVendor.Qualcomm,
            _ => CpuVendor.Other,
        };

        // "AMD64 Family 25 Model 80 Stepping 0" / "Intel64 Family 6 Model 183 Stepping 1" (valeurs décimales).
        int family = 0, model = 0;
        Match m = IdentifierRegex().Match(identifier);
        if (m.Success && vendor is CpuVendor.Intel or CpuVendor.Amd)
        {
            family = int.Parse(m.Groups["family"].Value, CultureInfo.InvariantCulture);
            model = int.Parse(m.Groups["model"].Value, CultureInfo.InvariantCulture);
        }

        return new CpuPlatform
        {
            Vendor = vendor,
            Name = name.Length > 0 ? name : "Processeur inconnu",
            Family = family,
            Model = model,
            IsX64 = RuntimeInformation.ProcessArchitecture == Architecture.X64,
            HasBattery = DetectBattery(),
            IsHybrid = DetectHybrid(),
        };
    }

    [GeneratedRegex(@"Family\s+(?<family>\d+)\s+Model\s+(?<model>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex IdentifierRegex();

    private static bool DetectBattery()
    {
        // BatteryFlag 128 = "pas de batterie système", 255 = état inconnu (traité comme "pas de batterie").
        return GetSystemPowerStatus(out SystemPowerStatus status) && status.BatteryFlag is not (128 or 255);
    }

    /// <summary>Vrai si les cœurs logiques n'ont pas tous la même classe d'efficacité. On lit le tampon de
    /// GetSystemCpuSetInformation à la main (entrées de taille variable, champ EfficiencyClass à l'octet 18)
    /// plutôt que via une structure marshalée, dont la taille change selon la version de Windows.</summary>
    private static bool DetectHybrid()
    {
        try
        {
            GetSystemCpuSetInformation(IntPtr.Zero, 0, out uint needed, IntPtr.Zero, 0);
            if (needed == 0) return false;

            IntPtr buffer = Marshal.AllocHGlobal((int)needed);
            try
            {
                if (!GetSystemCpuSetInformation(buffer, needed, out needed, IntPtr.Zero, 0)) return false;

                var classes = new HashSet<byte>();
                int offset = 0;
                while (offset + 19 <= needed)
                {
                    int size = Marshal.ReadInt32(buffer, offset);
                    if (size <= 0) break;
                    classes.Add(Marshal.ReadByte(buffer, offset + 18));
                    offset += size;
                }

                return classes.Count > 1;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch
        {
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemCpuSetInformation(
        IntPtr information, uint bufferLength, out uint returnedLength, IntPtr process, uint flags);
}
