using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using PCPerfSuite.Core.SystemInfo;

namespace PCPerfSuite.Core.Cache;

public sealed class CacheCleanerService
{
    /// <summary>
    /// Résout les variables d'environnement d'un chemin de cache.
    ///
    /// Attention : %TEMP%, %LOCALAPPDATA% et %APPDATA% sont ceux du *processus*. L'app étant manifestée
    /// requireAdministrator, lancée par « Exécuter en tant qu'administrateur » depuis un compte standard
    /// elle tourne sous le compte administrateur, et ces chemins désignent alors le profil de celui-ci.
    /// Le cas est détecté par <see cref="SessionUser.IsOtherProfile"/> et annoncé à l'utilisateur (onglet
    /// Nettoyage et diagnostic de compatibilité) plutôt que de laisser croire que ses caches sont vides.
    /// </summary>
    public static string ResolvePath(string template) => Environment.ExpandEnvironmentVariables(template);

    public async Task<CacheCategoryResult> ScanAsync(CacheCategory category, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            var existing = new List<string>();
            long total = 0;

            foreach (string template in category.PathTemplates)
            {
                string path = ResolvePath(template);
                if (!Directory.Exists(path)) continue;

                existing.Add(path);
                total += category.FileNamePattern is null
                    ? GetDirectorySize(path, ct)
                    : GetMatchingFilesSize(path, category.FileNamePattern, ct);
            }

            return new CacheCategoryResult
            {
                Category = category,
                ExistingPaths = existing,
                SizeBytes = total,
            };
        }, ct);
    }

    public async Task<IReadOnlyList<CacheCategoryResult>> ScanAllAsync(
        IEnumerable<CacheCategory> categories, IProgress<CacheCategoryResult>? progress = null, CancellationToken ct = default)
    {
        var results = new List<CacheCategoryResult>();
        foreach (CacheCategory category in categories)
        {
            ct.ThrowIfCancellationRequested();
            CacheCategoryResult result = await ScanAsync(category, ct);
            results.Add(result);
            progress?.Report(result);
        }
        return results;
    }

    public async Task<CacheCleanResult> CleanAsync(CacheCategory category, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            long freed = 0;
            int deleted = 0;
            int skipped = 0;
            var errors = new List<string>();

            foreach (string template in category.PathTemplates)
            {
                string path = ResolvePath(template);
                if (!Directory.Exists(path)) continue;

                IEnumerable<string> files = category.FileNamePattern is null
                    ? SafeEnumerateFiles(path, "*")
                    : SafeEnumerateFiles(path, category.FileNamePattern);

                foreach (string file in files)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var info = new FileInfo(file);
                        long size = info.Length;
                        info.Attributes = FileAttributes.Normal;
                        info.Delete();
                        freed += size;
                        deleted++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // Fichier verrouillé par un processus (pilote, jeu en cours...) — normal, on continue.
                        skipped++;
                        if (errors.Count < 25) errors.Add($"{Path.GetFileName(file)}: {ex.Message}");
                    }
                }

                if (category.RemoveEmptySubdirectories && category.FileNamePattern is null)
                {
                    TryRemoveEmptySubdirectories(path);
                }
            }

            return new CacheCleanResult
            {
                CategoryId = category.Id,
                BytesFreed = freed,
                FilesDeleted = deleted,
                FilesSkipped = skipped,
                Errors = errors,
            };
        }, ct);
    }

    public static void OpenFolder(string path)
    {
        if (!Directory.Exists(path)) return;
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{path}\"",
            UseShellExecute = true,
        });
    }

    /// <summary>Vide toutes les corbeilles du système, sans confirmation ni fenêtre de progression.
    /// Renvoie faux si Windows a refusé (best-effort : jamais d'exception). Une corbeille déjà vide fait
    /// échouer l'appel avec S_FALSE, ce qui n'est pas une erreur : le résultat voulu est atteint.</summary>
    public static bool EmptyRecycleBin()
    {
        const uint SHERB_NOCONFIRMATION = 0x00000001;
        const uint SHERB_NOPROGRESSUI = 0x00000002;
        const uint SHERB_NOSOUND = 0x00000004;
        const int S_OK = 0;
        const int S_FALSE = 1;
        // HRESULT renvoyé quand il n'y a rien à vider selon les versions de Windows.
        const int E_UNEXPECTED = unchecked((int)0x8000FFFF);

        try
        {
            int hr = SHEmptyRecycleBinW(IntPtr.Zero, null, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);
            return hr is S_OK or S_FALSE or E_UNEXPECTED;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Taille et nombre d'éléments de toutes les corbeilles du système, ou null si Windows ne
    /// répond pas (best-effort : la corbeille est alors affichée « N/D » plutôt que 0 octet).</summary>
    public static RecycleBinInfo? QueryRecycleBin()
    {
        try
        {
            var info = new SHQUERYRBINFO { cbSize = (uint)Marshal.SizeOf<SHQUERYRBINFO>() };
            int hr = SHQueryRecycleBinW(null, ref info);
            if (hr != 0) return null;

            return new RecycleBinInfo(info.i64Size, info.i64NumItems);
        }
        catch (Exception)
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct SHQUERYRBINFO
    {
        public uint cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBinW(IntPtr hwnd, string? pszRootPath, uint dwFlags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBinW(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

    private static long GetDirectorySize(string path, CancellationToken ct)
    {
        long size = 0;
        foreach (string file in SafeEnumerateFiles(path, "*"))
        {
            ct.ThrowIfCancellationRequested();
            try { size += new FileInfo(file).Length; }
            catch { /* fichier supprimé/inaccessible entre-temps, on ignore */ }
        }
        return size;
    }

    private static long GetMatchingFilesSize(string path, string pattern, CancellationToken ct)
    {
        long size = 0;
        foreach (string file in SafeEnumerateFiles(path, pattern))
        {
            ct.ThrowIfCancellationRequested();
            try { size += new FileInfo(file).Length; }
            catch { /* ignore */ }
        }
        return size;
    }

    private static IEnumerable<string> SafeEnumerateFiles(string root, string pattern)
    {
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            string dir = stack.Pop();
            IEnumerable<string> files = Array.Empty<string>();
            try { files = Directory.EnumerateFiles(dir, pattern); }
            catch { /* dossier protégé, on saute */ }

            foreach (string file in files) yield return file;

            IEnumerable<string> subDirs = Array.Empty<string>();
            try { subDirs = Directory.EnumerateDirectories(dir); }
            catch { /* ignore */ }

            foreach (string sub in subDirs) stack.Push(sub);
        }
    }

    private static void TryRemoveEmptySubdirectories(string root)
    {
        try
        {
            foreach (string dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                         .OrderByDescending(d => d.Length))
            {
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(dir).Any())
                    {
                        Directory.Delete(dir);
                    }
                }
                catch { /* ignore */ }
            }
        }
        catch { /* ignore */ }
    }
}
