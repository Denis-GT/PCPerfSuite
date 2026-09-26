using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using PCPerfSuite.Core.SystemInfo;

namespace PCPerfSuite.Core.Installations;

/// <summary>Un installeur à télécharger : son adresse, les seuls hôtes vers lesquels le téléchargement peut être
/// redirigé, et l'éditeur qui doit l'avoir signé. <paramref name="AllowedHosts"/> accepte un nom exact
/// (« github.com ») ou un suffixe (« *.githubusercontent.com »).</summary>
public sealed record OfficialInstallerSource(
    Uri Url,
    IReadOnlyList<string> AllowedHosts,
    string FileName,
    string Arguments,
    string ExpectedPublisher);

/// <summary>Issue d'une installation, avec un message prêt à afficher. Un échec n'est jamais une exception.</summary>
public readonly record struct InstallOutcome(bool Succeeded, string Message);

/// <summary>
/// Télécharge un installeur depuis sa source officielle, le vérifie, puis le lance.
///
/// Protections, dans l'ordre : HTTPS obligatoire à chaque étape, y compris chaque redirection, vers des hôtes
/// connus seulement ; taille bornée ; fichier ouvert en lecture seule pour que personne ne puisse le remplacer entre
/// la vérification et le lancement ; signature Authenticode valide ET éditeur attendu. Sans ces trois derniers,
/// un fichier posé dans %TEMP% par un autre programme de la même session deviendrait un exécutable lancé avec
/// les droits administrateur de PCPerfSuite.
///
/// Toute méthode publique est « best-effort » (règle de compatibilité 2) : réseau coupé, disque plein, signature
/// refusée, UAC refusé ou annulation renvoient un <see cref="InstallOutcome"/> en échec, jamais une exception.
/// </summary>
public static class OfficialInstaller
{
    /// <summary>Hôtes de GitHub : les téléchargements de versions passent par github.com, puis par un hôte de
    /// stockage « *.githubusercontent.com » (release-assets, objects…).</summary>
    public static readonly IReadOnlyList<string> GitHubHosts = new[] { "github.com", "*.githubusercontent.com" };

    private const long MaxBytes = 100L * 1024 * 1024;
    private const int MaxRedirects = 5;
    private const int BufferSize = 81920;

    /// <summary>ERROR_CANCELLED : l'utilisateur a refusé l'invite d'autorisation de Windows.</summary>
    private const int ErrorCancelled = 1223;

    /// <summary>ERROR_SUCCESS_REBOOT_REQUIRED : convention des installeurs Windows pour « réussi, redémarrer ».</summary>
    private const int ExitRebootRequired = 3010;

    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(10);

    // Les redirections sont suivies à la main, une par une : la vérification de l'hôte doit porter sur chaque étape,
    // pas seulement sur l'adresse finale (la requête est déjà partie vers l'hôte intermédiaire).
    private static readonly HttpClient Http = CreateClient();

    public static async Task<InstallOutcome> DownloadAndRunAsync(
        OfficialInstallerSource source, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        string? folder = null;
        try
        {
            if (source.FileName != Path.GetFileName(source.FileName)) throw new InstallFailure("Nom de fichier d'installeur invalide.");
            RequireAllowed(source.Url, source.AllowedHosts);

            folder = Path.Combine(Path.GetTempPath(), "PCPerfSuite", "Installations", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, source.FileName);

            progress?.Report("Téléchargement…");
            await DownloadAsync(source, path, progress, cancellationToken).ConfigureAwait(false);

            progress?.Report("Vérification de l'installeur…");

            // Lecture seule, partage en lecture : le fichier ne peut plus être modifié, remplacé ni supprimé tant que
            // ce flux est ouvert, donc ce qui est vérifié est exactement ce qui sera lancé.
            FileStream locked = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                Verify(locked, path, source.ExpectedPublisher);
            }
            catch
            {
                locked.Dispose();
                throw;
            }

            progress?.Report("Installation en cours…");
            return await LaunchAsync(locked, path, source.Arguments, cancellationToken).ConfigureAwait(false);
        }
        catch (InstallFailure ex)
        {
            return new InstallOutcome(false, ex.Message);
        }
        catch (OperationCanceledException)
        {
            return new InstallOutcome(false, "Installation annulée.");
        }
        catch (HttpRequestException ex)
        {
            return new InstallOutcome(false, $"Téléchargement impossible, vérifie ta connexion Internet ({ex.Message}).");
        }
        catch (Exception ex)
        {
            return new InstallOutcome(false, $"Installation impossible ({ex.Message}).");
        }
        finally
        {
            if (folder is not null) TryDeleteFolder(folder);
        }
    }

    /// <summary>Suit les redirections à la main, refuse tout ce qui n'est pas HTTPS vers un hôte autorisé, et écrit
    /// le fichier en bornant sa taille. Lève <see cref="InstallFailure"/> ou une exception réseau ou de disque.</summary>
    internal static async Task DownloadAsync(
        OfficialInstallerSource source, string destination, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DownloadTimeout);

        try
        {
            Uri current = source.Url;
            for (int hop = 0; ; hop++)
            {
                RequireAllowed(current, source.AllowedHosts);

                using HttpResponseMessage response = await Http
                    .GetAsync(current, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);

                if (response.StatusCode is HttpStatusCode.Moved or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod
                    or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                {
                    if (hop >= MaxRedirects) throw new InstallFailure("Trop de redirections depuis l'adresse officielle.");
                    if (response.Headers.Location is not { } location)
                    {
                        throw new InstallFailure("L'adresse officielle a répondu par une redirection sans destination.");
                    }

                    current = location.IsAbsoluteUri ? location : new Uri(current, location);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new InstallFailure(response.StatusCode == HttpStatusCode.NotFound
                        ? "Le fichier n'existe plus à l'adresse officielle (erreur 404)."
                        : $"Le serveur officiel a refusé le téléchargement (erreur {(int)response.StatusCode}).");
                }

                await SaveAsync(response, destination, progress, timeout.Token).ConfigureAwait(false);
                return;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InstallFailure($"Le téléchargement a dépassé {DownloadTimeout.TotalMinutes:0} minutes : connexion trop lente ou coupée.");
        }
    }

    private static async Task SaveAsync(
        HttpResponseMessage response, string destination, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        long? total = response.Content.Headers.ContentLength;
        if (total > MaxBytes) throw new InstallFailure("Le fichier annoncé est anormalement gros : téléchargement refusé.");

        await using Stream input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);

        byte[] buffer = new byte[BufferSize];
        long received = 0;
        int lastPercent = -1;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            received += read;
            if (received > MaxBytes) throw new InstallFailure("Le fichier dépasse la taille maximale attendue : téléchargement interrompu.");

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

        if (total is > 0 && received != total) throw new InstallFailure("Le téléchargement s'est arrêté avant la fin du fichier.");
    }

    /// <summary>Taille non nulle, en-tête d'exécutable Windows (« MZ »), signature valide, éditeur attendu.</summary>
    private static void Verify(FileStream locked, string path, string expectedPublisher)
    {
        if (locked.Length == 0) throw new InstallFailure("Le fichier téléchargé est vide.");

        int first = locked.ReadByte();
        int second = locked.ReadByte();
        locked.Position = 0;
        if (first != 'M' || second != 'Z') throw new InstallFailure("Le fichier téléchargé n'est pas un programme Windows : il n'a pas été lancé.");

        SignatureCheck check = AuthenticodeVerifier.Check(path, locked.SafeFileHandle);
        if (!check.IsValid) throw new InstallFailure($"Installeur refusé, {check.Error} : il n'a pas été lancé.");

        if (!string.Equals(check.Publisher, expectedPublisher, StringComparison.OrdinalIgnoreCase))
        {
            throw new InstallFailure(
                $"Installeur refusé : il est signé par « {check.Publisher} » et non par « {expectedPublisher} ». Il n'a pas été lancé.");
        }
    }

    /// <summary>Lance l'installeur et attend sa fin. Le verrou n'est levé qu'une fois le processus démarré : Windows
    /// protège ensuite lui-même le fichier de l'exécutable en cours.</summary>
    internal static async Task<InstallOutcome> LaunchAsync(
        FileStream locked, string path, string arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(path, arguments)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(path)!,
        };

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
            return new InstallOutcome(false, "Installation annulée : l'autorisation de Windows a été refusée.");
        }
        finally
        {
            locked.Dispose();
        }

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

            return process.ExitCode switch
            {
                0 => new InstallOutcome(true, "Installation terminée."),
                ExitRebootRequired => new InstallOutcome(true, "Installation terminée : Windows demande un redémarrage."),
                var code => new InstallOutcome(false, $"L'installeur s'est terminé avec une erreur (code {code})."),
            };
        }
    }

    /// <summary>HTTPS obligatoire, hôte dans la liste. Lève <see cref="InstallFailure"/> sinon.</summary>
    internal static void RequireAllowed(Uri uri, IReadOnlyList<string> allowedHosts)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InstallFailure("Téléchargement refusé : seule une adresse HTTPS est acceptée.");
        }

        foreach (string allowed in allowedHosts)
        {
            bool match = allowed.StartsWith("*.", StringComparison.Ordinal)
                ? uri.Host.EndsWith(allowed[1..], StringComparison.OrdinalIgnoreCase)
                : string.Equals(uri.Host, allowed, StringComparison.OrdinalIgnoreCase);
            if (match) return;
        }

        throw new InstallFailure($"Téléchargement refusé : « {uri.Host} » n'est pas une source officielle connue.");
    }

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PCPerfSuite", "1.0"));
        return client;
    }

    private static void TryDeleteFolder(string folder)
    {
        try { Directory.Delete(folder, recursive: true); }
        catch { /* best-effort : un dossier temporaire oublié ne gêne personne, et Windows nettoie %TEMP% */ }
    }

    /// <summary>Échec dont le message est destiné à l'utilisateur. Interne : jamais visible hors de cette classe.</summary>
    internal sealed class InstallFailure(string message) : Exception(message);
}
