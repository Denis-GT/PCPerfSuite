using PCPerfSuite.Core.Hardware.Cpu;

namespace PCPerfSuite.Core.Tests;

/// <summary>Les instances PDH par processeur logique, la charge plafonnée comme le Gestionnaire des tâches, et la part
/// du temps parqué.</summary>
public class CoreActivityReaderTests
{
    [Theory]
    [InlineData("0,3", 0, 3)]
    [InlineData("1,12", 1, 12)]
    [InlineData(" 0 , 7 ", 0, 7)]
    public void Une_instance_groupe_index_designe_un_processeur_logique(string name, int group, int index)
    {
        Assert.True(CoreActivityReader.TryParseInstance(name, out LogicalProcessorId id));
        Assert.Equal(new LogicalProcessorId(group, index), id);
    }

    [Theory]
    [InlineData("_Total")]
    [InlineData("0,_Total")]
    [InlineData("1,_Total")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("0")]
    [InlineData("0,1,2")]
    [InlineData("-1,2")]
    public void Les_totaux_et_les_noms_inattendus_sont_ignores(string? name)
    {
        Assert.False(CoreActivityReader.TryParseInstance(name, out _));
    }

    [Fact]
    public void L_instance_rendue_par_ToString_se_relit()
    {
        var id = new LogicalProcessorId(1, 5);

        Assert.True(CoreActivityReader.TryParseInstance(id.ToString(), out LogicalProcessorId read));
        Assert.Equal(id, read);
    }

    [Theory]
    [InlineData(143.2, 100)]
    [InlineData(-3, 0)]
    [InlineData(double.NaN, 0)]
    [InlineData(42.5, 42.5)]
    public void La_charge_est_plafonnee_a_100(double raw, double expected)
    {
        Assert.Equal(expected, CoreActivityReader.CapUtility(raw));
    }

    [Fact]
    public void La_part_parquee_porte_sur_les_derniers_releves()
    {
        var window = new ParkedShareWindow(capacity: 4);
        var p0 = new LogicalProcessorId(0, 0);

        foreach (bool parked in new[] { true, true, true, true, false, false })
        {
            window.Add([new KeyValuePair<LogicalProcessorId, bool>(p0, parked)]);
        }

        // Les deux premiers relevés sont sortis de la fenêtre : 2 parqués sur les 4 derniers.
        Assert.Equal(0.5, window.Share(p0));
    }

    [Fact]
    public void Un_processeur_jamais_releve_n_a_pas_de_part()
    {
        var window = new ParkedShareWindow();
        window.Add([new KeyValuePair<LogicalProcessorId, bool>(new LogicalProcessorId(0, 0), true)]);

        Assert.Null(window.Share(new LogicalProcessorId(0, 1)));

        window.Clear();
        Assert.Null(window.Share(new LogicalProcessorId(0, 0)));
    }
}
