using PCPerfSuite.Core.Hardware;

namespace PCPerfSuite.Core.Tests;

/// <summary>
/// La mise en pause d'un groupe de capteurs (mode éco, fenêtre cachée) : un groupe en pause n'est plus relu, sauf une
/// toute première fois, et reprend dès le tick suivant sans rien perdre de la cadence choisie par l'utilisateur.
/// </summary>
public class SensorReadScheduleTests
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);

    private static SensorReadSchedule ReadOnce()
    {
        var schedule = new SensorReadSchedule(SensorGroup.Storage);
        schedule.RecordRead(epoch: 0, tick: 0, TimeSpan.FromMilliseconds(1), Tick);
        return schedule;
    }

    [Fact]
    public void Un_groupe_en_pause_n_est_plus_relu()
    {
        SensorReadSchedule schedule = ReadOnce();
        schedule.IsSuspended = true;

        Assert.False(schedule.IsDue(epoch: 0, tick: 10, Tick));
        Assert.False(schedule.IsDue(epoch: 1, tick: 0, Tick));
    }

    [Fact]
    public void Un_groupe_jamais_lu_l_est_une_premiere_fois_meme_en_pause()
    {
        var schedule = new SensorReadSchedule(SensorGroup.Motherboard) { IsSuspended = true };

        Assert.True(schedule.IsDue(epoch: 0, tick: 0, Tick));
    }

    [Fact]
    public void A_la_reprise_le_groupe_est_relu_au_tick_suivant()
    {
        SensorReadSchedule schedule = ReadOnce();
        schedule.ManualInterval = TimeSpan.FromSeconds(10);
        schedule.IsSuspended = true;
        schedule.IsSuspended = false;

        // Un tick après la dernière lecture, bien avant les 10 s de sa cadence.
        Assert.True(schedule.IsDue(epoch: 0, tick: 1, Tick));
    }

    [Fact]
    public void La_pause_conserve_la_cadence_imposee()
    {
        SensorReadSchedule schedule = ReadOnce();
        schedule.ManualInterval = TimeSpan.FromSeconds(3);
        schedule.IsSuspended = true;
        schedule.IsSuspended = false;

        Assert.Equal(TimeSpan.FromSeconds(3), schedule.ManualInterval);
        Assert.Equal(TimeSpan.FromSeconds(3), schedule.RequestedInterval);
    }

    [Fact]
    public void Le_tick_ignore_les_groupes_en_pause()
    {
        var fast = new SensorReadSchedule(SensorGroup.Network) { ManualInterval = TimeSpan.FromMilliseconds(250), IsSuspended = true };
        var normal = new SensorReadSchedule(SensorGroup.Cpu) { BaseInterval = TimeSpan.FromSeconds(1) };

        Assert.Equal(TimeSpan.FromSeconds(1), HardwareMonitorService.ComputeTickInterval(new[] { fast, normal }));
    }

    [Fact]
    public void Tous_les_groupes_en_pause_le_releve_tourne_au_ralenti()
    {
        var a = new SensorReadSchedule(SensorGroup.Cpu) { IsSuspended = true };
        var b = new SensorReadSchedule(SensorGroup.Gpu) { IsSuspended = true };

        Assert.Equal(HardwareMonitorService.IdleTickInterval, HardwareMonitorService.ComputeTickInterval(new[] { a, b }));
    }
}
