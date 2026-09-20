using System.Globalization;
using PCPerfSuite.Core.Hardware.Gpu;

namespace PCPerfSuite.Core.Hardware;

/// <summary>
/// Contrôle GPU toutes marques : limite de puissance, overclocking (décalages d'horloge cœur/mémoire,
/// limite de température, tension) et ventilateurs, via l'API officielle du constructeur — NVAPI pour
/// NVIDIA, ADLX pour AMD Radeon, IGCL pour Intel Arc (voir les backends dans Hardware/Gpu).
///
/// Au démarrage, on essaie les backends dans cet ordre et on garde le premier qui trouve une carte
/// pilotable : dans un PC hybride (iGPU + carte dédiée), c'est la carte dédiée qui est retenue, les
/// iGPU n'exposant pas ces réglages. Les autres GPU du marché (Moore Threads, Qualcomm Adreno…)
/// n'ont pas d'API publique d'overclocking et restent donc non disponibles.
///
/// Toutes les méthodes sont "best-effort" : elles retournent false/null plutôt que de lever si le
/// pilote refuse ou si la fonction n'existe pas sur cette carte.
/// </summary>
public sealed class GpuControlService : IFanController, IDisposable
{
    private IGpuTuningBackend? _backend;
    private bool _initialized;

    /// <summary>Vrai dès qu'un réglage d'overclock a été appliqué : sert à ne rendre la carte à ses
    /// réglages d'origine à la fermeture que si on y a effectivement touché.</summary>
    private bool _overclockTouched;

    /// <summary>Vrai dès qu'une limite de puissance a été posée par l'app. Suivie à part de l'overclock :
    /// la restaurer sans y avoir touché effacerait celle qu'un autre outil (Afterburner, utilitaire du
    /// constructeur) avait posée avant nous.</summary>
    private bool _powerLimitTouched;

    /// <summary>Vrai dès qu'une consigne de ventilateur a été posée par l'app, pour la même raison :
    /// fermer PCPerfSuite ne doit pas effacer la courbe de ventilation d'un autre outil.</summary>
    private bool _fanTouched;

    /// <summary>À la fermeture, la carte est rendue au pilote : limite de puissance, ventilateurs et
    /// overclock repartent de leurs valeurs d'origine — mais uniquement ceux auxquels l'app a touché,
    /// et sauf si l'appelant demande de conserver l'overclock (case "appliquer au démarrage"), auquel
    /// cas seuls les ventilateurs repassent en automatique pour ne pas laisser une consigne figée sur
    /// une app fermée.</summary>
    public bool KeepOverclockOnExit { get; set; }

    /// <summary>Marque de la carte pilotée, null tant qu'aucune n'a été trouvée.</summary>
    public GpuVendor? Vendor => _backend?.Vendor;

    /// <summary>Vrai quand le constructeur exige que l'utilisateur accepte une renonciation de garantie
    /// avant tout overclock (Intel).</summary>
    public bool RequiresOverclockWaiver => _backend?.RequiresOverclockWaiver ?? false;

    /// <summary>Initialise l'API du constructeur et repère la carte à piloter. Ne lève jamais —
    /// retourne false si aucune carte NVIDIA, AMD ou Intel pilotable n'est trouvée.</summary>
    public bool TryInitialize()
    {
        if (_initialized) return _backend is not null;
        _initialized = true;

        foreach (Func<IGpuTuningBackend> create in new Func<IGpuTuningBackend>[]
                 {
                     () => new NvApiGpuBackend(),
                     () => new AdlxGpuBackend(),
                     () => new IgclGpuBackend(),
                 })
        {
            IGpuTuningBackend backend = create();
            bool ok;
            try
            {
                ok = backend.TryInitialize();
            }
            catch
            {
                // DllNotFoundException & co : pilote de cette marque absent.
                ok = false;
            }

            if (ok)
            {
                _backend = backend;
                return true;
            }

            try { backend.Dispose(); } catch { /* best-effort */ }
        }

        return false;
    }

    /// <summary>Transmet l'accord de l'utilisateur au pilote (Intel) ; sans effet ailleurs.</summary>
    public bool TryAcceptOverclockWaiver() => _backend?.TryAcceptOverclockWaiver() ?? false;

    public GpuControlSnapshot? GetSnapshot() => _backend?.GetSnapshot();

    public bool TrySetPowerLimitPercent(float percent)
    {
        bool applied = _backend?.TrySetPowerLimitPercent(percent) ?? false;
        if (applied) _powerLimitTouched = true;
        return applied;
    }

    public bool TryRestorePowerLimitDefault()
    {
        bool restored = _backend?.TryRestorePowerLimitDefault() ?? false;
        if (restored) _powerLimitTouched = false;
        return restored;
    }

    // ------------------------------------------------------------------
    // Overclocking
    // ------------------------------------------------------------------

    /// <summary>Relit l'état d'overclocking courant (décalages appliqués + plages autorisées par le
    /// pilote). Retourne null si aucune carte n'est pilotable.</summary>
    public GpuOverclockSnapshot? GetOverclock() => _backend?.GetOverclock();

    /// <summary>Ce qui bride la carte à l'instant T (puissance, température, tension...). Null si
    /// l'information n'est pas exposée par le pilote (c'est le cas chez AMD et Intel).</summary>
    public GpuPerformanceLimit? GetActiveLimit() => _backend?.GetActiveLimit();

    /// <summary>Applique les décalages d'horloge cœur et mémoire (en MHz, ou dans l'unité mémoire
    /// annoncée par <see cref="GpuOverclockSnapshot.MemoryOffsetUnit"/>).</summary>
    public bool TrySetClockOffsets(int coreMhz, int memoryMhz) => Touch(_backend?.TrySetClockOffsets(coreMhz, memoryMhz));

    /// <summary>Applique la limite de température (°C).</summary>
    public bool TrySetTemperatureLimit(int celsius) => Touch(_backend?.TrySetTemperatureLimit(celsius));

    /// <summary>Applique la tension, dans l'unité annoncée par <see cref="GpuOverclockSnapshot.VoltageUnit"/>.</summary>
    public bool TrySetVoltage(int value) => Touch(_backend?.TrySetVoltage(value));

    private bool Touch(bool? applied)
    {
        if (applied != true) return false;
        _overclockTouched = true;
        return true;
    }

    /// <summary>Rend la carte à ses réglages d'origine : horloges, tension, limites de température et
    /// de puissance.</summary>
    public void RestoreOverclockDefaults() => _backend?.RestoreOverclockDefaults();

    /// <summary>Préfixe des identifiants de ventilateur GPU côté onglet Ventilateurs : distingue un
    /// ventilateur de carte graphique d'un capteur de contrôle de carte mère dans le même fichier de
    /// réglages.</summary>
    public const string FanIdPrefix = "gpu:";

    public static string FanId(int coolerId) => FanIdPrefix + coolerId.ToString(CultureInfo.InvariantCulture);

    bool IFanController.TrySetPercent(string fanId, float percent)
        => TryParseCoolerId(fanId, out int coolerId) && TrySetFanPercent(coolerId, (int)Math.Round(percent));

    bool IFanController.TrySetAuto(string fanId) => TryParseCoolerId(fanId, out _) && TryRestoreFanAuto();

    private static bool TryParseCoolerId(string fanId, out int coolerId)
    {
        coolerId = 0;
        return fanId.StartsWith(FanIdPrefix, StringComparison.Ordinal)
               && int.TryParse(fanId.AsSpan(FanIdPrefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out coolerId);
    }

    public bool TrySetFanPercent(int coolerId, int percent)
    {
        bool applied = _backend?.TrySetFanPercent(coolerId, percent) ?? false;
        if (applied) _fanTouched = true;
        return applied;
    }

    public bool TryRestoreFanAuto()
    {
        bool restored = _backend?.TryRestoreFanAuto() ?? false;
        if (restored) _fanTouched = false;
        return restored;
    }

    /// <summary>
    /// On ne rend au pilote que ce que l'app lui a pris. PCPerfSuite n'est pas seul à écrire dans une
    /// carte graphique : MSI Afterburner ou l'utilitaire du constructeur ont pu poser leur propre
    /// limite de puissance et leur propre courbe de ventilation avant qu'on démarre, et les remettre
    /// par défaut à la fermeture les effacerait sans que l'utilisateur ait jamais ouvert l'onglet GPU.
    /// </summary>
    public void Dispose()
    {
        if (_backend is not { } backend) return;

        if (!KeepOverclockOnExit)
        {
            if (_overclockTouched) RestoreOverclockDefaults();
            else if (_powerLimitTouched) TryRestorePowerLimitDefault();
        }

        if (_fanTouched) TryRestoreFanAuto();

        try { backend.Dispose(); } catch { /* best-effort */ }
        _backend = null;
    }
}
