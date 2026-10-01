using PCPerfSuite.Core.PowerSettings;

namespace PCPerfSuite.Core.Installations;

/// <summary>Issue d'une action de la Boîte à outils, avec un message prêt à afficher. <paramref name="Path"/> : fichier
/// déposé pour l'utilisateur. <paramref name="ExitCode"/> : code rendu par un installeur.</summary>
public sealed record ToolActionOutcome(
    bool Succeeded,
    string Message,
    DownloadFailureKind Failure = DownloadFailureKind.None,
    string? Path = null,
    int? ExitCode = null);

/// <summary>
/// Ce que la Boîte à outils fait d'un outil : le remettre dans Téléchargements, le déposer en portable dans le dossier
/// sécurisé, lancer son installeur, le lancer, le supprimer. Chaque action vient d'un clic de l'utilisateur (rien
/// n'est installé sans son geste), porte sur la version qu'il a vue et confirmée (<see cref="ToolRelease"/> passée en
/// paramètre, jamais relue dans le catalogue entre-temps), et ne lève jamais (règle 2).
///
/// Tout fichier téléchargé, qu'il soit lancé ou remis à l'utilisateur, passe par un dossier que seuls les
/// administrateurs peuvent modifier (ProgramDataFolder, ou le dossier de travail d'<see cref="OfficialInstaller"/>) :
/// ce qui est vérifié (taille, SHA-256 du catalogue, signature et éditeur figés) est exactement ce qui est gardé. Un
/// outil portable est revérifié avant chaque lancement. Un outil non signé n'est jamais lancé.
///
/// Les préparations synchrones (création de dossier, icacls, suppression) tournent hors du thread appelant.
/// </summary>
public static class ToolboxActions
{
    /// <summary>Code de l'installeur de PawnIO quand cette version est déjà installée.</summary>
    private const int AlreadyInstalledExitCode = 183;

    private const string WorkFolderPrefix = ".partiel-";

    /// <summary>Fichiers jamais repris d'une ancienne version : les programmes viennent de la nouvelle, vérifiée.</summary>
    private static readonly HashSet<string> ProgramExtensions = new(StringComparer.OrdinalIgnoreCase) { ".exe", ".dll", ".sys", ".com", ".scr", ".cpl", ".ocx" };

    /// <summary>Source d'<see cref="OfficialInstaller"/> pour cette version : adresse et empreinte du catalogue, hôtes,
    /// éditeur, type et limites de la définition figée.</summary>
    public static OfficialInstallerSource SourceFor(ToolDefinition tool, ToolRelease release, string? fileName = null)
        => new(release.Url, tool.AllowedHosts, fileName ?? release.FileName, tool.InstallerArguments, tool.ExpectedPublisher)
        {
            Kind = tool.FileKind,
            ExpectedSha256 = release.Sha256,
            ExpectedSize = release.Size,
            MaxBytes = tool.MaxBytes,
            DownloadTimeout = tool.DownloadTimeout,
        };

    /// <summary>Ce qui a été vérifié sur le fichier remis à l'utilisateur : une archive n'est pas signée elle-même, seule
    /// son empreinte compte.</summary>
    public static string VerifiedDescription(ToolDefinition tool)
        => tool.IsSigned && tool.FileKind != InstallerFileKind.Zip
            ? $"empreinte et signature de {tool.ExpectedPublisher} vérifiées"
            : "empreinte vérifiée";

    /// <summary>Télécharge et vérifie dans un dossier de travail protégé, puis dépose une copie dans les Téléchargements
    /// de l'utilisateur (ou les Téléchargements publics si l'app tourne sous un autre compte), marquée comme venue
    /// d'Internet.</summary>
    public static async Task<ToolActionOutcome> DownloadForUserAsync(ToolDefinition tool, ToolRelease? release,
        IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (release is null || release.ToolId != tool.Id) return NoRelease(tool);
        if (UserDownloads.Choose() is not { } downloads)
        {
            return Fail("Le dossier Téléchargements est introuvable sur ce compte : utilise le lien ci-dessus dans ton navigateur.");
        }

        if (UserDownloads.RefusalReason(downloads.Path) is { } refusal)
        {
            return Fail($"Fichier non déposé : {refusal}. Utilise le lien ci-dessus dans ton navigateur.");
        }

        string? work = null;
        try
        {
            work = await Task.Run(OfficialInstaller.CreateWorkFolder, cancellationToken).ConfigureAwait(false);
            string partial = System.IO.Path.Combine(work, release.FileName);

            DownloadOutcome download = await OfficialInstaller.DownloadToFileAsync(SourceFor(tool, release), partial, progress, cancellationToken)
                .ConfigureAwait(false);
            if (!download.Succeeded) return new ToolActionOutcome(false, download.Message, download.Failure);

            progress?.Report("Copie dans Téléchargements…");
            string destination = await Task.Run(() =>
            {
                // La copie part du fichier vérifié, dans un dossier que l'utilisateur ne peut pas modifier, et crée un
                // fichier neuf qui prend les droits de son dossier : l'utilisateur peut ensuite le déplacer ou l'effacer.
                string target = UserDownloads.UniquePath(downloads.Path, release.FileName);
                File.Copy(partial, target, overwrite: false);
                UserDownloads.MarkFromInternet(target, release.Url);
                return target;
            }, cancellationToken).ConfigureAwait(false);

            string where = downloads.IsPublic
                ? $"dans les Téléchargements publics ({downloads.Path}), visibles de tous les comptes : PCPerfSuite tourne sous un autre compte que le tien"
                : $"dans tes Téléchargements ({downloads.Path})";
            string run = tool.IsSigned ? "" : " Il n'est pas signé par son éditeur : PCPerfSuite ne le lance pas.";
            return new ToolActionOutcome(true, $"{System.IO.Path.GetFileName(destination)} est {where}, {VerifiedDescription(tool)}.{run}",
                Path: destination);
        }
        catch (OperationCanceledException)
        {
            return Fail("Téléchargement annulé.");
        }
        catch (Exception ex)
        {
            return Fail($"Le fichier n'a pas pu être déposé ({ex.Message}).");
        }
        finally
        {
            if (work is not null) OfficialInstaller.DeleteWorkFolder(work);
        }
    }

    /// <summary>Télécharge l'outil portable dans le dossier sécurisé (<c>%ProgramData%\PCPerfSuite\Tools\&lt;id&gt;\&lt;version&gt;</c>),
    /// extrait l'archive sans laisser rien sortir du dossier, vérifie l'exe principal, reprend les réglages de l'ancienne
    /// version (fichiers que la nouvelle n'apporte pas, hors programmes), puis retire les anciennes versions. Rien n'est
    /// lancé.</summary>
    public static async Task<ToolActionOutcome> InstallPortableAsync(ToolDefinition tool, ToolRelease? release,
        IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (!tool.IsPortable || tool.ExpectedPublisher is not { } publisher || tool.LaunchFile is null)
        {
            return Fail("Cet outil ne s'utilise pas en version portable.");
        }

        if (release is null || release.ToolId != tool.Id) return NoRelease(tool);

        SecureFolderResult folder = await Task.Run(() => ProgramDataFolder.TryEnsure(ProgramDataFolder.ToolsFolderName, tool.Id), cancellationToken)
            .ConfigureAwait(false);
        if (folder.Path is not { } toolFolder) return Fail($"Le dossier sécurisé des outils est refusé : {folder.Error}.");

        // Restes d'une opération interrompue (app fermée en cours de route) : une seule opération par outil à la fois.
        await Task.Run(() => RemoveWorkFolders(toolFolder), cancellationToken).ConfigureAwait(false);

        string work = System.IO.Path.Combine(toolFolder, WorkFolderPrefix + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(work);
            string downloaded = System.IO.Path.Combine(work, tool.Delivery == ToolDelivery.PortableExe ? tool.LaunchFile : release.FileName);

            DownloadOutcome download = await OfficialInstaller
                .DownloadToFileAsync(SourceFor(tool, release, System.IO.Path.GetFileName(downloaded)), downloaded, progress, cancellationToken)
                .ConfigureAwait(false);
            if (!download.Succeeded) return new ToolActionOutcome(false, download.Message, download.Failure);

            return await Task.Run(() => Finish(tool, release, publisher, toolFolder, work, downloaded, progress, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Fail("Installation annulée : rien n'a été changé.");
        }
        catch (IOException ex)
        {
            return Fail($"{tool.Name} n'a pas pu être mis en place : un de ses fichiers est peut-être ouvert (outil encore lancé ?). {ex.Message}");
        }
        catch (Exception ex)
        {
            return Fail($"{tool.Name} n'a pas pu être mis en place ({ex.Message}).");
        }
        finally
        {
            await Task.Run(() => TryDeleteFolder(work), CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>Extraction, vérification, reprise des réglages, mise en place : la partie disque d'<see cref="InstallPortableAsync"/>.</summary>
    private static ToolActionOutcome Finish(ToolDefinition tool, ToolRelease release, string publisher, string toolFolder, string work,
        string downloaded, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        string payload = work;
        if (tool.Delivery == ToolDelivery.PortableZip)
        {
            progress?.Report("Extraction…");
            payload = System.IO.Path.Combine(work, "contenu");
            Directory.CreateDirectory(payload);
            ZipExtractionResult extraction = SafeZipExtractor.ExtractAll(downloaded, payload, tool.MaxExtractedBytes,
                progress: progress, cancellationToken: cancellationToken);
            if (!extraction.Succeeded) return new ToolActionOutcome(false, extraction.Error!, DownloadFailureKind.Mismatch);

            // L'exe sorti de l'archive n'a encore jamais été vérifié ; un exe autonome l'a été au téléchargement.
            progress?.Report("Vérification de l'outil…");
            string executable = System.IO.Path.Combine(payload, tool.LaunchFile!);
            if (!File.Exists(executable)) return Mismatch($"L'archive ne contient pas {tool.LaunchFile} : elle n'a pas la forme attendue.");
            if (!OfficialInstaller.TryVerifySignedFile(executable, InstallerFileKind.Exe, publisher, out string? signatureError))
            {
                return Mismatch(signatureError!);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        ToolInstallState previous = ToolDetection.DetectPortable(tool, toolFolder);
        int kept = previous is { IsPresent: true, Location: { } oldFolder } && !SameFolder(oldFolder, System.IO.Path.Combine(toolFolder, release.Version))
            ? CarryOverSettings(oldFolder, payload)
            : 0;

        string versionFolder = System.IO.Path.Combine(toolFolder, release.Version);
        if (Directory.Exists(versionFolder)) Directory.Delete(versionFolder, recursive: true);
        Directory.Move(payload, versionFolder);
        RemoveOtherVersions(toolFolder, release.Version);

        string settings = kept > 0 ? $" Ses réglages de la version {previous.Version} sont repris ({kept} fichier{(kept > 1 ? "s" : "")})." : "";
        return new ToolActionOutcome(true, $"{tool.Name} {release.Version} est prêt (signature de {publisher} vérifiée).{settings}");
    }

    /// <summary>Recopie dans <paramref name="newFolder"/> les fichiers de l'ancienne version qu'il n'a pas (réglages,
    /// historique : DiskInfo.ini, Smart\…), sans jamais reprendre un programme. Renvoie le nombre de fichiers repris.</summary>
    internal static int CarryOverSettings(string oldFolder, string newFolder)
    {
        int copied = 0;
        foreach (string file in Directory.EnumerateFiles(oldFolder, "*", SearchOption.AllDirectories))
        {
            if (ProgramExtensions.Contains(System.IO.Path.GetExtension(file))) continue;

            string relative = System.IO.Path.GetRelativePath(oldFolder, file);
            string target = System.IO.Path.Combine(newFolder, relative);
            if (File.Exists(target)) continue;

            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
            copied++;
        }

        return copied;
    }

    /// <summary>Télécharge l'installeur officiel (ou l'archive qui le contient), le vérifie et le lance. L'app étant
    /// élevée, l'installeur s'ouvre sans invite de Windows : l'appelant a demandé confirmation avant.</summary>
    public static async Task<ToolActionOutcome> RunInstallerAsync(ToolDefinition tool, ToolRelease? release,
        IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (!tool.IsInstaller || tool.ExpectedPublisher is not { } publisher) return Fail("Cet outil ne s'installe pas depuis PCPerfSuite.");
        if (release is null || release.ToolId != tool.Id) return NoRelease(tool);

        InstallOutcome outcome;
        if (tool.Delivery == ToolDelivery.Installer)
        {
            outcome = await OfficialInstaller.DownloadAndRunAsync(SourceFor(tool, release), progress, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            string? work = null;
            try
            {
                work = await Task.Run(OfficialInstaller.CreateWorkFolder, cancellationToken).ConfigureAwait(false);
                string archive = System.IO.Path.Combine(work, release.FileName);
                DownloadOutcome download = await OfficialInstaller.DownloadToFileAsync(SourceFor(tool, release), archive, progress, cancellationToken)
                    .ConfigureAwait(false);
                if (!download.Succeeded) return new ToolActionOutcome(false, download.Message, download.Failure);

                string installerFolder = System.IO.Path.Combine(work, "installeur");
                ZipExtractionResult extraction = await Task.Run(() =>
                {
                    Directory.CreateDirectory(installerFolder);
                    return SafeZipExtractor.ExtractSingle(archive, tool.LaunchFile!, installerFolder, tool.MaxBytes, cancellationToken);
                }, cancellationToken).ConfigureAwait(false);
                if (!extraction.Succeeded) return new ToolActionOutcome(false, extraction.Error!, DownloadFailureKind.Mismatch);

                outcome = await OfficialInstaller.RunVerifiedAsync(extraction.ExtractedPath!, InstallerFileKind.Exe, publisher,
                    tool.InstallerArguments, progress, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return Fail("Installation annulée avant le lancement de l'installeur.");
            }
            catch (Exception ex)
            {
                return Fail($"Installation impossible ({ex.Message}).");
            }
            finally
            {
                if (work is not null) OfficialInstaller.DeleteWorkFolder(work);
            }
        }

        if (outcome.ExitCode == AlreadyInstalledExitCode && tool.Detection == ToolDetectionKind.PawnIo)
        {
            return new ToolActionOutcome(true, "L'installeur indique que cette version de PawnIO est déjà installée.", ExitCode: outcome.ExitCode);
        }

        if (!outcome.Succeeded) return new ToolActionOutcome(false, outcome.Message, outcome.Failure, ExitCode: outcome.ExitCode);

        RecordInstall(tool, release.Version);
        return new ToolActionOutcome(true, outcome.Message, ExitCode: outcome.ExitCode);
    }

    /// <summary>Lance l'outil sans les droits administrateur de PCPerfSuite (<see cref="UnelevatedLauncher"/>). Un outil
    /// portable est revérifié avant : signature et éditeur de son exe. Lit et hache l'exe : hors du thread d'interface.</summary>
    public static ToolActionOutcome Launch(ToolDefinition tool, ToolInstallState state)
    {
        if (!tool.IsSigned && tool.Delivery != ToolDelivery.BuiltIn) return Fail("Cet outil n'est pas signé par son éditeur : PCPerfSuite ne le lance pas.");
        if (state.ExecutablePath is not { } path || !File.Exists(path)) return Fail($"L'emplacement de {tool.Name} est introuvable : lance-le depuis le menu Démarrer.");

        if (state.IsPortable
            && !OfficialInstaller.TryVerifySignedFile(path, InstallerFileKind.Exe, tool.ExpectedPublisher!, out string? signatureError))
        {
            return Mismatch($"{signatureError} Supprime l'outil puis réinstalle-le.");
        }

        return UnelevatedLauncher.TryLaunch(path, "", out string? error)
            ? new ToolActionOutcome(true, $"{tool.Name} est lancé avec tes droits habituels : s'il a besoin d'être administrateur, Windows le demande.")
            : Fail(error ?? $"{tool.Name} n'a pas pu être lancé.");
    }

    /// <summary>Supprime le dossier de l'outil portable, toutes versions comprises. Disque seulement : hors du thread
    /// d'interface.</summary>
    public static ToolActionOutcome RemovePortable(ToolDefinition tool)
    {
        try
        {
            if (ProgramDataFolder.TryGetExisting(ProgramDataFolder.ToolsFolderName, tool.Id) is not { } folder)
            {
                return new ToolActionOutcome(true, $"{tool.Name} n'est pas dans le dossier des outils.");
            }

            Directory.Delete(folder, recursive: true);
            return new ToolActionOutcome(true, $"{tool.Name} est supprimé.");
        }
        catch (Exception ex)
        {
            return Fail($"{tool.Name} n'a pas pu être supprimé : ferme-le s'il est lancé, puis réessaie ({ex.Message}).");
        }
    }

    /// <summary>Inscrit l'outil comme installé par la Boîte à outils (<see cref="ToolboxChanges"/> le liste).</summary>
    private static void RecordInstall(ToolDefinition tool, string version)
        => AppSettingsStore.Update(settings =>
        {
            settings.Toolbox.InstalledByApp.RemoveAll(record => record.Id == tool.Id);
            settings.Toolbox.InstalledByApp.Add(new ToolInstallRecord { Id = tool.Id, Version = version, InstalledUtc = DateTime.UtcNow });
        });

    private static void RemoveOtherVersions(string toolFolder, string keep)
    {
        foreach (string folder in Directory.EnumerateDirectories(toolFolder))
        {
            string name = System.IO.Path.GetFileName(folder);
            if (name == keep || name.StartsWith(WorkFolderPrefix, StringComparison.Ordinal)) continue;
            TryDeleteFolder(folder);
        }
    }

    private static void RemoveWorkFolders(string toolFolder)
    {
        foreach (string folder in Directory.EnumerateDirectories(toolFolder, WorkFolderPrefix + "*")) TryDeleteFolder(folder);
    }

    private static bool SameFolder(string a, string b)
        => string.Equals(System.IO.Path.GetFullPath(a).TrimEnd('\\'), System.IO.Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private static ToolActionOutcome NoRelease(ToolDefinition tool)
        => new(false, $"Le catalogue n'a pas de lien direct vérifié pour {tool.Name} : passe par la page officielle.",
            DownloadFailureKind.LinkUnavailable);

    private static ToolActionOutcome Fail(string message) => new(false, message, DownloadFailureKind.Other);

    private static ToolActionOutcome Mismatch(string message) => new(false, message, DownloadFailureKind.Mismatch);

    private static void TryDeleteFolder(string folder)
    {
        try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
        catch { /* best-effort : un dossier de travail oublié n'est jamais lancé, il sera repris la fois suivante */ }
    }
}
