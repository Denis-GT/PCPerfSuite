using System.Runtime.Intrinsics.X86;
using System.Threading;

namespace PCPerfSuite.Core.Hardware.Cpu;

/// <summary>Nom de code AMD, seul repère fiable : les commandes SMU changent d'une génération à l'autre.</summary>
public enum AmdCodeName
{
    Unknown,
    Matisse,        // Zen 2 bureau (Ryzen 3000)
    Vermeer,        // Zen 3 bureau (Ryzen 5000)
    Raphael,        // Zen 4 bureau (Ryzen 7000)
    GraniteRidge,   // Zen 5 bureau (Ryzen 9000)
    Renoir,         // Zen 2 portable (Ryzen 4000)
    Lucienne,       // Zen 2 portable (Ryzen 5000U)
    Cezanne,        // Zen 3 portable (Ryzen 5000H/HX)
    Rembrandt,      // Zen 3+ portable (Ryzen 6000)
    Phoenix,        // Zen 4 portable (Ryzen 7040)
    HawkPoint,      // Zen 4 portable (Ryzen 8040)
    DragonRange,    // Zen 4 portable haut de gamme (Ryzen 7045)
}

/// <summary>
/// Réglage de la limite de puissance AMD par la SMU (System Management Unit), le microcontrôleur qui
/// arbitre puissance, fréquence et tension du processeur. C'est le même chemin qu'empruntent Ryzen
/// Master et RyzenAdj.
///
/// Deux boîtes aux lettres coexistent selon la génération :
/// - sur les processeurs de bureau, la RSMU, à laquelle le module RyzenSMU de PawnIO sait parler
///   directement (ioctl_send_smu_command) ;
/// - sur les portables (APU), la MP1, que l'on pilote ici à la main — le module n'expose que la RSMU,
///   mais il autorise la lecture/écriture des registres de la plage des boîtes aux lettres, ce qui
///   suffit à dérouler le protocole.
///
/// Rien n'est envoyé "à l'aveugle" : seules les commandes connues pour chaque nom de code sont émises,
/// une famille non répertoriée est annoncée N/D plutôt que tentée au hasard, et chaque écriture est
/// vérifiée en relisant la table SMU. Une commande acceptée mais sans effet est donc signalée comme un
/// échec, pas comme un succès.
/// </summary>
public sealed class AmdSmuBackend : ICpuTuningBackend
{
    /// <summary>Mutex système partagé par tous les outils qui parlent au SMU (HWiNFO, Ryzen Master,
    /// LibreHardwareMonitor…). Sans lui, deux lecteurs s'écrasent mutuellement leurs adresses.</summary>
    private const string PciMutexName = @"Global\Access_PCI";

    private const float MinAllowedWatts = 5f;

    /// <summary>Boîte aux lettres MP1 des APU : registre de commande, de réponse et d'arguments.</summary>
    private readonly record struct Mailbox(uint Command, uint Response, uint Arguments);

    private static readonly Mailbox Mp1RenoirFamily = new(0x3B10528, 0x3B10564, 0x3B10998);
    private static readonly Mailbox Mp1RembrandtFamily = new(0x3B10528, 0x3B10578, 0x3B10998);

    private readonly PawnIoModule _smu;
    private readonly AmdCodeName _codeName;
    private readonly bool _isApu;
    private readonly Mailbox _mp1;

    private readonly float _defaultSustainedWatts;
    private readonly float _defaultBurstWatts;
    private readonly float _minWatts;
    private readonly float _maxWatts;
    private readonly CpuMaxWattsInfo _maxWattsInfo;

    public string Description { get; }

    public CpuCapability PowerLimit { get; }

    private AmdSmuBackend(
        PawnIoModule smu, AmdCodeName codeName, bool isApu, Mailbox mp1,
        float defaultSustained, float defaultBurst, float minWatts, CpuMaxWattsInfo maxWatts)
    {
        _smu = smu;
        _codeName = codeName;
        _isApu = isApu;
        _mp1 = mp1;
        _defaultSustainedWatts = defaultSustained;
        _defaultBurstWatts = defaultBurst;
        _minWatts = minWatts;
        _maxWatts = maxWatts.Watts;
        _maxWattsInfo = maxWatts;

        Description = isApu
            ? $"AMD {codeName} — limites de puissance par la boîte aux lettres MP1 du SMU."
            : $"AMD {codeName} — limite de puissance (PPT) par la RSMU.";

        // Détecté à l'exécution, jamais supposé : une famille de bureau dont la commande SMU d'écriture
        // n'est pas répertoriée (Dragon Range, par exemple) reste lisible mais pas réglable. On l'annonce
        // ici plutôt que de laisser l'utilisateur bouger un curseur actif pour buter sur un refus dont le
        // code d'erreur n'a aucun sens — la commande n'ayant jamais été envoyée.
        PowerLimit = isApu || DesktopCommandFor(codeName) != 0
            ? CpuCapability.Full
            : CpuCapability.ReadOnly(
                $"Les commandes SMU d'écriture de ce processeur (AMD {codeName}) ne sont pas répertoriées dans " +
                "cette version : ses limites de puissance sont lisibles, mais pas modifiables.");
    }

    public static ICpuTuningBackend Create()
    {
        AmdCodeName codeName = DetectCodeName();
        if (codeName == AmdCodeName.Unknown)
        {
            (int family, int model, _) = ReadCpuId();
            return new UnsupportedCpuBackend(
                $"Processeur AMD non répertorié (famille {family:X}h, modèle {model:X}h) : ses commandes SMU sont inconnues de cette version.");
        }

        PawnIoModule? smu = PawnIoDriver.TryLoadModule("RyzenSMU", out string? error);
        if (smu is null) return new UnsupportedCpuBackend(error ?? "Module RyzenSMU indisponible.");

        bool isApu = IsApu(codeName);
        Mailbox mp1 = codeName is AmdCodeName.Renoir or AmdCodeName.Lucienne or AmdCodeName.Cezanne
            ? Mp1RenoirFamily
            : Mp1RembrandtFamily;

        // Instance de sondage : seule la lecture de la table SMU lui sert, ses bornes sont remplacées juste après.
        var backend = new AmdSmuBackend(
            smu, codeName, isApu, mp1, 0, 0, MinAllowedWatts, CpuMaxWattsResolver.ForAmd(0, 0, MinAllowedWatts));

        // Les valeurs d'usine viennent de la table SMU : sans elle, on ne saurait ni quoi afficher, ni à
        // quoi revenir, et on refuse alors d'écrire quoi que ce soit.
        (float sustained, float burst)? factory = backend.ReadLimitsFromPmTable();
        if (factory is not { } values)
        {
            string reason = "La table SMU de ce processeur n'a pas pu être lue : limites non disponibles.";
            smu.Dispose();
            return new UnsupportedCpuBackend(reason);
        }

        // Aucune voie documentée ne donne la puissance maximale d'un processeur AMD : la limite lue, x 1,5, et
        // l'interface le dit (CpuMaxWattsResolver.ForAmd).
        CpuMaxWattsInfo maxWatts = CpuMaxWattsResolver.ForAmd(values.sustained, values.burst, MinAllowedWatts);

        return new AmdSmuBackend(
            smu, codeName, isApu, mp1, values.sustained, values.burst, MinAllowedWatts, maxWatts);
    }

    public CpuPowerLimitSnapshot? ReadPowerLimits()
    {
        if (ReadLimitsFromPmTable() is not { } current) return null;

        return new CpuPowerLimitSnapshot
        {
            SustainedWatts = current.Sustained,
            BurstWatts = current.Burst,
            DefaultSustainedWatts = _defaultSustainedWatts,
            DefaultBurstWatts = _defaultBurstWatts,
            MinWatts = _minWatts,
            MaxWatts = _maxWatts,
            MaxWattsInfo = _maxWattsInfo,
        };
    }

    public bool TrySetPowerLimits(float sustainedWatts, float? burstWatts, out string message)
    {
        if (!PowerLimit.CanWrite)
        {
            message = PowerLimit.Reason ?? "Les limites de puissance ne sont pas modifiables sur ce processeur.";
            return false;
        }

        float sustained = Math.Clamp(sustainedWatts, _minWatts, _maxWatts);
        float burst = Math.Clamp(burstWatts ?? sustained, sustained, _maxWatts);

        bool sent = _isApu ? SendApuLimits(sustained, burst) : SendDesktopLimit(sustained);
        if (!sent)
        {
            message = $"Le SMU a refusé la commande ({_smu.DescribeLastError()}).";
            return false;
        }

        if (ReadLimitsFromPmTable() is not { } applied)
        {
            message = "Commande envoyée, mais la table SMU n'a pas pu être relue pour vérifier.";
            return false;
        }

        // Le SMU répond "OK" même quand il ignore une consigne : seule la relecture fait foi.
        if (Math.Abs(applied.Sustained - sustained) > 2f)
        {
            message = $"Le SMU a retenu {applied.Sustained:0} W au lieu de {sustained:0} W demandés (limite imposée par le firmware du PC).";
            return false;
        }

        message = $"Limites appliquées : {applied.Sustained:0} W en soutenu, {applied.Burst:0} W en pointe.";
        return true;
    }

    public bool TryRestoreDefaults(out string message)
    {
        if (!TrySetPowerLimits(_defaultSustainedWatts, _defaultBurstWatts, out message)) return false;

        message = $"Limites d'origine rétablies ({_defaultSustainedWatts:0} W / {_defaultBurstWatts:0} W).";
        return true;
    }

    // ------------------------------------------------------------------
    // Envoi des consignes
    // ------------------------------------------------------------------

    /// <summary>Commande RSMU d'écriture de la limite PPT, ou 0 si elle n'est pas répertoriée pour cette
    /// famille. C'est cette même table qui décide, à la construction, si la limite est modifiable : sans
    /// cela, l'app annoncerait un réglage qu'elle ne peut pas appliquer.</summary>
    private static uint DesktopCommandFor(AmdCodeName codeName) => codeName switch
    {
        AmdCodeName.Matisse or AmdCodeName.Vermeer => 0x53,
        AmdCodeName.Raphael or AmdCodeName.GraniteRidge => 0x56,
        _ => 0,
    };

    /// <summary>Bureau : la RSMU prend la limite PPT, que le module sait adresser lui-même.</summary>
    private bool SendDesktopLimit(float watts)
    {
        uint command = DesktopCommandFor(_codeName);
        if (command == 0) return false;

        var input = new ulong[7];
        input[0] = command;
        input[1] = (ulong)Math.Round(watts * 1000f); // le SMU compte en milliwatts

        using var guard = new PciGuard();
        return _smu.TryExecute("ioctl_send_smu_command", input, 6, out _);
    }

    /// <summary>
    /// Portable : trois limites à poser sur la MP1 — STAPM (la moyenne longue durée, celle que l'on
    /// ressent après quelques minutes de charge), la limite lente et la limite rapide.
    /// </summary>
    private bool SendApuLimits(float sustainedWatts, float burstWatts)
    {
        uint sustained = (uint)Math.Round(sustainedWatts * 1000f);
        uint burst = (uint)Math.Round(burstWatts * 1000f);

        using var guard = new PciGuard();

        return SendMp1(0x14, sustained)   // STAPM
               && SendMp1(0x16, sustained) // limite lente (PPT slow)
               && SendMp1(0x15, burst);    // limite rapide (PPT fast)
    }

    /// <summary>Protocole de la boîte aux lettres SMU : attendre la réponse précédente, l'effacer, poser
    /// les arguments, poser la commande, attendre la réponse. 1 = OK.</summary>
    private bool SendMp1(uint command, uint argument)
    {
        if (!WaitForResponse(out _)) return false;
        if (!WriteRegister(_mp1.Response, 0)) return false;

        // Six arguments, dont seul le premier porte la valeur : le SMU lit les six quoi qu'il arrive.
        for (uint i = 0; i < 6; i++)
        {
            if (!WriteRegister(_mp1.Arguments + 4 * i, i == 0 ? argument : 0)) return false;
        }

        if (!WriteRegister(_mp1.Command, command)) return false;

        for (int attempt = 0; attempt < PollAttempts; attempt++)
        {
            if (!ReadRegister(_mp1.Response, out uint response)) return false;
            if (response != 0) return response == 1; // 1 = SMU_OK ; 0xFE = commande inconnue, 0xFF = échec
            Thread.Sleep(PollDelayMs);
        }

        return false;
    }

    /// <summary>Nombre de sondages de la boîte aux lettres, espacés de <see cref="PollDelayMs"/> :
    /// ensemble, ils font un délai d'expiration d'environ 200 ms.</summary>
    private const int PollAttempts = 200;

    /// <summary>Pause entre deux sondages. Le SMU met quelques centaines de microsecondes à répondre :
    /// enchaîner les lectures sans pause martèle le pilote et, surtout, épuise les tentatives avant qu'il
    /// n'ait fini — on conclurait alors à tort à un refus (c'est aussi ce que fait RyzenAdj).</summary>
    private const int PollDelayMs = 1;

    private bool WaitForResponse(out uint response)
    {
        for (int attempt = 0; attempt < PollAttempts; attempt++)
        {
            if (!ReadRegister(_mp1.Response, out response)) return false;
            if (response != 0) return true;
            Thread.Sleep(PollDelayMs);
        }

        response = 0;
        return false;
    }

    private bool ReadRegister(uint address, out uint value)
    {
        bool ok = _smu.TryExecute("ioctl_read_smu_register", [address], 1, out ulong[] output);
        value = ok ? (uint)output[0] : 0;
        return ok;
    }

    private bool WriteRegister(uint address, uint value)
        => _smu.TryExecute("ioctl_write_smu_register", [address, value], 0, out _);

    // ------------------------------------------------------------------
    // Lecture des limites dans la table SMU
    // ------------------------------------------------------------------

    /// <summary>
    /// La table SMU ("PM table") est un tableau de flottants que le SMU recopie en mémoire sur demande.
    /// Ses premières entrées portent les limites courantes et leur valeur instantanée — c'est ce que
    /// lisent HWiNFO ou ZenTimings. La disposition diffère entre bureau et portable.
    /// </summary>
    private (float Sustained, float Burst)? ReadLimitsFromPmTable()
    {
        using var guard = new PciGuard();

        if (!_smu.TryExecute("ioctl_resolve_pm_table", [], 2, out _)) return null;
        if (!_smu.TryExecute("ioctl_update_pm_table", [], 0, out _)) return null;

        // 16 qwords = les 32 premiers flottants : largement assez pour les limites, qui sont en tête.
        if (!_smu.TryExecute("ioctl_read_pm_table", [], 16, out ulong[] table)) return null;

        float[] values = new float[table.Length * 2];
        for (int i = 0; i < table.Length; i++)
        {
            values[i * 2] = BitConverter.UInt32BitsToSingle((uint)table[i]);
            values[i * 2 + 1] = BitConverter.UInt32BitsToSingle((uint)(table[i] >> 32));
        }

        // Portable : [0] STAPM, [2] limite rapide, [4] limite lente. Bureau : [0] PPT.
        float sustained = values[0];
        float burst = _isApu ? values[2] : values[0];

        // Garde-fou : une table mal interprétée donne des valeurs absurdes, qu'il vaut mieux refuser
        // qu'afficher — et surtout ne pas prendre comme référence pour un "Rétablir".
        if (!IsPlausibleWatts(sustained) || !IsPlausibleWatts(burst)) return null;

        return (sustained, burst);
    }

    private static bool IsPlausibleWatts(float value) => value is > 1f and < 1000f;

    // ------------------------------------------------------------------
    // Identification
    // ------------------------------------------------------------------

    /// <summary>
    /// Familles qui passent par la boîte aux lettres MP1 plutôt que par la RSMU.
    ///
    /// Dragon Range (Ryzen 7045) n'y figure volontairement pas : ce sont des puces de bureau Raphael en
    /// boîtier portable, et rien ne dit laquelle des deux boîtes aux lettres leur firmware expose. Les
    /// ajouter ici à l'aveugle reviendrait à envoyer des commandes au hasard au microcontrôleur qui
    /// arbitre la puissance du processeur. Elles sont donc annoncées en lecture seule, jusqu'à
    /// vérification sur une vraie machine.
    /// </summary>
    private static bool IsApu(AmdCodeName codeName) => codeName is
        AmdCodeName.Renoir or AmdCodeName.Lucienne or AmdCodeName.Cezanne or
        AmdCodeName.Rembrandt or AmdCodeName.Phoenix or AmdCodeName.HawkPoint;

    /// <summary>Famille, modèle et type de boîtier, lus par CPUID — le même triplet qu'utilise le module
    /// RyzenSMU pour se reconnaître, sans avoir besoin du pilote.</summary>
    private static (int Family, int Model, int PackageType) ReadCpuId()
    {
        if (!X86Base.IsSupported) return (0, 0, 0);

        (int eax, _, _, _) = X86Base.CpuId(1, 0);
        int family = ((eax >> 8) & 0xF) + ((eax >> 20) & 0xFF);
        int model = ((eax >> 4) & 0xF) | (((eax >> 16) & 0xF) << 4);

        (_, int ebx, _, _) = X86Base.CpuId(unchecked((int)0x80000001), 0);
        int packageType = (ebx >>> 28) & 0xF;

        return (family, model, packageType);
    }

    private static AmdCodeName DetectCodeName()
    {
        (int family, int model, int packageType) = ReadCpuId();

        return (family, model) switch
        {
            (0x17, 0x71) => AmdCodeName.Matisse,
            (0x17, 0x60) => AmdCodeName.Renoir,
            (0x17, 0x68) => AmdCodeName.Lucienne,
            (0x19, 0x21) => AmdCodeName.Vermeer,
            (0x19, 0x50) => AmdCodeName.Cezanne,
            (0x19, 0x44) => AmdCodeName.Rembrandt,
            // Boîtier 1 = portable (Dragon Range), sinon bureau (Raphael) : même famille/modèle.
            (0x19, 0x61) => packageType == 1 ? AmdCodeName.DragonRange : AmdCodeName.Raphael,
            (0x19, 0x74) or (0x19, 0x75) => AmdCodeName.Phoenix,
            (0x19, 0x7C) => AmdCodeName.HawkPoint,
            (0x1A, 0x44) => AmdCodeName.GraniteRidge,
            _ => AmdCodeName.Unknown,
        };
    }

    public void Dispose() => _smu.Dispose();

    /// <summary>Prend le mutex système du SMU le temps d'un échange, pour ne pas croiser le fer avec
    /// LibreHardwareMonitor ou un autre outil de monitoring qui lirait la même boîte aux lettres.</summary>
    private sealed class PciGuard : IDisposable
    {
        private readonly Mutex? _mutex;
        private readonly bool _held;

        public PciGuard()
        {
            try
            {
                _mutex = new Mutex(false, PciMutexName);
                _held = _mutex.WaitOne(TimeSpan.FromMilliseconds(500), false);
            }
            catch (AbandonedMutexException)
            {
                // Un autre outil a planté en le détenant. .NET signale l'abandon par une exception, mais
                // l'attente a bien réussi : le mutex est à nous, et c'est à nous de le relâcher. Le compter
                // comme un échec le laisserait abandonné à son tour, et l'outil suivant recevrait la même
                // exception, en chaîne.
                _held = true;
            }
            catch
            {
                // Mutex inaccessible (droits) : on continue sans, comme le fait LibreHardwareMonitor —
                // le risque est une lecture incohérente, pas une écriture ratée.
                _held = false;
            }
        }

        public void Dispose()
        {
            try
            {
                if (_held) _mutex?.ReleaseMutex();
                _mutex?.Dispose();
            }
            catch
            {
                // best-effort
            }
        }
    }
}
