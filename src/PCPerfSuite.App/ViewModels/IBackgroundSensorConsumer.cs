using PCPerfSuite.Core.Hardware;

namespace PCPerfSuite.App.ViewModels;

/// <summary>
/// Ce qui continue de travailler fenêtre cachée (overlay, courbes de ventilateurs, sécurités thermiques du GPU et du CPU) et
/// dit de quels groupes de capteurs il a besoin : en mode éco, <see cref="MonitoringViewModel"/> ne relit que ceux-là.
/// </summary>
public interface IBackgroundSensorConsumer
{
    /// <summary>Ajoute à <paramref name="into"/> les groupes nécessaires en ce moment ; rien s'il est inactif.</summary>
    void AddRequiredGroups(ISet<SensorGroup> into);
}

/// <summary>Besoins en capteurs des consommateurs d'arrière-plan, isolés ici pour être testés sans matériel.</summary>
public static class BackgroundSensorNeeds
{
    /// <summary>Groupes à relire pour réguler un ventilateur : celui qui porte sa vitesse et son pilotage (le GPU
    /// pour ses propres ventilateurs, la carte mère sinon), plus ceux de la température qu'il suit.</summary>
    public static void AddForFan(ISet<SensorGroup> into, bool isGpuFan, FanTempSource source)
    {
        into.Add(isGpuFan ? SensorGroup.Gpu : SensorGroup.Motherboard);

        switch (source)
        {
            case FanTempSource.CpuPackage:
                into.Add(SensorGroup.Cpu);
                break;
            case FanTempSource.GpuCore:
                into.Add(SensorGroup.Gpu);
                break;
            case FanTempSource.MotherboardSystem:
                into.Add(SensorGroup.Motherboard);
                break;
            case FanTempSource.HottestOfCpuGpu:
                into.Add(SensorGroup.Cpu);
                into.Add(SensorGroup.Gpu);
                break;
        }
    }

    /// <summary>Groupes lus par les métriques affichées dans l'overlay ; rien quand il est désactivé. L'heure, qui ne
    /// vient d'aucun capteur (groupe null), n'en demande aucun.</summary>
    public static void AddForOverlay(ISet<SensorGroup> into, bool isEnabled, IEnumerable<SensorGroup?> metricGroups)
    {
        if (!isEnabled) return;

        foreach (SensorGroup? group in metricGroups)
        {
            if (group is { } g) into.Add(g);
        }
    }
}
