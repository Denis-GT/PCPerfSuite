using System.Diagnostics;
using Microsoft.Win32.SafeHandles;
using PCPerfSuite.Core.Benchmark.Kernels;
using PCPerfSuite.Core.Benchmark.Protocol;
using PCPerfSuite.Core.Benchmark.Worker;

namespace PCPerfSuite.Core.Benchmark.Disk;

/// <summary>
/// Bench disque, côté worker : un fichier créé en <c>CreateNew</c>, ouvert sans cache (<c>FILE_FLAG_NO_BUFFERING</c>, E/S
/// asynchrones, tampons alignés sur la page), prérempli d'aléatoire de bout en bout (NTFS rendrait des zéros au-delà de
/// la longueur valide, et <c>SetFileValidData</c> est écarté), puis les phases du <see cref="DiskBenchPlan"/> : pour
/// chaque profil, lecture puis écriture, en gardant <c>QueueDepth</c> E/S en vol. Chaque phase est découpée en tranches
/// de 1 s : la médiane et le CV portent sur ces tranches. Mo/s décimaux (10⁶) et, pour le 4 Ko, IOPS. Le fichier est
/// supprimé en <c>finally</c>.
/// </summary>
public sealed class DiskBenchRunner
{
    public const string Unit = "Mo/s";
    public const string IopsUnit = "IOPS";
    public const string IopsSuffix = ".iops";
    public const double BytesPerMegabyte = 1e6;

    /// <summary>Données aléatoires écrites : 16 blocs de 1 Mo en rotation (pas de motif qu'un cache ou une compression
    /// matérielle sauraient avantager).</summary>
    private const int PoolBlocks = 16;
    private const int PoolBlockBytes = (int)DiskBenchPlan.Mebibyte;
    private const int PageBytes = 4096;
    private static readonly TimeSpan LiveReportInterval = TimeSpan.FromMilliseconds(250);

    public BenchJobResult Run(BenchJobRequest request, Action<BenchProgress>? progress, CancellationToken cancel)
        => RunAsync(request, progress, cancel).GetAwaiter().GetResult();

    public async Task<BenchJobResult> RunAsync(BenchJobRequest request, Action<BenchProgress>? progress, CancellationToken cancel)
    {
        DiskJobParameters p = request.Disk ?? new DiskJobParameters();
        if (string.IsNullOrWhiteSpace(p.Path)) return BenchJobResult.Failure(request.Id, request.Kind, "chemin du fichier de test absent");

        DiskBenchPlan plan;
        try
        {
            plan = DiskBenchPlan.Create(p.FileSizeBytes, p.SectorBytes, p.PhaseSeconds, p.IsRotational, p.WriteBudgetBytes);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return BenchJobResult.Failure(request.Id, request.Kind, ex.Message);
        }

        var result = new BenchJobResult { JobId = request.Id, Kind = request.Kind, IsComparable = true };
        var stopwatch = Stopwatch.StartNew();
        double plannedTotal = plan.Phases.Sum(ph => ph.DurationSeconds) + plan.Phases[0].DurationSeconds;
        double plannedDone = 0;
        long writtenBytes = 0;
        bool cacheExhaustion = false;
        string? deleteError = null;
        bool created = false;
        SafeFileHandle? handle = null;
        AlignedBuffer? pool = null;

        void Report(string phase, string? detail, double? value, string? unit = Unit)
            => progress?.Invoke(new BenchProgress
            {
                JobId = request.Id,
                Phase = phase,
                Percent = plannedTotal > 0 ? Math.Clamp(plannedDone / plannedTotal * 100, 0, 100) : 0,
                Detail = detail,
                Value = value,
                Unit = value is null ? null : unit,
            });

        try
        {
            try
            {
                handle = File.OpenHandle(p.Path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                    FileOptions.Asynchronous | NoBuffering, preallocationSize: plan.FileBytes);
                created = true;
            }
            catch (IOException ex) when (File.Exists(p.Path))
            {
                return BenchJobResult.Failure(request.Id, request.Kind, $"un fichier de test est déjà là ({ex.Message})");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return BenchJobResult.Failure(request.Id, request.Kind, $"fichier de test impossible à créer : {ex.Message}");
            }

            pool = AlignedBuffer.Allocate((long)PoolBlocks * PoolBlockBytes, PageBytes);
            new SeededRandom(CpuKernelCatalog.DefaultSeed).Fill(pool.AsSpan<byte>(0, PoolBlocks * PoolBlockBytes));
            Memory<byte> poolMemory = pool.AsMemory();

            // Préremplissage : séquentiel, file de 8, tout le fichier. Mesuré aussi (c'est une longue écriture : le cache
            // SLC s'y épuise).
            var prefill = new DiskPhase(DiskBenchPlan.PrefillKey, "Préremplissage", DiskIoOperation.Write, plan.Phases[0].BlockBytes, 8, false, double.MaxValue, plan.PrefillBytes);
            Report("préremplissage", $"{plan.FileBytes / DiskBenchPlan.Mebibyte} Mo aléatoires", null);
            DiskPhaseOutcome filled = await RunPhaseAsync(handle, plan, prefill, poolMemory, 0, v => Report("préremplissage", null, v), cancel).ConfigureAwait(false);
            writtenBytes += filled.Bytes;
            plannedDone += plan.Phases[0].DurationSeconds;
            cacheExhaustion |= DiskBenchPlan.LooksLikeCacheExhaustion(filled.SliceMegabytesPerSecond);
            result.Measurements.Add(BenchMeasurement.From(DiskBenchPlan.PrefillKey, "Préremplissage (écriture séquentielle)", Unit, filled.SliceMegabytesPerSecond));

            int phaseIndex = 1;
            foreach (DiskPhase phase in plan.Phases)
            {
                cancel.ThrowIfCancellationRequested();
                Report(phase.Label, $"{phase.QueueDepth} E/S en vol", null);
                DiskPhaseOutcome outcome = await RunPhaseAsync(handle, plan, phase, poolMemory, phaseIndex++, v => Report(phase.Label, null, v), cancel).ConfigureAwait(false);
                plannedDone += phase.DurationSeconds;
                if (phase.Operation == DiskIoOperation.Write)
                {
                    writtenBytes += outcome.Bytes;
                    if (!phase.IsRandom) cacheExhaustion |= DiskBenchPlan.LooksLikeCacheExhaustion(outcome.SliceMegabytesPerSecond);
                }
                result.Measurements.Add(BenchMeasurement.From(phase.MeasurementKey, phase.Label, Unit, outcome.SliceMegabytesPerSecond));
                if (phase.ReportsIops)
                {
                    result.Measurements.Add(BenchMeasurement.From(phase.MeasurementKey + IopsSuffix, phase.Label + " (IOPS)", IopsUnit, outcome.SliceIops));
                }
            }

            result.Succeeded = true;
        }
        catch (OperationCanceledException)
        {
            result.Succeeded = false;
            result.Error = BenchJobDispatcher.CancelledError;
        }
        catch (Exception ex)
        {
            result.Succeeded = false;
            result.Error = $"{ex.GetType().Name} : {ex.Message}";
        }
        finally
        {
            handle?.Dispose();
            pool?.Dispose();
            // Seul le fichier créé ici est supprimé : un fichier déjà là n'est pas à nous.
            deleteError = created ? DeleteWithRetries(p.Path) : null;
            result.DurationSeconds = stopwatch.Elapsed.TotalSeconds;
            result.Notes["fichier-mo"] = (plan.FileBytes / DiskBenchPlan.Mebibyte).ToString();
            result.Notes["secteur-o"] = plan.SectorBytes.ToString();
            result.Notes["budget-ecrit-mo"] = (plan.WriteBudgetBytes / DiskBenchPlan.Mebibyte).ToString();
            result.Notes["ecrit-mo"] = (writtenBytes / DiskBenchPlan.Mebibyte).ToString();
            result.Notes["disque-a-plateaux"] = plan.IsRotational ? "oui (files de 1 seulement)" : "non";
            result.Notes["e-s"] = "sans cache Windows (FILE_FLAG_NO_BUFFERING), asynchrones, sans write-through ni SetFileValidData";
            result.Notes["donnees"] = $"aléatoires, {PoolBlocks} blocs de 1 Mo en rotation";
            result.Notes["cache-slc"] = cacheExhaustion ? "chute du débit en écriture longue : cache SLC probablement épuisé" : "aucune chute de débit observée";
            result.Notes["fichier-supprime"] = !created ? "sans objet (jamais créé)" : deleteError is null ? "oui" : $"non : {deleteError}";
            result.Notes["unite"] = "Mo/s décimaux (10⁶ octets/s), médiane et CV sur des tranches de 1 s";
        }

        return result;
    }

    private const FileOptions NoBuffering = (FileOptions)0x20000000;

    private static async Task<DiskPhaseOutcome> RunPhaseAsync(SafeFileHandle handle, DiskBenchPlan plan, DiskPhase phase, Memory<byte> pool,
        int phaseIndex, Action<double> live, CancellationToken cancel)
    {
        long fileBlocks = plan.FileBytes / phase.BlockBytes;
        int queue = (int)Math.Max(1, Math.Min(phase.QueueDepth, fileBlocks));
        using AlignedBuffer? readBuffer = phase.Operation == DiskIoOperation.Read ? AlignedBuffer.Allocate((long)queue * phase.BlockBytes, PageBytes) : null;
        Memory<byte> readMemory = readBuffer?.AsMemory() ?? Memory<byte>.Empty;

        var random = new SeededRandom(CpuKernelCatalog.DefaultSeed ^ (ulong)phaseIndex);
        var slots = new Task<int>?[queue];
        long sequential = 0;
        long issuedBytes = 0;
        long issuedCount = 0;
        var slices = new List<double>();
        var sliceIops = new List<double>();
        long totalBytes = 0;
        long totalOps = 0;
        long sliceBytes = 0;
        long sliceOps = 0;
        var stopwatch = Stopwatch.StartNew();
        double sliceStart = 0;
        double lastLive = 0;
        bool stopIssuing = false;

        bool CanIssue()
        {
            if (stopIssuing || cancel.IsCancellationRequested) return false;
            if (stopwatch.Elapsed.TotalSeconds >= phase.DurationSeconds) return false;
            if (phase.Operation == DiskIoOperation.Write && issuedBytes + phase.BlockBytes > phase.MaxBytes) return false;
            return true;
        }

        Task<int> Issue(int slot)
        {
            long offset = phase.IsRandom
                ? (long)random.NextBelow((uint)Math.Min(fileBlocks, uint.MaxValue)) * phase.BlockBytes
                : (sequential++ % fileBlocks) * phase.BlockBytes;
            issuedBytes += phase.BlockBytes;
            long n = issuedCount++;
            if (phase.Operation == DiskIoOperation.Read)
            {
                return RandomAccess.ReadAsync(handle, readMemory.Slice(slot * phase.BlockBytes, phase.BlockBytes), offset, cancel).AsTask();
            }
            int poolOffset = (int)((n * phase.BlockBytes) % pool.Length);
            return WriteAsync(handle, pool.Slice(poolOffset, phase.BlockBytes), offset, cancel);
        }

        for (int i = 0; i < queue && CanIssue(); i++) slots[i] = Issue(i);

        while (true)
        {
            Task<int>[] active = slots.Where(t => t is not null).ToArray()!;
            if (active.Length == 0) break;
            Task<int> completed = await Task.WhenAny(active).ConfigureAwait(false);
            int slot = Array.IndexOf(slots, completed);
            int bytes = await completed.ConfigureAwait(false);
            totalBytes += bytes;
            totalOps++;
            sliceBytes += bytes;
            sliceOps++;

            double now = stopwatch.Elapsed.TotalSeconds;
            if (now - sliceStart >= DiskBenchPlan.SliceSeconds)
            {
                double seconds = now - sliceStart;
                slices.Add(sliceBytes / seconds / BytesPerMegabyte);
                sliceIops.Add(sliceOps / seconds);
                sliceStart = now;
                sliceBytes = 0;
                sliceOps = 0;
            }
            if (now - lastLive >= LiveReportInterval.TotalSeconds && now > 0)
            {
                live(totalBytes / now / BytesPerMegabyte);
                lastLive = now;
            }

            slots[slot] = CanIssue() ? Issue(slot) : null;
        }

        double end = stopwatch.Elapsed.TotalSeconds;
        double tail = end - sliceStart;
        if (sliceBytes > 0 && (slices.Count == 0 || tail >= DiskBenchPlan.SliceSeconds / 4))
        {
            slices.Add(sliceBytes / tail / BytesPerMegabyte);
            sliceIops.Add(sliceOps / tail);
        }
        if (end > 0) live(totalBytes / end / BytesPerMegabyte);

        return new DiskPhaseOutcome(totalBytes, totalOps, end, slices, sliceIops);
    }

    private static async Task<int> WriteAsync(SafeFileHandle handle, ReadOnlyMemory<byte> data, long offset, CancellationToken cancel)
    {
        await RandomAccess.WriteAsync(handle, data, offset, cancel).ConfigureAwait(false);
        return data.Length;
    }

    /// <summary>Supprime le fichier de test ; null si c'est fait (ou s'il n'existe pas), sinon la raison.</summary>
    private static string? DeleteWithRetries(string path)
    {
        string? error = null;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (!File.Exists(path)) return null;
                File.Delete(path);
                return null;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Thread.Sleep(200);
            }
        }
        return error;
    }
}

/// <summary>Issue d'une phase : octets et opérations, durée, et les tranches de 1 s en Mo/s et en IOPS.</summary>
public sealed record DiskPhaseOutcome(long Bytes, long Operations, double Seconds, IReadOnlyList<double> SliceMegabytesPerSecond, IReadOnlyList<double> SliceIops)
{
    public double MegabytesPerSecond => Seconds > 0 ? Bytes / Seconds / DiskBenchRunner.BytesPerMegabyte : 0;
}
