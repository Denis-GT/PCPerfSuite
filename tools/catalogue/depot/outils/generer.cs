#:package YamlDotNet@18.1.0
#:property Nullable=enable

// Régénère catalogue-outils.json : la dernière version de chaque outil (adresse versionnée, SHA-256, taille), d'après
// les manifestes winget-pkgs, l'API GitHub Releases ou la page de l'éditeur (sources.json). Chaque fichier nouveau est
// téléchargé et haché ici : une empreinte winget qui ne correspond pas au fichier réel fait garder l'ancienne entrée.
// Rien n'est signé : la clé reste hors ligne chez Denis (décision D8), qui signe après relecture de la PR.
//
//   dotnet run outils/generer.cs -- --catalogue catalogue-outils.json --sources sources.json --resume resume.md
//
// Code de sortie : 0 (catalogue à jour ou mis à jour, même si un outil a échoué), 1 si aucune source n'a répondu.

using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

string cataloguePath = Option("--catalogue") ?? "catalogue-outils.json";
string sourcesPath = Option("--sources") ?? "sources.json";
string? summaryPath = Option("--resume");

using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PCPerfSuite-catalogue", "1.0"));
string? token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");

JsonDocument current = JsonDocument.Parse(File.ReadAllBytes(cataloguePath));
long sequence = current.RootElement.GetProperty("sequence").GetInt64();
var previous = new Dictionary<string, Entry>();
foreach (JsonElement tool in current.RootElement.GetProperty("tools").EnumerateArray())
{
    Entry entry = new(tool.GetProperty("id").GetString()!, tool.GetProperty("version").GetString()!, tool.GetProperty("url").GetString()!,
        tool.GetProperty("sha256").GetString()!, tool.GetProperty("size").GetInt64());
    previous[entry.Id] = entry;
}

using JsonDocument sources = JsonDocument.Parse(File.ReadAllBytes(sourcesPath));
var entries = new List<Entry>();
var changes = new List<string>();
var problems = new List<string>();
int answered = 0;

foreach (JsonElement source in sources.RootElement.GetProperty("tools").EnumerateArray())
{
    string id = source.GetProperty("id").GetString()!;
    previous.TryGetValue(id, out Entry? old);
    try
    {
        Candidate candidate = await FindLatestAsync(source);
        answered++;
        RequireHost(candidate.Url, source);

        if (old is not null && old.Url == candidate.Url && (candidate.Sha256 is null || candidate.Sha256.Equals(old.Sha256, StringComparison.OrdinalIgnoreCase)))
        {
            entries.Add(old);
            continue;
        }

        Console.WriteLine($"{id} : téléchargement de {candidate.Url}");
        (string sha, long size) = await HashAsync(candidate.Url);
        if (candidate.Sha256 is not null && !candidate.Sha256.Equals(sha, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"le fichier ne correspond pas à l'empreinte de sa source ({candidate.Sha256[..12]}… annoncé, {sha[..12]}… reçu)");
        }

        var entry = new Entry(id, candidate.Version, candidate.Url, sha, size);
        entries.Add(entry);
        changes.Add(old is null ? $"- **{id}** : ajouté en {entry.Version}" : $"- **{id}** : {old.Version} → {entry.Version}");
    }
    catch (Exception ex)
    {
        problems.Add($"- **{id}** : {ex.Message} (entrée précédente gardée)");
        if (old is not null) entries.Add(old);
    }
}

if (answered == 0)
{
    Console.Error.WriteLine("Aucune source n'a répondu : catalogue inchangé.");
    WriteSummary("Aucune source n'a répondu : catalogue inchangé.");
    return 1;
}

bool changed = changes.Count > 0 || entries.Count != previous.Count;
if (changed)
{
    File.WriteAllBytes(cataloguePath, Serialize(sequence + 1, entries));
    Console.WriteLine($"Catalogue n° {sequence + 1} écrit ({changes.Count} changement(s)).");
}
else
{
    Console.WriteLine("Catalogue déjà à jour.");
}

WriteSummary(changed
    ? $"Catalogue n° {sequence + 1}, à relire puis à signer en local (outils/signer.cs) avant de fusionner.\n\n{string.Join('\n', changes)}"
    : "Catalogue déjà à jour.");
return 0;

// --- Sources -------------------------------------------------------------------------------------------------------

async Task<Candidate> FindLatestAsync(JsonElement source)
{
    string kind = source.GetProperty("source").GetString()!;
    return kind switch
    {
        "winget" => await FromWingetAsync(source, useInstaller: true),
        "winget-version" => await FromWingetAsync(source, useInstaller: false),
        "github" => await FromGitHubAsync(source),
        "page" => await FromPageAsync(source),
        _ => throw new InvalidOperationException($"source « {kind} » inconnue"),
    };
}

async Task<Candidate> FromWingetAsync(JsonElement source, bool useInstaller)
{
    string package = source.GetProperty("package").GetString()!;
    string folder = $"manifests/{char.ToLowerInvariant(package[0])}/{package.Replace('.', '/')}";
    using JsonDocument listing = JsonDocument.Parse(await GitHubApiAsync($"repos/microsoft/winget-pkgs/contents/{folder}"));

    string? version = listing.RootElement.EnumerateArray()
        .Where(e => e.GetProperty("type").GetString() == "dir")
        .Select(e => e.GetProperty("name").GetString()!)
        .Where(name => Regex.IsMatch(name, @"^\d+(\.\d+)*$"))
        .OrderByDescending(name => name, Comparer<string>.Create(CompareVersions))
        .FirstOrDefault();
    if (version is null) throw new InvalidOperationException($"aucune version dans winget-pkgs ({folder})");

    if (!useInstaller)
    {
        string template = source.GetProperty("url").GetString()!;
        return new Candidate(version, template.Replace("{version_}", version.Replace('.', '_')).Replace("{version}", version), null);
    }

    string yaml = await http.GetStringAsync($"https://raw.githubusercontent.com/microsoft/winget-pkgs/master/{folder}/{version}/{package}.installer.yaml");
    var stream = new YamlStream();
    stream.Load(new StringReader(yaml));
    var root = (YamlMappingNode)stream.Documents[0].RootNode;

    string? defaultType = Scalar(root, "InstallerType");
    string architecture = source.GetProperty("architecture").GetString()!;
    string? wantedType = source.TryGetProperty("installerType", out JsonElement t) ? t.GetString() : null;

    foreach (YamlMappingNode installer in ((YamlSequenceNode)root.Children[new YamlScalarNode("Installers")]).Children.OfType<YamlMappingNode>())
    {
        if (!string.Equals(Scalar(installer, "Architecture"), architecture, StringComparison.OrdinalIgnoreCase)) continue;
        string? type = Scalar(installer, "InstallerType") ?? defaultType;
        if (wantedType is not null && !string.Equals(type, wantedType, StringComparison.OrdinalIgnoreCase)) continue;

        string url = Scalar(installer, "InstallerUrl") ?? throw new InvalidOperationException("manifeste sans InstallerUrl");
        string sha = Scalar(installer, "InstallerSha256") ?? throw new InvalidOperationException("manifeste sans InstallerSha256");
        return new Candidate(Scalar(root, "PackageVersion") ?? version, url, sha.ToUpperInvariant());
    }

    throw new InvalidOperationException($"aucun installeur {architecture}{(wantedType is null ? "" : $" {wantedType}")} dans le manifeste {version}");
}

async Task<Candidate> FromGitHubAsync(JsonElement source)
{
    string repo = source.GetProperty("repo").GetString()!;
    var pattern = new Regex(source.GetProperty("asset").GetString()!);
    using JsonDocument release = JsonDocument.Parse(await GitHubApiAsync($"repos/{repo}/releases/latest"));

    string tag = release.RootElement.GetProperty("tag_name").GetString()!;
    foreach (JsonElement asset in release.RootElement.GetProperty("assets").EnumerateArray())
    {
        string name = asset.GetProperty("name").GetString()!;
        if (!pattern.IsMatch(name)) continue;

        string? digest = asset.TryGetProperty("digest", out JsonElement d) ? d.GetString() : null;
        string? sha = digest is not null && digest.StartsWith("sha256:", StringComparison.Ordinal) ? digest[7..].ToUpperInvariant() : null;
        return new Candidate(tag.TrimStart('v', 'V'), asset.GetProperty("browser_download_url").GetString()!, sha);
    }

    throw new InvalidOperationException($"aucun fichier « {pattern} » dans la version {tag} de {repo}");
}

async Task<Candidate> FromPageAsync(JsonElement source)
{
    string html = await http.GetStringAsync(source.GetProperty("page").GetString()!);
    Match match = Regex.Match(html, source.GetProperty("pattern").GetString()!);
    if (!match.Success) throw new InvalidOperationException("lien introuvable dans la page de l'éditeur");

    string Fill(string template) => Regex.Replace(template, @"\{(\d+)\}", m => match.Groups[int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)].Value);
    return new Candidate(Fill(source.GetProperty("version").GetString()!), Fill(source.GetProperty("url").GetString()!), null);
}

// --- Outils --------------------------------------------------------------------------------------------------------

async Task<string> GitHubApiAsync(string path)
{
    using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/{path}");
    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    if (!string.IsNullOrEmpty(token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    using HttpResponseMessage response = await http.SendAsync(request);
    response.EnsureSuccessStatusCode();
    return await response.Content.ReadAsStringAsync();
}

async Task<(string Sha, long Size)> HashAsync(string url)
{
    using HttpResponseMessage response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
    response.EnsureSuccessStatusCode();
    await using Stream input = await response.Content.ReadAsStreamAsync();

    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    byte[] buffer = new byte[81920];
    long size = 0;
    int read;
    while ((read = await input.ReadAsync(buffer)) > 0)
    {
        hash.AppendData(buffer, 0, read);
        size += read;
    }

    return (Convert.ToHexString(hash.GetHashAndReset()), size);
}

void RequireHost(string url, JsonElement source)
{
    var uri = new Uri(url);
    if (uri.Scheme != Uri.UriSchemeHttps) throw new InvalidOperationException($"adresse pas en HTTPS : {url}");
    string[] hosts = source.GetProperty("hosts").EnumerateArray().Select(h => h.GetString()!).ToArray();
    if (!hosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException($"« {uri.Host} » n'est pas un hôte autorisé pour cet outil ({string.Join(", ", hosts)})");
    }
}

static string? Scalar(YamlMappingNode node, string key)
    => node.Children.TryGetValue(new YamlScalarNode(key), out YamlNode? value) && value is YamlScalarNode scalar ? scalar.Value : null;

static int CompareVersions(string a, string b)
{
    long[] x = a.Split('.').Select(long.Parse).ToArray();
    long[] y = b.Split('.').Select(long.Parse).ToArray();
    for (int i = 0; i < Math.Max(x.Length, y.Length); i++)
    {
        long left = i < x.Length ? x[i] : 0;
        long right = i < y.Length ? y[i] : 0;
        if (left != right) return left.CompareTo(right);
    }

    return 0;
}

/// <summary>JSON stable d'une exécution à l'autre : clés dans le même ordre, deux espaces, fins de ligne LF (le fichier
/// est signé octet pour octet).</summary>
static byte[] Serialize(long sequence, IReadOnlyList<Entry> entries)
{
    using var buffer = new MemoryStream();
    using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
           {
               Indented = true,
               NewLine = "\n",
               Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
           }))
    {
        writer.WriteStartObject();
        writer.WriteNumber("format", 1);
        writer.WriteNumber("sequence", sequence);
        writer.WriteString("generatedUtc", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
        writer.WriteStartArray("tools");
        foreach (Entry entry in entries)
        {
            writer.WriteStartObject();
            writer.WriteString("id", entry.Id);
            writer.WriteString("version", entry.Version);
            writer.WriteString("url", entry.Url);
            writer.WriteString("sha256", entry.Sha256.ToUpperInvariant());
            writer.WriteNumber("size", entry.Size);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    buffer.WriteByte((byte)'\n');
    return buffer.ToArray();
}

void WriteSummary(string headline)
{
    if (summaryPath is null) return;
    var text = new StringBuilder(headline);
    if (problems.Count > 0) text.Append("\n\n**À vérifier**\n\n").Append(string.Join('\n', problems));
    File.WriteAllText(summaryPath, text.Append('\n').ToString());
}

string? Option(string name)
{
    int index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

record Entry(string Id, string Version, string Url, string Sha256, long Size);

record Candidate(string Version, string Url, string? Sha256);
