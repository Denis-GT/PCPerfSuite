using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using PCPerfSuite.Core.Processes;

namespace PCPerfSuite.Core.Profiles;

/// <summary>
/// Fichier usage.json (<c>AppDataPaths.UsageFile</c>), version 1 : l'historique d'usage de la bascule automatique (#9),
/// hors de settings.json qui est réécrit en entier à chaque enregistrement. C'est l'historique de l'utilisateur, pas un
/// témoin de session (le seul est le journal de session de #4). Tolérant : tout est facultatif, une valeur hors bornes est
/// écartée, et ce qu'une version plus récente a écrit est conservé (<see cref="ExtensionData"/>).
///
/// Contient des chemins d'exécutables (données personnelles) : il reste sur ce PC, jamais au diagnostic ni au journal de
/// session.
/// </summary>
public sealed class UsageHistoryFile
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    /// <summary>Un agrégat par jour (date locale) et par usage.</summary>
    public List<UsageDayRecord>? Days { get; set; }

    /// <summary>Applications vues au premier plan.</summary>
    public List<UsageAppRecord>? Apps { get; set; }

    /// <summary>Journal des bascules, du plus ancien au plus récent.</summary>
    public List<AutoSwitchJournalEntry>? Journal { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Ce qu'un usage a représenté un jour donné : durée, charges moyennes, histogrammes de températures (1 °C par
/// case, en secondes) dont on tire des percentiles exacts.</summary>
public sealed class UsageDayRecord
{
    /// <summary>Date locale « aaaa-mm-jj ».</summary>
    public string Date { get; set; } = "";

    public string Usage { get; set; } = "";

    public double Seconds { get; set; }

    public double BatterySeconds { get; set; }

    /// <summary>Somme charge × durée et durée couverte, pour la charge moyenne.</summary>
    public double CpuLoadSum { get; set; }
    public double CpuLoadSeconds { get; set; }
    public double GpuLoadSum { get; set; }
    public double GpuLoadSeconds { get; set; }

    /// <summary>Température (°C entier) → secondes passées à cette température.</summary>
    public Dictionary<string, double>? CpuTemps { get; set; }
    public Dictionary<string, double>? GpuTemps { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Une application vue au premier plan : combien de temps dans chaque usage, et quand pour la dernière fois.</summary>
public sealed class UsageAppRecord
{
    /// <summary>Chemin complet normalisé de l'exécutable.</summary>
    public string Path { get; set; } = "";

    public DateTimeOffset LastSeenUtc { get; set; }

    /// <summary>Usage → secondes.</summary>
    public Dictionary<string, double>? Seconds { get; set; }

    public double FullscreenSeconds { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    [JsonIgnore]
    public double TotalSeconds => Seconds?.Values.Where(v => v > 0).Sum() ?? 0;
}

/// <summary>Une ligne du journal des bascules. <see cref="Reason"/> peut nommer une application (règle) : elle reste
/// locale ; le diagnostic ne reprend que <see cref="ReasonKind"/>.</summary>
public sealed class AutoSwitchJournalEntry
{
    public DateTimeOffset TimeUtc { get; set; }

    /// <summary><see cref="AutoSwitchJournalKinds"/>.</summary>
    public string Kind { get; set; } = AutoSwitchJournalKinds.Switch;

    public string? Usage { get; set; }

    public string? GroupId { get; set; }

    public string? GroupName { get; set; }

    /// <summary><see cref="UsageReasonKind"/> en chaîne, sans nom d'application.</summary>
    public string? ReasonKind { get; set; }

    public string? Reason { get; set; }

    /// <summary>Parties non appliquées, avec leur raison (« Carte graphique : overclock non posé, … »).</summary>
    public List<string>? NotApplied { get; set; }

    /// <summary>Un incident déjà signalé à l'utilisateur.</summary>
    public bool Acknowledged { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public static class AutoSwitchJournalKinds
{
    public const string Switch = "bascule";

    /// <summary>Bascule voulue mais rien posé (bail d'un autre, tout refusé).</summary>
    public const string Refused = "refus";

    /// <summary>Incident (arrêt anormal, écran bleu, TDR) après une bascule : groupe suspendu.</summary>
    public const string Incident = "incident";

    public const string Pause = "pause";
    public const string Lock = "verrou";
    public const string Generation = "generation";
}

/// <summary>Un relevé enregistré dans l'historique. Une valeur absente (groupe non relu) ne compte pas.</summary>
public sealed record UsageObservation(
    DateTimeOffset TimeUtc,
    string Usage,
    float? CpuLoad,
    float? GpuLoad,
    float? CpuTempC,
    float? GpuTempC,
    bool OnBattery,
    string? AppKey,
    bool IsFullscreen);

/// <summary>Ce que l'historique dit d'un usage sur les 30 derniers jours.</summary>
public sealed record UsageStats(
    string Usage,
    double Seconds,
    int Days,
    double? CpuLoadAverage,
    double? GpuLoadAverage,
    int? CpuTempP50,
    int? CpuTempP95,
    int? GpuTempP50,
    int? GpuTempP95,
    double CpuTempSeconds,
    double GpuTempSeconds)
{
    public static UsageStats Empty(string usage) => new(usage, 0, 0, null, null, null, null, null, null, 0, 0);
}

/// <summary>
/// L'historique en mémoire : chaque relevé (O(1)) s'ajoute à l'agrégat du jour et de l'usage, pondéré par le temps qu'il
/// représente (5 s au plus, 1 s après un trou). La bascule automatique l'enregistre par tranches de 5 min, après une
/// bascule et à la fermeture (<see cref="UsageHistoryStore"/>). Rétention : 30 jours, 200 applications, 200 lignes de
/// journal. À utiliser depuis un seul fil (celui de l'interface) ; ne lève jamais.
/// </summary>
public sealed class UsageHistory
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);
    public const int MaxApps = 200;
    public const int MaxJournal = 200;
    public static readonly TimeSpan MaxObservationWeight = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan ObservationGap = TimeSpan.FromSeconds(60);

    private const int MinTempC = 0;
    private const int MaxTempC = 125;

    private readonly TimeZoneInfo _zone;
    private readonly Dictionary<string, UsageDayRecord> _days = new(StringComparer.Ordinal);
    private readonly Dictionary<string, UsageAppRecord> _apps = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<AutoSwitchJournalEntry> _journal = new();
    private Dictionary<string, JsonElement>? _extension;
    private DateTimeOffset? _lastObservationUtc;

    public UsageHistory(TimeZoneInfo? zone = null) => _zone = zone ?? TimeZoneInfo.Local;

    /// <summary>Modifié depuis le dernier <see cref="MarkSaved"/>.</summary>
    public bool IsDirty { get; private set; }

    public IReadOnlyList<AutoSwitchJournalEntry> Journal => _journal;

    public IReadOnlyCollection<UsageAppRecord> Apps => _apps.Values;

    /// <summary>Reprend un fichier lu : valeurs assainies, doublons fusionnés, ce qui a plus de 30 jours écarté.</summary>
    public static UsageHistory FromFile(UsageHistoryFile? file, DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        var history = new UsageHistory(zone);
        if (file is null) return history;

        history._extension = file.ExtensionData;
        foreach (UsageDayRecord? day in file.Days ?? [])
        {
            if (day is null || !TryParseDate(day.Date, out _) || string.IsNullOrWhiteSpace(day.Usage) || !Positive(day.Seconds)) continue;

            UsageDayRecord target = history.Day(day.Date, day.Usage.Trim());
            target.Seconds += day.Seconds;
            target.BatterySeconds += Positive(day.BatterySeconds) ? Math.Min(day.BatterySeconds, day.Seconds) : 0;
            if (Positive(day.CpuLoadSeconds) && Finite(day.CpuLoadSum) && day.CpuLoadSum >= 0)
            {
                target.CpuLoadSum += Math.Min(day.CpuLoadSum, day.CpuLoadSeconds * 100);
                target.CpuLoadSeconds += day.CpuLoadSeconds;
            }

            if (Positive(day.GpuLoadSeconds) && Finite(day.GpuLoadSum) && day.GpuLoadSum >= 0)
            {
                target.GpuLoadSum += Math.Min(day.GpuLoadSum, day.GpuLoadSeconds * 100);
                target.GpuLoadSeconds += day.GpuLoadSeconds;
            }

            MergeTemps(target.CpuTemps ??= new(), day.CpuTemps);
            MergeTemps(target.GpuTemps ??= new(), day.GpuTemps);
            target.ExtensionData ??= day.ExtensionData;
        }

        foreach (UsageAppRecord? app in file.Apps ?? [])
        {
            if (app is null || ApplicationPaths.Normalize(app.Path) is not { } path) continue;

            if (!history._apps.TryGetValue(path, out UsageAppRecord? target))
            {
                target = new UsageAppRecord { Path = path, Seconds = new(), ExtensionData = app.ExtensionData };
                history._apps[path] = target;
            }

            if (app.LastSeenUtc > target.LastSeenUtc) target.LastSeenUtc = app.LastSeenUtc;
            if (Positive(app.FullscreenSeconds)) target.FullscreenSeconds += app.FullscreenSeconds;
            foreach ((string usage, double seconds) in app.Seconds ?? new())
            {
                if (string.IsNullOrWhiteSpace(usage) || !Positive(seconds)) continue;
                target.Seconds![usage] = target.Seconds.GetValueOrDefault(usage) + seconds;
            }
        }

        foreach (AutoSwitchJournalEntry? entry in file.Journal ?? [])
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.Kind)) continue;
            history._journal.Add(entry);
        }

        history._journal.Sort((a, b) => a.TimeUtc.CompareTo(b.TimeUtc));
        history.Prune(now);
        history.IsDirty = false;
        return history;
    }

    /// <summary>Le fichier, en copie indépendante de l'historique en mémoire.</summary>
    public UsageHistoryFile ToFile() => ProfileGroupJson.Clone(Snapshot());

    /// <summary>Le contenu d'usage.json, sérialisé en une passe sur le fil de l'historique : seuls les octets partent
    /// ensuite vers l'écrivain.</summary>
    public byte[] Serialize() => UsageHistoryStore.Serialize(Snapshot());

    private UsageHistoryFile Snapshot() => new()
    {
        Version = UsageHistoryFile.CurrentVersion,
        Days = _days.Values.OrderBy(d => d.Date, StringComparer.Ordinal).ThenBy(d => d.Usage, StringComparer.Ordinal).ToList(),
        Apps = _apps.Values.OrderByDescending(a => a.LastSeenUtc).ToList(),
        Journal = _journal.ToList(),
        ExtensionData = _extension,
    };

    public void MarkSaved() => IsDirty = false;

    /// <summary>Ajoute un relevé à l'agrégat de son jour et de son usage, et à l'application vue.</summary>
    public void Record(UsageObservation observation)
    {
        try
        {
            DateTimeOffset now = observation.TimeUtc;
            double weight = 1;
            if (_lastObservationUtc is { } last)
            {
                TimeSpan elapsed = now - last;
                if (elapsed > TimeSpan.Zero && elapsed <= ObservationGap) weight = Math.Min(elapsed.TotalSeconds, MaxObservationWeight.TotalSeconds);
                else if (elapsed <= TimeSpan.Zero && elapsed > -TimeSpan.FromSeconds(1)) return;
            }

            _lastObservationUtc = now;
            if (string.IsNullOrWhiteSpace(observation.Usage)) return;

            UsageDayRecord day = Day(DateKey(now), observation.Usage);
            day.Seconds += weight;
            if (observation.OnBattery) day.BatterySeconds += weight;
            if (Load(observation.CpuLoad) is { } cpu)
            {
                day.CpuLoadSum += cpu * weight;
                day.CpuLoadSeconds += weight;
            }

            if (Load(observation.GpuLoad) is { } gpu)
            {
                day.GpuLoadSum += gpu * weight;
                day.GpuLoadSeconds += weight;
            }

            AddTemp(day.CpuTemps ??= new(), observation.CpuTempC, weight);
            AddTemp(day.GpuTemps ??= new(), observation.GpuTempC, weight);

            if (ApplicationPaths.Normalize(observation.AppKey) is { } path) RecordApp(path, observation, weight);
            IsDirty = true;
        }
        catch (Exception)
        {
            // Un relevé de moins dans l'historique : rien de grave.
        }
    }

    private void RecordApp(string path, UsageObservation observation, double weight)
    {
        if (!_apps.TryGetValue(path, out UsageAppRecord? app))
        {
            if (_apps.Count >= MaxApps)
            {
                UsageAppRecord oldest = _apps.Values.MinBy(a => a.LastSeenUtc)!;
                _apps.Remove(oldest.Path);
            }

            app = new UsageAppRecord { Path = path, Seconds = new() };
            _apps[path] = app;
        }

        app.LastSeenUtc = observation.TimeUtc;
        app.Seconds ??= new();
        app.Seconds[observation.Usage] = app.Seconds.GetValueOrDefault(observation.Usage) + weight;
        if (observation.IsFullscreen) app.FullscreenSeconds += weight;
    }

    /// <summary>Ajoute une ligne au journal des bascules (200 au plus).</summary>
    public void AddJournal(AutoSwitchJournalEntry entry)
    {
        _journal.Add(entry);
        if (_journal.Count > MaxJournal) _journal.RemoveRange(0, _journal.Count - MaxJournal);
        IsDirty = true;
    }

    /// <summary>Les incidents pas encore signalés.</summary>
    public IReadOnlyList<AutoSwitchJournalEntry> UnacknowledgedIncidents()
        => _journal.Where(e => e.Kind == AutoSwitchJournalKinds.Incident && !e.Acknowledged).ToList();

    public void Acknowledge(IEnumerable<AutoSwitchJournalEntry> entries)
    {
        foreach (AutoSwitchJournalEntry entry in entries)
        {
            if (entry.Acknowledged) continue;
            entry.Acknowledged = true;
            IsDirty = true;
        }
    }

    /// <summary>Efface tout l'historique (agrégats, applications, journal).</summary>
    public void Clear()
    {
        _days.Clear();
        _apps.Clear();
        _journal.Clear();
        _lastObservationUtc = null;
        IsDirty = true;
    }

    /// <summary>Écarte ce qui a plus de 30 jours.</summary>
    public void Prune(DateTimeOffset now)
    {
        DateTimeOffset limit = now - Retention;
        string firstDay = DateKey(limit);
        foreach (string key in _days.Where(p => string.CompareOrdinal(p.Value.Date, firstDay) < 0).Select(p => p.Key).ToList())
        {
            _days.Remove(key);
            IsDirty = true;
        }

        foreach (string path in _apps.Values.Where(a => a.LastSeenUtc < limit).Select(a => a.Path).ToList())
        {
            _apps.Remove(path);
            IsDirty = true;
        }

        int removed = _journal.RemoveAll(e => e.TimeUtc < limit);
        if (_journal.Count > MaxJournal)
        {
            removed += _journal.Count - MaxJournal;
            _journal.RemoveRange(0, _journal.Count - MaxJournal);
        }

        if (removed > 0) IsDirty = true;
    }

    /// <summary>Nombre de jours différents qui ont des relevés.</summary>
    public int DaysWithData => _days.Values.Select(d => d.Date).Distinct(StringComparer.Ordinal).Count();

    /// <summary>Premier jour enregistré, null sans historique.</summary>
    public DateOnly? FirstDay
        => _days.Values.Select(d => TryParseDate(d.Date, out DateOnly date) ? date : (DateOnly?)null).Min();

    /// <summary>Ce que l'historique dit de <paramref name="usage"/> sur les jours gardés.</summary>
    public UsageStats StatsFor(string usage)
    {
        List<UsageDayRecord> days = _days.Values.Where(d => string.Equals(d.Usage, usage, StringComparison.Ordinal)).ToList();
        if (days.Count == 0) return UsageStats.Empty(usage);

        var cpuTemps = new Dictionary<string, double>();
        var gpuTemps = new Dictionary<string, double>();
        foreach (UsageDayRecord day in days)
        {
            MergeTemps(cpuTemps, day.CpuTemps);
            MergeTemps(gpuTemps, day.GpuTemps);
        }

        double cpuSeconds = days.Sum(d => d.CpuLoadSeconds), gpuSeconds = days.Sum(d => d.GpuLoadSeconds);
        return new UsageStats(
            usage,
            days.Sum(d => d.Seconds),
            days.Select(d => d.Date).Distinct(StringComparer.Ordinal).Count(),
            cpuSeconds > 0 ? days.Sum(d => d.CpuLoadSum) / cpuSeconds : null,
            gpuSeconds > 0 ? days.Sum(d => d.GpuLoadSum) / gpuSeconds : null,
            Percentile(cpuTemps, 0.5),
            Percentile(cpuTemps, 0.95),
            Percentile(gpuTemps, 0.5),
            Percentile(gpuTemps, 0.95),
            cpuTemps.Values.Sum(),
            gpuTemps.Values.Sum());
    }

    /// <summary>La plus petite température sous laquelle (ou à laquelle) se trouve la part <paramref name="share"/> du
    /// temps ; null sans relevé.</summary>
    public static int? Percentile(IReadOnlyDictionary<string, double> histogram, double share)
    {
        var bins = histogram
            .Select(p => (Ok: int.TryParse(p.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out int t), Temp: t, Seconds: p.Value))
            .Where(b => b.Ok && Positive(b.Seconds))
            .OrderBy(b => b.Temp)
            .ToList();
        double total = bins.Sum(b => b.Seconds);
        if (total <= 0) return null;

        double threshold = total * Math.Clamp(share, 0, 1);
        double cumulative = 0;
        foreach (var bin in bins)
        {
            cumulative += bin.Seconds;
            if (cumulative >= threshold - 1e-9) return bin.Temp;
        }

        return bins[^1].Temp;
    }

    private UsageDayRecord Day(string date, string usage)
    {
        string key = $"{date}|{usage}";
        if (!_days.TryGetValue(key, out UsageDayRecord? day))
        {
            day = new UsageDayRecord { Date = date, Usage = usage, CpuTemps = new(), GpuTemps = new() };
            _days[key] = day;
        }

        return day;
    }

    private string DateKey(DateTimeOffset utc)
        => TimeZoneInfo.ConvertTime(utc, _zone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static bool TryParseDate(string? text, out DateOnly date)
        => DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    private static void AddTemp(Dictionary<string, double> histogram, float? temp, double weight)
    {
        if (temp is not { } t || !float.IsFinite(t) || t < MinTempC || t > MaxTempC) return;
        string key = ((int)Math.Round(t)).ToString(CultureInfo.InvariantCulture);
        histogram[key] = histogram.GetValueOrDefault(key) + weight;
    }

    private static void MergeTemps(Dictionary<string, double> into, Dictionary<string, double>? from)
    {
        foreach ((string key, double seconds) in from ?? new())
        {
            if (!Positive(seconds) || !int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out int temp)
                || temp < MinTempC || temp > MaxTempC) continue;
            string normalized = temp.ToString(CultureInfo.InvariantCulture);
            into[normalized] = into.GetValueOrDefault(normalized) + seconds;
        }
    }

    private static double? Load(float? value) => value is { } v && float.IsFinite(v) ? Math.Clamp(v, 0, 100) : null;

    private static bool Positive(double value) => double.IsFinite(value) && value > 0;

    private static bool Finite(double value) => double.IsFinite(value);
}

/// <summary>Résultat d'une lecture d'usage.json : le fichier (vide s'il manque ou est illisible) et ce qui n'allait pas.
/// <paramref name="Failed"/> : le fichier existe mais n'a pas pu être lu (verrouillé par un antivirus ou une
/// synchronisation, clé USB pas prête) ; il est intact, et ne doit surtout pas être réécrit à partir d'un historique vide.</summary>
public sealed record UsageHistoryRead(UsageHistoryFile File, string? Problem, bool Failed = false);

/// <summary>
/// Lecture et écriture d'usage.json. Lecture tolérante : un fichier absent donne un historique vide ; un fichier illisible
/// est mis de côté (« .corrupt ») et l'historique repart de zéro. Écriture atomique (temporaire complet puis remplacement),
/// à faire hors du fil d'interface : la racine peut être une clé USB (mode portable). Ne lève jamais.
/// </summary>
public static class UsageHistoryStore
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = false };

    public static UsageHistoryRead Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return new UsageHistoryRead(new UsageHistoryFile(), null);

            ReadOnlySpan<byte> bytes = File.ReadAllBytes(path);

            // Un fichier retouché au Bloc-notes peut commencer par la marque d'ordre UTF-8, que le lecteur JSON refuse.
            if (bytes.StartsWith("﻿"u8)) bytes = bytes[3..];
            if (bytes.Length == 0) return new UsageHistoryRead(new UsageHistoryFile(), "fichier vide");

            UsageHistoryFile? file = JsonSerializer.Deserialize<UsageHistoryFile>(bytes);
            return file is null
                ? new UsageHistoryRead(new UsageHistoryFile(), "fichier vide")
                : new UsageHistoryRead(file, file.Version > UsageHistoryFile.CurrentVersion
                    ? $"écrit par une version plus récente (v{file.Version.ToString(CultureInfo.InvariantCulture)}), relu en partie"
                    : null);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            TryBackupUnreadable(path);
            return new UsageHistoryRead(new UsageHistoryFile(), "fichier illisible, mis de côté (.corrupt)");
        }
        catch (Exception ex)
        {
            return new UsageHistoryRead(new UsageHistoryFile(), $"lecture impossible ({ex.GetType().Name})", Failed: true);
        }
    }

    public static byte[] Serialize(UsageHistoryFile file) => JsonSerializer.SerializeToUtf8Bytes(file, WriteOptions);

    /// <summary>Écrit le fichier ; null si tout s'est bien passé, sinon la raison.</summary>
    public static string? Write(string path, byte[] json)
    {
        string temp = path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(temp, json);
            File.Move(temp, path, overwrite: true);
            return null;
        }
        catch (Exception ex)
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best-effort */ }
            return $"enregistrement de l'historique impossible ({ex.GetType().Name})";
        }
    }

    /// <summary>Supprime la copie d'un fichier illisible (« .corrupt »), qui contient des chemins d'exécutables : « Effacer
    /// l'historique » doit tout effacer. (Un temporaire laissé par un arrêt brutal est écrasé par l'écriture suivante.) Null
    /// si elle est partie ou n'existait pas, sinon la raison.</summary>
    public static string? DeleteCorruptCopy(string path)
    {
        try
        {
            string corrupt = path + ".corrupt";
            if (File.Exists(corrupt)) File.Delete(corrupt);
            return null;
        }
        catch (Exception ex)
        {
            return $"copie usage.json.corrupt non supprimée ({ex.GetType().Name})";
        }
    }

    private static void TryBackupUnreadable(string path)
    {
        try
        {
            if (File.Exists(path)) File.Move(path, path + ".corrupt", overwrite: true);
        }
        catch { /* best-effort : il sera écrasé au prochain enregistrement */ }
    }
}

/// <summary>Un seul écrivain d'usage.json, hors du fil d'interface : les enregistrements partent à la suite, le dernier
/// demandé l'emporte, et <see cref="Flush"/> attend la fin (fermeture de l'app).</summary>
public sealed class UsageHistoryWriter
{
    private readonly object _gate = new();
    private readonly Func<string> _path;
    private readonly Func<string, byte[], string?> _write;
    private Task _tail = Task.CompletedTask;
    private byte[]? _next;

    public UsageHistoryWriter(Func<string> path, Func<string, byte[], string?>? write = null)
    {
        _path = path;
        _write = write ?? UsageHistoryStore.Write;
    }

    /// <summary>Dernière erreur d'écriture, null après une écriture réussie.</summary>
    public string? LastError { get; private set; }

    public void Save(byte[] json)
    {
        lock (_gate)
        {
            bool queued = _next is not null;
            _next = json;
            if (queued) return;
            _tail = _tail.ContinueWith(_ => WriteNext(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }

    /// <summary>Attend que tout soit écrit, au plus <paramref name="timeout"/>.</summary>
    public bool Flush(TimeSpan timeout)
    {
        Task tail;
        lock (_gate) tail = _tail;
        try
        {
            return tail.Wait(timeout);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void WriteNext()
    {
        byte[]? json;
        lock (_gate)
        {
            json = _next;
            _next = null;
        }

        if (json is null) return;
        try
        {
            LastError = _write(_path(), json);
        }
        catch (Exception ex)
        {
            LastError = $"enregistrement de l'historique impossible ({ex.GetType().Name})";
        }
    }
}
