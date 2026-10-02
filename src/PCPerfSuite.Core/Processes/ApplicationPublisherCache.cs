using PCPerfSuite.Core.Installations;

namespace PCPerfSuite.Core.Processes;

/// <summary>Taille et dates d'un exécutable : un fichier remplacé au même chemin est revérifié.</summary>
public readonly record struct ExecutableStamp(long Length, DateTime LastWriteUtc, DateTime CreationUtc = default);

/// <summary>
/// Éditeur validé des exécutables, pour les règles qui l'exigent (<see cref="ApplicationMatch.Publisher"/>). La
/// vérification Authenticode hache tout le fichier : de quelques millisecondes à quelques secondes pour un gros jeu.
/// Elle se fait donc hors du fil d'interface, une à la fois, et seulement à la demande ; tant qu'elle n'a pas abouti,
/// l'éditeur est inconnu (la règle ne correspond pas encore), puis <see cref="Resolved"/> est levé. Le résultat est gardé
/// tant que la taille et les dates du fichier ne changent pas ; leur relecture se fait elle aussi hors du fil de
/// l'appelant (un jeu sur un partage réseau ou un disque en veille ne doit pas figer l'interface) : l'éditeur connu est
/// rendu tout de suite, et un fichier remplacé repasse en vérification. Utilisable depuis n'importe quel fil ; ne lève
/// jamais.
///
/// Limite connue : un programme qui peut écrire dans le dossier de l'exécutable peut le remplacer en gardant sa taille et
/// ses dates ; l'éditeur vérifié reste alors en cache jusqu'à la relance de l'app. L'enjeu se borne au groupe que la règle
/// pose (des réglages que l'utilisateur a lui-même choisis), et un tel programme a déjà bien d'autres moyens d'agir.
/// </summary>
public sealed class ApplicationPublisherCache
{
    public const int MaxEntries = 64;

    /// <summary>Intervalle minimal entre deux relectures de la taille et de la date d'un même fichier.</summary>
    public static readonly TimeSpan StampRecheck = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    private readonly Dictionary<string, PublisherCacheEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
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
            PublisherCacheEntry entry;
            lock (_gate)
            {
                if (!_entries.TryGetValue(path, out PublisherCacheEntry? found))
                {
                    if (_entries.Count >= MaxEntries) ForgetIdle();
                    found = new PublisherCacheEntry();
                    _entries[path] = found;
                }

                entry = found;
                if (entry.Pending) return null;
                if (entry.Verified && (entry.Checking || (now - entry.StampReadUtc < StampRecheck && now >= entry.StampReadUtc)))
                {
                    return entry.Publisher;
                }

                if (entry.Verified)
                {
                    // Déjà vérifié : la taille et les dates sont relues en arrière-plan, l'éditeur connu reste rendu.
                    entry.StampReadUtc = now;
                    entry.Checking = true;
                }
                else
                {
                    entry.Pending = true;
                }
            }

            if (entry.Checking) _runInBackground(() => Recheck(path, entry));
            else _runInBackground(() => Verify(path, entry));

            lock (_gate) return entry.Verified ? entry.Publisher : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Relit la taille et les dates : un fichier inchangé garde son éditeur, un fichier remplacé repasse en
    /// vérification.</summary>
    private void Recheck(string path, PublisherCacheEntry entry)
    {
        ExecutableStamp? stamp;
        try
        {
            stamp = _stamp(path);
        }
        catch (Exception)
        {
            stamp = null;
        }

        lock (_gate)
        {
            entry.Checking = false;
            if (stamp == entry.Stamp) return;
            entry.Verified = false;
            entry.Publisher = null;
            entry.Pending = true;
        }

        Verify(path, entry);
    }

    /// <summary>Au-delà de <see cref="MaxEntries"/>, on oublie les entrées au repos ; une vérification en cours reste.</summary>
    private void ForgetIdle()
    {
        foreach (string key in _entries.Where(e => !e.Value.Pending && !e.Value.Checking).Select(e => e.Key).ToList())
        {
            _entries.Remove(key);
        }
    }

    private void Verify(string path, PublisherCacheEntry entry)
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
            return info.Exists ? new ExecutableStamp(info.Length, info.LastWriteTimeUtc, info.CreationTimeUtc) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>Une entrée de <see cref="ApplicationPublisherCache"/>, qui seul s'en sert : lue et écrite sous son verrou.</summary>
internal sealed class PublisherCacheEntry
{
    public ExecutableStamp? Stamp { get; set; }

    public DateTimeOffset StampReadUtc { get; set; }

    public string? Publisher { get; set; }

    public bool Verified { get; set; }

    /// <summary>Vérification Authenticode en cours.</summary>
    public bool Pending { get; set; }

    /// <summary>Relecture de la taille et des dates en cours.</summary>
    public bool Checking { get; set; }
}
