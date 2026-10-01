using System.Globalization;

namespace PCPerfSuite.Core.Profiles;

/// <summary>Qui tient le bail de réglage, pourquoi, et depuis quand.</summary>
/// <param name="RequesterId">Identifiant stable du demandeur, en kebab-case (« bascule-auto », « bench »…).</param>
/// <param name="Label">Nom affiché (« la bascule automatique », « le bench »).</param>
/// <param name="Reason">Ce qu'il fait (« mesure en cours »).</param>
public sealed record TuningLeaseHolder(string RequesterId, string Label, string Reason, DateTimeOffset SinceUtc)
{
    /// <summary>« Réglages pilotés par le bench depuis 14:05 (mesure en cours). » ; la date s'ajoute pour un bail pris un
    /// autre jour.</summary>
    public string Describe(DateTimeOffset nowUtc, TimeZoneInfo zone)
    {
        DateTime since = TimeZoneInfo.ConvertTime(SinceUtc, zone).DateTime;
        DateTime now = TimeZoneInfo.ConvertTime(nowUtc, zone).DateTime;
        string when = since.Date == now.Date
            ? since.ToString("HH:mm", CultureInfo.InvariantCulture)
            : $"le {since.ToString("dd/MM", CultureInfo.InvariantCulture)} à {since.ToString("HH:mm", CultureInfo.InvariantCulture)}";
        string reason = string.IsNullOrWhiteSpace(Reason) ? "" : $" ({Reason.Trim()})";
        return $"Réglages pilotés par {Label} depuis {when}{reason}.";
    }
}

/// <summary>Réponse à une demande de bail : la poignée si elle est accordée, sinon le détenteur qui le tient.</summary>
public sealed record TuningLeaseResult(TuningLeaseHandle? Handle, TuningLeaseHolder? Holder)
{
    public bool Acquired => Handle is not null;
}

/// <summary>
/// Bail de réglage : un seul pilote automatique des réglages processeur, carte graphique et ventilation à la fois
/// (bascule automatique #9, bench #10 et #11, vérification de l'OC #14, recherche d'OC #15 et #16). Tant qu'un
/// demandeur le tient, aucun autre n'applique de groupe, et les onglets Processeur, GPU et Ventilateurs refusent les
/// écritures manuelles avec la raison. Les sécurités thermiques, elles, agissent toujours : elles ne passent pas par ici.
///
/// C'est le seul moyen de suspendre la bascule automatique : il n'y a pas d'autre « pause ».
///
/// Un bail peut durer des heures (recherche d'OC). Il tombe de lui-même si son détenteur meurt : la fonction
/// <c>isAlive</c> donnée à la prise (processus enfant, tâche) est consultée à chaque lecture du bail, hors du verrou, et
/// une fonction qui lève vaut un détenteur mort. Logique pure, heure injectée, utilisable depuis n'importe quel thread ;
/// <see cref="Changed"/> est levé hors du verrou, sur le thread de l'appelant. Modèle : <c>CadenceLeases</c>.
/// </summary>
public sealed class TuningLease
{
    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private TuningLeaseHandle? _current;

    public TuningLease(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    /// <summary>Levé après chaque prise ou libération (détenteur mort compris).</summary>
    public event Action? Changed;

    public DateTimeOffset UtcNow => _time.GetUtcNow();

    public TimeZoneInfo LocalTimeZone => _time.LocalTimeZone;

    /// <summary>Le détenteur, null si le bail est libre. Un détenteur mort est libéré au passage.</summary>
    public TuningLeaseHolder? Holder
    {
        get
        {
            Sweep();
            lock (_gate) return _current?.Holder;
        }
    }

    /// <summary>Prend le bail s'il est libre. Sinon, rend le détenteur, et rien n'est pris : un demandeur n'en évince
    /// jamais un autre.</summary>
    public TuningLeaseResult TryAcquire(string requesterId, string label, string reason, Func<bool>? isAlive = null)
    {
        Sweep();

        TuningLeaseHandle handle;
        lock (_gate)
        {
            if (_current is { } held) return new TuningLeaseResult(null, held.Holder);

            handle = new TuningLeaseHandle(this, new TuningLeaseHolder(requesterId, label, reason, _time.GetUtcNow()), isAlive);
            _current = handle;
        }

        Changed?.Invoke();
        return new TuningLeaseResult(handle, handle.Holder);
    }

    /// <summary>Vrai si une écriture est permise : bail libre, ou tenu par cette poignée. Une écriture manuelle passe
    /// null.</summary>
    public bool CanWrite(TuningLeaseHandle? handle)
    {
        Sweep();
        lock (_gate) return _current is null || ReferenceEquals(_current, handle);
    }

    /// <summary>Vrai si cette poignée tient encore le bail.</summary>
    public bool IsHeldBy(TuningLeaseHandle? handle)
    {
        if (handle is null) return false;
        Sweep();
        lock (_gate) return ReferenceEquals(_current, handle);
    }

    /// <summary>Pourquoi une écriture est refusée (« Réglages pilotés par… »), null si elle est permise.</summary>
    public string? RefusalText(TuningLeaseHandle? handle)
    {
        Sweep();
        TuningLeaseHolder? holder;
        lock (_gate)
        {
            if (_current is null || ReferenceEquals(_current, handle)) return null;
            holder = _current.Holder;
        }

        return holder.Describe(_time.GetUtcNow(), _time.LocalTimeZone);
    }

    /// <summary>Libère le bail d'un détenteur mort. À appeler régulièrement (minuterie de l'interface) pour que la
    /// bannière tombe sans attendre une lecture.</summary>
    public void Sweep()
    {
        TuningLeaseHandle? current;
        lock (_gate) current = _current;

        // Hors du verrou : la fonction du détenteur peut interroger un processus, et ne doit rien bloquer ici.
        if (current is null || current.IsAlive()) return;
        Release(current);
    }

    internal void Release(TuningLeaseHandle handle)
    {
        bool removed;
        lock (_gate)
        {
            removed = ReferenceEquals(_current, handle);
            if (removed) _current = null;
        }

        if (removed) Changed?.Invoke();
    }
}

/// <summary>Le bail tenu. <see cref="Dispose"/> le rend ; une seconde libération ne fait rien.</summary>
public sealed class TuningLeaseHandle : IDisposable
{
    private readonly TuningLease _owner;
    private readonly Func<bool>? _isAlive;
    private int _released;

    internal TuningLeaseHandle(TuningLease owner, TuningLeaseHolder holder, Func<bool>? isAlive)
    {
        _owner = owner;
        Holder = holder;
        _isAlive = isAlive;
    }

    public TuningLeaseHolder Holder { get; }

    public bool IsReleased => Volatile.Read(ref _released) != 0;

    internal bool IsAlive()
    {
        if (IsReleased) return false;
        if (_isAlive is null) return true;

        try
        {
            return _isAlive();
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0) _owner.Release(this);
    }
}
