namespace PCPerfSuite.Core.Hardware.Cpu;

/// <summary>
/// Identité d'un processeur : fabricant, famille et modèle CPUID, nom commercial. Sert à ne pas poser des limites en
/// watts pensées pour un autre processeur (groupes de profils, #8) : un 65 W raisonnable sur un i5 n'a pas de sens sur
/// un autre modèle, même de la même génération.
///
/// Tout est facultatif et en chaînes ou en nombres simples : l'identité est enregistrée dans settings.json, qu'une
/// valeur inattendue ne doit jamais rendre illisible.
/// </summary>
public sealed record CpuIdentity
{
    /// <summary>« Intel », « Amd », « Qualcomm » ou « Other » (nom de <see cref="CpuVendor"/>).</summary>
    public string? Vendor { get; init; }

    /// <summary>Famille CPUID affichée, null si inconnue (ARM, registre muet).</summary>
    public int? Family { get; init; }

    public int? Model { get; init; }

    /// <summary>Nom commercial lu dans le registre (« 13th Gen Intel(R) Core(TM) i5-13500T »), null s'il manque.</summary>
    public string? Name { get; init; }

    /// <summary>Le nom du processeur est connu : sans lui, <see cref="Matches"/> ne reconnaît jamais le même processeur.</summary>
    public bool HasName => IsKnownName(Name);

    public static CpuIdentity Of(CpuPlatform platform) => new()
    {
        Vendor = platform.Vendor.ToString(),
        Family = platform.Family == 0 ? null : platform.Family,
        Model = platform.Model == 0 ? null : platform.Model,
        Name = IsKnownName(platform.Name) ? platform.Name.Trim() : null,
    };

    /// <summary>
    /// Vrai seulement quand c'est sûrement le même processeur : même fabricant, nom connu des deux côtés et identique
    /// (casse et espaces ignorés), et famille et modèle identiques quand les deux les connaissent. Plus strict que
    /// <see cref="Gpu.GpuIdentity.Matches"/> : sans nom, deux processeurs d'une même génération ne se distinguent pas,
    /// et des watts ne se transposent pas d'un modèle à l'autre.
    /// </summary>
    public static bool Matches(CpuIdentity? saved, CpuIdentity? current)
    {
        if (saved is null || current is null) return false;
        if (saved.Vendor is not { Length: > 0 } savedVendor || current.Vendor is not { Length: > 0 } currentVendor
            || !string.Equals(savedVendor, currentVendor, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!IsKnownName(saved.Name) || !IsKnownName(current.Name)
            || !string.Equals(NormalizeName(saved.Name!), NormalizeName(current.Name!), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return SameWhenBothKnown(saved.Family, current.Family) && SameWhenBothKnown(saved.Model, current.Model);
    }

    /// <summary>Le nom en clair pour un message (« Intel Core i5-14600K »), ou ce qu'on en sait.</summary>
    public string Describe()
    {
        if (IsKnownName(Name)) return NormalizeName(Name!);
        string vendor = Vendor is { Length: > 0 } v ? v : "fabricant inconnu";
        return Family is { } family && Model is { } model ? $"{vendor} famille {family} modèle {model}" : $"processeur {vendor}";
    }

    private static bool IsKnownName(string? name)
        => !string.IsNullOrWhiteSpace(name) && !string.Equals(name.Trim(), CpuPlatformDetector.UnknownName, StringComparison.Ordinal);

    private static bool SameWhenBothKnown(int? a, int? b) => a is null || b is null || a == b;

    private static string NormalizeName(string name)
        => string.Join(' ', name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
