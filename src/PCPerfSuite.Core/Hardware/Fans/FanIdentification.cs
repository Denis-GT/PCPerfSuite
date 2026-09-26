using System.Globalization;
using System.Text.RegularExpressions;
using PCPerfSuite.Core.Hardware.LaptopFans;

namespace PCPerfSuite.Core.Hardware.Fans;

/// <summary>Ce qu'il faut savoir d'un ventilateur pour lui donner un libellé (voir
/// <see cref="FanIdentification.AssignLabels"/>).</summary>
/// <param name="RawName">Nom du capteur tel que le matériel le donne.</param>
/// <param name="NameFromHardware">Le nom identifie vraiment le connecteur (« CPU Fan »), au lieu d'être un simple
/// numéro de canal (« Fan #6 »).</param>
/// <param name="Channel">Numéro du canal dans la puce ou le pilote, quand on le connaît.</param>
/// <param name="HardwareId">Puce ou pilote qui porte le canal : deux puces peuvent numéroter chacune à partir de 1.</param>
public readonly record struct FanLabelInput(
    FanCategory Category, string RawName, bool NameFromHardware, int? Channel, string? HardwareId);

/// <summary>
/// Range chaque ventilateur dans une catégorie et lui donne un libellé lisible. Fonctions pures, sans accès au
/// matériel : tout part du nom que la puce, le pilote graphique ou l'interface du constructeur fournit.
///
/// Le nom d'un connecteur n'est connu que si la bibliothèque de capteurs a une table pour ce modèle de carte
/// mère. Sinon la puce ne donne que « Fan #N » : on ne peut alors ni retrouver le connecteur, ni savoir si c'est
/// un ventilateur ou une pompe — le ventilateur reste « non identifié » plutôt que de deviner.
/// </summary>
public static class FanIdentification
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    // « AIO Pump », « W_PUMP+ », « Water Pump », « CPU Pump Fan ». Les connecteurs mixtes (« System Fan #5 / Pump »)
    // tombent ici aussi : les traiter en pompe est le choix sûr, puisqu'une pompe ne doit jamais être arrêtée.
    private static readonly Regex PumpName = new(@"pump|pompe|\baio\b|water", Options);

    // « CPU Fan », « CPU Optional Fan », « CPU_OPT », « Radiator Fan » (le radiateur refroidit le processeur).
    private static readonly Regex CpuName = new(@"\bcpu|radiator", Options);

    // Les mots courts sont suivis de (?![a-z]) : « cha » doit reconnaître « CHA_FAN1 » sans reconnaître « Channel 1 ».
    private static readonly Regex CaseName = new(
        @"\b(?:chassis|system|sys|case|cha|aux(?:iliary)?|rear|front|top|exhaust|intake|high amp)(?![a-z])", Options);

    // « Fan #6 », « Fan 6 », « Fan Control #6 » : ce que produit une puce dont la carte n'est pas dans la table de
    // la bibliothèque. « Ventilateur 1 » est le même cas côté portables (MSI).
    private static readonly Regex GenericName = new(
        @"^(?:fan|ventilateur)(?:\s*(?:control|controller))?(?:\s*#?\s*\d+)?$", Options);

    /// <summary>Le nom n'est qu'un numéro de canal : il ne dit pas ce qui est branché.</summary>
    public static bool IsGenericName(string? sensorName)
        => string.IsNullOrWhiteSpace(sensorName) || GenericName.IsMatch(sensorName.Trim());

    /// <param name="sensorName">Nom du capteur tel que le matériel le donne.</param>
    /// <param name="onGpu">Le ventilateur appartient à une carte graphique.</param>
    /// <param name="laptopRole">Rôle annoncé par l'interface du constructeur d'un portable, quand il en précise un.</param>
    public static FanCategory Categorize(string? sensorName, bool onGpu, LaptopFanRole? laptopRole = null)
    {
        if (onGpu) return FanCategory.Gpu;

        switch (laptopRole)
        {
            case LaptopFanRole.Cpu: return FanCategory.Cpu;
            case LaptopFanRole.Gpu: return FanCategory.Gpu;
        }

        if (IsGenericName(sensorName)) return FanCategory.Unidentified;

        string name = sensorName!.Trim();
        if (PumpName.IsMatch(name)) return FanCategory.Pump;
        if (CpuName.IsMatch(name)) return FanCategory.Cpu;
        if (CaseName.IsMatch(name)) return FanCategory.Case;
        return FanCategory.Other;
    }

    /// <summary>
    /// Libellé de chaque ventilateur, dans l'ordre de <paramref name="fans"/>.
    /// - Un nom donné par le matériel est repris tel quel (« CPU Fan », « AIO Pump ») : c'est ce qui est sérigraphié
    ///   sur la carte.
    /// - Sinon, un ventilateur non identifié porte le numéro de son canal (« Ventilateur 6 »), et un autre est
    ///   numéroté dans sa catégorie (« Boîtier 2 », « GPU 3 »), sans numéro quand il est seul (« Pompe »).
    ///
    /// Le rang dans la catégorie suit l'ordre des canaux, et non l'ordre de lecture ni la vitesse : un canal vide
    /// compte comme un autre, si bien qu'un libellé ne bouge pas quand on branche ou débranche un ventilateur.
    /// </summary>
    public static IReadOnlyList<string> AssignLabels(IReadOnlyList<FanLabelInput> fans)
    {
        var rank = new int[fans.Count];
        var size = new Dictionary<FanCategory, int>();

        foreach (IGrouping<FanCategory, int> group in Enumerable.Range(0, fans.Count).GroupBy(i => fans[i].Category))
        {
            int position = 0;
            foreach (int i in group.OrderBy(i => fans[i].Channel ?? int.MaxValue).ThenBy(i => i))
            {
                rank[i] = ++position;
            }
            size[group.Key] = position;
        }

        int?[] channelNumber = ChannelNumbers(fans);
        var labels = new string[fans.Count];

        for (int i = 0; i < fans.Count; i++)
        {
            FanLabelInput fan = fans[i];
            labels[i] = fan.NameFromHardware && !string.IsNullOrWhiteSpace(fan.RawName)
                ? fan.RawName.Trim()
                : UnnamedLabel(fan.Category, rank[i], size[fan.Category], channelNumber[i] ?? rank[i]);
        }

        return DisambiguateDuplicates(labels);
    }

    /// <summary>Numéro affiché pour un ventilateur sans nom : celui de son canal (« Fan #6 » → 6). Ce numéro n'est
    /// utilisé que s'il est unique parmi les ventilateurs sans nom : deux puces qui numérotent chacune à partir de
    /// 1 donneraient deux « Ventilateur 1 », auquel cas on retombe sur le rang.</summary>
    private static int?[] ChannelNumbers(IReadOnlyList<FanLabelInput> fans)
    {
        var numbers = new int?[fans.Count];
        for (int i = 0; i < fans.Count; i++)
        {
            FanLabelInput fan = fans[i];
            if (fan.NameFromHardware) continue;

            numbers[i] = fan.Channel is { } channel ? channel + 1 : NumberInName(fan.RawName);
        }

        bool duplicated = numbers.Where(n => n is not null).GroupBy(n => n).Any(g => g.Count() > 1);
        return duplicated ? new int?[fans.Count] : numbers;
    }

    private static int? NumberInName(string name)
    {
        Match digits = Regex.Match(name, @"\d+");
        return digits.Success && int.TryParse(digits.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : null;
    }

    private static string UnnamedLabel(FanCategory category, int rank, int categorySize, int channelNumber)
    {
        string? noun = category switch
        {
            FanCategory.Cpu => "CPU",
            FanCategory.Gpu => "GPU",
            FanCategory.Pump => "Pompe",
            FanCategory.Case => "Boîtier",
            _ => null,
        };

        if (noun is null) return $"Ventilateur {channelNumber}";
        return categorySize > 1 ? $"{noun} {rank}" : noun;
    }

    /// <summary>Deux ventilateurs qui porteraient le même libellé (deux puces avec un « CPU Fan » chacune) sont
    /// numérotés : le libellé sert d'étiquette, deux étiquettes identiques ne servent à rien.</summary>
    private static string[] DisambiguateDuplicates(string[] labels)
    {
        var result = (string[])labels.Clone();

        foreach (var group in labels.Select((label, index) => (label, index))
                     .GroupBy(x => x.label, StringComparer.OrdinalIgnoreCase)
                     .Where(g => g.Count() > 1))
        {
            int n = 0;
            foreach (var (label, index) in group)
            {
                result[index] = $"{label} ({++n})";
            }
        }

        return result;
    }

    /// <summary>Copie de <paramref name="fans"/> avec le libellé de chacun calculé sur l'ensemble.</summary>
    public static IReadOnlyList<FanReading> Label(IReadOnlyList<FanReading> fans)
    {
        IReadOnlyList<string> labels = AssignLabels(fans
            .Select(f => new FanLabelInput(f.Category, f.SensorName, f.NameFromHardware, f.Channel, f.HardwareId))
            .ToList());

        return fans.Select((fan, i) => fan.WithLabel(labels[i])).ToList();
    }
}
