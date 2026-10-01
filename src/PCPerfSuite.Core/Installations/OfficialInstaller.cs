using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using PCPerfSuite.Core.SystemInfo;

namespace PCPerfSuite.Core.Installations;

/// <summary>Nature du fichier attendu, contrôlée sur ses premiers octets : un .msi ou un .zip qui commencerait par
/// « MZ » serait un exécutable déguisé, et un fichier qui n'a pas l'en-tête annoncé n'est pas celui de l'éditeur.</summary>
public enum InstallerFileKind
{
    /// <summary>Programme Windows (« MZ ») : installeur ou outil portable.</summary>
    Exe,

    /// <summary>Paquet Windows Installer (fichier composé OLE), lancé par msiexec.</summary>
    Msi,

    /// <summary>Archive zip (« PK ») : jamais lancée telle quelle, son contenu est extrait puis vérifié.</summary>
    Zip,
}

/// <summary>Un fichier à télécharger depuis sa source officielle : son adresse, les seuls hôtes vers lesquels le
/// téléchargement peut être redirigé, et l'éditeur qui doit l'avoir signé. <paramref name="AllowedHosts"/> accepte un
/// nom exact (« github.com ») ou un suffixe (« *.githubusercontent.com »). <paramref name="ExpectedPublisher"/> null :
/// fichier non signé, qui peut être téléchargé (avec son empreinte) mais jamais lancé.
///
/// Les limites se règlent par source, jamais globalement : relever la taille ou le délai pour un gros outil ne doit
/// pas affaiblir le téléchargement de PawnIO.</summary>
public sealed record OfficialInstallerSource(
    Uri Url,
    IReadOnlyList<string> AllowedHosts,
    string FileName,
    string Arguments,
    string? ExpectedPublisher)
{
    public InstallerFileKind Kind { get; init; } = InstallerFileKind.Exe;

    /// <summary>Empreinte SHA-256 attendue, en hexadécimal (64 caractères). Obligatoire pour un fichier non signé ; une
    /// source sans signature ni empreinte est refusée. Toujours donnée avec une adresse versionnée : une adresse
    /// « dernière version » changerait de fichier à chaque sortie, et l'empreinte de la veille le ferait refuser.</summary>
    public string? ExpectedSha256 { get; init; }

    /// <summary>Taille exacte attendue, en octets, quand la source la connaît.</summary>
    public long? ExpectedSize { get; init; }

    /// <summary>Taille au-delà de laquelle le téléchargement est interrompu (au plus <see cref="OfficialInstaller.MaxAllowedBytes"/>).</summary>
    public long MaxBytes { get; init; } = OfficialInstaller.DefaultMaxBytes;

    /// <summary>Délai du téléchargement (au plus <see cref="OfficialInstaller.MaxAllowedDownloadTimeout"/>).</summary>
    public TimeSpan DownloadTimeout { get; init; } = OfficialInstaller.DefaultDownloadTimeout;
}

/// <summary>Issue d'une installation, avec un message prêt à afficher. Un échec n'est jamais une exception.
/// <paramref name="ExitCode"/> est le code rendu par l'installeur, null s'il n'a pas été lancé ou pas attendu jusqu'au
/// bout : chaque installeur a ses propres codes, c'est à l'appelant, qui le connaît, de les interpréter.</summary>
public readonly record struct InstallOutcome(bool Succeeded, string Message, int? ExitCode = null);

/// <summary>Pourquoi un téléchargement a échoué : de quoi proposer la bonne suite (page officielle, winget, réessayer).</summary>
public enum DownloadFailureKind
{
    None,

    /// <summary>Source refusée avant tout accès réseau (hôte, HTTPS, nom de fichier, limites).</summary>
    Refused,

    /// <summary>Réseau coupé, serveur injoignable, délai dépassé.</summary>
    Network,

    /// <summary>Le fichier n'est plus à l'adresse (404, 410) ou le serveur le refuse.</summary>
    LinkUnavailable,

    /// <summary>Le fichier reçu n'est pas celui attendu : empreinte, taille, en-tête ou signature.</summary>
    Mismatch,

    /// <summary>Disque plein, dossier inaccessible, annulation.</summary>
    Other,
}

/// <summary>Issue d'un téléchargement sans lancement : le fichier vérifié est à <paramref name="Path"/>, ou il n'existe
/// pas et <paramref name="Message"/> dit pourquoi.</summary>
public readonly record struct DownloadOutcome(bool Succeeded, string Message, string? Path, DownloadFailureKind Failure = DownloadFailureKind.None);

/// <summary>
/// Télécharge un fichier depuis sa source officielle, le vérifie, puis le lance (installeur) ou le garde (outil
/// portable, fichier remis à l'utilisateur).
///
/// Protections, dans l'ordre : HTTPS obligatoire à chaque étape, y compris chaque redirection, vers des hôtes
/// connus seulement ; taille bornée ; empreinte SHA-256 quand la source la donne ; fichier ouvert en lecture seule
/// pour que personne ne puisse le remplacer entre la vérification et le lancement ; en-tête du type attendu ;
/// signature Authenticode valide ET éditeur attendu. Sans ces derniers, un fichier posé dans %TEMP% par un autre
/// programme de la même session deviendrait un exécutable lancé avec les droits administrateur de PCPerfSuite. Un
/// fichier non signé n'est jamais lancé, quelle que soit son empreinte.
///
/// Toute méthode publique est « best-effort » (règle de compatibilité 2) : réseau coupé, disque plein, signature
/// refusée, UAC refusé ou annulation renvoient un échec avec son message, jamais une exception.
/// </summary>
public static class OfficialInstaller
{
    /// <summary>Hôtes de GitHub : les téléchargements de versions passent par github.com, puis par un hôte de
    /// stockage « *.githubusercontent.com » (release-assets, objects…).</summary>
    public static readonly IReadOnlyList<string> GitHubHosts = new[] { "github.com", "*.githubusercontent.com" };

    /// <summary>Taille par défaut d'une source : un installeur comme celui de PawnIO pèse quelques Mo.</summary>
    public const long DefaultMaxBytes = 100L * 1024 * 1024;

    /// <summary>Plafond qu'aucune source ne peut dépasser (un pilote graphique, le plus gros fichier prévu, en fait moins).</summary>
    public const long MaxAllowedBytes = 1536L * 1024 * 1024;

    public static readonly TimeSpan DefaultDownloadTimeout = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan MaxAllowedDownloadTimeout = TimeSpan.FromMinutes(60);

    private const int MaxRedirects = 5;
    private const int BufferSize = 81920;

    /// <summary>ERROR_CANCELLED : l'utilisateur a refusé l'invite d'autorisation de Windows.</summary>
    private const int ErrorCancelled = 1223;

    /// <summary>ERROR_SUCCESS_REBOOT_REQUIRED : convention des installeurs Windows pour « réussi, redémarrer ».</summary>
    private const int ExitRebootRequired = 3010;

    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(10);

    /// <summary>Lire la dernière version n'est qu'un raccourci : passé ce délai, on télécharge sans savoir.</summary>
    private static readonly TimeSpan VersionCheckTimeout = TimeSpan.FromSeconds(10);

    /// <summary>En-tête d'un fichier composé OLE, celui des paquets .msi.</summary>
    private static readonly byte[] CompoundFileHeader = { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 };

    // Les redirections sont suivies à la main, une par une : la vérification de l'hôte doit porter sur chaque étape,
    // pas seulement sur l'adresse finale (la requête est déjà partie vers l'hôte intermédiaire).
    private static readonly HttpClient Http = CreateClient();

    /// <summary>Télécharge l'installeur dans un dossier temporaire protégé, le vérifie, le lance et attend sa fin. Le
    /// dossier est effacé ensuite. Une source non signée ou un zip sont refusés : rien n'est lancé sans signature.</summary>
    public static async Task<InstallOutcome> DownloadAndRunAsync(
        OfficialInstallerSource source, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        string? folder = null;
        try
        {
            ValidateSource(source);
            if (source.ExpectedPublisher is null)
            {
                throw new InstallFailure("Ce fichier n'est pas signé par son éditeur : PCPerfSuite ne le lance jamais.");
            }

            if (source.Kind == InstallerFileKind.Zip) throw new InstallFailure("Une archive zip ne se lance pas : elle doit d'abord être extraite.");

            folder = CreateWorkFolder();
            string path = Path.Combine(folder, source.FileName);

            progress?.Report("Téléchargement…");
            await DownloadAsync(source, path, progress, cancellationToken).ConfigureAwait(false);

            return await RunVerifiedAsync(path, source.Kind, source.ExpectedPublisher, source.Arguments, progress, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return ToInstallOutcome(ex);
        }
        finally
        {
            if (folder is not null) DeleteWorkFolder(folder);
        }
    }

    /// <summary>
    /// Télécharge vers <paramref name="destinationPath"/> (le dossier doit exister, le fichier ne doit pas) et vérifie
    /// taille, empreinte et en-tête ; pour un exe ou un msi signé, aussi la signature et l'éditeur. Rien n'est lancé.
    /// En cas d'échec, le fichier partiel est supprimé. À l'appelant de choisir un dossier à la hauteur de ce qu'il fera
    /// du fichier : un dossier que seuls les administrateurs peuvent modifier si le fichier doit être lancé ensuite.
    /// </summary>
    public static async Task<DownloadOutcome> DownloadToFileAsync(
        OfficialInstallerSource source, string destinationPath, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        bool created = false;
        try
        {
            ValidateSource(source);

            // Jamais supprimer un fichier qui n'est pas le nôtre : celui qui porte déjà ce nom reste intact.
            if (File.Exists(destinationPath)) throw new InstallFailure("Un fichier du même nom existe déjà à cet endroit.", DownloadFailureKind.Other);
            progress?.Report("Téléchargement…");
            created = true;
            await DownloadAsync(source, destinationPath, progress, cancellationToken).ConfigureAwait(false);

            progress?.Report("Vérification du fichier…");
            using (var stream = new FileStream(destinationPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                VerifyHeader(stream, source.Kind);
                if (source.ExpectedPublisher is not null && source.Kind != InstallerFileKind.Zip)
                {
                    VerifySignature(stream, destinationPath, source.ExpectedPublisher);
                }
            }

            return new DownloadOutcome(true, "Fichier téléchargé et vérifié.", destinationPath);
        }
        catch (Exception ex)
        {
            if (created) TryDeleteFile(destinationPath);
            (string message, DownloadFailureKind kind) = Describe(ex);
            return new DownloadOutcome(false, message, null, kind);
        }
    }

    /// <summary>Verrouille un fichier déjà sur le disque, le vérifie (en-tête, signature, éditeur), le lance et attend sa
    /// fin. Pour un installeur extrait d'une archive : l'appelant l'a posé dans un dossier de <see cref="CreateWorkFolder"/>.</summary>
    public static async Task<InstallOutcome> RunVerifiedAsync(
        string path, InstallerFileKind kind, string expectedPublisher, string arguments, IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            if (kind == InstallerFileKind.Zip) throw new InstallFailure("Une archive zip ne se lance pas : elle doit d'abord être extraite.");
            progress?.Report("Vérification de l'installeur…");

            // Lecture seule, partage en lecture : le fichier ne peut plus être modifié, remplacé ni supprimé tant que
            // ce flux est ouvert, donc ce qui est vérifié est exactement ce qui sera lancé.
            FileStream locked = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                VerifyHeader(locked, kind);
                VerifySignature(locked, path, expectedPublisher);
            }
            catch
            {
                locked.Dispose();
                throw;
            }

            progress?.Report("Installation en cours…");
            return await LaunchAsync(locked, path, kind, arguments, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return ToInstallOutcome(ex);
        }
    }

    /// <summary>Vrai si le fichier a l'en-tête attendu et la signature valide de <paramref name="expectedPublisher"/>.
    /// Sert à l'exécutable sorti d'une archive, et avant chaque lancement d'un outil portable. Ne lève jamais.</summary>
    public static bool TryVerifySignedFile(string path, InstallerFileKind kind, string expectedPublisher, out string? error)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            VerifyHeader(stream, kind);
            VerifySignature(stream, path, expectedPublisher);
            error = null;
            return true;
        }
        catch (InstallFailure ex)
        {
            error = ex.Message;
            return false;
        }
        catch (Exception ex)
        {
            error = $"Vérification impossible ({ex.Message}).";
            return false;
        }
    }

    /// <summary>Dossier de travail neuf sous %TEMP%\PCPerfSuite\Installations, réservé aux processus élevés (voir
    /// <see cref="RestrictToElevatedProcesses"/>). À effacer par <see cref="DeleteWorkFolder"/>.</summary>
    public static string CreateWorkFolder()
    {
        string folder = Path.Combine(Path.GetTempPath(), "PCPerfSuite", "Installations", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        RestrictToElevatedProcesses(folder);
        return folder;
    }

    public static void DeleteWorkFolder(string folder)
    {
        try { Directory.Delete(folder, recursive: true); }
        catch { /* best-effort : un dossier temporaire oublié ne gêne personne, et Windows nettoie %TEMP% */ }
    }

    /// <summary>Contrôles faits avant tout accès réseau. Lève <see cref="InstallFailure"/>.</summary>
    internal static void ValidateSource(OfficialInstallerSource source)
    {
        if (string.IsNullOrWhiteSpace(source.FileName) || source.FileName != Path.GetFileName(source.FileName)
            || source.FileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InstallFailure("Nom de fichier d'installeur invalide.");
        }

        RequireAllowed(source.Url, source.AllowedHosts);

        if (source.MaxBytes <= 0 || source.MaxBytes > MaxAllowedBytes) throw new InstallFailure("Taille maximale de la source invalide.");
        if (source.DownloadTimeout <= TimeSpan.Zero || source.DownloadTimeout > MaxAllowedDownloadTimeout)
        {
            throw new InstallFailure("Délai de téléchargement de la source invalide.");
        }

        if (source.ExpectedSize is { } size && (size <= 0 || size > source.MaxBytes))
        {
            throw new InstallFailure("La taille annoncée par la source dépasse sa limite.");
        }

        if (source.ExpectedSha256 is { } sha && !IsSha256Hex(sha)) throw new InstallFailure("Empreinte SHA-256 de la source invalide.");

        if (source.ExpectedPublisher is null && source.ExpectedSha256 is null)
        {
            throw new InstallFailure("Fichier ni signé ni accompagné de son empreinte : téléchargement refusé.");
        }
    }

    /// <summary>64 caractères hexadécimaux.</summary>
    public static bool IsSha256Hex(string? value)
        => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    /// <summary>Suit les redirections à la main, refuse tout ce qui n'est pas HTTPS vers un hôte autorisé, et écrit
    /// le fichier en bornant sa taille et en calculant son empreinte au passage. Lève <see cref="InstallFailure"/> ou
    /// une exception réseau ou de disque.</summary>
    internal static async Task DownloadAsync(
        OfficialInstallerSource source, string destination, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(source.DownloadTimeout);

        try
        {
            using HttpResponseMessage response = await GetFollowingRedirectsAsync(source.Url, source.AllowedHosts, timeout.Token)
                .ConfigureAwait(false);
            await SaveAsync(response, destination, source, progress, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InstallFailure(
                $"Le téléchargement a dépassé {source.DownloadTimeout.TotalMinutes:0} minutes : connexion trop lente ou coupée.",
                DownloadFailureKind.Network);
        }
    }

    /// <summary>Petit fichier (catalogue, signature) lu en mémoire, avec les mêmes garde-fous que les installeurs.</summary>
    internal static async Task<byte[]> DownloadBytesAsync(
        Uri url, IReadOnlyList<string> allowedHosts, long maxBytes, TimeSpan timeoutDelay, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutDelay);

        try
        {
            using HttpResponseMessage response = await GetFollowingRedirectsAsync(url, allowedHosts, timeout.Token).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength > maxBytes) throw new InstallFailure("Le fichier annoncé est anormalement gros.");

            await using Stream input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var output = new MemoryStream();
            byte[] buffer = new byte[BufferSize];
            int read;
            while ((read = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
            {
                if (output.Length + read > maxBytes) throw new InstallFailure("Le fichier dépasse la taille maximale attendue.");
                output.Write(buffer, 0, read);
            }

            return output.ToArray();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InstallFailure("Le serveur n'a pas répondu à temps.", DownloadFailureKind.Network);
        }
    }

    /// <summary>Réponse de la première adresse qui n'est pas une redirection. Chaque étape passe par
    /// <see cref="RequireAllowed"/>. Lève <see cref="InstallFailure"/> pour un refus ou une erreur HTTP.</summary>
    private static async Task<HttpResponseMessage> GetFollowingRedirectsAsync(
        Uri url, IReadOnlyList<string> allowedHosts, CancellationToken cancellationToken)
    {
        Uri current = url;
        for (int hop = 0; ; hop++)
        {
            RequireAllowed(current, allowedHosts);

            HttpResponseMessage response = await Http
                .GetAsync(current, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.Moved or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod
                or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                using (response)
                {
                    if (hop >= MaxRedirects) throw new InstallFailure("Trop de redirections depuis l'adresse officielle.", DownloadFailureKind.LinkUnavailable);
                    if (response.Headers.Location is not { } location)
                    {
                        throw new InstallFailure("L'adresse officielle a répondu par une redirection sans destination.", DownloadFailureKind.LinkUnavailable);
                    }

                    current = location.IsAbsoluteUri ? location : new Uri(current, location);
                    continue;
                }
            }

            if (!response.IsSuccessStatusCode)
            {
                HttpStatusCode status = response.StatusCode;
                response.Dispose();
                throw new InstallFailure(status is HttpStatusCode.NotFound or HttpStatusCode.Gone
                    ? $"Le fichier n'existe plus à l'adresse officielle (erreur {(int)status})."
                    : $"Le serveur officiel a refusé le téléchargement (erreur {(int)status}).", DownloadFailureKind.LinkUnavailable);
            }

            return response;
        }
    }

    /// <summary>Écrit le corps de la réponse en contrôlant taille et empreinte. Interne pour être testé sur une réponse
    /// fabriquée, sans réseau.</summary>
    internal static async Task SaveAsync(HttpResponseMessage response, string destination, OfficialInstallerSource source,
        IProgress<string>? progress, CancellationToken cancellationToken)
    {
        long? total = response.Content.Headers.ContentLength;
        if (total > source.MaxBytes) throw new InstallFailure("Le fichier annoncé est anormalement gros : téléchargement refusé.", DownloadFailureKind.Mismatch);
        if (total is { } announced && source.ExpectedSize is { } expected && announced != expected)
        {
            throw new InstallFailure(
                $"Le serveur annonce un fichier de {announced} octets, alors que {expected} étaient attendus : ce n'est pas le fichier vérifié. Téléchargement refusé.",
                DownloadFailureKind.Mismatch);
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (Stream input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
        {
            byte[] buffer = new byte[BufferSize];
            long received = 0;
            int lastPercent = -1;
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                received += read;
                if (received > source.MaxBytes)
                {
                    throw new InstallFailure("Le fichier dépasse la taille maximale attendue : téléchargement interrompu.", DownloadFailureKind.Mismatch);
                }

                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);

                if (total is > 0)
                {
                    int percent = (int)(received * 100 / total.Value);
                    if (percent != lastPercent)
                    {
                        lastPercent = percent;
                        progress?.Report($"Téléchargement… {percent} %");
                    }
                }
            }

            if (total is > 0 && received != total) throw new InstallFailure("Le téléchargement s'est arrêté avant la fin du fichier.", DownloadFailureKind.Network);
            if (source.ExpectedSize is { } size && received != size)
            {
                throw new InstallFailure($"Le fichier reçu fait {received} octets, alors que {size} étaient attendus : il n'a pas été gardé.",
                    DownloadFailureKind.Mismatch);
            }
        }

        if (source.ExpectedSha256 is { } expectedHash)
        {
            string actual = Convert.ToHexString(hash.GetHashAndReset());
            if (!string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InstallFailure(
                    "Le fichier reçu ne correspond pas à l'empreinte SHA-256 attendue : l'éditeur a pu publier un autre fichier " +
                    "à la même adresse, ou il a été modifié en route. Il n'a pas été gardé.", DownloadFailureKind.Mismatch);
            }
        }
    }

    /// <summary>Taille non nulle et premiers octets du type attendu.</summary>
    private static void VerifyHeader(FileStream file, InstallerFileKind kind)
    {
        if (file.Length == 0) throw new InstallFailure("Le fichier téléchargé est vide.", DownloadFailureKind.Mismatch);

        Span<byte> head = stackalloc byte[8];
        file.Position = 0;
        int read = file.Read(head);
        file.Position = 0;
        ReadOnlySpan<byte> start = head[..read];

        bool ok = kind switch
        {
            InstallerFileKind.Exe => start.Length >= 2 && start[0] == 'M' && start[1] == 'Z',
            InstallerFileKind.Msi => start.SequenceEqual(CompoundFileHeader),
            _ => start.Length >= 4 && start[0] == 'P' && start[1] == 'K' && start[2] == 3 && start[3] == 4,
        };

        if (!ok)
        {
            throw new InstallFailure(kind switch
            {
                InstallerFileKind.Exe => "Le fichier téléchargé n'est pas un programme Windows : il n'a pas été lancé.",
                InstallerFileKind.Msi => "Le fichier téléchargé n'est pas un paquet Windows Installer : il n'a pas été lancé.",
                _ => "Le fichier téléchargé n'est pas une archive zip.",
            }, DownloadFailureKind.Mismatch);
        }
    }

    /// <summary>Signature valide, éditeur attendu.</summary>
    private static void VerifySignature(FileStream locked, string path, string expectedPublisher)
    {
        SignatureCheck check = AuthenticodeVerifier.Check(path, locked.SafeFileHandle);
        if (!check.IsValid) throw new InstallFailure($"Fichier refusé, {check.Error} : il n'a pas été lancé.", DownloadFailureKind.Mismatch);

        if (!string.Equals(check.Publisher, expectedPublisher, StringComparison.OrdinalIgnoreCase))
        {
            throw new InstallFailure(
                $"Fichier refusé : il est signé par « {check.Publisher} » et non par « {expectedPublisher} ». Il n'a pas été lancé.",
                DownloadFailureKind.Mismatch);
        }
    }

    /// <summary>Lance l'installeur et attend sa fin. Pour un exe, le verrou est levé une fois le processus démarré :
    /// Windows protège ensuite lui-même le fichier de l'exécutable en cours. Un .msi, lui, n'est lu par msiexec qu'après
    /// son démarrage : le verrou est gardé jusqu'à la fin.</summary>
    private static async Task<InstallOutcome> LaunchAsync(
        FileStream locked, string path, InstallerFileKind kind, string arguments, CancellationToken cancellationToken)
    {
        // msiexec par son chemin complet : une association de fichier .msi détournée dans le profil n'a pas de prise.
        ProcessStartInfo start = kind == InstallerFileKind.Msi
            ? new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "msiexec.exe"), $"/i \"{path}\" {arguments}".TrimEnd())
            : new ProcessStartInfo(path, arguments);
        start.UseShellExecute = true;
        start.WorkingDirectory = Path.GetDirectoryName(path)!;

        // Lancée en administrateur, l'app transmet ses droits à l'installeur sans invite. Sinon, l'invite d'autorisation
        // de Windows s'affiche : c'est elle qui protège l'utilisateur, pas une suppression de sa part.
        if (!ElevationHelper.IsAdministrator()) start.Verb = "runas";

        Process? process;
        try
        {
            process = Process.Start(start);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            locked.Dispose();
            return new InstallOutcome(false, "Installation annulée : l'autorisation de Windows a été refusée.");
        }
        catch
        {
            locked.Dispose();
            throw;
        }
        finally
        {
            if (kind != InstallerFileKind.Msi) locked.Dispose();
        }

        using (locked)
        {
            if (process is null) return new InstallOutcome(false, "L'installeur n'a pas pu démarrer.");

            using (process)
            {
                using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(InstallTimeout);

                try
                {
                    await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return new InstallOutcome(false,
                        $"L'installeur tourne encore après {InstallTimeout.TotalMinutes:0} minutes. Laisse-le finir, puis clique sur « Vérifier à nouveau ».");
                }

                int exitCode = process.ExitCode;
                return exitCode switch
                {
                    0 => new InstallOutcome(true, "Installation terminée.", exitCode),
                    ExitRebootRequired => new InstallOutcome(true, "Installation terminée : Windows demande un redémarrage.", exitCode),
                    _ => new InstallOutcome(false, $"L'installeur s'est terminé avec une erreur ({DescribeExitCode(exitCode)}).", exitCode),
                };
            }
        }
    }

    /// <summary>« code 5 : Accès refusé » : le code, suivi du texte que Windows lui associe quand il en connaît un. Les
    /// installeurs renvoient le plus souvent des codes d'erreur Windows ; un code qu'il ne connaît pas reste affiché seul.</summary>
    public static string DescribeExitCode(int exitCode)
    {
        string? text = null;
        try
        {
            text = new Win32Exception(exitCode).Message.Trim().TrimEnd('.');
        }
        catch
        {
            // best-effort : le code seul reste une information utile.
        }

        // Pour un code que Windows ne connaît pas, .NET renvoie « Unknown error (0x…) », qui n'apprend rien de plus.
        bool meaningful = !string.IsNullOrEmpty(text) && !text.Contains($"0x{exitCode:x}", StringComparison.OrdinalIgnoreCase);
        return meaningful ? $"code {exitCode} : {text}" : $"code {exitCode}";
    }

    /// <summary>
    /// Dernière version publiée sur une page « releases/latest » de GitHub, lue dans la redirection qu'elle renvoie vers
    /// « releases/tag/2.2.0 » : rien n'est téléchargé, et cette adresse, contrairement à l'API de GitHub, n'a pas de
    /// quota. Null quand la version ne peut pas être lue (réseau coupé, page déplacée, étiquette qui n'est pas un
    /// numéro de version) : l'appelant fait alors comme s'il ne savait pas, jamais comme si tout était à jour.
    /// </summary>
    public static async Task<Version?> TryGetLatestReleaseVersionAsync(
        Uri latestReleasePage, IReadOnlyList<string> allowedHosts, CancellationToken cancellationToken)
    {
        try
        {
            RequireAllowed(latestReleasePage, allowedHosts);

            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(VersionCheckTimeout);

            using HttpResponseMessage response = await Http
                .GetAsync(latestReleasePage, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);

            if (response.Headers.Location is not { } location) return null;
            return ReleaseVersionFromLocation(location.IsAbsoluteUri ? location : new Uri(latestReleasePage, location));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Numéro de version d'une adresse « …/releases/tag/2.2.0 » (ou « v2.2.0 »), null si elle n'en porte pas.</summary>
    public static Version? ReleaseVersionFromLocation(Uri location)
    {
        string[] segments = location.AbsolutePath.Trim('/').Split('/');
        if (segments.Length < 2 || !string.Equals(segments[^2], "tag", StringComparison.OrdinalIgnoreCase)) return null;

        string tag = Uri.UnescapeDataString(segments[^1]).TrimStart('v', 'V');
        return Version.TryParse(tag, out Version? version) ? version : null;
    }

    /// <summary>HTTPS obligatoire, hôte dans la liste. Lève <see cref="InstallFailure"/> sinon.</summary>
    internal static void RequireAllowed(Uri uri, IReadOnlyList<string> allowedHosts)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InstallFailure("Téléchargement refusé : seule une adresse HTTPS est acceptée.");
        }

        if (IsAllowedHost(uri, allowedHosts)) return;

        throw new InstallFailure($"Téléchargement refusé : « {uri.Host} » n'est pas une source officielle connue.");
    }

    /// <summary>Hôte de <paramref name="uri"/> dans la liste : nom exact, ou suffixe « *.exemple.com » (qui n'accepte
    /// pas « exemple.com » lui-même ni « faux-exemple.com »).</summary>
    public static bool IsAllowedHost(Uri uri, IReadOnlyList<string> allowedHosts)
    {
        foreach (string allowed in allowedHosts)
        {
            bool match = allowed.StartsWith("*.", StringComparison.Ordinal)
                ? uri.Host.EndsWith(allowed[1..], StringComparison.OrdinalIgnoreCase)
                : string.Equals(uri.Host, allowed, StringComparison.OrdinalIgnoreCase);
            if (match) return true;
        }

        return false;
    }

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PCPerfSuite", "1.0"));
        return client;
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); }
        catch { /* best-effort : un fichier partiel dans un dossier de travail ne sera jamais lancé */ }
    }

    private static InstallOutcome ToInstallOutcome(Exception ex) => ex switch
    {
        InstallFailure or HttpRequestException => new InstallOutcome(false, Describe(ex).Message),
        OperationCanceledException => new InstallOutcome(false, "Installation annulée."),
        _ => new InstallOutcome(false, $"Installation impossible ({ex.Message})."),
    };

    /// <summary>Message prêt à afficher et nature de l'échec, pour toute exception d'un téléchargement.</summary>
    private static (string Message, DownloadFailureKind Kind) Describe(Exception ex) => ex switch
    {
        InstallFailure failure => (failure.Message, failure.Kind),
        OperationCanceledException => ("Opération annulée.", DownloadFailureKind.Other),
        HttpRequestException http => ($"Téléchargement impossible, vérifie ta connexion Internet ({http.Message}).", DownloadFailureKind.Network),
        IOException io => ($"Écriture du fichier impossible ({io.Message}).", DownloadFailureKind.Other),
        UnauthorizedAccessException access => ($"Dossier inaccessible ({access.Message}).", DownloadFailureKind.Other),
        _ => ($"Opération impossible ({ex.Message}).", DownloadFailureKind.Other),
    };

    /// <summary>Relève le niveau d'intégrité obligatoire du dossier à « Élevé » : même un processus qui
    /// tourne sous le même compte (celui de l'administrateur, si PCPerfSuite est lancé par « Exécuter en
    /// tant qu'administrateur » depuis ce compte) ne peut plus y écrire tant qu'il n'est pas lui-même
    /// élevé. Sans ça, seul le fichier de l'installeur est protégé (ouverture en lecture seule) : un
    /// programme non élevé pourrait déposer une DLL malveillante à côté avant le lancement, que
    /// l'installeur — lancé en administrateur — chargerait alors depuis son propre dossier
    /// (« DLL planting »). Best-effort : si icacls échoue, l'installation continue quand même, protégée
    /// par la vérification de signature/éditeur de l'exécutable lui-même.</summary>
    internal static void RestrictToElevatedProcesses(string folder)
    {
        try
        {
            using var icacls = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "icacls.exe"),
                ArgumentList = { folder, "/setintegritylevel", "(OI)(CI)", "High" },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            icacls?.WaitForExit(5000);
        }
        catch { /* best-effort : voir le résumé ci-dessus */ }
    }

    /// <summary>Échec dont le message est destiné à l'utilisateur. Interne : jamais visible hors de cet assemblage.</summary>
    internal sealed class InstallFailure(string message, DownloadFailureKind kind = DownloadFailureKind.Refused) : Exception(message)
    {
        public DownloadFailureKind Kind { get; } = kind;
    }
}
