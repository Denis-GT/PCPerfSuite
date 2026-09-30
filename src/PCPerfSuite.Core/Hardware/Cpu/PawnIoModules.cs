using System.Security.Cryptography;

namespace PCPerfSuite.Core.Hardware.Cpu;

/// <summary>D'où vient un module PawnIO chargé par l'app.</summary>
public enum PawnIoModuleSource
{
    /// <summary>Fichier livré à côté de l'exe (dossier <see cref="ShippedPawnIoModules.FolderName"/>).</summary>
    PCPerfSuite,

    /// <summary>Ressource embarquée dans LibreHardwareMonitorLib.</summary>
    LibreHardwareMonitor,
}

/// <summary>
/// Ce qu'on sait d'un module PawnIO : sa provenance, sa version, les fonctions qu'il expose et son empreinte.
/// <paramref name="IsOfficialCopy"/> ne vaut que pour un module livré par PCPerfSuite : faux quand le fichier a été
/// remplacé (ce qui reste permis, voir <see cref="ShippedPawnIoModules"/>), null pour un module de LHM.
/// <paramref name="Note"/> dit pourquoi un meilleur candidat n'a pas été retenu (module livré refusé par PawnIO…).
/// </summary>
public sealed record PawnIoModuleInfo(
    string Name,
    PawnIoModuleSource Source,
    string? Version,
    IReadOnlyList<string> Functions,
    string Sha256,
    bool? IsOfficialCopy,
    string? Note = null)
{
    public bool Supports(string function) => Functions.Contains(function, StringComparer.Ordinal);

    /// <summary>Provenance lisible, reprise telle quelle dans les messages et le diagnostic.</summary>
    public string SourceLabel => Source switch
    {
        PawnIoModuleSource.PCPerfSuite => IsOfficialCopy == false
            ? $"fichier {ShippedPawnIoModules.RelativePath(Name)}, remplacé (ce n'est plus celui de PawnIO.Modules {Version})"
            : $"livré avec PCPerfSuite, PawnIO.Modules {Version}",
        _ => $"LibreHardwareMonitorLib {Version ?? "?"}",
    };
}

/// <summary>Un module lisible, pas encore chargé : ses informations et son contenu.</summary>
public sealed record PawnIoModuleCandidate(PawnIoModuleInfo Info, byte[] Blob);

/// <summary>
/// Fonctions exposées par un module PawnIO, relevées dans son contenu compilé. Un module ne publie pas la liste de ses
/// points d'entrée ; ils y figurent en clair, sous forme de chaînes ASCII « ioctl_… ». C'est ainsi qu'a été constaté
/// que l'IntelMSR de LibreHardwareMonitorLib 0.9.6 ne sait que lire les MSR.
/// </summary>
public static class PawnIoModuleFunctions
{
    private static ReadOnlySpan<byte> Prefix => "ioctl_"u8;

    /// <summary>Chaînes <c>ioctl_[a-z0-9_]+</c> du module, sans doublon, dans l'ordre où elles apparaissent. Une
    /// chaîne collée à une lettre ou un chiffre qui la précède (« xioctl_a ») n'en est pas une.</summary>
    public static IReadOnlyList<string> Parse(ReadOnlySpan<byte> blob)
    {
        var found = new List<string>();
        int index = 0;

        while (index < blob.Length)
        {
            int start = blob[index..].IndexOf(Prefix);
            if (start < 0) break;
            start += index;

            int end = start + Prefix.Length;
            while (end < blob.Length && IsNameByte(blob[end])) end++;

            bool bounded = start == 0 || !IsNameByte(blob[start - 1]);
            if (bounded && end > start + Prefix.Length)
            {
                string name = System.Text.Encoding.ASCII.GetString(blob[start..end]);
                if (!found.Contains(name)) found.Add(name);
            }

            index = Math.Max(end, start + 1);
        }

        return found;
    }

    private static bool IsNameByte(byte b) => b is >= (byte)'a' and <= (byte)'z' or >= (byte)'0' and <= (byte)'9' or (byte)'_';

    /// <summary>Empreinte SHA-256 en hexadécimal minuscule.</summary>
    public static string Sha256(ReadOnlySpan<byte> blob) => Convert.ToHexString(SHA256.HashData(blob)).ToLowerInvariant();
}

/// <summary>Ordre dans lequel essayer les modules d'un même nom, isolé ici pour être testé sans pilote.</summary>
public static class PawnIoModuleChoice
{
    /// <summary>Le module livré par PCPerfSuite d'abord, celui de LHM ensuite, sauf pour un module à qui manque la
    /// fonction voulue (<paramref name="preferredFunction"/>) : il passe après ceux qui l'ont. L'ordre de départ est
    /// gardé à égalité.</summary>
    public static IReadOnlyList<PawnIoModuleCandidate> Order(
        IEnumerable<PawnIoModuleCandidate> candidates, string? preferredFunction)
        => candidates
            .Select((candidate, position) => (candidate, position))
            .OrderBy(c => preferredFunction is not null && !c.candidate.Info.Supports(preferredFunction) ? 1 : 0)
            .ThenBy(c => c.candidate.Info.Source == PawnIoModuleSource.PCPerfSuite ? 0 : 1)
            .ThenBy(c => c.position)
            .Select(c => c.candidate)
            .ToList();
}

/// <summary>
/// Modules PawnIO livrés par PCPerfSuite, à côté de l'exe, en plus de ceux de LibreHardwareMonitorLib. Seul IntelMSR
/// l'est : celui de LHM 0.9.6 ne sait pas écrire les MSR, donc pas les limites PL1/PL2. RyzenSMU et AMDFamily17 de LHM
/// suffisent, et ceux que LHM charge pour ses propres capteurs ne passent pas par ici.
///
/// Les fichiers sont ceux de la release officielle (signés par namazso, LGPL-2.1) et restent des fichiers à part, que
/// l'utilisateur peut remplacer (décision D1). L'empreinte n'est donc vérifiée que par un test ; au lancement, une
/// empreinte différente est seulement signalée. PawnIO ne charge de toute façon que des modules signés.
/// </summary>
public static class ShippedPawnIoModules
{
    /// <summary>Release de PawnIO.Modules d'où viennent les fichiers (30/08/2026).</summary>
    public const string Release = "0.2.11";

    public const string SourceUrl = "https://github.com/namazso/PawnIO.Modules/tree/0.2.11";

    public const string FolderName = "PawnIO";

    /// <summary>Empreinte de chaque module livré, tel qu'extrait de release_0_2_11.zip.</summary>
    public static IReadOnlyDictionary<string, string> ExpectedSha256 { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["IntelMSR"] = "d6ed85d65ab17a22f813ef98207d6d537155ee2ded5976a21cb48413c9b92e5f",
    };

    public static bool IsShipped(string moduleName) => ExpectedSha256.ContainsKey(moduleName);

    public static string RelativePath(string moduleName) => Path.Combine(FolderName, moduleName + ".bin");

    /// <summary>Relu à chaque appel : le dossier de l'exe ne change pas, mais rien ne justifie de le figer.</summary>
    public static string PathFor(string moduleName) => Path.Combine(AppContext.BaseDirectory, RelativePath(moduleName));

    /// <summary>Le module livré, s'il est sur le disque et lisible. Null sinon, sans lever.</summary>
    public static PawnIoModuleCandidate? TryRead(string moduleName)
    {
        if (!IsShipped(moduleName)) return null;

        try
        {
            string path = PathFor(moduleName);
            if (!File.Exists(path)) return null;

            byte[] blob = File.ReadAllBytes(path);
            string sha = PawnIoModuleFunctions.Sha256(blob);
            bool official = string.Equals(sha, ExpectedSha256[moduleName], StringComparison.Ordinal);
            var info = new PawnIoModuleInfo(moduleName, PawnIoModuleSource.PCPerfSuite, Release,
                PawnIoModuleFunctions.Parse(blob), sha, official);
            return new PawnIoModuleCandidate(info, blob);
        }
        catch
        {
            return null;
        }
    }
}
