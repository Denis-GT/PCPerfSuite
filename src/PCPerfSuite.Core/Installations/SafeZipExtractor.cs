using System.IO.Compression;

namespace PCPerfSuite.Core.Installations;

/// <summary>Issue d'une extraction : <paramref name="ExtractedPath"/> est le dossier (archive entière) ou le fichier
/// (une seule entrée) écrit, null en cas d'échec, que <paramref name="Error"/> explique.</summary>
public readonly record struct ZipExtractionResult(bool Succeeded, string? Error, string? ExtractedPath = null);

/// <summary>
/// Extraction d'une archive téléchargée, sans jamais écrire hors du dossier choisi.
///
/// Une entrée nommée « ..\..\Windows\evil.dll », « C:\evil.exe » ou « fichier.exe:flux » (« zip-slip ») est refusée,
/// et avec elle toute l'archive : une archive qui en contient une n'est pas celle de l'éditeur. La taille totale et le
/// nombre d'entrées sont bornés (archive « bombe »), contrôlés sur ce qui est réellement décompressé et pas seulement
/// sur ce que l'archive annonce. Aucun fichier existant n'est écrasé.
///
/// Best-effort (règle 2) : ne lève jamais, renvoie un échec avec son message. En cas d'échec, ce qui a déjà été écrit
/// reste dans le dossier de destination : à l'appelant de l'effacer (il l'a créé exprès, vide).
/// </summary>
public static class SafeZipExtractor
{
    public const int DefaultMaxEntries = 20_000;
    private const int BufferSize = 81920;

    /// <summary>Extrait toute l'archive dans <paramref name="destination"/>, qui doit exister. Annulable entre deux blocs ;
    /// <paramref name="progress"/> reçoit « Extraction… 1 200 / 3 300 fichiers », au plus quelques fois par seconde.</summary>
    public static ZipExtractionResult ExtractAll(string zipPath, string destination, long maxTotalBytes, int maxEntries = DefaultMaxEntries,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        try
        {
            string root = RootOf(destination);
            using ZipArchive archive = ZipFile.OpenRead(zipPath);

            if (archive.Entries.Count > maxEntries) return Fail($"L'archive contient plus de {maxEntries} fichiers : extraction refusée.");

            // Tout est contrôlé avant la première écriture : une archive piégée ne laisse rien derrière elle.
            var targets = new List<(ZipArchiveEntry Entry, string Path, bool IsFolder)>(archive.Entries.Count);
            long declared = 0;
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                if (TargetPath(root, entry.FullName) is not { } target) return Fail(UnsafeEntryMessage(entry.FullName));

                bool isFolder = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
                declared += Math.Max(0, entry.Length);
                if (declared > maxTotalBytes) return Fail(TooLargeMessage(maxTotalBytes));
                targets.Add((entry, target, isFolder));
            }

            long written = 0;
            int done = 0;
            int files = targets.Count(t => !t.IsFolder);
            long lastReport = 0;
            byte[] buffer = new byte[BufferSize];
            foreach ((ZipArchiveEntry entry, string target, bool isFolder) in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (isFolder)
                {
                    Directory.CreateDirectory(target);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                written = CopyEntry(entry, target, written, maxTotalBytes, buffer, cancellationToken);

                done++;
                if (progress is not null && (Environment.TickCount64 - lastReport > 250 || done == files))
                {
                    lastReport = Environment.TickCount64;
                    progress.Report($"Extraction… {done} / {files} fichiers");
                }
            }

            return new ZipExtractionResult(true, null, root);
        }
        catch (ZipLimitException ex)
        {
            return Fail(ex.Message);
        }
        catch (OperationCanceledException)
        {
            return Fail("Extraction annulée.");
        }
        catch (InvalidDataException ex)
        {
            return Fail($"Archive endommagée ou illisible ({ex.Message}).");
        }
        catch (Exception ex)
        {
            return Fail($"Extraction impossible ({ex.Message}).");
        }
    }

    /// <summary>Extrait la seule entrée dont le nom de fichier (sans son dossier) correspond à <paramref name="pattern"/>
    /// (« RTSSSetup*.exe », casse ignorée), dans <paramref name="destination"/>. Aucune, ou plusieurs : refus, l'archive
    /// n'a pas la forme connue.</summary>
    public static ZipExtractionResult ExtractSingle(string zipPath, string pattern, string destination, long maxBytes,
        CancellationToken cancellationToken = default)
    {
        try
        {
            string root = RootOf(destination);
            using ZipArchive archive = ZipFile.OpenRead(zipPath);

            List<ZipArchiveEntry> matches = archive.Entries
                .Where(e => !e.FullName.EndsWith('/') && !e.FullName.EndsWith('\\') && Wildcard.IsMatch(pattern, FileNameOf(e.FullName)))
                .ToList();

            if (matches.Count == 0) return Fail($"L'archive ne contient pas le fichier attendu ({pattern}).");
            if (matches.Count > 1) return Fail($"L'archive contient plusieurs fichiers « {pattern} » : elle n'a pas la forme attendue.");

            ZipArchiveEntry entry = matches[0];
            if (TargetPath(root, entry.FullName) is null) return Fail(UnsafeEntryMessage(entry.FullName));

            string name = FileNameOf(entry.FullName);
            if (TargetPath(root, name) is not { } target) return Fail(UnsafeEntryMessage(entry.FullName));
            if (entry.Length > maxBytes) return Fail(TooLargeMessage(maxBytes));

            CopyEntry(entry, target, 0, maxBytes, new byte[BufferSize], cancellationToken);
            return new ZipExtractionResult(true, null, target);
        }
        catch (ZipLimitException ex)
        {
            return Fail(ex.Message);
        }
        catch (OperationCanceledException)
        {
            return Fail("Extraction annulée.");
        }
        catch (InvalidDataException ex)
        {
            return Fail($"Archive endommagée ou illisible ({ex.Message}).");
        }
        catch (Exception ex)
        {
            return Fail($"Extraction impossible ({ex.Message}).");
        }
    }

    /// <summary>Chemin complet d'une entrée sous <paramref name="root"/>, ou null si elle en sortirait ou si son nom
    /// n'est pas un chemin relatif ordinaire (lecteur, chemin absolu, « .. », flux de données « : »).</summary>
    internal static string? TargetPath(string root, string entryName)
    {
        if (string.IsNullOrEmpty(entryName) || entryName.Contains(':') || entryName.Contains('\0')) return null;

        string relative = entryName.Replace('\\', '/');
        if (relative.StartsWith('/')) return null;

        string[] segments = relative.TrimEnd('/').Split('/');
        if (segments.Any(s => s.Length == 0 || s == "." || s == "..")) return null;
        if (segments.Any(s => s.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)) return null;

        // Windows retire les points et espaces de fin (« .. . » redeviendrait « .. » une fois normalisé) et réserve les
        // noms de périphériques (CON, NUL…) : de tels noms n'existent pas dans une archive d'éditeur.
        if (segments.Any(s => s.EndsWith('.') || s.EndsWith(' ') || IsReservedDeviceName(s))) return null;

        // Dernier rempart, celui qui compte : le chemin normalisé doit rester sous la racine.
        string full = Path.GetFullPath(Path.Combine(root, Path.Combine(segments)));
        return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private static bool IsReservedDeviceName(string segment)
    {
        int dot = segment.IndexOf('.');
        return ReservedDeviceNames.Contains(dot < 0 ? segment : segment[..dot]);
    }

    private static string RootOf(string destination) => Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar);

    private static string FileNameOf(string entryName)
    {
        string normalized = entryName.Replace('\\', '/');
        int slash = normalized.LastIndexOf('/');
        return slash < 0 ? normalized : normalized[(slash + 1)..];
    }

    /// <summary>Copie une entrée en comptant ce qui sort vraiment du décompresseur : une archive qui ment sur ses
    /// tailles est arrêtée au plafond. Renvoie le total écrit depuis le début de l'extraction.</summary>
    private static long CopyEntry(ZipArchiveEntry entry, string target, long writtenSoFar, long maxTotalBytes, byte[] buffer,
        CancellationToken cancellationToken)
    {
        using Stream input = entry.Open();
        // Tampon d'écriture minimal : les écritures se font déjà par blocs de la taille du tampon de lecture.
        using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 1);

        long written = writtenSoFar;
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            written += read;
            if (written > maxTotalBytes) throw new ZipLimitException(TooLargeMessage(maxTotalBytes));
            output.Write(buffer, 0, read);
        }

        return written;
    }

    private static string UnsafeEntryMessage(string entryName)
        => $"L'archive contient un chemin dangereux (« {entryName} ») : extraction refusée, rien n'a été lancé.";

    private static string TooLargeMessage(long max)
        => $"L'archive décompressée dépasserait {max / (1024 * 1024)} Mo : extraction refusée.";

    private static ZipExtractionResult Fail(string message) => new(false, message);

    private sealed class ZipLimitException(string message) : Exception(message);
}

/// <summary>Motif de nom de fichier avec « * » seulement (aucun « ? », aucun dossier), casse ignorée.</summary>
public static class Wildcard
{
    public static bool IsMatch(string pattern, string name)
    {
        string[] parts = pattern.Split('*');
        if (parts.Length == 1) return string.Equals(pattern, name, StringComparison.OrdinalIgnoreCase);

        if (!name.StartsWith(parts[0], StringComparison.OrdinalIgnoreCase)) return false;
        int position = parts[0].Length;

        for (int i = 1; i < parts.Length - 1; i++)
        {
            int found = name.IndexOf(parts[i], position, StringComparison.OrdinalIgnoreCase);
            if (found < 0) return false;
            position = found + parts[i].Length;
        }

        string last = parts[^1];
        return name.Length - position >= last.Length && name.EndsWith(last, StringComparison.OrdinalIgnoreCase);
    }
}
