namespace PCPerfSuite.Core.Hardware.Cpu.Throttle;

/// <summary>Position des limites dans une version de PM table, en indices de flottants (4 octets).</summary>
public sealed record PmTableLayout(
    uint Version,
    string Family,
    int PptLimit, int PptValue,
    int TdcLimit, int TdcValue,
    int EdcLimit, int EdcValue,
    int ThmLimit, int ThmValue)
{
    /// <summary>Flottants à lire : jusqu'au dernier indice utilisé.</summary>
    public int FloatsNeeded => new[] { PptLimit, PptValue, TdcLimit, TdcValue, EdcLimit, EdcValue, ThmLimit, ThmValue }.Max() + 1;

    /// <summary>Le module rend la table par blocs de 64 bits, deux flottants chacun.</summary>
    public int QwordsNeeded => (FloatsNeeded + 1) / 2;
}

/// <summary>
/// Versions de PM table dont on connaît la position des limites, et décodage pur. AMD ne documente pas cette table, et
/// ses positions changent avec sa version : seules les versions cartographiées sont lues, les autres restent N/D
/// (« pas encore prise en charge »). Positions reprises de ryzen_monitor (hattedsquirrel, pm_tables.c) : les nombres
/// seulement, jamais le code (GPL). Zen 4 et Zen 5 manquent : aucune source publique ne donne encore la position de
/// leurs limites. Expérimental tant que ce n'est pas vérifié sur une vraie machine de chaque famille.
/// </summary>
public static class PmTableLayouts
{
    private static readonly PmTableLayout[] Known =
    [
        // Matisse (Zen 2 bureau) et Vermeer (Zen 3 bureau) : PPT, TDC, THM puis EDC en tête de table.
        Desktop(0x240803, "Matisse"),
        Desktop(0x240903, "Matisse"),
        Desktop(0x380804, "Vermeer"),
        Desktop(0x380805, "Vermeer"),
        Desktop(0x380904, "Vermeer"),
        Desktop(0x380905, "Vermeer"),
        // Cezanne (Zen 3 portable) : STAPM et PPT rapide d'abord ; la PPT lente sert de PPT.
        new PmTableLayout(0x400005, "Cezanne", PptLimit: 4, PptValue: 5, TdcLimit: 8, TdcValue: 9,
            EdcLimit: 12, EdcValue: 13, ThmLimit: 16, ThmValue: 17),
    ];

    private static PmTableLayout Desktop(uint version, string family)
        => new(version, family, PptLimit: 0, PptValue: 1, TdcLimit: 2, TdcValue: 3, EdcLimit: 8, EdcValue: 9, ThmLimit: 4, ThmValue: 5);

    public static IReadOnlyList<PmTableLayout> All => Known;

    public static PmTableLayout? For(uint version) => Known.FirstOrDefault(layout => layout.Version == version);

    /// <summary>Flottants d'une table rendue en blocs de 64 bits : le poids faible d'abord.</summary>
    public static float[] ToFloats(IReadOnlyList<ulong> qwords, int returned)
    {
        int count = Math.Clamp(returned, 0, qwords.Count);
        var values = new float[count * 2];
        for (int i = 0; i < count; i++)
        {
            values[i * 2] = BitConverter.UInt32BitsToSingle((uint)qwords[i]);
            values[i * 2 + 1] = BitConverter.UInt32BitsToSingle((uint)(qwords[i] >> 32));
        }
        return values;
    }

    /// <summary>
    /// Limites lues d'après la disposition. Null si la table est trop courte ou si une limite n'est pas plausible :
    /// une disposition fausse donne des valeurs absurdes, qu'il vaut mieux ne pas afficher. Une valeur du moment
    /// implausible est seulement écartée.
    /// </summary>
    public static AmdPowerLimits? Decode(PmTableLayout layout, IReadOnlyList<float> values)
    {
        if (values.Count < layout.FloatsNeeded) return null;

        float pptLimit = values[layout.PptLimit];
        float tdcLimit = values[layout.TdcLimit];
        float edcLimit = values[layout.EdcLimit];
        float thmLimit = values[layout.ThmLimit];

        if (!InRange(pptLimit, 1, 1000) || !InRange(tdcLimit, 1, 1000) || !InRange(edcLimit, 1, 1000) || !InRange(thmLimit, 50, 125))
        {
            return null;
        }

        return new AmdPowerLimits(
            layout.Version,
            Value(values[layout.PptValue], 0, 1000), pptLimit,
            Value(values[layout.TdcValue], 0, 1000), tdcLimit,
            Value(values[layout.EdcValue], 0, 1000), edcLimit,
            Value(values[layout.ThmValue], 0, 125), thmLimit);
    }

    private static bool InRange(float value, float min, float max) => !float.IsNaN(value) && value >= min && value <= max;

    private static float? Value(float value, float min, float max) => InRange(value, min, max) ? value : null;
}
