using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using PCPerfSuite.Core.SystemInfo;

namespace PCPerfSuite.Core.Safety;

/// <summary>
/// Journal de session : la trace, sur disque, des opérations risquées en cours (test combiné, palier d'OC, essai
/// d'écran, bascule de profil…). Une opération ouverte et jamais close dit, au lancement suivant, que l'app ou le PC
/// s'est arrêté pendant elle ; <see cref="StartupRecovery"/> la qualifie alors d'après les journaux Windows. C'est le
/// seul marqueur de ce genre de l'app (docs/decisions.md) : aucune fonction n'en invente un autre.
///
/// Format v1, fichier <see cref="AppDataPaths.SessionJournalFile"/> (journal-session.jsonl) : une ligne JSON par
/// écriture, jamais réécrite. L'ouverture d'une opération écrit
/// <c>{"v":1,"id":…,"timeUtc":…,"boot":…,"pid":…,"component":"test-combine","action":"debut","values":{…},"state":"InProgress"}</c> ;
/// sa clôture ajoute une ligne du même id, état <c>Completed</c> ou <c>Failed</c> et sa <c>cause</c>. L'état d'une
/// opération est celui de sa dernière ligne. « boot » est l'heure du démarrage de Windows (heure moins durée depuis le
/// démarrage) : il dit, au lancement suivant, si le PC a redémarré entre-temps.
///
/// Survivre à une coupure : chaque écriture est courte (moins de 1 Ko, un secteur), faite d'un seul bloc en ajout,
/// écrite en direct (WriteThrough) puis vidée sur le disque. Elle commence par un saut de ligne, si bien qu'une ligne
/// arrachée par une coupure ne colle jamais à la suivante ; la lecture ignore une ligne illisible sans lever. Le fichier
/// n'est réécrit qu'au lancement (<see cref="Compact"/>), jamais pendant une opération.
///
/// Jamais d'exception (règle 2) : une écriture ratée renvoie false et remplit <see cref="LastError"/>.
/// </summary>
public sealed class SessionJournal
{
    public const int FormatVersion = 1;

    /// <summary>Durée de conservation des opérations closes (décision de Denis, 30/09/2026).</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    /// <summary>Taille au-delà de laquelle le compactage retire aussi les plus anciennes opérations closes.</summary>
    public const long MaxFileBytes = 1024 * 1024;

    /// <summary>Longueur maximale d'une ligne : une ligne tient dans un secteur, écrite d'un bloc.</summary>
    internal const int MaxLineBytes = 1024;

    /// <summary>Cause écrite quand une opération est abandonnée sans être close (exception dans l'app).</summary>
    public const string AbandonedCause = "interrompu par une erreur de l'app";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Cause écrite pour les opérations ouvertes quand l'app meurt d'une exception non gérée.</summary>
    public const string CrashedCause = "app plantée (erreur inattendue)";

    private readonly Func<string> _path;
    private readonly TimeProvider _time;
    private readonly object _writeGate = new();
    private readonly ConcurrentDictionary<Guid, SessionOperation> _open = new();
    private string? _lastWriteError;
    private string? _lastReadError;

    /// <summary>Journal de l'app, dans son dossier de données (chemin relu à chaque écriture, mode portable).</summary>
    public static SessionJournal Current { get; } = new(() => AppDataPaths.Current.SessionJournalFile, TimeProvider.System);

    public SessionJournal(string path, TimeProvider? time = null) : this(() => path, time ?? TimeProvider.System)
    {
    }

    public SessionJournal(Func<string> path, TimeProvider time)
    {
        _path = path;
        _time = time;
    }

    public string FilePath => _path();

    /// <summary>Raison de la dernière écriture ratée (ouverture, clôture, compactage), null si la dernière a réussi.
    /// Un texte court, sans chemin : il part dans le diagnostic.</summary>
    public string? LastWriteError => Volatile.Read(ref _lastWriteError);

    /// <summary>Raison de la dernière lecture ratée, null si la dernière a réussi.</summary>
    public string? LastReadError => Volatile.Read(ref _lastReadError);

    /// <summary>Dernier problème, d'écriture d'abord.</summary>
    public string? LastError => LastWriteError ?? LastReadError;

    /// <summary>
    /// Clôt, comme échouées, les opérations encore ouvertes par ce processus : à appeler quand l'app meurt d'une
    /// exception non gérée. Sans cela, l'opération resterait « en cours » et serait imputée au prochain arrêt de Windows,
    /// même survenu des heures plus tard (une coupure le soir après un bench planté l'après-midi). Un processus tué de
    /// l'extérieur (Gestionnaire des tâches) n'a pas cette chance : son opération reste imputée au redémarrage qui suit.
    /// Renvoie le nombre d'opérations closes ; ne lève jamais.
    /// </summary>
    public int AbandonAll(string cause = CrashedCause)
    {
        int closed = 0;
        foreach (SessionOperation operation in _open.Values)
        {
            if (operation.Fail(cause)) closed++;
        }
        return closed;
    }

    internal void Forget(Guid id) => _open.TryRemove(id, out _);

    /// <summary>Message court d'une erreur de fichier, sans le chemin que porte <see cref="Exception.Message"/>.</summary>
    internal static string Describe(Exception exception) => exception switch
    {
        UnauthorizedAccessException => "accès refusé au fichier du journal",
        FileNotFoundException or DirectoryNotFoundException => "dossier du journal introuvable",
        IOException => "fichier du journal occupé ou disque indisponible",
        _ => $"erreur {exception.GetType().Name}",
    };

    /// <summary>
    /// Ouvre une opération : écrit tout de suite sa ligne « en cours ». <paramref name="component"/> et
    /// <paramref name="action"/> sont des clés fixes en kebab-case (« test-combine », « palier ») ;
    /// <paramref name="values"/> des nombres ou des mots, jamais un nom d'application ou de fichier (masqués sinon).
    /// </summary>
    public SessionOperation Begin(string component, string action, IReadOnlyDictionary<string, string>? values = null)
    {
        DateTimeOffset now = _time.GetUtcNow();
        var line = new SessionJournalLine
        {
            Version = FormatVersion,
            Id = Guid.NewGuid(),
            TimeUtc = now,
            BootUtc = BootTime(now),
            ProcessId = Environment.ProcessId,
            Component = SessionJournalText.Key(component),
            Action = SessionJournalText.Key(action),
            Values = SessionJournalText.Values(values),
            State = SessionEntryState.InProgress,
        };

        bool durable = TryAppend(line);
        var operation = new SessionOperation(this, line.Id, line.Component, durable);
        if (durable) _open[line.Id] = operation;
        return operation;
    }

    /// <summary>Clôt une opération (celle d'une session précédente, par <see cref="StartupRecovery"/>, ou la sienne par
    /// <see cref="SessionOperation"/>). <paramref name="state"/> ne peut pas être <see cref="SessionEntryState.InProgress"/>.</summary>
    public bool Close(Guid id, string component, SessionEntryState state, string? cause)
    {
        if (state == SessionEntryState.InProgress) return false;

        DateTimeOffset now = _time.GetUtcNow();
        return TryAppend(new SessionJournalLine
        {
            Version = FormatVersion,
            Id = id,
            TimeUtc = now,
            BootUtc = BootTime(now),
            ProcessId = Environment.ProcessId,
            Component = SessionJournalText.Key(component),
            State = state,
            Cause = SessionJournalText.Cause(cause),
        });
    }

    /// <summary>Lit le journal. Un fichier absent donne un contenu vide ; un fichier illisible, un contenu vide et sa
    /// raison. Les lignes vides sont sautées ; les lignes illisibles (fin arrachée, zéros laissés par une coupure) sont
    /// ignorées et comptées.</summary>
    public SessionJournalContent Read()
    {
        string path;
        byte[] bytes;
        try
        {
            path = _path();
            if (!File.Exists(path)) return SessionJournalContent.Empty;
            bytes = ReadAllBytesShared(path);
        }
        catch (Exception ex)
        {
            string problem = Describe(ex);
            Volatile.Write(ref _lastReadError, problem);
            return new SessionJournalContent([], 0, problem);
        }

        Volatile.Write(ref _lastReadError, null);
        (List<SessionJournalLine> lines, int ignored) = ParseLines(bytes);
        return new SessionJournalContent(Merge(lines), ignored, null);
    }

    /// <summary>
    /// Compactage, au lancement seulement (après la reprise, jamais pendant une opération) : garde les opérations en
    /// cours et celles closes depuis moins de <see cref="Retention"/>, puis retire les plus anciennes closes tant que
    /// le fichier dépasse <see cref="MaxFileBytes"/>. Réécrit dans un fichier voisin puis remplace l'original d'un coup :
    /// une coupure pendant le compactage laisse l'ancien fichier ou le nouveau, jamais un mélange. En cas d'échec, le
    /// fichier reste tel quel. Renvoie false seulement sur un échec.
    /// </summary>
    public bool Compact(DateTimeOffset now)
    {
        string path;
        try
        {
            path = _path();
            if (!File.Exists(path)) return true;

            byte[] bytes = ReadAllBytesShared(path);
            (List<SessionJournalLine> lines, int ignored) = ParseLines(bytes);
            List<SessionJournalLine> kept = SelectKept(lines, now);
            if (ignored == 0 && kept.Count == lines.Count) return true;

            string temp = path + ".compactage";
            try
            {
                var builder = new StringBuilder();
                foreach (SessionJournalLine line in kept)
                {
                    builder.Append('\n').Append(JsonSerializer.Serialize(line, JsonOptions));
                }

                using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    stream.Write(Encoding.UTF8.GetBytes(builder.ToString()));
                    stream.Flush(flushToDisk: true);
                }

                File.Replace(temp, path, destinationBackupFileName: null);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { /* reste un fichier voisin, sans effet */ }
            }

            Volatile.Write(ref _lastWriteError, null);
            return true;
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _lastWriteError, $"compactage : {Describe(ex)}");
            return false;
        }
    }

    /// <summary>Heure du démarrage de Windows, à la seconde près : <paramref name="now"/> moins la durée écoulée depuis.</summary>
    public static DateTimeOffset BootTime(DateTimeOffset now)
    {
        DateTimeOffset boot = now - TimeSpan.FromMilliseconds(Environment.TickCount64);
        return new DateTimeOffset(boot.Ticks - boot.Ticks % TimeSpan.TicksPerSecond, TimeSpan.Zero);
    }

    private bool TryAppend(SessionJournalLine line)
    {
        byte[] bytes = Serialize(line);

        lock (_writeGate)
        {
            // Plusieurs processus peuvent écrire (modes secondaires du même exe) : un fichier ouvert ailleurs à l'instant
            // donne une violation de partage, qu'un bref nouvel essai lève.
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    string path = _path();
                    if (Path.GetDirectoryName(path) is { Length: > 0 } folder) Directory.CreateDirectory(folder);

                    using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read,
                        bufferSize: 1, FileOptions.WriteThrough);
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);

                    Volatile.Write(ref _lastWriteError, null);
                    return true;
                }
                catch (IOException) when (attempt < 3)
                {
                    Thread.Sleep(20 * attempt);
                }
                catch (Exception ex)
                {
                    Volatile.Write(ref _lastWriteError, Describe(ex));
                    return false;
                }
            }
        }
    }

    /// <summary>Une ligne prête à écrire : un saut de ligne, puis le JSON. Au-delà de <see cref="MaxLineBytes"/>, les
    /// dernières valeurs sont retirées une à une.</summary>
    internal static byte[] Serialize(SessionJournalLine line)
    {
        while (true)
        {
            byte[] bytes = Encoding.UTF8.GetBytes("\n" + JsonSerializer.Serialize(line, JsonOptions));
            if (bytes.Length <= MaxLineBytes || line.Values is not { Count: > 0 } values) return bytes;

            values.Remove(values.Keys.Last());
            if (values.Count == 0) line.Values = null;
        }
    }

    private static byte[] ReadAllBytesShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    internal static (List<SessionJournalLine> Lines, int Ignored) ParseLines(byte[] bytes)
    {
        var lines = new List<SessionJournalLine>();
        int ignored = 0;

        foreach (string raw in Encoding.UTF8.GetString(bytes).Split('\n'))
        {
            string text = raw.Trim('\r', '\0', ' ', '\t', '﻿');
            if (text.Length == 0) continue;

            try
            {
                if (JsonSerializer.Deserialize<SessionJournalLine>(text, JsonOptions) is { } line
                    && line.Id != Guid.Empty && line.Version >= 1)
                {
                    lines.Add(line);
                    continue;
                }
            }
            catch (JsonException)
            {
            }

            ignored++;
        }

        return (lines, ignored);
    }

    /// <summary>Dernier état de chaque opération, dans l'ordre de leur première ligne. Une clôture sans ouverture lue (ligne
    /// d'ouverture arrachée) garde ce qu'elle dit d'elle-même.</summary>
    internal static List<SessionJournalEntry> Merge(IReadOnlyList<SessionJournalLine> lines)
    {
        var order = new List<Guid>();
        var entries = new Dictionary<Guid, SessionJournalEntry>();

        foreach (SessionJournalLine line in lines)
        {
            if (!entries.TryGetValue(line.Id, out SessionJournalEntry? entry))
            {
                order.Add(line.Id);
                entries[line.Id] = new SessionJournalEntry(
                    line.Id,
                    line.Component ?? "inconnu",
                    line.Action ?? "",
                    line.Values ?? new Dictionary<string, string>(),
                    line.TimeUtc,
                    line.BootUtc,
                    line.ProcessId,
                    line.State,
                    line.Cause,
                    line.TimeUtc);
                continue;
            }

            entries[line.Id] = entry with { State = line.State, Cause = line.Cause, UpdatedUtc = line.TimeUtc };
        }

        return order.Select(id => entries[id]).ToList();
    }

    /// <summary>Lignes gardées par le compactage, dans leur ordre d'origine.</summary>
    internal static List<SessionJournalLine> SelectKept(IReadOnlyList<SessionJournalLine> lines, DateTimeOffset now)
    {
        List<SessionJournalEntry> entries = Merge(lines);
        var keep = entries
            .Where(entry => entry.State == SessionEntryState.InProgress || now - entry.UpdatedUtc < Retention)
            .Select(entry => entry.Id)
            .ToHashSet();

        List<SessionJournalLine> kept = lines.Where(line => keep.Contains(line.Id)).ToList();

        // Plafond de taille : les plus anciennes opérations closes partent d'abord, jamais une opération en cours.
        long size = kept.Sum(line => (long)Serialize(line).Length);
        foreach (SessionJournalEntry entry in entries.Where(entry => keep.Contains(entry.Id) && entry.State != SessionEntryState.InProgress))
        {
            if (size <= MaxFileBytes) break;
            size -= kept.Where(line => line.Id == entry.Id).Sum(line => (long)Serialize(line).Length);
            kept.RemoveAll(line => line.Id == entry.Id);
        }

        return kept;
    }
}

/// <summary>
/// Une opération ouverte dans le journal de session. <see cref="Complete"/> ou <see cref="Fail"/> la close ; la libérer
/// sans l'avoir close (exception) l'écrit « échouée » avec <see cref="SessionJournal.AbandonedCause"/>. Si l'app ou le
/// PC s'arrête avant, rien n'est écrit, et c'est voulu : l'opération reste « en cours » pour le lancement suivant.
/// </summary>
public sealed class SessionOperation : IDisposable
{
    private readonly SessionJournal _journal;
    private int _closed;

    internal SessionOperation(SessionJournal journal, Guid id, string component, bool isDurable)
    {
        _journal = journal;
        Id = id;
        Component = component;
        IsDurable = isDurable;
    }

    public Guid Id { get; }

    public string Component { get; }

    /// <summary>Vrai si la ligne « en cours » est bien sur le disque. Une opération qui ne peut pas être reprise après
    /// un plantage (test combiné, palier d'OC) ne doit pas commencer sans elle.</summary>
    public bool IsDurable { get; }

    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    public bool Complete() => Close(SessionEntryState.Completed, null);

    public bool Fail(string cause) => Close(SessionEntryState.Failed, cause);

    public void Dispose() => Close(SessionEntryState.Failed, SessionJournal.AbandonedCause);

    private bool Close(SessionEntryState state, string? cause)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return false;
        _journal.Forget(Id);
        return _journal.Close(Id, Component, state, cause);
    }
}
