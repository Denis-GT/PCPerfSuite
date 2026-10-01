using System.Text.Json;
using System.Text.Json.Serialization;
using PCPerfSuite.Core.Hardware;
using PCPerfSuite.Core.Hardware.Cpu;
using PCPerfSuite.Core.Hardware.Fans;

namespace PCPerfSuite.Core.Profiles;

/// <summary>
/// Bloc « ProfileGroups » de settings.json : les groupes, le dernier appliqué, et ceux suspendus après un incident.
/// Écrit par la page Profils (ProfileGroupsViewModel) par AppSettingsStore.Update, et par une seule autre voie : le
/// gestionnaire de reprise au démarrage (<see cref="ProfileGroupRecoveryHandler"/>), qui ne touche qu'aux suspensions et
/// passe avant que la page n'existe.
/// </summary>
public sealed class ProfileGroupsSettings
{
    public List<ProfileGroup> Groups { get; set; } = new();

    /// <summary>Dernier groupe appliqué et ce que le matériel en a retenu, null si aucun.</summary>
    public ProfileGroupActiveState? Active { get; set; }

    /// <summary>Groupes suspendus après un incident (clé = <see cref="ProfileGroup.Id"/>) : ils ne sont plus réappliqués
    /// sans une confirmation.</summary>
    public Dictionary<string, ProfileGroupSuspension> Suspensions { get; set; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    /// <summary>
    /// Remet d'aplomb un bloc édité à la main ou écrit par une autre version : groupes nuls retirés, identifiant absent
    /// ou en double régénéré, nom vide remplacé, état actif et suspensions qui désignent un groupe disparu effacés.
    /// Renvoie vrai si quelque chose a changé. Ne lève jamais.
    /// </summary>
    public bool Normalize()
    {
        bool changed = false;
        if (Groups is null)
        {
            Groups = new List<ProfileGroup>();
            changed = true;
        }

        changed |= Groups.RemoveAll(g => g is null) > 0;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ProfileGroup group in Groups)
        {
            if (string.IsNullOrWhiteSpace(group.Id) || !seen.Add(group.Id))
            {
                group.Id = ProfileGroup.NewId();
                seen.Add(group.Id);
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(group.Name))
            {
                group.Name = "Groupe";
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(group.Origin))
            {
                group.Origin = ProfileGroupOrigin.Manual;
                changed = true;
            }
        }

        if (Active is not null && Find(Active.GroupId) is null)
        {
            Active = null;
            changed = true;
        }

        if (Suspensions is null)
        {
            Suspensions = new Dictionary<string, ProfileGroupSuspension>();
            changed = true;
        }

        foreach (string id in Suspensions.Keys.Where(id => Find(id) is null || Suspensions[id] is null).ToList())
        {
            Suspensions.Remove(id);
            changed = true;
        }

        return changed;
    }

    public ProfileGroup? Find(string? id)
        => string.IsNullOrEmpty(id) ? null : Groups.FirstOrDefault(g => string.Equals(g.Id, id, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Le dernier groupe appliqué, et ce que le matériel en a <b>retenu</b> à ce moment-là (relu après écriture, et non les
/// valeurs demandées, que le pilote rogne) : c'est à cet état qu'on compare l'état courant pour dire « conforme ».
/// Chaque partie ne contient que ce que le groupe a touché.
/// </summary>
public sealed class ProfileGroupActiveState
{
    public string GroupId { get; set; } = "";

    public DateTimeOffset SinceUtc { get; set; }

    /// <summary><see cref="ProfileGroup.Revision"/> au moment de l'application : le groupe a été modifié depuis s'il
    /// diffère.</summary>
    public int Revision { get; set; }

    /// <summary>L'application a fait de ces valeurs l'état de démarrage des onglets (option par défaut).</summary>
    public bool MadeStartupState { get; set; }

    /// <summary>Qui l'a appliqué : <see cref="ProfileGroupRequesters.Manual"/>, la bascule automatique…</summary>
    public string? RequesterId { get; set; }

    public CpuProfile? Cpu { get; set; }

    public GpuRetainedValues? Gpu { get; set; }

    /// <summary>Les limites relevées (OC GPU, watts) au moment de l'application.</summary>
    public bool GpuRaised { get; set; }

    public bool WattsRaised { get; set; }

    public FanProfile? Fans { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Réglages de la carte graphique tels que relus, chacun null quand il n'est pas concerné : contrairement à
/// <see cref="GpuOverclockProfile"/>, dont les décalages ne sont pas nullables, on sait ici ce que le groupe a touché.
/// La tension est dans l'unité de la carte.</summary>
public sealed class GpuRetainedValues
{
    public int? CoreOffsetMhz { get; set; }
    public int? MemoryOffsetMhz { get; set; }
    public float? PowerLimitPercent { get; set; }
    public int? TemperatureLimitC { get; set; }
    public int? Voltage { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Un groupe suspendu : un incident a suivi son application (arrêt anormal, écran bleu, TDR…).</summary>
public sealed class ProfileGroupSuspension
{
    public DateTimeOffset SinceUtc { get; set; }

    /// <summary>Ce qui est arrivé, en clair (qualification du journal Système).</summary>
    public string Cause { get; set; } = "";

    /// <summary>« Appliquer au démarrage » a été décoché dans Processeur ou GPU à cause de cet incident.</summary>
    public bool CpuStartupUnchecked { get; set; }

    public bool GpuStartupUnchecked { get; set; }

    /// <summary>L'avertissement a été vu dans la page : il n'est plus mis en avant, la suspension reste.</summary>
    public bool Acknowledged { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Demandeurs connus d'une application de groupe : le bail et le journal les nomment.</summary>
public static class ProfileGroupRequesters
{
    /// <summary>Un clic dans la page Profils.</summary>
    public const string Manual = "manuel";

    /// <summary>Les commandes « Appliquer » des onglets Processeur, GPU et Ventilateurs.</summary>
    public const string Tab = "onglet";
}
