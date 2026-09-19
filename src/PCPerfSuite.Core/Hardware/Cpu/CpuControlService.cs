namespace PCPerfSuite.Core.Hardware.Cpu;

/// <summary>
/// Point d'entrée unique du réglage processeur : choisit le backend qui va bien (Intel, AMD, ou
/// "indisponible" avec sa raison), applique les limites de puissance et surveille la température.
///
/// Politique de sécurité, la même que pour le GPU : rien n'est appliqué au lancement, et tout est rendu
/// au firmware en quittant — sauf si l'utilisateur demande explicitement de conserver ses réglages.
/// Les limites de puissance ne survivent de toute façon pas à un redémarrage : le firmware les repose
/// à chaque démarrage, ce qui fait du bouton d'arrêt le filet de sécurité ultime.
/// </summary>
public sealed class CpuControlService : IDisposable
{
    /// <summary>Température à partir de laquelle une limite relevée est annulée d'office. Un processeur
    /// moderne se protège seul en réduisant sa fréquence, mais rester collé à cette valeur signifie que la
    /// limite demandée dépasse ce que le refroidissement encaisse : autant revenir d'origine.</summary>
    private const float EmergencyTempC = 98f;

    /// <summary>Durée pendant laquelle la température doit rester au plafond avant de déclencher le retour
    /// aux valeurs d'origine : une pointe d'une seconde au lancement d'un jeu n'est pas un problème.</summary>
    private static readonly TimeSpan EmergencyDelay = TimeSpan.FromSeconds(15);

    private DateTime? _hotSince;
    private bool _limitsRaised;
    private bool _touched;

    public CpuPlatform Platform { get; }

    public ICpuTuningBackend Backend { get; }

    /// <summary>Laisse les limites en place à la fermeture de l'app (case "Appliquer au démarrage").</summary>
    public bool KeepLimitsOnExit { get; set; }

    /// <summary>Levé quand la sécurité thermique a rendu le processeur à ses limites d'origine.</summary>
    public event Action<string>? EmergencyRestored;

    public CpuControlService()
    {
        Platform = CpuPlatformDetector.Detect();
        Backend = CreateBackend(Platform);
    }

    private static ICpuTuningBackend CreateBackend(CpuPlatform platform)
    {
        if (platform.Vendor == CpuVendor.Qualcomm)
        {
            return new UnsupportedCpuBackend(
                "Sur Snapdragon, la fréquence, la tension et les limites de puissance sont verrouillées par le firmware Qualcomm : aucune interface publique ne permet de les modifier.");
        }

        if (!platform.IsX64)
        {
            return new UnsupportedCpuBackend(
                "Le réglage bas niveau n'est disponible que sur les processeurs x64.");
        }

        return platform.Vendor switch
        {
            CpuVendor.Intel => IntelPowerLimitBackend.Create(),
            CpuVendor.Amd => AmdSmuBackend.Create(),
            _ => new UnsupportedCpuBackend(
                "Processeur d'un fabricant non pris en charge pour le réglage des limites de puissance."),
        };
    }

    public CpuPowerLimitSnapshot? ReadPowerLimits() => Backend.ReadPowerLimits();

    public bool TrySetPowerLimits(float sustainedWatts, float? burstWatts, out string message)
    {
        bool ok = Backend.TrySetPowerLimits(sustainedWatts, burstWatts, out message);
        if (!ok) return false;

        _touched = true;
        _hotSince = null;

        // La surveillance thermique ne sert que si l'utilisateur a *relevé* une limite : l'abaisser ne peut
        // pas faire chauffer davantage, et déclencher un retour d'office dans ce cas serait absurde.
        CpuPowerLimitSnapshot? snapshot = Backend.ReadPowerLimits();
        _limitsRaised = snapshot is not null && snapshot.SustainedWatts > snapshot.DefaultSustainedWatts + 1f;

        return true;
    }

    public bool TryRestoreDefaults(out string message)
    {
        bool ok = Backend.TryRestoreDefaults(out message);
        if (ok)
        {
            _limitsRaised = false;
            _hotSince = null;
        }

        return ok;
    }

    /// <summary>
    /// À appeler à chaque relevé du monitoring, avec la température du package. Rend le processeur à ses
    /// limites d'origine s'il reste au plafond thermique assez longtemps.
    /// </summary>
    public void NoteTemperature(float? packageTempC)
    {
        if (!_limitsRaised || packageTempC is not { } temp)
        {
            _hotSince = null;
            return;
        }

        if (temp < EmergencyTempC)
        {
            _hotSince = null;
            return;
        }

        _hotSince ??= DateTime.UtcNow;
        if (DateTime.UtcNow - _hotSince < EmergencyDelay) return;

        _hotSince = null;
        _limitsRaised = false;

        bool restored = Backend.TryRestoreDefaults(out string message);
        EmergencyRestored?.Invoke(restored
            ? $"Sécurité : le processeur est resté à {temp:0} °C, les limites d'origine ont été rétablies."
            : $"Sécurité : le processeur est resté à {temp:0} °C, mais le retour aux limites d'origine a échoué ({message}).");
    }

    public void Dispose()
    {
        if (_touched && !KeepLimitsOnExit) Backend.TryRestoreDefaults(out _);
        Backend.Dispose();
    }
}
