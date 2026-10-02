using PCPerfSuite.App.ViewModels;
using PCPerfSuite.Core.Hardware;

namespace PCPerfSuite.App.Tests;

/// <summary>Fenêtre cachée, la bascule ne demande ses capteurs que toutes les 5 s, et le délai ne repart qu'une fois relu
/// tout ce qu'elle a demandé.</summary>
public class EcoSampleGateTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 20, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    private static readonly SensorGroup[] AutoSwitch = [SensorGroup.CpuLoad, SensorGroup.Fps, SensorGroup.Battery, SensorGroup.Gpu];

    [Fact]
    public void L_overlay_qui_relit_la_charge_cpu_chaque_seconde_ne_fait_pas_repartir_le_delai()
    {
        var gate = new EcoSampleGate(Interval);
        gate.Saw(AutoSwitch);
        Assert.True(gate.IsDue(T0));
        gate.Requested(AutoSwitch);

        // Sur un relevé où seule la charge CPU a été relue (l'overlay), la batterie et les FPS restent à relire.
        gate.OnRead([SensorGroup.CpuLoad], T0.AddSeconds(1));

        Assert.True(gate.IsDue(T0.AddSeconds(2)));
    }

    [Fact]
    public void Une_fois_tout_relu_meme_en_plusieurs_releves_le_delai_repart()
    {
        var gate = new EcoSampleGate(Interval);
        gate.Saw(AutoSwitch);
        gate.Requested(AutoSwitch);

        gate.OnRead([SensorGroup.CpuLoad, SensorGroup.Fps], T0);
        Assert.True(gate.IsDue(T0.AddSeconds(1)));
        gate.OnRead([SensorGroup.Battery, SensorGroup.Gpu], T0.AddSeconds(1));

        Assert.False(gate.IsDue(T0.AddSeconds(3)));
        Assert.True(gate.IsDue(T0.AddSeconds(6)));
    }

    [Fact]
    public void Un_groupe_que_ce_pc_ne_lit_jamais_n_annule_pas_le_mode_eco()
    {
        // Machine virtuelle ou GPU que le relevé ne voit pas : le groupe Gpu n'est jamais lu.
        var gate = new EcoSampleGate(Interval);
        gate.Requested(AutoSwitch);

        gate.OnRead([SensorGroup.CpuLoad, SensorGroup.Fps, SensorGroup.Battery], T0);

        Assert.False(gate.IsDue(T0.AddSeconds(2)));
    }

    [Fact]
    public void Une_horloge_qui_recule_ne_bloque_pas_les_releves()
    {
        var gate = new EcoSampleGate(Interval);
        gate.Requested(AutoSwitch);
        gate.OnRead(AutoSwitch, T0);

        Assert.True(gate.IsDue(T0.AddHours(-1)));
    }

    [Fact]
    public void Sans_rien_demande_le_delai_ne_repart_pas()
    {
        var gate = new EcoSampleGate(Interval);
        gate.Requested([]);
        gate.OnRead(AutoSwitch, T0);

        Assert.True(gate.IsDue(T0.AddSeconds(1)));
    }
}
