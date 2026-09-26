namespace PCPerfSuite.Core.Hardware.Cpu;

/// <summary>
/// Réglage des limites de puissance Intel (PL1 soutenue, PL2 courte durée) par le registre MSR
/// MSR_PKG_POWER_LIMIT, celui-là même que règle le BIOS et que lisent HWiNFO ou XTU.
///
/// Trois registres suffisent : 0x606 donne l'unité de puissance (les registres parlent en "unités",
/// pas en watts), 0x614 donne le TDP et les bornes du processeur, et 0x610 porte les deux limites.
/// Le bit 63 de 0x610 est un verrou : une fois posé par le BIOS, plus rien n'est modifiable jusqu'au
/// prochain démarrage — c'est courant sur les portables, et ça se dit à l'utilisateur.
///
/// Un quatrième, 0x601, n'est lu que pour connaître PL4 (la limite de crête), sur les générations qui
/// l'y mettent : elle sert de maximum quand 0x614 n'en donne pas. Ce registre n'est jamais écrit.
/// Le choix du maximum est fait par <see cref="CpuMaxWattsResolver"/>.
///
/// Les ratios turbo (MSR 0x1AD) ne sont volontairement pas touchés : le module IntelMSR de PawnIO
/// n'en autorise que la lecture.
/// </summary>
public sealed class IntelPowerLimitBackend : ICpuTuningBackend
{
    private const uint MsrRaplPowerUnit = 0x606;
    private const uint MsrPkgPowerLimit = 0x610;
    private const uint MsrPkgPowerInfo = 0x614;

    /// <summary>MSR_VR_CURRENT_CONFIG : sur les générations qui l'ont (voir <see cref="CpuMaxWattsResolver.HasPl4Register"/>),
    /// les bits 12:0 portent PL4, en unités de puissance. Lecture seule : le module PawnIO en autorise l'écriture,
    /// mais elle règle le courant du régulateur de tension et n'a rien à faire ici.</summary>
    private const uint MsrVrCurrentConfig = 0x601;

    /// <summary>Plancher absolu : en dessous, un PC peut devenir inutilisable jusqu'au redémarrage.</summary>
    private const float MinAllowedWatts = 5f;

    private readonly PawnIoModule _msr;
    private readonly float _powerUnitWatts;
    private readonly bool _locked;

    private readonly float _defaultSustainedWatts;
    private readonly float _defaultBurstWatts;
    private readonly float _minWatts;
    private readonly float _maxWatts;
    private readonly CpuMaxWattsInfo _maxWattsInfo;

    public string Description { get; }

    public CpuCapability PowerLimit { get; }

    private IntelPowerLimitBackend(
        PawnIoModule msr, float powerUnitWatts, bool locked,
        float defaultSustained, float defaultBurst, float minWatts, CpuMaxWattsInfo maxWatts)
    {
        _msr = msr;
        _powerUnitWatts = powerUnitWatts;
        _locked = locked;
        _defaultSustainedWatts = defaultSustained;
        _defaultBurstWatts = defaultBurst;
        _minWatts = minWatts;
        _maxWatts = maxWatts.Watts;
        _maxWattsInfo = maxWatts;

        Description = "Intel — limites de puissance par registre MSR (module IntelMSR de PawnIO).";
        PowerLimit = locked
            ? CpuCapability.ReadOnly("Les limites de puissance sont verrouillées par le BIOS/UEFI jusqu'au prochain démarrage.")
            : CpuCapability.Full;
    }

    /// <summary>Charge le module IntelMSR et relève les valeurs d'usine. Retourne un backend "indisponible"
    /// porteur de la raison quand le pilote, le module ou le processeur ne suivent pas. La plateforme donne
    /// la famille et le modèle, qui décident si PL4 est lisible sur ce processeur.</summary>
    public static ICpuTuningBackend Create(CpuPlatform platform)
    {
        PawnIoModule? msr = PawnIoDriver.TryLoadModule("IntelMSR", out string? error);
        if (msr is null) return new UnsupportedCpuBackend(error ?? "Module IntelMSR indisponible.");

        if (!TryReadMsr(msr, MsrRaplPowerUnit, out ulong units))
        {
            string reason = $"Lecture des unités de puissance refusée ({msr.DescribeLastError()}).";
            msr.Dispose();
            return new UnsupportedCpuBackend(reason);
        }

        // Unité de puissance = 1 / 2^PU watts (PU = bits 3:0). 1/8 W sur la quasi-totalité des Intel.
        float powerUnitWatts = 1f / (1 << (int)(units & 0xF));

        if (!TryReadMsr(msr, MsrPkgPowerLimit, out ulong limit))
        {
            string reason = $"Lecture des limites de puissance refusée ({msr.DescribeLastError()}).";
            msr.Dispose();
            return new UnsupportedCpuBackend(reason);
        }

        float sustained = (limit & 0x7FFF) * powerUnitWatts;
        float burst = ((limit >> 32) & 0x7FFF) * powerUnitWatts;
        bool locked = (limit & (1UL << 63)) != 0;

        // Des limites nulles veulent dire qu'on n'a rien lu de exploitable : le MSR est peut-être présent
        // mais vide (machine virtuelle, firmware qui masque le RAPL). Continuer produirait un curseur de
        // 5 à 6 W, absurde et sans rapport avec ce processeur — mieux vaut annoncer l'indisponibilité.
        if (Math.Max(sustained, burst) <= 0)
        {
            msr.Dispose();
            return new UnsupportedCpuBackend(
                "Ce processeur ne publie pas ses limites de puissance (registre RAPL vide) : elles ne sont " +
                "ni lisibles ni modifiables. C'est le cas sous certaines machines virtuelles et sur les " +
                "firmwares qui masquent ce registre.");
        }

        // 0x614 donne la puissance de base (bits 14:0), le minimum (30:16) et le maximum (46:32) du processeur ;
        // vide ou illisible sur certains modèles, d'où le repli sur PL4 puis sur la limite du BIOS.
        float hardwareMin = MinAllowedWatts;
        float? tdp = null;
        float? infoMax = null;
        string? infoError = null;
        if (TryReadMsr(msr, MsrPkgPowerInfo, out ulong info))
        {
            float infoMin = ((info >> 16) & 0x7FFF) * powerUnitWatts;
            tdp = (info & 0x7FFF) * powerUnitWatts;
            infoMax = ((info >> 32) & 0x7FFF) * powerUnitWatts;
            if (infoMin > 0) hardwareMin = Math.Max(MinAllowedWatts, infoMin);
        }
        else
        {
            infoError = msr.DescribeLastError();
        }

        // PL4 : seulement sur les générations où 0x601 la porte en watts (ailleurs, ce sont des ampères).
        bool pl4Exposed = CpuMaxWattsResolver.HasPl4Register(platform.Family, platform.Model);
        float? pl4 = null;
        string? pl4Error = null;
        if (pl4Exposed)
        {
            if (TryReadMsr(msr, MsrVrCurrentConfig, out ulong vrConfig)) pl4 = (vrConfig & 0x1FFF) * powerUnitWatts;
            else pl4Error = msr.DescribeLastError();
        }

        CpuMaxWattsInfo maxWatts = CpuMaxWattsResolver.ForIntel(new IntelPowerReadings(
            sustained, burst, hardwareMin, tdp, infoMax, infoError, pl4Exposed, pl4, pl4Error));

        return new IntelPowerLimitBackend(
            msr, powerUnitWatts, locked, sustained, burst, hardwareMin, maxWatts);
    }

    public CpuPowerLimitSnapshot? ReadPowerLimits()
    {
        if (!TryReadMsr(_msr, MsrPkgPowerLimit, out ulong limit)) return null;

        return new CpuPowerLimitSnapshot
        {
            SustainedWatts = (limit & 0x7FFF) * _powerUnitWatts,
            BurstWatts = ((limit >> 32) & 0x7FFF) * _powerUnitWatts,
            DefaultSustainedWatts = _defaultSustainedWatts,
            DefaultBurstWatts = _defaultBurstWatts,
            MinWatts = _minWatts,
            MaxWatts = _maxWatts,
            MaxWattsInfo = _maxWattsInfo,
        };
    }

    public bool TrySetPowerLimits(float sustainedWatts, float? burstWatts, out string message)
    {
        if (_locked)
        {
            message = "Limites verrouillées par le BIOS/UEFI : elles ne sont modifiables qu'après un redémarrage, et seulement si le BIOS ne les reverrouille pas.";
            return false;
        }

        float sustained = Math.Clamp(sustainedWatts, _minWatts, _maxWatts);
        // La limite courte durée n'a de sens qu'au-dessus de la soutenue.
        float burst = Math.Clamp(burstWatts ?? sustained, sustained, _maxWatts);

        if (!TryReadMsr(_msr, MsrPkgPowerLimit, out ulong current))
        {
            message = $"Lecture du registre impossible ({_msr.DescribeLastError()}).";
            return false;
        }

        // On ne réécrit que les deux champs de puissance : les fenêtres de temps, les bits "clamp" et tout
        // ce que le BIOS a posé dans ce registre restent tels quels.
        ulong value = current;
        value &= ~0x7FFFUL;
        value |= (ulong)Math.Round(sustained / _powerUnitWatts) & 0x7FFF;
        value |= 1UL << 15; // PL1 active

        value &= ~(0x7FFFUL << 32);
        value |= ((ulong)Math.Round(burst / _powerUnitWatts) & 0x7FFF) << 32;
        value |= 1UL << 47; // PL2 active

        if (!TryWriteMsr(_msr, MsrPkgPowerLimit, value))
        {
            message = $"Le pilote a refusé l'écriture ({_msr.DescribeLastError()}).";
            return false;
        }

        // Un MSR peut accepter une écriture sans rien changer : on relit pour ne pas annoncer un succès en l'air.
        CpuPowerLimitSnapshot? applied = ReadPowerLimits();
        if (applied is null)
        {
            message = "Limites écrites, mais impossible de les relire pour vérifier.";
            return false;
        }

        if (Math.Abs(applied.SustainedWatts - sustained) > 1f)
        {
            message = $"Le processeur a retenu {applied.SustainedWatts:0} W au lieu de {sustained:0} W demandés (limite imposée par le BIOS ou le firmware).";
            return false;
        }

        message = applied.BurstWatts is { } appliedBurst
            ? $"Limites appliquées : {applied.SustainedWatts:0} W en soutenu, {appliedBurst:0} W en pointe."
            : $"Limite appliquée : {applied.SustainedWatts:0} W.";
        return true;
    }

    public bool TryRestoreDefaults(out string message)
    {
        if (!TrySetPowerLimits(_defaultSustainedWatts, _defaultBurstWatts, out message)) return false;

        message = $"Limites d'origine rétablies ({_defaultSustainedWatts:0} W / {_defaultBurstWatts:0} W).";
        return true;
    }

    private static bool TryReadMsr(PawnIoModule module, uint msr, out ulong value)
    {
        bool ok = module.TryExecute("ioctl_read_msr", [msr], 1, out ulong[] output);
        value = ok ? output[0] : 0;
        return ok;
    }

    private static bool TryWriteMsr(PawnIoModule module, uint msr, ulong value)
        => module.TryExecute("ioctl_write_msr", [msr, value], 0, out _);

    public void Dispose() => _msr.Dispose();
}
