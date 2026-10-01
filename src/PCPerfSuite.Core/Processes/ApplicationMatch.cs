using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PCPerfSuite.Core.Processes;

/// <summary>
/// Désigne une application par son exécutable, pour une règle : bascule automatique de profils (#9), demain limites
/// par processus (#21) et préférence de GPU (#22). Par défaut, le <b>chemin complet normalisé</b> : deux programmes
/// nommés « game.exe » dans deux dossiers sont deux applications. Le <b>nom d'exécutable seul</b> n'est utilisé que si
/// l'utilisateur le choisit (application qui change de dossier à chaque mise à jour). L'<b>éditeur</b> (signature
/// Authenticode validée) est facultatif : exigé, un exécutable remplacé par un autre au même chemin ne correspond plus.
///
/// Enregistré dans settings.json, et tolérant : tout en chaînes, aucun membre obligatoire, un mode inconnu (version plus
/// récente) ne correspond à rien, et ce qu'une version plus récente a écrit est conservé (<see cref="ExtensionData"/>).
/// Un chemin d'exécutable est une donnée personnelle : jamais dans le diagnostic ni dans le journal de session.
/// </summary>
public sealed class ApplicationMatch
{
    /// <summary><see cref="ApplicationMatchModes.Path"/> (défaut) ou <see cref="ApplicationMatchModes.Name"/>.</summary>
    public string? Mode { get; set; } = ApplicationMatchModes.Path;

    /// <summary>Chemin complet normalisé (<see cref="ApplicationPaths.Normalize"/>), tel que relevé.</summary>
    public string? Path { get; set; }

    /// <summary>Nom de l'exécutable avec son extension (« game.exe »), comparé en mode « nom ».</summary>
    public string? FileName { get; set; }

    /// <summary>Éditeur exigé (organisation du certificat validé), null : non vérifié.</summary>
    public string? Publisher { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    /// <summary>Une règle pour cet exécutable : par chemin, ou par nom seul si <paramref name="nameOnly"/>. Null si le
    /// chemin n'est pas un chemin complet utilisable.</summary>
    public static ApplicationMatch? For(string? path, bool nameOnly = false, string? publisher = null)
    {
        if (ApplicationPaths.Normalize(path) is not { } normalized) return null;
        return new ApplicationMatch
        {
            Mode = nameOnly ? ApplicationMatchModes.Name : ApplicationMatchModes.Path,
            Path = normalized,
            FileName = ApplicationPaths.FileNameOf(normalized),
            Publisher = string.IsNullOrWhiteSpace(publisher) ? null : publisher.Trim(),
        };
    }

    [JsonIgnore]
    public ApplicationMatchMode ParsedMode => ApplicationMatchModes.Parse(Mode);

    /// <summary>Vrai si la règle peut correspondre à quelque chose : mode connu et chemin ou nom renseigné.</summary>
    [JsonIgnore]
    public bool IsUsable => ParsedMode switch
    {
        ApplicationMatchMode.Path => ApplicationPaths.Normalize(Path) is not null,
        ApplicationMatchMode.Name => !string.IsNullOrWhiteSpace(EffectiveFileName),
        _ => false,
    };

    [JsonIgnore]
    public bool RequiresPublisher => !string.IsNullOrWhiteSpace(Publisher);

    /// <summary>Le nom comparé en mode « nom » : <see cref="FileName"/>, ou celui du chemin s'il manque.</summary>
    private string? EffectiveFileName
        => !string.IsNullOrWhiteSpace(FileName) ? FileName.Trim()
            : ApplicationPaths.Normalize(Path) is { } path ? ApplicationPaths.FileNameOf(path) : null;

    /// <summary>
    /// Vrai si l'exécutable <paramref name="path"/> correspond. <paramref name="publisherOf"/> donne l'éditeur validé d'un
    /// exécutable (null : non signé, signature invalide, ou pas encore vérifié) ; il n'est consulté que si la règle exige
    /// un éditeur et que le chemin ou le nom correspond déjà. Ne lève jamais.
    /// </summary>
    public bool Matches(string? path, Func<string, string?>? publisherOf = null)
    {
        try
        {
            if (ApplicationPaths.Normalize(path) is not { } candidate) return false;

            bool sameExecutable = ParsedMode switch
            {
                ApplicationMatchMode.Path => ApplicationPaths.Normalize(Path) is { } own && ApplicationPaths.SamePath(own, candidate),
                ApplicationMatchMode.Name => EffectiveFileName is { } name
                                             && string.Equals(name, ApplicationPaths.FileNameOf(candidate), StringComparison.OrdinalIgnoreCase),
                _ => false,
            };
            if (!sameExecutable) return false;
            if (!RequiresPublisher) return true;

            return publisherOf?.Invoke(candidate) is { } publisher
                   && string.Equals(publisher.Trim(), Publisher!.Trim(), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>« C:\Jeux\game.exe », « game.exe (dans tout dossier) », suivi de l'éditeur exigé.</summary>
    public string Describe()
    {
        string target = ParsedMode switch
        {
            ApplicationMatchMode.Path => Path ?? "chemin manquant",
            ApplicationMatchMode.Name => $"{EffectiveFileName ?? "nom manquant"} (dans tout dossier)",
            _ => $"règle d'une version plus récente de PCPerfSuite ({Mode})",
        };
        return RequiresPublisher ? $"{target} · éditeur « {Publisher!.Trim()} »" : target;
    }

    public ApplicationMatch Clone() => new()
    {
        Mode = Mode,
        Path = Path,
        FileName = FileName,
        Publisher = Publisher,
        ExtensionData = ExtensionData is null ? null : new Dictionary<string, JsonElement>(ExtensionData),
    };
}

/// <summary>Ce que compare une règle, lu depuis son <c>Mode</c> en chaîne. En mémoire seulement.</summary>
public enum ApplicationMatchMode
{
    Path,
    Name,

    /// <summary>Écrit par une version plus récente : ne correspond à rien.</summary>
    Unknown,
}

public static class ApplicationMatchModes
{
    public const string Path = "chemin";
    public const string Name = "nom";

    public static ApplicationMatchMode Parse(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode)) return ApplicationMatchMode.Path;
        if (string.Equals(mode.Trim(), Path, StringComparison.OrdinalIgnoreCase)) return ApplicationMatchMode.Path;
        if (string.Equals(mode.Trim(), Name, StringComparison.OrdinalIgnoreCase)) return ApplicationMatchMode.Name;
        return ApplicationMatchMode.Unknown;
    }
}

/// <summary>Chemins d'exécutables, en logique pure.</summary>
public static partial class ApplicationPaths
{
    /// <summary>
    /// Chemin complet comparable : guillemets et espaces retirés, « / » ramenés à « \ », préfixes « \\?\ » et
    /// « \\?\UNC\ » retirés, « . » et « .. » résolus, séparateur final retiré. Null pour un chemin vide, relatif ou
    /// invalide. La casse est gardée pour l'affichage ; la comparaison l'ignore (<see cref="SamePath"/>).
    /// </summary>
    public static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        string value = path.Trim().Trim('"').Trim().Replace('/', '\\');
        if (value.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) value = @"\\" + value[8..];
        else if (value.StartsWith(@"\\?\", StringComparison.Ordinal) || value.StartsWith(@"\??\", StringComparison.Ordinal)) value = value[4..];

        try
        {
            if (!System.IO.Path.IsPathFullyQualified(value)) return null;
            string full = System.IO.Path.GetFullPath(value);
            string trimmed = full.TrimEnd('\\');
            // « C:\ » seul n'est pas un exécutable : sans nom de fichier, rien à comparer.
            return trimmed.Length == 0 || trimmed.EndsWith(':') ? null : trimmed;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static bool SamePath(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>« game.exe » pour « C:\Jeux\game.exe ».</summary>
    public static string FileNameOf(string path) => System.IO.Path.GetFileName(path.TrimEnd('\\'));

    /// <summary>Nom affiché : le nom de fichier sans « .exe ».</summary>
    public static string DisplayName(string path)
    {
        string name = FileNameOf(path);
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    /// <summary>
    /// Vrai pour un exécutable dont le dossier change à chaque mise à jour : application du Microsoft Store
    /// (« WindowsApps\Editeur.Appli_1.2.3.0_x64__… »), dossier versionné (« app-1.0.9 », « 120.0.6099.71 »). Une règle
    /// par chemin ne survivrait pas à la mise à jour : la page propose alors le nom seul.
    /// </summary>
    public static bool SuggestsNameMode(string? path)
    {
        if (Normalize(path) is not { } normalized) return false;

        string[] segments = normalized.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        foreach (string segment in segments[..^1])
        {
            if (string.Equals(segment, "WindowsApps", StringComparison.OrdinalIgnoreCase)) return true;
            if (VersionedSegment().IsMatch(segment)) return true;
        }

        return false;
    }

    /// <summary>« app-1.0.9 », « v2.3 », « 120.0.6099.71 », ou un nom de paquet « Nom_1.2.3.0_x64__hash ».</summary>
    [GeneratedRegex(@"^(app-|v)?\d+(\.\d+){1,3}$|_\d+\.\d+\.\d+(\.\d+)?_", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VersionedSegment();
}
