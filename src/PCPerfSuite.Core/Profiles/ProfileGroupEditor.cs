using PCPerfSuite.Core.Hardware;
using PCPerfSuite.Core.Hardware.Cpu;
using PCPerfSuite.Core.Hardware.Fans;
using PCPerfSuite.Core.Hardware.Gpu;

namespace PCPerfSuite.Core.Profiles;

/// <summary>
/// Modifications d'un groupe, en logique pure. Toute modification de ce qu'il règle (usage, dimension, mise à jour
/// depuis l'état actuel) fait monter <see cref="ProfileGroup.Revision"/> et, sur un groupe généré, passe
/// <see cref="ProfileGroup.EditedByUser"/> à vrai : la bascule automatique (#9) ne régénère jamais un groupe réglé à la
/// main (F18). Renommer n'en est pas une : la bascule reposerait sinon le groupe entier pour un simple nom.
/// </summary>
public static class ProfileGroupEditor
{
    public const int MaxNameLength = 40;

    /// <summary>Un groupe neuf, fait à la main.</summary>
    public static ProfileGroup Create(string name, string? usage, DateTimeOffset now) => new()
    {
        Name = name,
        Usage = usage,
        Origin = ProfileGroupOrigin.Manual,
        CreatedUtc = now,
        UpdatedUtc = now,
    };

    /// <summary>Le nom retenu, ou null s'il est vide ; tronqué à <see cref="MaxNameLength"/>.</summary>
    public static string? CleanName(string? name)
    {
        string trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0) return null;
        return trimmed.Length > MaxNameLength ? trimmed[..MaxNameLength].TrimEnd() : trimmed;
    }

    /// <summary>« Groupe 3 », ou <paramref name="wanted"/> suivi d'un numéro s'il est déjà pris (casse ignorée).</summary>
    public static string UniqueName(string wanted, IEnumerable<string> taken)
    {
        var names = new HashSet<string>(taken, StringComparer.CurrentCultureIgnoreCase);
        if (!names.Contains(wanted)) return wanted;

        for (int i = 2; ; i++)
        {
            string candidate = $"{wanted} {i}";
            if (!names.Contains(candidate)) return candidate;
        }
    }

    public static void Rename(ProfileGroup group, string name, DateTimeOffset now)
    {
        if (string.Equals(group.Name, name, StringComparison.Ordinal)) return;

        // Un nom n'est pas un réglage : ni nouvelle révision (la bascule reposerait le groupe, Régénérer l'écarterait),
        // ni date de modification (elle départage les groupes d'un même usage).
        group.Name = name;
        group.UpdatedUtc ??= now;
    }

    public static void SetUsage(ProfileGroup group, string? usage, DateTimeOffset now)
    {
        if (string.Equals(group.Usage, usage, StringComparison.Ordinal)) return;
        group.Usage = usage;
        Touch(group, now);
    }

    public static void SetCpu(ProfileGroup group, ProfileGroupCpuPart? part, DateTimeOffset now)
    {
        group.Cpu = part;
        Touch(group, now);
    }

    public static void SetGpu(ProfileGroup group, ProfileGroupGpuPart? part, DateTimeOffset now)
    {
        group.Gpu = part;
        Touch(group, now);
    }

    public static void SetFans(ProfileGroup group, ProfileGroupFansPart? part, DateTimeOffset now)
    {
        group.Fans = part;
        Touch(group, now);
    }

    /// <summary>Une copie faite à la main : nouvel identifiant, sans usage (deux groupes pour un même usage
    /// troubleraient la bascule automatique), et jamais « générée ».</summary>
    public static ProfileGroup Duplicate(ProfileGroup source, IEnumerable<string> takenNames, DateTimeOffset now)
    {
        ProfileGroup copy = source.Clone();
        copy.Id = ProfileGroup.NewId();
        copy.Name = UniqueName(CleanName($"{source.Name} (copie)") ?? "Groupe", takenNames);
        copy.Origin = ProfileGroupOrigin.Manual;
        copy.Usage = null;
        copy.EditedByUser = false;
        copy.Revision = 0;
        copy.CreatedUtc = now;
        copy.UpdatedUtc = now;
        return copy;
    }

    /// <summary>Retire un groupe, et avec lui son état actif, son état de démarrage et sa suspension.</summary>
    public static bool Delete(ProfileGroupsSettings settings, string groupId)
    {
        int removed = settings.Groups.RemoveAll(g => string.Equals(g.Id, groupId, StringComparison.OrdinalIgnoreCase));
        if (string.Equals(settings.Active?.GroupId, groupId, StringComparison.OrdinalIgnoreCase)) settings.Active = null;
        if (string.Equals(settings.StartupState?.GroupId, groupId, StringComparison.OrdinalIgnoreCase)) settings.StartupState = null;
        settings.Suspensions.Remove(groupId);
        return removed > 0;
    }

    // ---- Parties, depuis l'état actuel ou un profil d'onglet ----

    public static ProfileGroupCpuPart CpuValues(CpuProfile profile, CpuIdentity capturedOn) => new()
    {
        Kind = ProfilePartKinds.Values,
        Values = ProfileGroupJson.Clone(profile),
        CapturedOn = capturedOn,
    };

    public static ProfileGroupGpuPart GpuValues(GpuOverclockProfile profile, GpuIdentity? capturedOn) => new()
    {
        Kind = ProfilePartKinds.Values,
        Values = ProfileGroupJson.Clone(profile),
        CapturedOn = capturedOn,
    };

    public static ProfileGroupFansPart FanValues(FanProfile profile) => new()
    {
        Kind = ProfilePartKinds.Values,
        Values = ProfileGroupJson.CloneFanProfile(profile),
    };

    public static ProfileGroupCpuPart CpuOrigin() => new() { Kind = ProfilePartKinds.Origin };

    public static ProfileGroupGpuPart GpuOrigin() => new() { Kind = ProfilePartKinds.Origin };

    public static ProfileGroupFansPart FanOrigin() => new() { Kind = ProfilePartKinds.Origin };

    /// <summary>Ce qu'il faut dire d'un profil d'onglet importé : il n'a pas d'identité, on suppose qu'il a été fait sur
    /// ce matériel-ci. Null quand il n'y a rien à en dire (ventilation, ou profil sans watts).</summary>
    public static string? ImportNote(ProfileDimension dimension, bool hasHardwareValues) => (dimension, hasHardwareValues) switch
    {
        (ProfileDimension.Cpu, true) => "Profil d'onglet sans identité : les watts sont rattachés au processeur actuel.",
        (ProfileDimension.Gpu, true) => "Profil d'onglet sans identité : les réglages sont rattachés à la carte actuelle.",
        _ => null,
    };

    /// <summary>Une modification : la révision monte, et un groupe généré devient « modifié à la main ».</summary>
    public static void Touch(ProfileGroup group, DateTimeOffset now)
    {
        group.Revision++;
        group.UpdatedUtc = now;
        if (group.IsGenerated) group.EditedByUser = true;
    }
}
