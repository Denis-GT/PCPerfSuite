using System.Globalization;

namespace PCPerfSuite.Core.Hardware.Cpu;

/// <summary>D'où vient le maximum proposé pour les limites de puissance.</summary>
public enum CpuMaxWattsSource
{
    /// <summary>Puissance maximale publiée par le processeur (Intel : MSR_PKG_POWER_INFO, bits 46:32).</summary>
    ProcessorMaxPower,

    /// <summary>Limite de crête PL4 du processeur (Intel : MSR_VR_CURRENT_CONFIG, bits 12:0).</summary>
    PeakLimitPl4,

    /// <summary>Repli : la limite posée par le BIOS, multipliée par 1,5.</summary>
    FactoryLimit,

    /// <summary>Repli : plafond fixe, quand le BIOS ne fixe aucune limite et que le processeur ne dit rien.</summary>
    SafetyCap,
}

/// <summary>
/// Le maximum proposé pour les champs de limite de puissance, et surtout d'où il vient : l'interface et le
/// diagnostic « Compatibilité de ce PC » l'affichent tel quel, pour qu'un maximum de repli ne passe jamais
/// pour la limite réelle du processeur.
/// </summary>
/// <param name="Watts">Le maximum, déjà borné par le plancher.</param>
/// <param name="Source">La source retenue.</param>
/// <param name="Explanation">Une phrase pour l'utilisateur : ce qui a été retenu et, pour un repli, pourquoi
/// les sources du processeur n'ont rien donné.</param>
/// <param name="IsExperimental">Vrai si le choix repose sur une lecture pas encore vérifiée sur une vraie machine.</param>
/// <param name="RawValues">Les valeurs brutes lues, pour le rapport de compatibilité.</param>
public sealed record CpuMaxWattsInfo(
    float Watts, CpuMaxWattsSource Source, string Explanation, bool IsExperimental, string RawValues)
{
    /// <summary>Ajoutée partout où un maximum <see cref="IsExperimental"/> est affiché (onglet Processeur et
    /// diagnostic), pour que les deux disent la même chose.</summary>
    public const string ExperimentalNotice =
        "Expérimental : non vérifié sur une vraie machine, signale toute valeur incohérente.";

    /// <summary>Vrai si la valeur vient du processeur lui-même, pas d'un repli de l'app.</summary>
    public bool FromProcessor => Source is CpuMaxWattsSource.ProcessorMaxPower or CpuMaxWattsSource.PeakLimitPl4;

    public string SourceLabel => Source switch
    {
        CpuMaxWattsSource.ProcessorMaxPower => "puissance maximale du processeur",
        CpuMaxWattsSource.PeakLimitPl4 => "limite de crête PL4 du processeur",
        CpuMaxWattsSource.FactoryLimit => "limite du BIOS × 1,5",
        _ => "plafond de sécurité",
    };
}

/// <summary>Ce que l'on a pu lire dans le processeur Intel pour décider du maximum. Tout est en watts.</summary>
/// <param name="SustainedWatts">PL1 lue au lancement (avant toute écriture de l'app).</param>
/// <param name="BurstWatts">PL2 lue au lancement.</param>
/// <param name="MinWatts">Plancher, déjà borné (≥ 5 W).</param>
/// <param name="TdpWatts">Puissance de base (MSR 0x614, bits 14:0), null si illisible.</param>
/// <param name="InfoMaxWatts">Puissance maximale (MSR 0x614, bits 46:32), null si illisible. 0 = registre vide.</param>
/// <param name="InfoError">Raison du refus quand 0x614 n'a pas pu être lu.</param>
/// <param name="Pl4Exposed">Cette génération de processeur a PL4 dans le MSR 0x601 (voir <see cref="CpuMaxWattsResolver.HasPl4Register"/>).</param>
/// <param name="Pl4Watts">PL4 (MSR 0x601, bits 12:0), null si non lue.</param>
/// <param name="Pl4Error">Raison du refus quand 0x601 n'a pas pu être lu.</param>
public sealed record IntelPowerReadings(
    float SustainedWatts,
    float BurstWatts,
    float MinWatts,
    float? TdpWatts,
    float? InfoMaxWatts,
    string? InfoError,
    bool Pl4Exposed,
    float? Pl4Watts,
    string? Pl4Error);

/// <summary>
/// Décide du maximum proposé pour les limites de puissance. Aucun accès matériel : les backends lisent, cette
/// classe tranche, ce qui permet de tester chaque branche sans pilote ni processeur particulier.
///
/// Intel, dans l'ordre : puissance maximale publiée par le processeur, puis PL4, puis limite du BIOS × 1,5,
/// puis un plafond fixe. AMD : la limite lue × 1,5 — aucune voie documentée ne donne mieux par la SMU.
/// </summary>
public static class CpuMaxWattsResolver
{
    /// <summary>Au-delà, une limite lue n'en est pas une : le registre RAPL tient sur 15 bits (4095,875 W à 1/8 W
    /// près) et beaucoup de cartes mères de bureau y écrivent 4095 W pour dire « sans limite ». Aucun processeur
    /// grand public n'approche 1000 W.</summary>
    public const float UnlimitedWatts = 1000f;

    /// <summary>Plafond retenu quand rien d'autre n'est exploitable : couvre les réglages usine des plus gros
    /// processeurs de bureau (≈ 250-350 W) sans laisser monter plus haut.</summary>
    public const float SafetyCapWatts = 400f;

    /// <summary>Plafond de sécurité, en multiple de la limite du BIOS : monter au-delà ne sert à rien (le
    /// processeur est de toute façon bridé par sa température et son VRM) et cuit le matériel.</summary>
    public const float FactoryMultiplier = 1.5f;

    /// <summary>
    /// Vrai si ce processeur Intel a PL4 dans MSR_VR_CURRENT_CONFIG (0x601, bits 12:0, en unités de puissance).
    /// Sur les générations antérieures, ce registre contient un courant en ampères : le lire en watts donnerait
    /// une valeur absurde, d'où une liste de modèles plutôt qu'un simple essai.
    ///
    /// Recopié à l'identique de drivers/powercap/intel_rapl_msr.c (noyau Linux, table rapl_ids, entrées
    /// rapl_defaults_core_pl4 et rapl_defaults_core_pl4_pmu, relue le 26/09/2026), avec les numéros de
    /// arch/x86/include/asm/intel-family.h. Arrow Lake de bureau (0xC6) et Raptor Lake-S (0xBF) n'y
    /// figurent pas : ils passent par les replis.
    /// </summary>
    public static bool HasPl4Register(int family, int model) => (family, model) switch
    {
        (6, 0x7E) => true, // Ice Lake-L
        (6, 0x8C) => true, // Tiger Lake-L
        (6, 0x97) => true, // Alder Lake
        (6, 0x9A) => true, // Alder Lake-L
        (6, 0xBE) => true, // Atom Gracemont
        (6, 0xB7) => true, // Raptor Lake
        (6, 0xBA) => true, // Raptor Lake-P
        (6, 0xAC) => true, // Meteor Lake
        (6, 0xAA) => true, // Meteor Lake-L
        (6, 0xCC) => true, // Panther Lake-L
        (6, 0xD5) => true, // Wildcat Lake-L
        (6, 0xC5) => true, // Arrow Lake-H
        (6, 0xB5) => true, // Arrow Lake-U
        (18, 0x01) => true, // Nova Lake
        (18, 0x03) => true, // Nova Lake-L
        _ => false,
    };

    public static CpuMaxWattsInfo ForIntel(IntelPowerReadings r)
    {
        float factory = Math.Max(r.SustainedWatts, r.BurstWatts);
        float? factoryCap = factory is > 0 and < UnlimitedWatts ? factory * FactoryMultiplier : null;
        float? tdp = r.TdpWatts is > 0 ? r.TdpWatts : null;
        float floor = r.MinWatts + 1f;

        string? infoWhy = RejectionReason(r.InfoMaxWatts, r.InfoError, tdp, "puissance maximale (registre 0x614)");
        string? pl4Why = r.Pl4Exposed
            ? RejectionReason(r.Pl4Watts, r.Pl4Error, tdp, "PL4 (registre 0x601)")
            : "PL4 : cette génération de processeur ne l'expose pas dans un registre lisible";

        string raw = DescribeRaw(r);

        float? hardware = null;
        CpuMaxWattsSource hardwareSource = CpuMaxWattsSource.ProcessorMaxPower;
        if (infoWhy is null)
        {
            hardware = r.InfoMaxWatts;
        }
        else if (pl4Why is null)
        {
            hardware = r.Pl4Watts;
            hardwareSource = CpuMaxWattsSource.PeakLimitPl4;
        }

        if (hardware is { } hardwareWatts)
        {
            string label = hardwareSource == CpuMaxWattsSource.ProcessorMaxPower
                ? "puissance maximale publiée par le processeur"
                : "limite de crête PL4 du processeur";

            // Comme avant : on ne dépasse pas 1,5 fois ce que le BIOS a posé, même si le processeur en annonce plus.
            if (factoryCap is { } cap && cap < hardwareWatts)
            {
                return new CpuMaxWattsInfo(
                    Math.Max(floor, cap), CpuMaxWattsSource.FactoryLimit,
                    $"Limite du BIOS × 1,5, plus basse que la {label} ({hardwareWatts:0} W).",
                    IsExperimental: true, raw);
            }

            string detail = hardwareSource == CpuMaxWattsSource.ProcessorMaxPower
                ? "Puissance maximale publiée par le processeur (registre 0x614)."
                : $"Limite de crête PL4 lue dans le processeur (registre 0x601) ; sa puissance maximale n'est pas exploitable ({infoWhy}).";

            return new CpuMaxWattsInfo(Math.Max(floor, hardwareWatts), hardwareSource, detail, IsExperimental: true, raw);
        }

        string why = $"{infoWhy} ; {pl4Why}";

        if (factoryCap is { } fallback)
        {
            return new CpuMaxWattsInfo(
                Math.Max(floor, fallback), CpuMaxWattsSource.FactoryLimit,
                $"Repli : limite du BIOS ({factory:0} W) × 1,5, faute de puissance maximale exploitable dans le processeur ({why}).",
                IsExperimental: false, raw);
        }

        return new CpuMaxWattsInfo(
            Math.Max(floor, SafetyCapWatts), CpuMaxWattsSource.SafetyCap,
            $"Repli : plafond fixe de {SafetyCapWatts:0} W. Le BIOS ne fixe pas de limite ({factory:0} W) et le processeur ne donne pas de puissance maximale exploitable ({why}).",
            IsExperimental: false, raw);
    }

    /// <summary>AMD : la SMU ne donne, par le pilote, ni la puissance maximale ni la puissance de base du
    /// processeur — seulement les limites en place. Le maximum reste donc un multiple de la limite lue.</summary>
    public static CpuMaxWattsInfo ForAmd(float sustainedWatts, float burstWatts, float minWatts)
    {
        float factory = Math.Max(sustainedWatts, burstWatts);

        return new CpuMaxWattsInfo(
            Math.Max(minWatts + 1f, factory * FactoryMultiplier), CpuMaxWattsSource.FactoryLimit,
            $"Repli : limite lue dans la table SMU ({factory:0} W) × 1,5. Le pilote ne donne accès ni à la puissance maximale ni à la puissance de base des processeurs AMD.",
            IsExperimental: false,
            $"limite soutenue {Format(sustainedWatts)} · limite de pointe {Format(burstWatts)} (table SMU)");
    }

    /// <summary>Pourquoi une valeur lue n'est pas retenue comme puissance maximale, ou null si elle l'est.</summary>
    private static string? RejectionReason(float? watts, string? readError, float? tdp, string what)
    {
        if (readError is not null) return $"{what} : lecture refusée ({readError})";
        if (watts is not > 0) return $"{what} : registre vide";
        if (watts >= UnlimitedWatts) return $"{what} : {watts:0} W, valeur « sans limite » et non une puissance";
        if (tdp is { } t && watts < t) return $"{what} : {watts:0} W, sous la puissance de base ({t:0} W)";
        return null;
    }

    private static string DescribeRaw(IntelPowerReadings r) => string.Join(" · ",
        $"PL1 {Format(r.SustainedWatts)}",
        $"PL2 {Format(r.BurstWatts)}",
        $"base (0x614) {Format(r.TdpWatts)}",
        $"max (0x614) {Format(r.InfoMaxWatts)}",
        $"PL4 (0x601) {(r.Pl4Exposed ? Format(r.Pl4Watts) : "non exposé par cette génération")}");

    /// <summary>« 125 W », ou « illisible » : un zéro n'est jamais confondu avec une valeur absente. Précision
    /// invariante, au huitième de watt du registre, pour qu'un rapport se lise pareil sur tous les PC.</summary>
    private static string Format(float? watts)
        => watts is { } w ? w.ToString("0.###", CultureInfo.InvariantCulture) + " W" : "illisible";
}
