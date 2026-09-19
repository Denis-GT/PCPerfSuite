using System.Globalization;
using PCPerfSuite.Core.Hardware;

namespace PCPerfSuite.App.Metrics;

/// <summary>
/// Conso totale du PC et batterie : proposées au Monitoring comme à l'overlay, mais seulement quand la machine
/// sait les mesurer — une batterie (portable) ou une alimentation connectée (fixe). Ailleurs elles ne sont pas
/// proposées du tout, plutôt que d'afficher "--" en permanence. Construites à partir d'un relevé, comme
/// <see cref="MonitoringSensorCatalog"/> ; la sélection enregistrée attend leur apparition.
/// </summary>
public static class PowerMetricCatalog
{
    private const string OnMainsNote =
        "Sur secteur, Windows ne mesure que ce qui entre dans la batterie, pas la consommation du PC : " +
        "le chargeur alimente le PC directement, sans capteur. Débranchez-le pour voir la conso totale.";

    public static IEnumerable<MetricDefinition> FromSnapshot(HardwareSnapshot snapshot)
    {
        BatterySnapshot? battery = snapshot.Battery;

        if (snapshot.PsuPowerWatts is not null || battery?.RateMw is not null)
        {
            yield return Describe(MetricCatalog.Numeric("power.total", MetricCatalog.Power, "Conso totale du PC", "total", "W", "0.0",
                    s => TotalPowerWatts(s.Hardware)),
                "Puissance consommée par tout le PC. Mesurée par l'alimentation connectée sur un fixe ; sur un portable, " +
                "c'est la décharge de la batterie, donc mesurable seulement sur batterie (\"secteur\" sinon).",
                read: s => s.Hardware.PsuPowerWatts is null && s.Hardware.Battery is { PowerOnline: true }
                    ? new MetricReading(null, "secteur", "", OnMainsNote)
                    : null);
        }

        if (battery is null) yield break;

        if (battery.RateMw is not null)
        {
            yield return Signed("battery.rate.w", "Charge/décharge (W)", "W", "0.0", 10, s => s.Hardware.Battery?.RateMw / 1000);
        }

        if (battery.RateMa is not null)
        {
            yield return Signed("battery.rate.ma", "Charge/décharge (mA)", "mA", "0", 500, s => s.Hardware.Battery?.RateMa);
        }

        if (battery.ChargePercent is not null)
        {
            yield return MetricCatalog.Numeric("battery.level", MetricCatalog.Power, "Batterie restante (%)", "batt", "%", "0",
                s => s.Hardware.Battery?.ChargePercent);
        }

        if (battery.ToMah(battery.FullChargeMWh) is { } fullMah)
        {
            yield return Capacity(fullMah);
        }
    }

    /// <summary>Puissance totale en W : l'alimentation connectée si elle existe, sinon la décharge de la batterie.
    /// Null sur secteur, où rien ne la mesure.</summary>
    private static double? TotalPowerWatts(HardwareSnapshot s)
    {
        if (s.PsuPowerWatts is { } psu) return psu;
        if (s.Battery is not { PowerOnline: false, RateMw: { } rate }) return null;
        return Math.Max(0, -rate) / 1000;
    }

    /// <summary>Débit signé : "+" en charge, "-" en décharge (ASCII, lisible dans l'OSD RTSS).</summary>
    private static MetricDefinition Signed(string id, string label, string unit, string format, double minimumScale,
        Func<MetricSample, double?> get)
    {
        string signedFormat = $"+{format};-{format};{format}";
        MetricReading Format(double value) => new(value, value.ToString(signedFormat, CultureInfo.CurrentCulture), unit);

        return new MetricDefinition
        {
            Id = id,
            Category = MetricCatalog.Power,
            Label = label,
            OsdLabel = unit == "W" ? "charge" : "courant",
            Description = "Positif quand la batterie se charge, négatif quand elle se décharge.",
            IsSigned = true,
            GraphMinimumScale = minimumScale,
            ReadGroup = SensorGroup.Battery,
            Read = s => get(s) is { } v ? Format(v) : MetricReading.Missing,
            FormatNumber = Format,
        };
    }

    /// <summary>Capacité restante "actuelle / max" en mAh. Le max est la capacité nominale ajustée de l'usure
    /// (capacité à pleine charge) ; les deux sont convertis depuis les mWh du pilote à la tension mesurée.
    /// La courbe suit la valeur actuelle, sur une échelle fixe allant jusqu'au max relevé à la découverte.</summary>
    private static MetricDefinition Capacity(double fullMahAtDiscovery)
    {
        static MetricReading FormatCurrent(double mah) => new(mah, mah.ToString("0", CultureInfo.CurrentCulture), "mAh");

        return new MetricDefinition
        {
            Id = "battery.capacity",
            Category = MetricCatalog.Power,
            Label = "Capacité restante (mAh)",
            OsdLabel = "mAh",
            Description = "Charge restante / capacité maximale estimée (capacité nominale × état de santé), " +
                          "converties depuis les mWh du pilote à la tension actuelle.",
            ReadGroup = SensorGroup.Battery,
            GraphMaximum = Math.Ceiling(fullMahAtDiscovery),
            Read = s =>
            {
                BatterySnapshot? b = s.Hardware.Battery;
                if (b?.ToMah(b.RemainingMWh) is not { } current) return MetricReading.Missing;

                string max = b.ToMah(b.FullChargeMWh) is { } full ? full.ToString("0", CultureInfo.CurrentCulture) : "--";
                return new MetricReading(current, $"{current.ToString("0", CultureInfo.CurrentCulture)} / {max}", "mAh");
            },
            FormatNumber = FormatCurrent,
        };
    }

    /// <summary>Recopie une définition du catalogue en y ajoutant sa description, et au besoin une lecture qui
    /// prend la main avant la lecture numérique (texte à la place du nombre).</summary>
    private static MetricDefinition Describe(MetricDefinition definition, string description,
        Func<MetricSample, MetricReading?>? read = null) => new()
    {
        Id = definition.Id,
        Category = definition.Category,
        Label = definition.Label,
        OsdLabel = definition.OsdLabel,
        Description = description,
        IsPercent = definition.IsPercent,
        IsSigned = definition.IsSigned,
        GraphMaximum = definition.GraphMaximum,
        GraphMinimumScale = definition.GraphMinimumScale,
        HasGraph = definition.HasGraph,
        ReadGroup = definition.ReadGroup,
        FormatNumber = definition.FormatNumber,
        Read = read is null ? definition.Read : s => read(s) ?? definition.Read(s),
    };
}
