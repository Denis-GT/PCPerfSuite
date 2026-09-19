using PCPerfSuite.Core.Overlay;

namespace PCPerfSuite.Core.Hardware;

public sealed class FanReading
{
    public required string HardwareName { get; init; }
    public required string SensorName { get; init; }
    public float? Rpm { get; init; }
    public float? PercentControl { get; init; }

    /// <summary>Identifiant stable du capteur RPM.</summary>
    public required string SensorId { get; init; }

    /// <summary>Identifiant stable du capteur de contrôle (%) associé, quand la puce Super I/O permet
    /// de le piloter en écriture — null si ce ventilateur n'est lisible qu'en lecture seule.</summary>
    public string? PercentControlSensorId { get; init; }

    /// <summary>Groupe dont la lecture rafraîchit ce ventilateur (GPU ou carte mère).</summary>
    public SensorGroup Group { get; init; } = SensorGroup.Motherboard;

    public bool CanControl => PercentControlSensorId is not null;
}

public sealed class CpuSnapshot
{
    public string Name { get; init; } = "CPU inconnu";
    public float? LoadPercent { get; init; }
    public float? MaxCoreLoadPercent { get; init; }
    public float? PackageTempC { get; init; }
    public float? MaxCoreTempC { get; init; }
    public float? PowerWatts { get; init; }
    public float? MaxClockMhz { get; init; }
    public float? CoreVoltage { get; init; }
}

public sealed class GpuSnapshot
{
    public string Name { get; init; } = "GPU inconnu";
    public string Vendor { get; init; } = "Inconnu";
    public float? LoadPercent { get; init; }
    public float? CoreTempC { get; init; }
    public float? HotSpotTempC { get; init; }
    public float? MemoryJunctionTempC { get; init; }
    public float? CoreClockMhz { get; init; }
    public float? MemoryClockMhz { get; init; }
    public float? PowerWatts { get; init; }
    public float? VramUsedMb { get; init; }
    public float? VramTotalMb { get; init; }
    public float? FanRpm { get; init; }
    public float? FanPercent { get; init; }
}

public sealed class MemorySnapshot
{
    public float? UsedGb { get; init; }
    public float? AvailableGb { get; init; }
    public float? TotalGb { get; init; }
    public float? LoadPercent { get; init; }
    public float? VirtualUsedGb { get; init; }
}

/// <summary>Capteur nommé générique (température, tension...) pour les valeurs dont la liste
/// varie selon la carte mère et qu'on ne peut donc pas modéliser avec des propriétés fixes.</summary>
public sealed class SensorReading
{
    public required string Name { get; init; }
    public float? Value { get; init; }
}

public sealed class MotherboardSnapshot
{
    public string Name { get; init; } = "Carte mère inconnue";

    /// <summary>Libellé de la 1ère case température : "Température système" quand LibreHardwareMonitor
    /// a pu identifier la sonde par son nom, sinon le nom brut de la sonde utilisée en repli (carte
    /// mère absente de sa table de correspondance par modèle — cas de nombreuses cartes récentes).</summary>
    public string SystemTempLabel { get; init; } = "Température système";
    public float? SystemTempC { get; init; }

    public string VrmTempLabel { get; init; } = "VRM";
    public float? VrmTempC { get; init; }

    /// <summary>Autres zones de température exposées par la puce Super I/O (chipset, PCH, sondes numérotées...).</summary>
    public IReadOnlyList<SensorReading> OtherTemperatures { get; init; } = Array.Empty<SensorReading>();

    /// <summary>Tensions (Vcore, +12V, +5V, +3.3V, DRAM...) — la liste dépend entièrement du modèle de carte mère.</summary>
    public IReadOnlyList<SensorReading> Voltages { get; init; } = Array.Empty<SensorReading>();
}

public sealed class DiskSnapshot
{
    public string Name { get; init; } = "Disque inconnu";

    /// <summary>Identifiant stable côté LibreHardwareMonitor (pas le numéro PhysicalDriveN de Windows).</summary>
    public string Identifier { get; init; } = "";

    public float? UsedPercent { get; init; }
    public float? ReadRateBytesPerSecond { get; init; }
    public float? WriteRateBytesPerSecond { get; init; }
    public float? TemperatureC { get; init; }

    /// <summary>Vie restante estimée (%), quand le firmware l'expose (SMART SSD/NVMe) — null pour la plupart des HDD.</summary>
    public float? RemainingLifePercent { get; init; }
}

/// <summary>Débits cumulés de toutes les cartes réseau vues par LibreHardwareMonitor, en octets/seconde —
/// null si aucune n'expose de débit.</summary>
public sealed class NetworkSnapshot
{
    public float? UploadBytesPerSecond { get; init; }
    public float? DownloadBytesPerSecond { get; init; }
}

/// <summary>Batterie(s) du portable telles que les rapporte le pilote Windows — additionnées s'il y en a
/// plusieurs. Capacités en mWh, null si le pilote ne donne que des unités relatives.</summary>
public sealed class BatterySnapshot
{
    /// <summary>Chargeur branché.</summary>
    public bool PowerOnline { get; init; }
    public bool Charging { get; init; }
    public bool Discharging { get; init; }

    public double? RemainingMWh { get; init; }

    /// <summary>Capacité à pleine charge actuelle : la capacité nominale diminuée de l'usure.</summary>
    public double? FullChargeMWh { get; init; }

    /// <summary>Capacité nominale, celle de la batterie neuve.</summary>
    public double? DesignMWh { get; init; }

    public double? VoltageMv { get; init; }

    /// <summary>Débit en mW : positif en charge, négatif en décharge, null si le pilote ne le mesure pas.</summary>
    public double? RateMw { get; init; }

    public int? CycleCount { get; init; }
    public string? Name { get; init; }
    public string? Manufacturer { get; init; }
    public string? Chemistry { get; init; }
    public int BatteryCount { get; init; } = 1;

    public double? ChargePercent => RemainingMWh is { } r && FullChargeMWh is { } f && f > 0 ? Math.Min(100, r / f * 100) : null;

    /// <summary>État de santé : capacité à pleine charge rapportée à la capacité nominale.</summary>
    public double? HealthPercent => FullChargeMWh is { } f && DesignMWh is { } d && d > 0 ? f / d * 100 : null;

    /// <summary>Courant en mA (mW ÷ V), même signe que <see cref="RateMw"/>.</summary>
    public double? RateMa => RateMw is { } p && VoltageMv is { } v && v > 0 ? p / v * 1000 : null;

    /// <summary>Convertit une énergie en mWh en charge en mAh, à la tension mesurée.</summary>
    public double? ToMah(double? mWh) => mWh is { } e && VoltageMv is { } v && v > 0 ? e / v * 1000 : null;
}

/// <summary>Durée de la mise à jour d'un matériel (sous-matériel compris) lors d'un relevé.</summary>
public sealed class HardwareReadTiming
{
    /// <summary>Identifiant stable côté LibreHardwareMonitor, pour distinguer deux disques de même nom.</summary>
    public required string Identifier { get; init; }
    public required string Name { get; init; }

    public TimeSpan Duration { get; init; }
}

public sealed class HardwareSnapshot
{
    public CpuSnapshot Cpu { get; init; } = new();
    public GpuSnapshot? Gpu { get; init; }
    public MemorySnapshot Memory { get; init; } = new();
    public MotherboardSnapshot Motherboard { get; init; } = new();
    public IReadOnlyList<FanReading> Fans { get; init; } = Array.Empty<FanReading>();
    public IReadOnlyList<DiskSnapshot> Disks { get; init; } = Array.Empty<DiskSnapshot>();
    public NetworkSnapshot Network { get; init; } = new();

    /// <summary>Null sans batterie (PC fixe).</summary>
    public BatterySnapshot? Battery { get; init; }

    /// <summary>Puissance mesurée par une alimentation connectée (Corsair HXi/RMi...), null sinon.</summary>
    public float? PsuPowerWatts { get; init; }

    public DateTime CapturedAtUtc { get; init; } = DateTime.UtcNow;

    /// <summary>Matériel effectivement mis à jour pour ce relevé : le matériel lent en est absent
    /// quand il n'était pas encore temps de le relire.</summary>
    public IReadOnlyList<HardwareReadTiming> ReadTimings { get; init; } = Array.Empty<HardwareReadTiming>();

    /// <summary>Durée totale de GetSnapshot : mises à jour plus extraction des valeurs.</summary>
    public TimeSpan ReadDuration { get; init; }

    /// <summary>Cadence de chaque groupe de capteurs après ce relevé.</summary>
    public IReadOnlyList<SensorGroupReadStatus> GroupStatuses { get; init; } = Array.Empty<SensorGroupReadStatus>();

    /// <summary>Groupes effectivement relus pendant ce relevé ; les valeurs des autres sont celles de leur dernière lecture.</summary>
    public IReadOnlyCollection<SensorGroup> GroupsRead { get; init; } = Array.Empty<SensorGroup>();

    /// <summary>Statistiques RTSS de l'application au premier plan — null hors jeu ou sans RTSS.</summary>
    public RtssFrameStats? Game { get; init; }
}
