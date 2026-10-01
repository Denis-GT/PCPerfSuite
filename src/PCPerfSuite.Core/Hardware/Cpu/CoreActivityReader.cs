using System.Globalization;
using System.Runtime.InteropServices;
using PCPerfSuite.Core.Compatibility;

namespace PCPerfSuite.Core.Hardware.Cpu;

/// <summary>Activité d'un processeur logique entre deux relevés. Chaque valeur est null si son compteur manque.</summary>
/// <param name="UtilityPercent">Charge comme le Gestionnaire des tâches (vue des processeurs logiques), 0 à 100.</param>
/// <param name="IsParked">Parqué au moment du relevé (« Parking Status »).</param>
/// <param name="PerformancePercent">Fréquence relative à la fréquence de base, au-delà de 100 en turbo.</param>
public sealed record CoreActivity(double? UtilityPercent, bool? IsParked, double? PerformancePercent);

/// <summary>Un relevé : l'activité de chaque processeur logique lu.</summary>
public sealed record CoreActivitySample(IReadOnlyDictionary<LogicalProcessorId, CoreActivity> Processors);

/// <summary>
/// Charge, état parqué et fréquence relative de chaque processeur logique, par les compteurs de performances
/// Windows (PDH), sans pilote ni administrateur. Une seule requête pour les trois compteurs, ajoutés par leur nom
/// anglais (en français, « Parking Status » s'appelle « État de parcage ») :
/// - « % Processor Utility », plafonné à 100, la même mesure que le Gestionnaire des tâches (le temps processeur brut
///   et la charge par fil de LibreHardwareMonitor n'y collent pas) ;
/// - « Parking Status », 0 ou 1 ;
/// - « % Processor Performance ».
///
/// Réutilisable par le bench (#10). Pas thread-safe : un seul relevé à la fois, toujours depuis le même appelant. Les
/// compteurs de taux se calculent entre deux collectes : le premier relevé après la création n'a pas encore de charge.
/// </summary>
public sealed class CoreActivityReader : IDisposable
{
    public const string UtilityPath = @"\Processor Information(*)\% Processor Utility";
    public const string ParkingStatusPath = @"\Processor Information(*)\Parking Status";
    public const string PerformancePath = @"\Processor Information(*)\% Processor Performance";

    private IntPtr _query;
    private readonly IntPtr _utility;
    private readonly IntPtr _parking;
    private readonly IntPtr _performance;

    public CoreActivityReader()
    {
        try
        {
            if (PdhNative.PdhOpenQueryW(null, IntPtr.Zero, out _query) != 0)
            {
                _query = IntPtr.Zero;
                QueryUnavailable = new Unavailable(UnavailableCause.HardwareOrDriver,
                    "les compteurs de performances de Windows ne répondent pas (service désactivé ou compteurs abîmés)");
                return;
            }

            _utility = Add(UtilityPath);
            _parking = Add(ParkingStatusPath);
            _performance = Add(PerformancePath);

            // Point de départ des compteurs de taux : le premier vrai relevé sera la moyenne depuis ici.
            PdhNative.PdhCollectQueryData(_query);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _query = IntPtr.Zero;
            QueryUnavailable = new Unavailable(UnavailableCause.HardwareOrDriver, "pdh.dll est absent de ce Windows");
        }
    }

    /// <summary>Pourquoi aucun compteur n'est lisible, null si la requête est ouverte.</summary>
    public Unavailable? QueryUnavailable { get; }

    public bool HasUtility => _utility != IntPtr.Zero;

    /// <summary>« Parking Status » existe : il manque sur certaines éditions et sur ARM.</summary>
    public bool HasParkingStatus => _parking != IntPtr.Zero;

    public bool HasPerformance => _performance != IntPtr.Zero;

    /// <summary>Collecte et rend l'activité de chaque processeur logique, null si la collecte échoue.</summary>
    public CoreActivitySample? Sample()
    {
        if (_query == IntPtr.Zero) return null;

        try
        {
            if (PdhNative.PdhCollectQueryData(_query) != 0) return null;

            Dictionary<LogicalProcessorId, double> utility = ReadArray(_utility);
            Dictionary<LogicalProcessorId, double> parking = ReadArray(_parking);
            Dictionary<LogicalProcessorId, double> performance = ReadArray(_performance);

            var processors = new Dictionary<LogicalProcessorId, CoreActivity>();
            foreach (LogicalProcessorId id in utility.Keys.Union(parking.Keys).Union(performance.Keys))
            {
                processors[id] = new CoreActivity(
                    utility.TryGetValue(id, out double load) ? CapUtility(load) : null,
                    parking.TryGetValue(id, out double parked) ? parked >= 0.5 : null,
                    performance.TryGetValue(id, out double perf) ? Math.Max(0, perf) : null);
            }

            return new CoreActivitySample(processors);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>La charge d'un fil passe au-dessus de 100 en turbo : le Gestionnaire des tâches l'arrête à 100.</summary>
    public static double CapUtility(double value) => double.IsNaN(value) ? 0 : Math.Clamp(value, 0, 100);

    /// <summary>
    /// Instance PDH « groupe,index » d'un processeur logique (« 0,3 », « 1,12 » au-delà de 64 processeurs). Les
    /// totaux (« _Total », « 0,_Total ») et tout nom inattendu sont refusés.
    /// </summary>
    public static bool TryParseInstance(string? name, out LogicalProcessorId id)
    {
        id = default;
        if (string.IsNullOrWhiteSpace(name)) return false;

        string[] parts = name.Split(',');
        if (parts.Length != 2) return false;

        if (!int.TryParse(parts[0].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int group)
            || !int.TryParse(parts[1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int index))
        {
            return false;
        }

        id = new LogicalProcessorId(group, index);
        return true;
    }

    private IntPtr Add(string path)
        => PdhNative.PdhAddEnglishCounterW(_query, path, IntPtr.Zero, out IntPtr counter) == 0 ? counter : IntPtr.Zero;

    /// <summary>
    /// Toutes les instances d'un compteur. PDH_FMT_COUNTERVALUE_ITEM_W : le pointeur vers le nom, puis la valeur
    /// (CStatus, puis le double aligné sur 8 octets), soit 24 octets en x64 comme en x86. Un processeur ajouté entre les
    /// deux appels (rare) fait redemander la taille.
    /// </summary>
    private static Dictionary<LogicalProcessorId, double> ReadArray(IntPtr counter)
    {
        var values = new Dictionary<LogicalProcessorId, double>();
        if (counter == IntPtr.Zero) return values;

        const uint format = PdhNative.FmtDouble | PdhNative.FmtNoCap100;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            uint size = 0;
            uint status = PdhNative.PdhGetFormattedCounterArrayW(counter, format, ref size, out _, IntPtr.Zero);
            if (status != PdhNative.MoreData || size == 0) return values;

            IntPtr buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                status = PdhNative.PdhGetFormattedCounterArrayW(counter, format, ref size, out uint count, buffer);
                if (status == PdhNative.MoreData) continue;
                if (status != 0) return values;

                const int valueOffset = 8;
                const int itemSize = valueOffset + 16;
                for (int i = 0; i < count; i++)
                {
                    IntPtr item = buffer + i * itemSize;
                    string? name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item));
                    var value = Marshal.PtrToStructure<PdhNative.FmtCounterValue>(item + valueOffset);
                    if (!PdhNative.IsValid(value.CStatus) || !TryParseInstance(name, out LogicalProcessorId id)) continue;

                    values[id] = value.DoubleValue;
                }

                return values;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return values;
    }

    public void Dispose()
    {
        if (_query == IntPtr.Zero) return;
        try { PdhNative.PdhCloseQuery(_query); } catch { /* best-effort */ }
        _query = IntPtr.Zero;
    }
}

/// <summary>
/// Part du temps passé parqué par chaque processeur logique sur les derniers relevés : l'état bascule souvent plusieurs
/// fois par seconde, un instantané clignoterait sans rien dire. Logique pure.
/// </summary>
public sealed class ParkedShareWindow
{
    private readonly int _capacity;
    private readonly Dictionary<LogicalProcessorId, Queue<bool>> _samples = new();

    /// <param name="capacity">Nombre de relevés retenus (5 à une seconde d'intervalle : les 5 dernières secondes).</param>
    public ParkedShareWindow(int capacity = 5) => _capacity = Math.Max(1, capacity);

    public int Capacity => _capacity;

    /// <summary>Ajoute un relevé. Un processeur absent du relevé (état inconnu) garde ses relevés précédents.</summary>
    public void Add(IEnumerable<KeyValuePair<LogicalProcessorId, bool>> parked)
    {
        foreach ((LogicalProcessorId id, bool isParked) in parked)
        {
            if (!_samples.TryGetValue(id, out Queue<bool>? queue))
            {
                queue = new Queue<bool>(_capacity);
                _samples[id] = queue;
            }

            if (queue.Count == _capacity) queue.Dequeue();
            queue.Enqueue(isParked);
        }
    }

    /// <summary>Part parquée, de 0 à 1, null tant que ce processeur n'a aucun relevé.</summary>
    public double? Share(LogicalProcessorId id)
        => _samples.TryGetValue(id, out Queue<bool>? queue) && queue.Count > 0 ? queue.Count(p => p) / (double)queue.Count : null;

    public void Clear() => _samples.Clear();
}
