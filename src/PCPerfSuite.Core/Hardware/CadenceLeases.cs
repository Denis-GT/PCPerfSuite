namespace PCPerfSuite.Core.Hardware;

/// <summary>Un bail de cadence en cours : qui l'a demandé, à quel rythme, pour quels groupes (null : tous).</summary>
public sealed record CadenceLeaseInfo(string Requester, TimeSpan Interval, IReadOnlyCollection<SensorGroup>? Groups, DateTimeOffset SinceUtc);

/// <summary>
/// Baux de cadence, en logique pure : un demandeur (bench, test combiné) obtient un relevé plus rapide de certains
/// groupes le temps d'une mesure, sans toucher aux réglages de l'utilisateur. Le bail le plus rapide l'emporte, sur la
/// cadence automatique comme sur la cadence imposée par l'utilisateur et le plafond du GPU ; le libérer rend la cadence
/// d'avant, d'elle-même. Jamais plus rapide que <see cref="HardwareMonitorService.MinTickInterval"/>.
/// </summary>
public sealed class CadenceLeases
{
    private readonly object _gate = new();
    private readonly List<Lease> _leases = [];
    private readonly TimeProvider _time;

    public CadenceLeases(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    /// <summary>Levé après chaque prise ou libération d'un bail, hors du verrou, sur le thread de l'appelant.</summary>
    public event Action? Changed;

    /// <summary>Prend un bail. <paramref name="groups"/> null : tous les groupes. À libérer (Dispose) à la fin de la
    /// mesure ; une seconde libération ne fait rien.</summary>
    public IDisposable Acquire(string requester, TimeSpan interval, IReadOnlyCollection<SensorGroup>? groups = null)
    {
        TimeSpan clamped = interval < HardwareMonitorService.MinTickInterval ? HardwareMonitorService.MinTickInterval : interval;
        var lease = new Lease(this, new CadenceLeaseInfo(requester, clamped, groups?.ToArray(), _time.GetUtcNow()));
        lock (_gate) _leases.Add(lease);
        Changed?.Invoke();
        return lease;
    }

    /// <summary>Cadence la plus rapide demandée pour ce groupe, null sans bail.</summary>
    public TimeSpan? IntervalFor(SensorGroup group)
    {
        lock (_gate)
        {
            TimeSpan? shortest = null;
            foreach (Lease lease in _leases)
            {
                if (!lease.Covers(group)) continue;
                if (shortest is null || lease.Info.Interval < shortest) shortest = lease.Info.Interval;
            }
            return shortest;
        }
    }

    public bool IsLeased(SensorGroup group) => IntervalFor(group) is not null;

    /// <summary>Baux en cours, pour le diagnostic.</summary>
    public IReadOnlyList<CadenceLeaseInfo> Active
    {
        get { lock (_gate) return _leases.Select(lease => lease.Info).ToList(); }
    }

    private void Release(Lease lease)
    {
        bool removed;
        lock (_gate) removed = _leases.Remove(lease);
        if (removed) Changed?.Invoke();
    }

    private sealed class Lease(CadenceLeases owner, CadenceLeaseInfo info) : IDisposable
    {
        private int _released;

        public CadenceLeaseInfo Info { get; } = info;

        public bool Covers(SensorGroup group) => Info.Groups is null || Info.Groups.Contains(group);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) owner.Release(this);
        }
    }
}
