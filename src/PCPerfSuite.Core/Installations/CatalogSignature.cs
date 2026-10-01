using System.Security.Cryptography;
using System.Text;

namespace PCPerfSuite.Core.Installations;

/// <summary>
/// Signature d'un fichier publié par Denis (catalogue d'outils, demain table de scores de référence) : ECDSA P-256 sur
/// SHA-256, au format IEEE P1363 (64 octets r‖s), encodée en base64 dans un fichier « .sig » à côté du fichier signé.
///
/// Ce qui est signé est un contexte (« PCPerfSuite/catalogue-outils/v1 » suivi d'un saut de ligne) puis les octets
/// exacts du fichier, tels qu'ils sont téléchargés : aucune remise en forme du JSON n'entre en jeu, et une signature
/// faite pour un autre fichier signé avec la même clé (contexte différent) ne vaut pas ici.
///
/// La clé privée reste hors ligne chez Denis (décision D8), jamais dans la CI : un dépôt ou une Action compromis ne
/// peut pas publier de catalogue que l'app accepterait. L'outil de signature est dans tools/catalogue/.
/// </summary>
public static class CatalogSignature
{
    /// <summary>OID de la courbe NIST P-256 (secp256r1).</summary>
    private const string P256Oid = "1.2.840.10045.3.1.7";

    private const int SignatureLength = 64;

    /// <summary>Vrai si <paramref name="signatureBase64"/> est une signature valide de <paramref name="content"/> pour
    /// <paramref name="context"/> par la clé publique <paramref name="publicKeyBase64"/> (SubjectPublicKeyInfo DER, en
    /// base64). Ne lève jamais : toute donnée mal formée, ou une clé qui n'est pas une P-256, vaut « non ».</summary>
    public static bool Verify(ReadOnlySpan<byte> content, string? signatureBase64, string context, string? publicKeyBase64)
    {
        if (string.IsNullOrWhiteSpace(signatureBase64) || string.IsNullOrWhiteSpace(publicKeyBase64)) return false;

        try
        {
            byte[] signature = Convert.FromBase64String(signatureBase64.Trim());
            if (signature.Length != SignatureLength) return false;

            using ECDsa key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64.Trim()), out _);
            if (key.KeySize != 256 || key.ExportParameters(false).Curve.Oid.Value != P256Oid) return false;

            return key.VerifyData(SignedBytes(content, context), signature, HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Signe <paramref name="content"/> pour <paramref name="context"/> : sert aux tests et à l'outil de
    /// signature, jamais à l'app (qui n'a pas de clé privée).</summary>
    public static string Sign(ReadOnlySpan<byte> content, string context, ECDsa privateKey)
        => Convert.ToBase64String(privateKey.SignData(SignedBytes(content, context), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

    private static byte[] SignedBytes(ReadOnlySpan<byte> content, string context)
    {
        byte[] prefix = Encoding.UTF8.GetBytes(context + "\n");
        byte[] all = new byte[prefix.Length + content.Length];
        prefix.CopyTo(all, 0);
        content.CopyTo(all.AsSpan(prefix.Length));
        return all;
    }
}
