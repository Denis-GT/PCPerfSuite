using PCPerfSuite.Core.Hardware.Cpu;

namespace PCPerfSuite.Core.Profiles;

/// <summary>
/// Le compte rendu d'une écriture du plan d'alimentation par un groupe (partie processeur), en logique pure : ce que
/// Windows a retenu, rogné ou refusé, réglage par réglage, et si le plan reste changé après la fermeture (D7). L'onglet
/// Processeur s'en sert ; les libellés et unités viennent de <c>describe</c>, dans les termes de ce PC.
/// </summary>
public static class CpuPlanReport
{
    /// <summary>Le plan reste changé après la fermeture : un réglage au moins a été retenu, ou l'écriture a abouti. Un
    /// refus de Windows sans rien de retenu ne laisse rien de permanent.</summary>
    public static bool IsPermanent(PowerPlanWriteResult result) => result.Settings.Any(s => s.Retained) || result.Succeeded;

    /// <param name="restoring">Retour à l'origine (« Tout rétablir », partie « origine ») plutôt qu'une écriture.</param>
    /// <param name="describe">La valeur en clair : réglage, valeur sur secteur, valeur sur batterie (null si la même).</param>
    public static List<ReportItem> Items(PowerPlanWriteResult result, bool restoring, Func<CpuPowerSetting, uint, uint?, string> describe)
    {
        if (result.Settings.Count == 0 && !result.Succeeded)
        {
            return [ReportItem.Refused(CpuGroupPlanner.PlanLabel, $"réglages du plan non écrits : {result.Error}")];
        }

        var items = new List<ReportItem>();
        foreach (PowerPlanSettingResult setting in result.Settings)
        {
            CpuPowerSetting definition = setting.Setting;
            string label = definition.Label;
            string value = setting.Ac is { } shown ? describe(definition, shown, setting.Dc) : "";
            items.Add(setting switch
            {
                { Retained: true } => ReportItem.Applied(label, restoring ? $"« {label} » rendu à son origine ({value})" : $"« {label} » : {value}"),
                { Ac: null } => new ReportItem(label, ReportItemStatus.NotReadBack, $"« {label} » envoyé, non relu"),
                _ when !result.Succeeded => ReportItem.Refused(label, $"« {label} » refusé : {result.Error}"),
                _ => new ReportItem(label, ReportItemStatus.Trimmed,
                    $"« {label} » : Windows a retenu {value} au lieu de {describe(definition, setting.RequestedAc, setting.RequestedDc)}"),
            });
        }

        return items;
    }
}
