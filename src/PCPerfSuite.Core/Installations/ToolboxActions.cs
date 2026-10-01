using PCPerfSuite.Core.PowerSettings;
using PCPerfSuite.Core.SystemInfo;

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
/// n'est installé sans son geste) et ne lève jamais (règle 2).
///
/// Ce qui sera lancé est téléchargé dans un dossier que seuls les administrateurs peuvent modifier (ProgramDataFolder
/// pour un outil portable, dossier de travail d'<see cref="OfficialInstaller"/> pour un installeur), vérifié (taille,
/// SHA-256 du catalogue, signature et éditeur figés) avant d'être gardé, et revérifié avant chaque lancement. Ce qui est
/// remis à l'utilisateur passe par <see cref="AppDataPaths.ToolDownloadsFolder"/>, et n'arrive dans ses Téléchargements
/// qu'une fois vérifié. Un outil non signé n'est jamais lancé.
/// </summary>
public sealed class ToolboxActions
{
    /// <summary>Code de l'installeur de PawnIO quand cette version est déjà installée.</summary>
    private const int AlreadyInstalledExitCode = 183;

    private const string WorkFolderPrefix = ".partiel-";

    private readonly ToolCatalogStore _catalog;

    public ToolboxActions(ToolCatalogStore catalog) => _catalog = catalog;

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

    /// <summary>Télécharge, vérifie, puis dépose le fichier dans les Téléchargements de l'utilisateur (ou les
    /// Téléchargements publics si l'app tourne sous un autre compte), marqué comme venu d'Internet.</summary>
    public async Task<ToolActionOutcome> DownloadForUserAsync(ToolDefinition tool, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (_catalog.ReleaseOf(tool.Id) is not { } release) return NoRelease(tool);
        if (UserDownloads.Choose() is not { } downloads)
        {
            return Fail("Le dossier Téléchargements est introuvable sur ce compte : utilise le lien ci-dessus dans ton navigateur.");
        }

        string? work = null;
        try
        {
            work = Path.Combine(AppDataPaths.Current.ToolDownloadsFolder, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            string partial = Path.Combine(work, release.FileName);

            DownloadOutcome download = await OfficialInstaller.DownloadToFileAsync(SourceFor(tool, release), partial, progress, cancellationToken)
                .ConfigureAwait(false);
            if (!download.Succeeded) return new ToolActionOutcome(false, download.Message, download.Failure);

            string destination = UserDownloads.UniquePath(downloads.Path, release.FileName);
            File.Copy(partial, destination, overwrite: false);
            UserDownloads.MarkFromInternet(destination, release.Url);

            string where = downloads.IsPublic
                ? "dans les Téléchargements publics (C:\\Users\\Public\\Downloads), visibles de tous les comptes : PCPerfSuite tourne sous un autre compte que le tien"
                : "dans tes Téléchargements";
            string verified = tool.IsSigned ? $"empreinte et signature de {tool.ExpectedPublisher} vérifiées" : "empreinte vérifiée";
            string run = tool.IsSigned ? "" : " Il n'est pas signé par son éditeur : PCPerfSuite ne le lance pas.";
            return new ToolActionOutcome(true, $"{Path.GetFileName(destination)} est {where} ({verified}).{run}", Path: destination);
        }
        catch (Exception ex)
        {
            return Fail($"Le fichier n'a pas pu être déposé ({ex.Message}).");
        }
        finally
        {
            if (work is not null) TryDeleteFolder(work);
        }
    }

    /// <summary>Télécharge l'outil portable dans le dossier sécurisé (<c>%ProgramData%\PCPerfSuite\Tools\&lt;id&gt;\&lt;version&gt;</c>),
    /// extrait l'archive sans laisser rien sortir du dossier, vérifie l'exe principal, puis retire les versions plus
    /// anciennes. Rien n'est lancé.</summary>
    public async Task<ToolActionOutcome> InstallPortableAsync(ToolDefinition tool, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (!tool.IsPortable || tool.ExpectedPublisher is not { } publisher || tool.LaunchFile is null)
        {
            return Fail("Cet outil ne s'utilise pas en version portable.");
        }

        if (_catalog.ReleaseOf(tool.Id) is not { } release) return NoRelease(tool);

        SecureFolderResult folder = ProgramDataFolder.TryEnsure(ProgramDataFolder.ToolsFolderName, tool.Id);
        if (folder.Path is not { } toolFolder) return Fail($"Le dossier sécurisé des outils est refusé : {folder.Error}.");

        string work = Path.Combine(toolFolder, WorkFolderPrefix + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(work);
            string downloaded = Path.Combine(work, tool.Delivery == ToolDelivery.PortableExe ? tool.LaunchFile : release.FileName);

            DownloadOutcome download = await OfficialInstaller
                .DownloadToFileAsync(SourceFor(tool, release, Path.GetFileName(downloaded)), downloaded, progress, cancellationToken)
                .ConfigureAwait(false);
            if (!download.Succeeded) return new ToolActionOutcome(false, download.Message, download.Failure);

            string payload = work;
            if (tool.Delivery == ToolDelivery.PortableZip)
            {
                progress?.Report("Extraction…");
                payload = Path.Combine(work, "contenu");
                Directory.CreateDirectory(payload);
                ZipExtractionResult extraction = SafeZipExtractor.ExtractAll(downloaded, payload, tool.MaxExtractedBytes);
                if (!extraction.Succeeded) return new ToolActionOutcome(false, extraction.Error!, DownloadFailureKind.Mismatch);
            }

            progress?.Report("Vérification de l'outil…");
            string executable = Path.Combine(payload, tool.LaunchFile);
            if (!File.Exists(executable)) return Mismatch($"L'archive ne contient pas {tool.LaunchFile} : elle n'a pas la forme attendue.");
            if (!OfficialInstaller.TryVerifySignedFile(executable, InstallerFileKind.Exe, publisher, out string? signatureError))
            {
                return Mismatch(signatureError!);
            }

            string versionFolder = Path.Combine(toolFolder, release.Version);
            if (Directory.Exists(versionFolder)) Directory.Delete(versionFolder, recursive: true);
            Directory.Move(payload, versionFolder);
            RemoveOtherVersions(toolFolder, release.Version);

            return new ToolActionOutcome(true, $"{tool.Name} {release.Version} est prêt (signature de {publisher} vérifiée).");
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
            TryDeleteFolder(work);
        }
    }

    /// <summary>Télécharge l'installeur officiel (ou l'archive qui le contient), le vérifie et le lance. L'app étant
    /// élevée, l'installeur s'ouvre sans invite de Windows : l'appelant a demandé confirmation avant.</summary>
    public async Task<ToolActionOutcome> RunInstallerAsync(ToolDefinition tool, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (!tool.IsInstaller || tool.ExpectedPublisher is not { } publisher) return Fail("Cet outil ne s'installe pas depuis PCPerfSuite.");
        if (_catalog.ReleaseOf(tool.Id) is not { } release) return NoRelease(tool);

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
                work = OfficialInstaller.CreateWorkFolder();
                string archive = Path.Combine(work, release.FileName);
                DownloadOutcome download = await OfficialInstaller.DownloadToFileAsync(SourceFor(tool, release), archive, progress, cancellationToken)
                    .ConfigureAwait(false);
                if (!download.Succeeded) return new ToolActionOutcome(false, download.Message, download.Failure);

                string installerFolder = Path.Combine(work, "installeur");
                Directory.CreateDirectory(installerFolder);
                ZipExtractionResult extraction = SafeZipExtractor.ExtractSingle(archive, tool.LaunchFile!, installerFolder, tool.MaxBytes);
                if (!extraction.Succeeded) return new ToolActionOutcome(false, extraction.Error!, DownloadFailureKind.Mismatch);

                outcome = await OfficialInstaller.RunVerifiedAsync(extraction.ExtractedPath!, InstallerFileKind.Exe, publisher,
                    tool.InstallerArguments, progress, cancellationToken).ConfigureAwait(false);
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

        if (!outcome.Succeeded) return new ToolActionOutcome(false, outcome.Message, ExitCode: outcome.ExitCode);

        RecordInstall(tool, release.Version);
        return new ToolActionOutcome(true, outcome.Message, ExitCode: outcome.ExitCode);
    }

    /// <summary>Lance l'outil sans les droits administrateur de PCPerfSuite (<see cref="UnelevatedLauncher"/>). Un outil
    /// portable est revérifié avant : signature et éditeur de son exe.</summary>
    public static ToolActionOutcome Launch(ToolDefinition tool, ToolInstallState state)
    {
        if (!tool.IsSigned && tool.Delivery != ToolDelivery.BuiltIn) return Fail("Cet outil n'est pas signé par son éditeur : PCPerfSuite ne le lance pas.");
        if (state.ExecutablePath is not { } path || !File.Exists(path)) return Fail($"L'emplacement de {tool.Name} est introuvable : lance-le depuis le menu Démarrer.");

        if (state.IsPortable
            && !OfficialInstaller.TryVerifySignedFile(path, InstallerFileKind.Exe, tool.ExpectedPublisher!, out string? signatureError))
        {
            return Mismatch($"{signatureError} Supprime l'outil puis réinstalle-le.");
        }

        return UnelevatedLauncher.Launch(path, "", out string? error) switch
        {
            UnelevatedLaunchResult.Launched => new ToolActionOutcome(true,
                $"{tool.Name} est lancé avec tes droits habituels : s'il a besoin d'être administrateur, Windows le demande."),
            UnelevatedLaunchResult.ShownInExplorer => new ToolActionOutcome(true,
                $"L'Explorateur est ouvert sur {Path.GetFileName(path)} : double-clique dessus pour le lancer."),
            _ => Fail(error ?? $"{tool.Name} n'a pas pu être lancé."),
        };
    }

    /// <summary>Supprime le dossier de l'outil portable, toutes versions comprises.</summary>
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
            string name = Path.GetFileName(folder);
            if (name == keep || name.StartsWith(WorkFolderPrefix, StringComparison.Ordinal)) continue;
            TryDeleteFolder(folder);
        }
    }

    private static ToolActionOutcome NoRelease(ToolDefinition tool)
        => new(false, $"Le catalogue n'a pas de lien direct vérifié pour {tool.Name} : passe par la page officielle.",
            DownloadFailureKind.LinkUnavailable);

    private static ToolActionOutcome Fail(string message) => new(false, message, DownloadFailureKind.Other);

    private static ToolActionOutcome Mismatch(string message) => new(false, message, DownloadFailureKind.Mismatch);

    private static void TryDeleteFolder(string folder)
    {
        try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
        catch { /* best-effort : un dossier de travail oublié n'est jamais lancé, et le suivant porte un autre nom */ }
    }
}
