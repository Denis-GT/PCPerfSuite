using PCPerfSuite.Core.Hardware;

namespace PCPerfSuite.Core.Tests;

/// <summary>
/// Les baux de cadence : un demandeur (bench, test) accélère un groupe le temps d'une mesure, sans toucher à la cadence
/// choisie par l'utilisateur ni au plafond automatique, et tout revient à la libération.
/// </summary>
public class CadenceLeasesTests
{
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(250);

    private static SensorReadSchedule ReadOnce(SensorGroup group)
    {
        var schedule = new SensorReadSchedule(group);
        schedule.RecordRead(epoch: 0, tick: 0, TimeSpan.FromMilliseconds(1), Tick);
        return schedule;
    }

    [Fact]
    public void Le_bail_le_plus_rapide_l_emporte_et_la_liberation_rend_le_suivant()
    {
        var leases = new CadenceLeases();

        IDisposable slow = leases.Acquire("bench CPU", TimeSpan.FromMilliseconds(500), [SensorGroup.Cpu]);
        IDisposable fast = leases.Acquire("test combiné", TimeSpan.FromMilliseconds(200), [SensorGroup.Cpu, SensorGroup.Gpu]);

        Assert.Equal(TimeSpan.FromMilliseconds(200), leases.IntervalFor(SensorGroup.Cpu));
        Assert.Equal(TimeSpan.FromMilliseconds(200), leases.IntervalFor(SensorGroup.Gpu));
        Assert.Null(leases.IntervalFor(SensorGroup.Storage));

        fast.Dispose();
        Assert.Equal(TimeSpan.FromMilliseconds(500), leases.IntervalFor(SensorGroup.Cpu));
        Assert.False(leases.IsLeased(SensorGroup.Gpu));

        slow.Dispose();
        Assert.Null(leases.IntervalFor(SensorGroup.Cpu));
    }

    [Fact]
    public void Un_bail_sans_groupe_couvre_tous_les_groupes()
    {
        var leases = new CadenceLeases();
        using IDisposable all = leases.Acquire("bench", TimeSpan.FromMilliseconds(300));

        Assert.All(Enum.GetValues<SensorGroup>(), group => Assert.True(leases.IsLeased(group)));
    }

    [Fact]
    public void Un_bail_ne_descend_jamais_sous_le_tick_minimal()
    {
        var leases = new CadenceLeases();
        using IDisposable lease = leases.Acquire("trop rapide", TimeSpan.FromMilliseconds(10), [SensorGroup.Cpu]);

        Assert.Equal(HardwareMonitorService.MinTickInterval, leases.IntervalFor(SensorGroup.Cpu));
    }

    [Fact]
    public void Une_seconde_liberation_ne_fait_rien()
    {
        var leases = new CadenceLeases();
        int changes = 0;
        leases.Changed += () => changes++;

        IDisposable lease = leases.Acquire("bench", TimeSpan.FromMilliseconds(300), [SensorGroup.Cpu]);
        lease.Dispose();
        lease.Dispose();

        Assert.Equal(2, changes);
        Assert.Empty(leases.Active);
    }

    [Fact]
    public void Les_baux_actifs_disent_qui_les_tient()
    {
        var leases = new CadenceLeases();
        using IDisposable lease = leases.Acquire("bench CPU", TimeSpan.FromMilliseconds(300), [SensorGroup.Cpu]);

        CadenceLeaseInfo info = Assert.Single(leases.Active);
        Assert.Equal("bench CPU", info.Requester);
        Assert.Equal([SensorGroup.Cpu], info.Groups);
    }

    [Fact]
    public void Le_bail_l_emporte_sur_la_cadence_imposee_sans_la_modifier()
    {
        SensorReadSchedule schedule = ReadOnce(SensorGroup.Cpu);
        schedule.ManualInterval = TimeSpan.FromSeconds(2);
        schedule.LeaseInterval = TimeSpan.FromMilliseconds(250);

        Assert.Equal(TimeSpan.FromMilliseconds(250), schedule.RequestedInterval);
        Assert.True(schedule.IsDue(epoch: 0, tick: 1, Tick));
        Assert.Equal(TimeSpan.FromSeconds(2), schedule.ManualInterval);

        schedule.LeaseInterval = null;
        Assert.Equal(TimeSpan.FromSeconds(2), schedule.RequestedInterval);
        Assert.False(schedule.IsDue(epoch: 0, tick: 1, Tick));
    }

    [Fact]
    public void Un_bail_plus_lent_que_la_cadence_voulue_ne_ralentit_rien()
    {
        SensorReadSchedule schedule = ReadOnce(SensorGroup.Cpu);
        schedule.BaseInterval = TimeSpan.FromMilliseconds(250);
        schedule.LeaseInterval = TimeSpan.FromSeconds(1);

        Assert.Equal(TimeSpan.FromMilliseconds(250), schedule.RequestedInterval);
        Assert.True(schedule.IsDue(epoch: 0, tick: 1, Tick));
    }

    [Fact]
    public void Le_bail_l_emporte_sur_l_espacement_automatique_et_le_plafond_du_gpu()
    {
        SensorReadSchedule schedule = ReadOnce(SensorGroup.Gpu);
        schedule.BaseInterval = Tick;
        // Lectures coûteuses : l'automatique espace le GPU jusqu'à son plafond de 750 ms, soit 3 ticks.
        for (int tick = 1; tick <= 5; tick++) schedule.RecordRead(0, tick * 3, TimeSpan.FromMilliseconds(200), Tick);
        Assert.False(schedule.IsDue(epoch: 0, tick: 16, Tick));

        schedule.LeaseInterval = Tick;

        Assert.True(schedule.IsDue(epoch: 0, tick: 16, Tick));
        Assert.False(schedule.GetStatus(Tick).IsSpacedOut);
        Assert.Equal(Tick, schedule.GetStatus(Tick).LeaseInterval);
    }

    [Fact]
    public void Un_groupe_sous_bail_reste_relu_en_mode_eco()
    {
        SensorReadSchedule schedule = ReadOnce(SensorGroup.Cpu);
        schedule.IsSuspended = true;
        Assert.True(schedule.IsEffectivelySuspended);
        Assert.False(schedule.IsDue(epoch: 0, tick: 10, Tick));

        schedule.LeaseInterval = Tick;

        Assert.False(schedule.IsEffectivelySuspended);
        Assert.True(schedule.IsDue(epoch: 0, tick: 1, Tick));
    }

    [Fact]
    public void Le_tick_suit_le_bail_le_plus_rapide_sans_descendre_sous_le_minimum()
    {
        SensorReadSchedule cpu = ReadOnce(SensorGroup.Cpu);
        SensorReadSchedule storage = ReadOnce(SensorGroup.Storage);
        cpu.LeaseInterval = TimeSpan.FromMilliseconds(200);

        Assert.Equal(TimeSpan.FromMilliseconds(200), HardwareMonitorService.ComputeTickInterval([cpu, storage]));

        cpu.LeaseInterval = null;
        cpu.ManualInterval = TimeSpan.FromMilliseconds(40);
        Assert.Equal(HardwareMonitorService.MinTickInterval, HardwareMonitorService.ComputeTickInterval([cpu, storage]));
    }

    [Fact]
    public void Un_groupe_en_pause_sous_bail_compte_pour_le_tick()
    {
        SensorReadSchedule cpu = ReadOnce(SensorGroup.Cpu);
        cpu.IsSuspended = true;
        Assert.Equal(HardwareMonitorService.IdleTickInterval, HardwareMonitorService.ComputeTickInterval([cpu]));

        cpu.LeaseInterval = TimeSpan.FromMilliseconds(300);

        Assert.Equal(TimeSpan.FromMilliseconds(300), HardwareMonitorService.ComputeTickInterval([cpu]));
    }
}
