using PCPerfSuite.Core.Hardware;

namespace PCPerfSuite.App.Metrics;

/// <summary>
/// Capteurs dont la liste dépend de la machine : chaque ventilateur, chaque disque, chaque sonde de température
/// et chaque tension de la carte mère. Construits à partir d'un relevé et proposés en tuiles graphiques dans le
/// Monitoring, en plus du catalogue fixe que partage l'overlay. Chaque capteur est retrouvé à chaque relevé par
/// son identifiant (ou son nom pour la carte mère), et affiche "--" s'il disparaît.
/// </summary>
public static class MonitoringSensorCatalog
{
    private static readonly MetricCategory Fans = new("fans", "Ventilateurs", "FAN", "#8FB8FF", "#5F8FD9");

    public static IEnumerable<MetricDefinition> FromSnapshot(HardwareSnapshot snapshot)
    {
        foreach (SensorReading temperature in snapshot.Motherboard.OtherTemperatures)
        {
            string name = temperature.Name;
            yield return MetricCatalog.Numeric($"mb.temp:{name}", MetricCatalog.Motherboard, name, "temp", "°C", "0",
                s => s.Hardware.Motherboard.OtherTemperatures.FirstOrDefault(t => t.Name == name)?.Value, hint: MetricCatalog.MotherboardHint);
        }

        foreach (SensorReading voltage in snapshot.Motherboard.Voltages)
        {
            string name = voltage.Name;
            yield return MetricCatalog.Numeric($"mb.volt:{name}", MetricCatalog.Motherboard, $"Tension {name}", "volt", "V", "0.000",
                s => s.Hardware.Motherboard.Voltages.FirstOrDefault(v => v.Name == name)?.Value);
        }

        foreach (DiskSnapshot disk in snapshot.Disks)
        {
            string id = disk.Identifier;
            yield return MetricCatalog.Numeric($"disk:{id}:load", MetricCatalog.Storage, $"{disk.Name} · charge", "charge", "%", "0",
                s => Disk(s, id)?.ActivityPercent, hint: MetricCatalog.DiskLoadHint);
            yield return MetricCatalog.Rate($"disk:{id}:read", MetricCatalog.Storage, $"{disk.Name} · lecture", "lect",
                MetricCatalog.DiskRateFloor, s => Disk(s, id)?.ReadRateBytesPerSecond);
            yield return MetricCatalog.Rate($"disk:{id}:write", MetricCatalog.Storage, $"{disk.Name} · écriture", "ecr",
                MetricCatalog.DiskRateFloor, s => Disk(s, id)?.WriteRateBytesPerSecond);
            yield return MetricCatalog.Numeric($"disk:{id}:temp", MetricCatalog.Storage, $"{disk.Name} · température", "temp", "°C", "0",
                s => Disk(s, id)?.TemperatureC);
            yield return MetricCatalog.Numeric($"disk:{id}:used", MetricCatalog.Storage, $"{disk.Name} · espace utilisé", "util", "%", "0",
                s => Disk(s, id)?.UsedPercent);
        }

        foreach (FanReading fan in snapshot.Fans)
        {
            string id = fan.SensorId;
            yield return MetricCatalog.Numeric($"fan:{id}", Fans, $"{fan.SensorName} ({fan.HardwareName})", "fan", "RPM", "0",
                s => s.Hardware.Fans.FirstOrDefault(f => f.SensorId == id)?.Rpm, fan.Group, MetricCatalog.FanHint);

            // Le % n'est proposé que pour un ventilateur qui en fournit un (commande de la carte mère, maximum connu
            // du portable) : sinon la tuile afficherait toujours "N/D".
            if (fan.PercentControl is not null)
            {
                yield return MetricCatalog.Numeric($"fan:{id}:percent", Fans, $"{fan.SensorName} % ({fan.HardwareName})", "fan", "%", "0",
                    s => s.Hardware.Fans.FirstOrDefault(f => f.SensorId == id)?.PercentControl, fan.Group, MetricCatalog.FanHint);
            }
        }
    }

    private static DiskSnapshot? Disk(MetricSample sample, string identifier)
        => sample.Hardware.Disks.FirstOrDefault(d => d.Identifier == identifier);
}
