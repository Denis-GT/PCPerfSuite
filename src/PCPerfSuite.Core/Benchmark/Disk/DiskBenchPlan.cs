namespace PCPerfSuite.Core.Benchmark.Disk;

public enum DiskIoOperation
{
    Read,
    Write,
}

/// <summary>Une phase du test disque : un profil (bloc, file d'attente, accès), une opération, une durée et, pour une
/// écriture, le volume écrit au plus (budget).</summary>
public sealed record DiskPhase(
    string ProfileKey, string ProfileLabel, DiskIoOperation Operation, int BlockBytes, int QueueDepth, bool IsRandom,
    double DurationSeconds, long MaxBytes)
{
    public const string ReadSuffix = ".lecture";
    public const string WriteSuffix = ".ecriture";

    /// <summary>Clé de la mesure (« seq1m-q8.lecture », « alea4k-q32.ecriture »).</summary>
    public string MeasurementKey => ProfileKey + (Operation == DiskIoOperation.Read ? ReadSuffix : WriteSuffix);

    public string Label => $"{ProfileLabel}, {(Operation == DiskIoOperation.Read ? "lecture" : "écriture")}";

    /// <summary>Les petits blocs se jugent aussi en opérations par seconde.</summary>
    public bool ReportsIops => BlockBytes <= 64 * 1024;
}

/// <summary>
/// Plan du test disque, en logique pure, façon CrystalDiskMark : séquentiel 1 Mo en file de 8 puis de 1, aléatoire 4 Ko
/// en file de 32 puis de 1, lecture puis écriture pour chaque profil. Un disque à plateaux ne passe que les files de 1
/// (les files profondes n'y mesurent que le réordonnancement). Le fichier est prérempli d'aléatoire (1 ×) avant toute
/// lecture ; le volume écrit est budgété : séquentiel 1 × la taille par phase, aléatoire 0,5 ×, soit 4 × la taille au
/// plus pour un SSD. Blocs et offsets sont des multiples du secteur physique. Durées expérimentales (règle 6).
/// </summary>
public sealed record DiskBenchPlan(long FileBytes, int SectorBytes, long WriteBudgetBytes, long PrefillBytes, bool IsRotational, IReadOnlyList<DiskPhase> Phases)
{
    public const long Mebibyte = 1L << 20;
    public const long MinimumFileBytes = 8 * Mebibyte;
    public const int DefaultWriteBudgetFactor = 4;
    public const double DefaultPhaseSeconds = 5;
    public const int SequentialBlockBytes = (int)Mebibyte;
    public const int RandomBlockBytes = 4096;
    public const string PrefillKey = "preremplissage";
    public const double SliceSeconds = 1;

    /// <summary>Tout ce que le test écrira : préremplissage et plafonds des phases d'écriture.</summary>
    public long PlannedWriteBytes => PrefillBytes + Phases.Where(p => p.Operation == DiskIoOperation.Write).Sum(p => p.MaxBytes);

    public static DiskBenchPlan Create(long fileBytes, int sectorBytes, double phaseSeconds, bool isRotational, long writeBudgetBytes = 0)
    {
        if (sectorBytes <= 0 || (sectorBytes & (sectorBytes - 1)) != 0) throw new ArgumentOutOfRangeException(nameof(sectorBytes), sectorBytes, "Le secteur doit être une puissance de deux.");
        if (phaseSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(phaseSeconds));
        long file = fileBytes - fileBytes % Mebibyte;
        if (file < MinimumFileBytes) throw new ArgumentOutOfRangeException(nameof(fileBytes), fileBytes, $"Au moins {MinimumFileBytes / Mebibyte} Mo.");
        if (writeBudgetBytes <= 0) writeBudgetBytes = DefaultWriteBudgetFactor * file;
        if (writeBudgetBytes <= file) throw new ArgumentOutOfRangeException(nameof(writeBudgetBytes), writeBudgetBytes, "Le budget d'écriture doit dépasser le préremplissage (1 × la taille).");

        int sequential = Math.Max(SequentialBlockBytes, sectorBytes);
        int random = Math.Max(RandomBlockBytes, sectorBytes);
        var profiles = new List<(string Key, string Label, int Block, int Queue, bool Random, long WriteMax)>
        {
            ("seq1m-q8", "Séquentiel 1 Mo, file 8", sequential, 8, false, file),
            ("seq1m-q1", "Séquentiel 1 Mo, file 1", sequential, 1, false, file),
            ("alea4k-q32", "Aléatoire 4 Ko, file 32", random, 32, true, file / 2),
            ("alea4k-q1", "Aléatoire 4 Ko, file 1", random, 1, true, file / 2),
        };
        if (isRotational) profiles.RemoveAll(p => p.Queue > 1);

        // Budget : le préremplissage est dû ; le reste se partage entre les écritures, réduites d'autant s'il manque.
        long remaining = writeBudgetBytes - file;
        long wanted = profiles.Sum(p => p.WriteMax);
        double factor = wanted > 0 ? Math.Min(1, (double)remaining / wanted) : 1;

        var phases = new List<DiskPhase>();
        foreach ((string key, string label, int block, int queue, bool isRandom, long writeMax) in profiles)
        {
            phases.Add(new DiskPhase(key, label, DiskIoOperation.Read, block, queue, isRandom, phaseSeconds, 0));
            long max = (long)(writeMax * factor);
            phases.Add(new DiskPhase(key, label, DiskIoOperation.Write, block, queue, isRandom, phaseSeconds, max - max % block));
        }

        return new DiskBenchPlan(file, sectorBytes, writeBudgetBytes, file, isRotational, phases);
    }

    /// <summary>Vrai quand le débit s'effondre au fil d'une écriture longue : le dernier tiers des tranches sous la moitié
    /// du premier. C'est la signature d'un cache SLC épuisé (ou d'un disque qui chauffe), à dire dans le résultat.</summary>
    public static bool LooksLikeCacheExhaustion(IReadOnlyList<double> slices)
    {
        if (slices.Count < 3) return false;
        int third = slices.Count / 3;
        double first = slices.Take(third).Average();
        double last = slices.Skip(slices.Count - third).Average();
        return first > 0 && last < first / 2;
    }
}
