namespace PCPerfSuite.Core.Hardware.Gpu;

/// <summary>
/// Pilotage d'un GPU par l'API de son constructeur (NVAPI, ADLX, IGCL). GpuControlService choisit le
/// premier backend qui trouve une carte pilotable et lui délègue tout ; le reste de l'app ne voit pas
/// la différence.
///
/// Contrat commun : aucune méthode ne lève. Un réglage absent ou refusé par le pilote se traduit par
/// false/null, et par l'indicateur "…Supported" correspondant dans les instantanés.
/// </summary>
internal interface IGpuTuningBackend : IDisposable
{
    GpuVendor Vendor { get; }

    /// <summary>Charge l'API constructeur et repère la carte à piloter. False si la DLL du pilote est
    /// absente ou si aucune carte de cette marque n'accepte de réglage.</summary>
    bool TryInitialize();

    GpuControlSnapshot? GetSnapshot();

    GpuOverclockSnapshot? GetOverclock();

    GpuPerformanceLimit? GetActiveLimit();

    bool TrySetPowerLimitPercent(float percent);

    bool TryRestorePowerLimitDefault();

    /// <summary>Applique les décalages cœur/mémoire ; un décalage que la carte ne gère pas est ignoré.</summary>
    bool TrySetClockOffsets(int coreMhz, int memoryMhz);

    bool TrySetTemperatureLimit(int celsius);

    /// <summary>Tension dans l'unité annoncée par <see cref="GpuOverclockSnapshot.VoltageUnit"/>.</summary>
    bool TrySetVoltage(int value);

    /// <summary>Rend à la carte ses réglages d'usine (horloges, tension, limites de puissance et de
    /// température).</summary>
    void RestoreOverclockDefaults();

    bool TrySetFanPercent(int coolerId, int percent);

    bool TryRestoreFanAuto();

    /// <summary>Vrai quand le constructeur exige un accord explicite de l'utilisateur avant tout
    /// overclock (renonciation de garantie Intel).</summary>
    bool RequiresOverclockWaiver { get; }

    /// <summary>Transmet au pilote l'accord de l'utilisateur ; sans effet chez les autres marques.</summary>
    bool TryAcceptOverclockWaiver();
}
