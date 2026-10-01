#:property Nullable=enable

// Signature du catalogue d'outils, en local, avec une clé qui n'entre jamais dans la CI (décision D8).
//
//   dotnet run outils/signer.cs -- nouvelle-cle <dossier hors du dépôt>
//       crée cle-privee-catalogue.pem (PKCS#8 chiffré par un mot de passe) et affiche la clé publique à inscrire dans
//       ToolCatalogTrust.PublicKey (src/PCPerfSuite.Core/Installations/ToolCatalogStore.cs).
//   dotnet run outils/signer.cs -- signer catalogue-outils.json <cle-privee-catalogue.pem>
//       écrit catalogue-outils.json.sig, après avoir rappelé le numéro et le contenu du catalogue.
//   dotnet run outils/signer.cs -- verifier catalogue-outils.json <clé publique en base64>
//   dotnet run outils/signer.cs -- cle-publique <cle-privee-catalogue.pem>
//
// Ce qui est signé : « PCPerfSuite/catalogue-outils/v1 » suivi d'un saut de ligne, puis les octets exacts du fichier.
// ECDSA P-256 / SHA-256, signature IEEE P1363 (64 octets) en base64. Doit rester identique à CatalogSignature de l'app.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

const string Context = "PCPerfSuite/catalogue-outils/v1";
const string KeyFileName = "cle-privee-catalogue.pem";

if (args.Length < 2) return Usage();

switch (args[0])
{
    case "nouvelle-cle":
    {
        string folder = Path.GetFullPath(args[1]);
        string keyPath = Path.Combine(folder, KeyFileName);
        if (File.Exists(keyPath)) return Fail($"{keyPath} existe déjà : rien n'est écrasé.");
        if (IsInsideGitRepository(folder)) return Fail("Ce dossier est dans un dépôt git : la clé privée doit vivre ailleurs (clé USB, coffre).");

        string password = ReadPassword("Mot de passe de la clé : ");
        if (password.Length < 12) return Fail("Mot de passe trop court (12 caractères au moins).");
        if (ReadPassword("Encore une fois : ") != password) return Fail("Les deux mots de passe diffèrent.");

        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Directory.CreateDirectory(folder);
        File.WriteAllText(keyPath, key.ExportEncryptedPkcs8PrivateKeyPem(password,
            new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 600_000)));

        Console.WriteLine($"Clé privée écrite : {keyPath}");
        Console.WriteLine("Clé publique, à inscrire dans ToolCatalogTrust.PublicKey :");
        Console.WriteLine(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
        return 0;
    }

    case "cle-publique":
    {
        using ECDsa key = LoadPrivateKey(args[1]);
        Console.WriteLine(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
        return 0;
    }

    case "signer" when args.Length >= 3:
    {
        byte[] content = File.ReadAllBytes(args[1]);
        if (Describe(content) is not { } description) return Fail("Ce fichier n'est pas un catalogue lisible : rien n'est signé.");
        if (content.Contains((byte)'\r')) return Fail("Le catalogue contient des fins de ligne CRLF : vérifie le .gitattributes (le fichier publié serait différent).");

        Console.WriteLine(description);
        Console.Write("Signer ce catalogue ? (o/N) ");
        if (!string.Equals(Console.ReadLine()?.Trim(), "o", StringComparison.OrdinalIgnoreCase)) return Fail("Rien n'est signé.");

        using ECDsa key = LoadPrivateKey(args[2]);
        byte[] signature = key.SignData(Signed(content), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        string signaturePath = args[1] + ".sig";
        File.WriteAllText(signaturePath, Convert.ToBase64String(signature));
        Console.WriteLine($"Signature écrite : {signaturePath}");
        return 0;
    }

    case "verifier" when args.Length >= 3:
    {
        byte[] content = File.ReadAllBytes(args[1]);
        string signature = File.ReadAllText(args[1] + ".sig").Trim();
        using ECDsa key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(args[2].Trim()), out _);
        bool valid = key.VerifyData(Signed(content), Convert.FromBase64String(signature), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        Console.WriteLine(valid ? "Signature valide." : "Signature INVALIDE.");
        return valid ? 0 : 1;
    }

    default:
        return Usage();
}

static byte[] Signed(byte[] content) => Encoding.UTF8.GetBytes(Context + "\n").Concat(content).ToArray();

static ECDsa LoadPrivateKey(string path)
{
    string pem = File.ReadAllText(path);
    var key = ECDsa.Create();
    key.ImportFromEncryptedPem(pem, ReadPassword("Mot de passe de la clé : "));
    if (key.KeySize != 256) throw new InvalidOperationException("Ce n'est pas une clé P-256.");
    return key;
}

/// <summary>Numéro, date et outils du catalogue, pour que Denis voie ce qu'il signe ; null s'il est illisible.</summary>
static string? Describe(byte[] content)
{
    try
    {
        using JsonDocument document = JsonDocument.Parse(content);
        JsonElement root = document.RootElement;
        var text = new StringBuilder();
        text.AppendLine($"Catalogue n° {root.GetProperty("sequence").GetInt64()} (format {root.GetProperty("format").GetInt32()}), " +
                        $"généré le {(root.TryGetProperty("generatedUtc", out JsonElement date) ? date.GetString() : "?")} :");
        foreach (JsonElement tool in root.GetProperty("tools").EnumerateArray())
        {
            text.AppendLine($"  {tool.GetProperty("id").GetString(),-16} {tool.GetProperty("version").GetString(),-14} {tool.GetProperty("url").GetString()}");
        }

        return text.ToString();
    }
    catch
    {
        return null;
    }
}

static string ReadPassword(string prompt)
{
    Console.Write(prompt);
    if (Console.IsInputRedirected) return Console.ReadLine() ?? "";

    var password = new StringBuilder();
    while (true)
    {
        ConsoleKeyInfo key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter) break;
        if (key.Key == ConsoleKey.Backspace) { if (password.Length > 0) password.Length--; continue; }
        if (!char.IsControl(key.KeyChar)) password.Append(key.KeyChar);
    }

    Console.WriteLine();
    return password.ToString();
}

static bool IsInsideGitRepository(string folder)
{
    for (DirectoryInfo? current = new(folder); current is not null; current = current.Parent)
    {
        if (Directory.Exists(Path.Combine(current.FullName, ".git")) || File.Exists(Path.Combine(current.FullName, ".git"))) return true;
    }

    return false;
}

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}

static int Usage()
{
    Console.Error.WriteLine("Usage : nouvelle-cle <dossier> | signer <catalogue.json> <clé.pem> | verifier <catalogue.json> <clé publique> | cle-publique <clé.pem>");
    return 2;
}
