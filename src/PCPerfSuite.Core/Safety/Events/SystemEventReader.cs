using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Text.RegularExpressions;
using PCPerfSuite.Core.Compatibility;

namespace PCPerfSuite.Core.Safety.Events;

/// <summary>
/// Lecture du journal Système de Windows (EventLogReader, requête XPath) : arrêts brutaux, écrans bleus, arrêts
/// inattendus, démarrages et arrêts propres, erreurs matérielles WHEA, TDR, erreurs de disque, limitation du processeur
/// par le micrologiciel. Seuls des champs techniques choisis sont gardés, jamais le texte des messages (il peut nommer
/// des applications) : rien de personnel ne sort d'ici.
///
/// Lent (quelques dizaines à centaines de ms) : jamais sur le thread d'interface. Ne lève jamais (règle 2).
/// </summary>
public static class SystemEventReader
{
    /// <summary>Plafond d'événements lus en une fois : un journal inondé d'erreurs de disque ne bloque pas la lecture.</summary>
    public const int MaxRecords = 10_000;

    private const string LogName = "System";

    private const string KernelPower = "Microsoft-Windows-Kernel-Power";
    private const string KernelGeneral = "Microsoft-Windows-Kernel-General";
    private const string KernelProcessorPower = "Microsoft-Windows-Kernel-Processor-Power";
    private const string WerSystem = "Microsoft-Windows-WER-SystemErrorReporting";
    private const string Whea = "Microsoft-Windows-WHEA-Logger";

    private static readonly EventLogPropertySelector KernelPower41Fields = new(
    [
        "Event/EventData/Data[@Name='BugcheckCode']",
        "Event/EventData/Data[@Name='PowerButtonTimestamp']",
    ]);

    private static readonly Regex HexCode = new(@"^\s*(0x[0-9A-Fa-f]{1,16})", RegexOptions.CultureInvariant);
    private static readonly Regex DriverName = new(@"^[A-Za-z0-9_]{1,32}$", RegexOptions.CultureInvariant);
    private static readonly Regex DiskNumber = new(@"Harddisk(\d{1,3})", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>Tous les types.</summary>
    public static IReadOnlyCollection<SystemEventKind> AllKinds { get; } = Enum.GetValues<SystemEventKind>();

    /// <summary>Types utiles à la reprise au lancement : tous sauf la limitation par le micrologiciel (un événement par
    /// processeur logique à chaque démarrage, sans rapport avec un arrêt).</summary>
    public static IReadOnlyCollection<SystemEventKind> RecoveryKinds { get; } =
        AllKinds.Where(kind => kind != SystemEventKind.FirmwareLimited).ToArray();

    /// <summary>Événements des <paramref name="days"/> derniers jours. Lus du plus récent au plus ancien : au plafond,
    /// ce sont les plus anciens qui manquent, jamais l'écran bleu d'hier.</summary>
    public static SystemEventReadResult ReadLastDays(int days, IReadOnlyCollection<SystemEventKind>? kinds = null,
        CancellationToken cancellationToken = default)
    {
        long milliseconds = (long)TimeSpan.FromDays(Math.Max(1, days)).TotalMilliseconds;
        return Read($"TimeCreated[timediff(@SystemTime) <= {milliseconds.ToString(CultureInfo.InvariantCulture)}]",
            kinds, newestFirst: true, cancellationToken);
    }

    /// <summary>Événements journalisés depuis <paramref name="sinceUtc"/>, du plus ancien au plus récent.</summary>
    public static SystemEventReadResult ReadSince(DateTimeOffset sinceUtc, IReadOnlyCollection<SystemEventKind>? kinds = null,
        CancellationToken cancellationToken = default)
    {
        string since = sinceUtc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        return Read($"TimeCreated[@SystemTime >= '{since}']", kinds, newestFirst: false, cancellationToken);
    }

    private static SystemEventReadResult Read(string timeFilter, IReadOnlyCollection<SystemEventKind>? kinds,
        bool newestFirst, CancellationToken cancellationToken)
    {
        try
        {
            var events = new List<SystemEventRecord>();
            bool truncated = false;
            var query = new EventLogQuery(LogName, PathType.LogName, BuildQuery(kinds ?? AllKinds, timeFilter))
            {
                ReverseDirection = newestFirst,
            };
            using var reader = new EventLogReader(query);

            for (EventRecord? record = reader.ReadEvent(); record is not null; record = reader.ReadEvent())
            {
                using (record)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (events.Count >= MaxRecords)
                    {
                        truncated = true;
                        break;
                    }

                    if (Convert(record) is { } converted && (kinds is null || kinds.Contains(converted.Kind)))
                    {
                        events.Add(converted);
                    }
                }
            }

            events.Sort((a, b) => a.TimeUtc.CompareTo(b.TimeUtc));
            return new SystemEventReadResult(events, null, truncated);
        }
        catch (OperationCanceledException)
        {
            return SystemEventReadResult.Failed(UnavailableCause.HardwareOrDriver, "lecture du journal Système annulée");
        }
        catch (UnauthorizedAccessException)
        {
            return SystemEventReadResult.Failed(UnavailableCause.MissingRights,
                "journal Système illisible sans les droits d'administrateur");
        }
        catch (Exception ex)
        {
            return SystemEventReadResult.Failed(UnavailableCause.HardwareOrDriver,
                $"service Journal d'événements de Windows indisponible ({ex.GetType().Name})");
        }
    }

    /// <summary>Requête XPath : les numéros d'événement voulus et la fenêtre de temps. Le fournisseur est vérifié à la
    /// lecture (<see cref="KindOf"/>) : un filtre par fournisseur dans la requête la rendrait trop complexe pour
    /// Windows.</summary>
    internal static string BuildQuery(IReadOnlyCollection<SystemEventKind> kinds, string timeFilter)
    {
        IEnumerable<int> ids = kinds.SelectMany(IdsOf).Distinct().Order();
        string idFilter = string.Join(" or ", ids.Select(id => $"EventID={id.ToString(CultureInfo.InvariantCulture)}"));
        return $"*[System[({idFilter}) and {timeFilter}]]";
    }

    internal static IEnumerable<int> IdsOf(SystemEventKind kind) => kind switch
    {
        SystemEventKind.KernelPower41 => [41],
        SystemEventKind.BugCheck => [1001],
        SystemEventKind.UnexpectedShutdown => [6008],
        SystemEventKind.BootStarted => [12, 6005],
        SystemEventKind.CleanShutdown => [13, 109, 6006],
        SystemEventKind.HardwareError => [17, 18, 19, 47],
        SystemEventKind.DisplayDriverReset => [4101],
        SystemEventKind.DiskError => [7, 51, 153],
        SystemEventKind.FirmwareLimited => [37],
        _ => [],
    };

    /// <summary>Type d'un événement d'après son fournisseur et son numéro ; null pour un événement d'un autre fournisseur
    /// qui porte le même numéro.</summary>
    internal static SystemEventKind? KindOf(string? provider, int id) => (provider, id) switch
    {
        (KernelPower, 41) => SystemEventKind.KernelPower41,
        (WerSystem, 1001) => SystemEventKind.BugCheck,
        ("EventLog", 6008) => SystemEventKind.UnexpectedShutdown,
        (KernelGeneral, 12) or ("EventLog", 6005) => SystemEventKind.BootStarted,
        (KernelGeneral, 13) or (KernelPower, 109) or ("EventLog", 6006) => SystemEventKind.CleanShutdown,
        (Whea, 17 or 18 or 19 or 47) => SystemEventKind.HardwareError,
        ("Display", 4101) => SystemEventKind.DisplayDriverReset,
        ("disk", 7 or 51 or 153) => SystemEventKind.DiskError,
        (KernelProcessorPower, 37) => SystemEventKind.FirmwareLimited,
        _ => null,
    };

    private static SystemEventRecord? Convert(EventRecord record)
    {
        if (record.TimeCreated is not { } created) return null;
        if (KindOf(record.ProviderName, record.Id) is not { } kind) return null;

        var time = new DateTimeOffset(created.ToUniversalTime(), TimeSpan.Zero);
        var result = new SystemEventRecord(kind, record.Id, time);

        return kind switch
        {
            SystemEventKind.KernelPower41 => ReadKernelPower41(record, result),
            SystemEventKind.BugCheck => result with { Detail = BugcheckCodeOf(FirstProperty(record)) },
            SystemEventKind.DisplayDriverReset => result with { Detail = DriverOf(FirstProperty(record)) },
            SystemEventKind.DiskError => result with { Detail = DiskOf(FirstProperty(record)) },
            _ => result,
        };
    }

    private static SystemEventRecord ReadKernelPower41(EventRecord record, SystemEventRecord result)
    {
        if (record is not EventLogRecord logRecord) return result;
        try
        {
            IList<object> values = logRecord.GetPropertyValues(KernelPower41Fields);
            return result with
            {
                BugcheckCode = values.Count > 0 ? ToUInt64(values[0]) : null,
                PowerButtonTimestamp = values.Count > 1 ? ToUInt64(values[1]) : null,
            };
        }
        catch (Exception)
        {
            return result;
        }
    }

    private static string? FirstProperty(EventRecord record)
    {
        try
        {
            return record.Properties.Count > 0 ? record.Properties[0].Value?.ToString() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static ulong? ToUInt64(object? value) => value switch
    {
        null => null,
        ulong u => u,
        uint u => u,
        long l when l >= 0 => (ulong)l,
        int i when i >= 0 => (ulong)i,
        ushort s => s,
        byte b => b,
        string text when ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong parsed) => parsed,
        _ => null,
    };

    /// <summary>Code d'arrêt d'un 1001 (« 0x0000009f (0x…, …) ») : le code seul, sans ses paramètres.</summary>
    internal static string? BugcheckCodeOf(string? param1)
        => param1 is not null && HexCode.Match(param1) is { Success: true } match ? match.Groups[1].Value.ToUpperInvariant().Replace("0X", "0x") : null;

    /// <summary>Nom du pilote d'un 4101 (« nvlddmkm »), seulement s'il a la forme d'un nom de pilote.</summary>
    internal static string? DriverOf(string? value)
        => value is not null && DriverName.IsMatch(value.Trim()) ? value.Trim() : null;

    /// <summary>Numéro du disque d'une erreur (« \Device\Harddisk1\DR1 » → « disque 1 »).</summary>
    internal static string? DiskOf(string? value)
        => value is not null && DiskNumber.Match(value) is { Success: true } match ? $"disque {match.Groups[1].Value}" : null;
}
