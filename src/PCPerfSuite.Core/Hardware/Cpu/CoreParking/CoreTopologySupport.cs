using Microsoft.Win32;

namespace PCPerfSuite.Core.Hardware.Cpu.CoreParking;

/// <summary>
/// Le pilote AMD 3D V-Cache Performance Optimizer (service « amd3dvcache », livré avec le pilote chipset AMD) : sur un
/// Ryzen X3D à deux CCD, c'est lui qui parque le CCD sans V-Cache en jeu. Sa préférence (clé Preferences) a un format
/// non documenté : PCPerfSuite ne la lit ni ne l'écrit.
/// </summary>
public static class AmdVCacheDriver
{
    private const string ServiceKey = @"SYSTEM\CurrentControlSet\Services\amd3dvcache";

    /// <summary>Service installé. Lu dans le registre, sans droits particuliers ; false si la lecture échoue.</summary>
    public static bool IsInstalled()
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(ServiceKey);
            return key is not null;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>Ce qui n'a pas été vérifié sur une vraie machine dans la lecture de cette topologie (règle 6).</summary>
/// <param name="ExperimentalReasons">Vide quand tout est vérifié ; sinon, une raison par point non vérifié.</param>
public sealed record CoreTopologyAssessment(IReadOnlyList<string> ExperimentalReasons)
{
    public bool IsVerified => ExperimentalReasons.Count == 0;
}

/// <summary>
/// Vérifié sur une vraie machine : les processeurs à une seule classe de cœurs, et les hybrides Intel de 12e à 14e
/// génération (Alder Lake, Raptor Lake : i5-13500T de Denis, 6 P-cores et 8 E-cores). Le reste est expérimental :
/// classes des Snapdragon X, des Meteor Lake et Lunar Lake (cœurs LP-E), des Strix Point (Zen 5 et Zen 5c), et repérage
/// du CCD avec 3D V-Cache. Logique pure.
/// </summary>
public static class CoreTopologySupport
{
    /// <summary>Modèles CPUID (famille 6) des hybrides Intel vérifiés : Alder Lake S et P, Raptor Lake S, P et
    /// variante S à puce Alder Lake.</summary>
    private static readonly HashSet<int> VerifiedIntelHybridModels = [0x97, 0x9A, 0xB7, 0xBA, 0xBF];

    /// <summary>Un X3D à deux CCD : deux groupes de cache de même forme, dont l'un a nettement plus de L3. Le pilote AMD
    /// 3D V-Cache ne sert que de repli quand Windows ne donne pas les tailles de L3 : il peut être installé avec le pilote
    /// chipset sur un Ryzen à deux CCD sans V-Cache.</summary>
    public static bool IsDualCcdX3D(CpuVendor vendor, CpuTopology topology, bool vcacheDriverInstalled)
        => vendor == CpuVendor.Amd && topology.Clusters.Count == 2 && topology.HasUniformClusters
           && (topology.HasMixedL3Sizes || (vcacheDriverInstalled && topology.Clusters.Any(c => c.L3Bytes is null)));

    public static CoreTopologyAssessment Assess(CpuVendor vendor, int family, int model, CpuTopology topology)
    {
        var reasons = new List<string>();
        int classes = topology.EfficiencyClasses.Count;

        if (vendor == CpuVendor.Qualcomm)
        {
            reasons.Add("classes de cœurs des Snapdragon X pas encore vérifiées sur une vraie machine");
        }
        else if (classes > 2)
        {
            reasons.Add($"{classes} classes de cœurs (cœurs LP-E des Meteor Lake ?) : étiquettes pas encore vérifiées sur une vraie machine");
        }
        else if (classes == 2 && vendor == CpuVendor.Amd)
        {
            reasons.Add("cœurs Zen 5 et Zen 5c (Strix Point) : classes pas encore vérifiées sur une vraie machine");
        }
        else if (classes == 2 && vendor == CpuVendor.Intel && !(family == 6 && VerifiedIntelHybridModels.Contains(model)))
        {
            reasons.Add("hybride Intel plus récent que la 14e génération (Meteor Lake, Lunar Lake, Arrow Lake…) : étiquettes P/E pas encore vérifiées sur une vraie machine");
        }
        else if (classes == 2 && vendor == CpuVendor.Other)
        {
            reasons.Add("fabricant non reconnu : classes de cœurs pas encore vérifiées");
        }

        if (topology.HasMixedL3Sizes)
        {
            reasons.Add("CCD avec 3D V-Cache repéré d'après la taille des caches L3 : pas encore vérifié sur une vraie machine");
        }

        return new CoreTopologyAssessment(reasons);
    }
}
