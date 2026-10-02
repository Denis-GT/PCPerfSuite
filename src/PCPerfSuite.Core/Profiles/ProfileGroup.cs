using System.Text.Json;
using System.Text.Json.Serialization;
using PCPerfSuite.Core.Hardware;
using PCPerfSuite.Core.Hardware.Cpu;
using PCPerfSuite.Core.Hardware.Fans;
using PCPerfSuite.Core.Hardware.Gpu;

namespace PCPerfSuite.Core.Profiles;

/// <summary>
/// Un groupe de profils : les réglages du processeur, de la carte graphique et de la ventilation, appliqués d'un clic
/// (page Profils › Groupes). Les groupes générés par la bascule automatique (#9) en sont aussi, et restent réglables à
/// la main.
///
/// Chaque dimension est une copie intégrée, jamais une référence par nom à un profil d'onglet : ceux-ci se renomment,
/// ou sont remplacés à nom égal. Une dimension null veut dire « ne pas toucher » ; « remettre d'origine » est une
/// valeur à part (<see cref="ProfilePartKinds.Origin"/>), jamais un profil à zéro.
///
/// Enregistré dans settings.json, et volontairement tolérant : tout est en chaînes (aucun enum ajouté, aucun membre
/// obligatoire, une exception de lecture remettant tout le fichier à zéro), et ce qu'une version plus récente a écrit
/// est conservé tel quel (<see cref="ExtensionData"/>). Les enums des configurations intégrées (FanControlMode,
/// GpuVoltageUnit…) partent en nombres, comme dans les onglets.
/// </summary>
public sealed class ProfileGroup
{
    public string Id { get; set; } = NewId();

    public string Name { get; set; } = "Groupe";

    /// <summary>Usage visé (<see cref="ProfileGroupUsage"/>), null pour un groupe fait à la main sans usage.</summary>
    public string? Usage { get; set; }

    /// <summary><see cref="ProfileGroupOrigin.Manual"/> ou <see cref="ProfileGroupOrigin.Generated"/>.</summary>
    public string Origin { get; set; } = ProfileGroupOrigin.Manual;

    /// <summary>Un groupe généré modifié à la main : la bascule automatique (#9) ne le régénère jamais.</summary>
    public bool EditedByUser { get; set; }

    /// <summary>Augmente à chaque modification : l'état actif sait ainsi si le groupe a changé depuis son application.</summary>
    public int Revision { get; set; }

    public DateTimeOffset? CreatedUtc { get; set; }

    public DateTimeOffset? UpdatedUtc { get; set; }

    public ProfileGroupCpuPart? Cpu { get; set; }

    public ProfileGroupGpuPart? Gpu { get; set; }

    public ProfileGroupFansPart? Fans { get; set; }

    /// <summary>Ce qu'une version plus récente a écrit (éclairage #24, préférence GPU #22…), relu et réécrit tel quel.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    public static string NewId() => Guid.NewGuid().ToString("D");

    [JsonIgnore]
    public bool IsGenerated => string.Equals(Origin, ProfileGroupOrigin.Generated, StringComparison.Ordinal);

    [JsonIgnore]
    public bool IsEmpty => Cpu is null && Gpu is null && Fans is null;

    /// <summary>Copie profonde, extensions comprises.</summary>
    public ProfileGroup Clone() => ProfileGroupJson.Clone(this);
}

/// <summary>Partie processeur : réglages du plan d'alimentation (permanents) et limites en watts (volatiles).</summary>
public sealed class ProfileGroupCpuPart
{
    /// <summary><see cref="ProfilePartKinds.Values"/> ou <see cref="ProfilePartKinds.Origin"/> ; absent avec des valeurs :
    /// des valeurs.</summary>
    public string? Kind { get; set; } = ProfilePartKinds.Values;

    public CpuProfile? Values { get; set; }

    /// <summary>Processeur sur lequel les valeurs ont été relevées : les watts ne sont posés que sur le même.</summary>
    public CpuIdentity? CapturedOn { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    [JsonIgnore]
    public ProfilePartKind ParsedKind => ProfilePartKinds.Parse(Kind, Values is not null);
}

/// <summary>Partie carte graphique : décalages, limites et tension.</summary>
public sealed class ProfileGroupGpuPart
{
    public string? Kind { get; set; } = ProfilePartKinds.Values;

    public GpuOverclockProfile? Values { get; set; }

    /// <summary>Carte sur laquelle les valeurs ont été relevées : elles ne sont posées que sur la même
    /// (<see cref="GpuIdentity.IsSameCard"/>).</summary>
    public GpuIdentity? CapturedOn { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    [JsonIgnore]
    public ProfilePartKind ParsedKind => ProfilePartKinds.Parse(Kind, Values is not null);
}

/// <summary>Partie ventilation : les courbes, rapprochées des ventilateurs de ce PC à l'application
/// (<see cref="FanProfileMatcher"/>).</summary>
public sealed class ProfileGroupFansPart
{
    public string? Kind { get; set; } = ProfilePartKinds.Values;

    public FanProfile? Values { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    [JsonIgnore]
    public ProfilePartKind ParsedKind => ProfilePartKinds.Parse(Kind, Values is not null);
}

/// <summary>Ce que veut une partie, lu depuis son <c>Kind</c> en chaîne. En mémoire seulement : jamais enregistré.</summary>
public enum ProfilePartKind
{
    /// <summary>Poser les valeurs de la partie.</summary>
    Values,

    /// <summary>Remettre d'origine : OC GPU retiré, watts d'usine, plan rendu à son origine, ventilateurs au BIOS.</summary>
    Origin,

    /// <summary>« valeurs » sans valeurs (fichier édité à la main) : rien à poser.</summary>
    Empty,

    /// <summary>Écrit par une version plus récente : ignoré, avec la raison.</summary>
    Unknown,
}

public static class ProfilePartKinds
{
    public const string Values = "valeurs";
    public const string Origin = "origine";

    public static ProfilePartKind Parse(string? kind, bool hasValues)
    {
        if (string.IsNullOrWhiteSpace(kind)) return hasValues ? ProfilePartKind.Values : ProfilePartKind.Empty;
        if (string.Equals(kind.Trim(), Values, StringComparison.OrdinalIgnoreCase))
            return hasValues ? ProfilePartKind.Values : ProfilePartKind.Empty;
        if (string.Equals(kind.Trim(), Origin, StringComparison.OrdinalIgnoreCase)) return ProfilePartKind.Origin;
        return ProfilePartKind.Unknown;
    }
}

/// <summary>Usages reconnus, en chaînes : ceux que la bascule automatique (#9) génère.</summary>
public static class ProfileGroupUsage
{
    public const string Office = "bureautique";
    public const string LightGaming = "gaming-leger";
    public const string HeavyGaming = "gaming-intensif";

    public static IReadOnlyList<string> All { get; } = [Office, LightGaming, HeavyGaming];

    /// <summary>Libellé affiché ; un usage inconnu (version plus récente) s'affiche tel quel.</summary>
    public static string? Label(string? usage) => usage switch
    {
        null or "" => null,
        Office => "Bureautique",
        LightGaming => "Jeu léger",
        HeavyGaming => "Jeu exigeant",
        _ => usage,
    };
}

public static class ProfileGroupOrigin
{
    public const string Manual = "manuel";
    public const string Generated = "genere";
}

/// <summary>Copies profondes par aller-retour JSON, avec les options du fichier de réglages : ce qui passe par ici
/// ressort comme il serait relu de settings.json, extensions comprises.</summary>
public static class ProfileGroupJson
{
    public static T Clone<T>(T value) where T : class
        => JsonSerializer.Deserialize<T>(JsonSerializer.SerializeToUtf8Bytes(value))
           ?? throw new InvalidOperationException("Copie impossible.");

    /// <summary>Copie de la configuration d'un ventilateur. <see cref="FanCurveConfig"/> a déjà sa copie, sans JSON.</summary>
    public static FanProfile CloneFanProfile(FanProfile profile) => new()
    {
        Name = profile.Name,
        Fans = (profile.Fans ?? []).Where(f => f is not null).Select(f => f.Clone()).ToList(),
        FanNames = new Dictionary<string, string>(profile.FanNames ?? new Dictionary<string, string>()),
    };
}
