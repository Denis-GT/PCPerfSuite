using PCPerfSuite.Core.Profiles;

namespace PCPerfSuite.Core.Tests;

/// <summary>Un seul pilote automatique des réglages à la fois, et jamais un bail orphelin.</summary>
public class TuningLeaseTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 12, 5, 0, TimeSpan.Zero);
    private readonly ManualClock _clock = new(T0);

    private TuningLease NewLease() => new(_clock);

    [Fact]
    public void Un_bail_libre_est_accorde()
    {
        TuningLease lease = NewLease();

        TuningLeaseResult result = lease.TryAcquire("bench", "le bench", "mesure en cours");

        Assert.True(result.Acquired);
        Assert.Equal("bench", lease.Holder!.RequesterId);
        Assert.Equal(T0, lease.Holder.SinceUtc);
    }

    [Fact]
    public void Un_seul_detenteur_a_la_fois()
    {
        TuningLease lease = NewLease();
        TuningLeaseHandle bench = lease.TryAcquire("bench", "le bench", "mesure").Handle!;

        TuningLeaseResult refused = lease.TryAcquire("bascule-auto", "la bascule automatique", "");

        Assert.False(refused.Acquired);
        Assert.Equal("bench", refused.Holder!.RequesterId);
        Assert.True(lease.IsHeldBy(bench));
    }

    [Fact]
    public void Une_ecriture_manuelle_est_refusee_avec_la_raison()
    {
        TuningLease lease = NewLease();
        TuningLeaseHandle handle = lease.TryAcquire("bench", "le bench", "mesure en cours").Handle!;

        Assert.False(lease.CanWrite(null));
        Assert.True(lease.CanWrite(handle));
        Assert.Null(lease.RefusalText(handle));

        string refusal = lease.RefusalText(null)!;
        Assert.StartsWith("Réglages pilotés par le bench depuis ", refusal);
        Assert.EndsWith("(mesure en cours).", refusal);
    }

    [Fact]
    public void La_raison_donne_l_heure_et_la_date_d_un_autre_jour()
    {
        var holder = new TuningLeaseHolder("oc", "la recherche d'OC", "", T0);

        Assert.Equal("Réglages pilotés par la recherche d'OC depuis 12:05.", holder.Describe(T0.AddHours(1), TimeZoneInfo.Utc));
        Assert.Equal("Réglages pilotés par la recherche d'OC depuis le 01/10 à 12:05.", holder.Describe(T0.AddDays(1), TimeZoneInfo.Utc));
    }

    [Fact]
    public void Liberer_rend_le_bail_et_une_seconde_liberation_ne_fait_rien()
    {
        TuningLease lease = NewLease();
        TuningLeaseHandle first = lease.TryAcquire("bench", "le bench", "").Handle!;
        first.Dispose();

        TuningLeaseHandle second = lease.TryAcquire("bascule-auto", "la bascule automatique", "").Handle!;
        first.Dispose();

        Assert.True(lease.IsHeldBy(second));
        Assert.False(lease.CanWrite(first));
        Assert.True(first.IsReleased);
    }

    [Fact]
    public void Changed_est_leve_a_la_prise_et_a_la_liberation()
    {
        TuningLease lease = NewLease();
        int changes = 0;
        lease.Changed += () => changes++;

        TuningLeaseHandle handle = lease.TryAcquire("bench", "le bench", "").Handle!;
        lease.TryAcquire("autre", "un autre", "");
        handle.Dispose();
        handle.Dispose();

        Assert.Equal(2, changes);
    }

    [Fact]
    public void Un_abonne_peut_relire_le_bail_sans_blocage()
    {
        TuningLease lease = NewLease();
        TuningLeaseHolder? seen = null;
        lease.Changed += () => seen = lease.Holder;

        lease.TryAcquire("bench", "le bench", "");

        Assert.Equal("bench", seen!.RequesterId);
    }

    [Fact]
    public void Un_detenteur_mort_libere_le_bail()
    {
        TuningLease lease = NewLease();
        bool alive = true;
        lease.TryAcquire("bench", "le bench", "", () => alive);

        alive = false;

        Assert.Null(lease.Holder);
        Assert.True(lease.CanWrite(null));
        Assert.True(lease.TryAcquire("bascule-auto", "la bascule automatique", "").Acquired);
    }

    [Fact]
    public void Un_detenteur_dont_la_sonde_leve_est_tenu_pour_mort()
    {
        TuningLease lease = NewLease();
        lease.TryAcquire("bench", "le bench", "", () => throw new InvalidOperationException("processus disparu"));

        Assert.True(lease.TryAcquire("bascule-auto", "la bascule automatique", "").Acquired);
    }

    [Fact]
    public void Le_balayage_signale_la_liberation()
    {
        TuningLease lease = NewLease();
        bool alive = true;
        lease.TryAcquire("bench", "le bench", "", () => alive);
        int changes = 0;
        lease.Changed += () => changes++;

        lease.Sweep();
        alive = false;
        lease.Sweep();
        lease.Sweep();

        Assert.Equal(1, changes);
    }

    [Fact]
    public void Une_poignee_nulle_ne_tient_jamais_le_bail()
        => Assert.False(NewLease().IsHeldBy(null));
}
