using PCPerfSuite.Core.Installations;

namespace PCPerfSuite.Core.Processes;

/// <summary>Taille et date d'un exécutable : un fichier remplacé au même chemin est revérifié.</summary>
public readonly record struct ExecutableStamp(long Length, DateTime LastWriteUtc);

/// <summary>
/// Éditeur validé des exécutables, pour les règles qui l'exigent (<see cref="ApplicationMatch.Publisher"/>). La
/// vérification Authenticode hache tout le fichier : de quelques millisecondes à quelques secondes pour un gros jeu.
/// Elle se fait donc hors du fil d'interface, une à la fois, et seulement à la demande ; tant qu'elle n'a pas abouti,
/// l'éditeur est inconnu (la règle ne correspond pas encore), puis <see cref="Resolved"/> est levé. Le résultat est gardé
/// tant que la taille et la date du fichier ne changent pas. Utilisable depuis n'importe quel fil ; ne lève jamais.
/// </summary>
public sealed class ApplicationPublisherCache
{
    public const int MaxEntries = 64;

    /// <summary>Intervalle minimal entre deux relectures de la taille et de la date d'un même fichier.</summary>
    public static readonly TimeSpan StampRecheck = TimeSpan.FromSeconds(30);

    private sealed class Entry
    {
        public ExecutableStamp? Stamp;
        public DateTimeOffset StampReadUtc;
        public string? Publisher;
        public bool Verified;
        public bool Pending;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, string?> _verify;
    private readonly Func<string, ExecutableStamp?> _stamp;
    private readonly Action<Action> _runInBackground;
    private readonly TimeProvider _time;
    private Task _queue = Task.CompletedTask;

    /// <param name="verify">Éditeur validé d'un fichier, null si non signé ou signature invalide.</param>
    /// <param name="stamp">Taille et date d'un fichier, null s'il est illisible.</param>
    /// <param name="runInBackground">Lance une vérification ; par défaut, à la suite des précédentes sur le pool.</param>
    public ApplicationPublisherCache(
        Func<string, string?>? verify = null,
        Func<string, ExecutableStamp?>? stamp = null,
        Action<Action>? runInBackground = null,
        TimeProvider? time = null)
    {
        _verify = verify ?? VerifyWithAuthenticode;
        _stamp = stamp ?? ReadStamp;
        _runInBackground = runInBackground ?? Enqueue;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Une vérification vient d'aboutir (sur un fil du pool).</summary>
    public event Action? Resolved;

    /// <summary>L'éditeur validé de <paramref name="path"/> s'il est connu et à jour ; sinon null, et la vérification
    /// est lancée si elle ne l'est pas déjà.</summary>
    public string? PublisherOf(string path)
    {
        try
        {
            DateTimeOffset now = _time.GetUtcNow();
            Entry entry;
            bool verified;
            ExecutableStamp? known;
            lock (_gate)
            {
                if (!_entries.TryGetValue(path, out Entry? found))
                {
                    if (_entries.Count >= MaxEntries) _entries.Clear();
                    found = new Entry();
                    _entries[path] = found;
                }

                entry = found;
                if (entry.Pending) return null;
                if (entry.Verified && now - entry.StampReadUtc < StampRecheck && now >= entry.StampReadUtc) return entry.Publisher;
                if (entry.Verified) entry.StampReadUtc = now;
                verified = entry.Verified;
                known = entry.Stamp;
            }

            // Déjà vérifié : un fichier inchangé garde son éditeur, un fichier remplacé repasse en vérification.
            if (verified && _stamp(path) == known)
            {
                lock (_gate) return entry.Publisher;
            }

            lock (_gate)
            {
                if (entry.Pending) return null;
                entry.Verified = false;
                entry.Publisher = null;
                entry.Pending = true;
            }

            _runInBackground(() => Verify(path, entry));
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void Verify(string path, Entry entry)
    {
        string? publisher = null;
        ExecutableStamp? stamp = null;
        try
        {
            stamp = _stamp(path);
            publisher = stamp is null ? null : _verify(path);
        }
        catch (Exception)
        {
            publisher = null;
        }

        lock (_gate)
        {
            entry.Stamp = stamp;
            entry.StampReadUtc = _time.GetUtcNow();
            entry.Publisher = publisher;
            entry.Verified = true;
            entry.Pending = false;
        }

        try
        {
            Resolved?.Invoke();
        }
        catch (Exception)
        {
            // Un abonné en échec ne doit pas bloquer la file.
        }
    }

    /// <summary>Vérifications à la suite les unes des autres : jamais deux fichiers hachés en même temps.</summary>
    private void Enqueue(Action work)
    {
        lock (_gate) _queue = _queue.ContinueWith(_ => work(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    /// <summary>L'éditeur validé d'un exécutable, lu tout de suite (création d'une règle qui l'exige) : à appeler hors du
    /// fil d'interface. Null si non signé, signature invalide ou fichier illisible ; ne lève jamais.</summary>
    public static string? ReadPublisher(string path)
    {
        try
        {
            return VerifyWithAuthenticode(path);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? VerifyWithAuthenticode(string path)
    {
        SignatureCheck check = AuthenticodeVerifier.Check(path);
        return check.IsValid ? check.Publisher : null;
    }

    private static ExecutableStamp? ReadStamp(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? new ExecutableStamp(info.Length, info.LastWriteTimeUtc) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
