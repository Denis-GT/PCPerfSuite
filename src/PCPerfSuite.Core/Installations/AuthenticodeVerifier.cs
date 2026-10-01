using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32.SafeHandles;

namespace PCPerfSuite.Core.Installations;

/// <summary>Résultat du contrôle d'une signature. <paramref name="Publisher"/> est l'organisation du certificat
/// (« namazso »), renseignée seulement quand la signature est valide.</summary>
internal readonly record struct SignatureCheck(bool IsValid, string? Publisher, string? Error);

/// <summary>
/// Contrôle la signature Authenticode d'un exécutable avec le mécanisme de Windows (WinVerifyTrust), celui qu'utilise
/// le pare-feu SmartScreen : la signature doit être intacte, et son certificat remonter à une autorité de confiance
/// de ce PC. La révocation n'est pas interrogée : elle exigerait un accès réseau supplémentaire qui, en cas d'échec,
/// ferait refuser un installeur pourtant légitime.
///
/// Une signature valide ne suffit pas à elle seule : n'importe qui peut signer un programme avec son propre certificat.
/// L'appelant compare donc aussi <see cref="SignatureCheck.Publisher"/> à l'éditeur attendu.
///
/// L'éditeur est lu sur le certificat que WinVerifyTrust vient de valider (son état de vérification), jamais sur « le
/// premier certificat du fichier » : un exe peut porter plusieurs blocs de signature et des certificats en plus. Lire le
/// premier venu (X509Certificate.CreateFromSignedFile) refusait OCCT, dont le premier certificat est « ocbase.com » alors
/// que la signature validée est celle d'« OCBASE » ; et, dans l'autre sens, aurait accepté un fichier signé par n'importe
/// qui, auquel on aurait ajouté un faux certificat au nom de l'éditeur attendu.
/// </summary>
internal static class AuthenticodeVerifier
{
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private const uint UiNone = 2;
    private const uint RevokeNone = 0;
    private const uint ChoiceFile = 1;
    private const uint StateVerify = 1;
    private const uint StateClose = 2;
    private const uint RevocationCheckNone = 0x10;
    private const string OrganizationOid = "2.5.4.10";
    private const string CommonNameOid = "2.5.4.3";

    /// <summary>Ne lève jamais. <paramref name="openHandle"/> est le fichier déjà ouvert par l'appelant : Windows
    /// vérifie alors ce handle-là, celui dont l'appelant a verrouillé le contenu, et non le fichier qui se
    /// trouverait au même chemin une fraction de seconde plus tard.</summary>
    public static SignatureCheck Check(string path, SafeFileHandle? openHandle = null)
    {
        try
        {
            (int status, string? publisher, string? publisherError) = Verify(path, openHandle);
            if (status != 0) return new SignatureCheck(false, null, Describe(status));
            return publisher is null
                ? new SignatureCheck(false, null, $"éditeur illisible ({publisherError ?? "certificat du signataire introuvable"})")
                : new SignatureCheck(true, publisher, null);
        }
        catch (Exception ex)
        {
            return new SignatureCheck(false, null, $"vérification impossible ({ex.Message})");
        }
    }

    /// <summary>Statut de WinVerifyTrust et, si la signature est valide, l'éditeur du certificat qu'il a validé, lu dans
    /// son état de vérification avant de le refermer.</summary>
    private static (int Status, string? Publisher, string? PublisherError) Verify(string path, SafeFileHandle? openHandle)
    {
        IntPtr pathPointer = Marshal.StringToHGlobalUni(path);
        IntPtr filePointer = Marshal.AllocHGlobal(Marshal.SizeOf<WintrustFileInfo>());
        Guid action = GenericVerifyV2;

        try
        {
            Marshal.StructureToPtr(new WintrustFileInfo
            {
                StructSize = (uint)Marshal.SizeOf<WintrustFileInfo>(),
                FilePath = pathPointer,
                FileHandle = openHandle is { IsInvalid: false, IsClosed: false } ? openHandle.DangerousGetHandle() : IntPtr.Zero,
            }, filePointer, false);

            var data = new WintrustData
            {
                StructSize = (uint)Marshal.SizeOf<WintrustData>(),
                UiChoice = UiNone,
                RevocationChecks = RevokeNone,
                UnionChoice = ChoiceFile,
                File = filePointer,
                StateAction = StateVerify,
                ProviderFlags = RevocationCheckNone,
            };

            // -1 : pas de fenêtre parente, donc aucune interface (UiNone).
            int status = WinVerifyTrust(new IntPtr(-1), ref action, ref data);

            string? publisher = null;
            string? publisherError = null;
            try
            {
                if (status == 0) publisher = SignerPublisher(data.StateData);
            }
            catch (Exception ex)
            {
                publisherError = ex.Message;
            }
            finally
            {
                // Toujours fermer, même en cas d'échec : Windows garde un état de vérification qui ne se libère qu'ainsi.
                data.StateAction = StateClose;
                WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            }

            return (status, publisher, publisherError);
        }
        finally
        {
            Marshal.FreeHGlobal(filePointer);
            Marshal.FreeHGlobal(pathPointer);
        }
    }

    /// <summary>Éditeur du certificat de tête du premier signataire, celui dont WinVerifyTrust vient de valider la chaîne.
    /// Null si l'état ne le donne pas.</summary>
    private static string? SignerPublisher(IntPtr stateData)
    {
        if (stateData == IntPtr.Zero) return null;

        IntPtr provider = WTHelperProvDataFromStateData(stateData);
        if (provider == IntPtr.Zero) return null;

        IntPtr signer = WTHelperGetProvSignerFromChain(provider, 0, false, 0);
        if (signer == IntPtr.Zero) return null;

        IntPtr providerCert = WTHelperGetProvCertFromChain(signer, 0);
        if (providerCert == IntPtr.Zero) return null;

        // CRYPT_PROVIDER_CERT : un DWORD (cbStruct), puis le PCCERT_CONTEXT, aligné sur la taille d'un pointeur.
        IntPtr certContext = Marshal.ReadIntPtr(providerCert, IntPtr.Size);
        if (certContext == IntPtr.Zero) return null;

        // Le constructeur duplique le contexte : il reste valable après la fermeture de l'état.
        using var certificate = new X509Certificate2(certContext);
        return PublisherOf(certificate);
    }

    /// <summary>Organisation du certificat, ou à défaut son nom courant.</summary>
    private static string? PublisherOf(X509Certificate2 certificate)
    {
        string? organization = null;
        string? commonName = null;

        foreach (X500RelativeDistinguishedName part in certificate.SubjectName.EnumerateRelativeDistinguishedNames())
        {
            string? oid = part.GetSingleElementType().Value;
            if (oid == OrganizationOid) organization ??= part.GetSingleElementValue();
            else if (oid == CommonNameOid) commonName ??= part.GetSingleElementValue();
        }

        return organization ?? commonName;
    }

    /// <summary>Les codes que Windows renvoie le plus souvent, en clair. Un code inconnu reste affiché : c'est
    /// ce qui permet de chercher la cause dans un signalement.</summary>
    private static string Describe(int status) => (uint)status switch
    {
        0x800B0100 => "l'installeur n'est pas signé",
        0x80096010 => "le fichier a été modifié depuis sa signature",
        0x800B0101 => "le certificat de signature a expiré",
        0x800B0109 or 0x800B010A => "le certificat de signature ne remonte pas à une autorité reconnue par ce PC",
        0x800B0111 => "l'éditeur est explicitement bloqué sur ce PC",
        var code => $"signature invalide (code 0x{code:X8})",
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustFileInfo
    {
        public uint StructSize;
        public IntPtr FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustData
    {
        public uint StructSize;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr File;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref WintrustData data);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperProvDataFromStateData(IntPtr stateData);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperGetProvSignerFromChain(IntPtr providerData, uint signerIndex,
        [MarshalAs(UnmanagedType.Bool)] bool counterSigner, uint counterSignerIndex);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperGetProvCertFromChain(IntPtr signer, uint certIndex);
}
