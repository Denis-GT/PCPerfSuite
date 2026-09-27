using PCPerfSuite.Core.SystemInfo;

namespace PCPerfSuite.Core.Tests;

/// <summary>La tâche planifiée de démarrage de Windows : ces tests ne créent ni ne suppriment rien, ils vérifient que
/// la lecture d'état reste sans danger quel que soit le PC et le compte qui les exécute.</summary>
public class StartupTaskTests
{
    [Fact]
    public void Le_nom_de_la_tache_ne_contient_aucun_separateur_de_dossier()
    {
        // Le Planificateur lirait « DOMAINE\compte » comme un dossier : une tâche par compte, sans antislash.
        Assert.StartsWith("PCPerfSuite (", StartupTask.TaskName);
        Assert.DoesNotContain('\\', StartupTask.TaskName);
        Assert.DoesNotContain('/', StartupTask.TaskName);
    }

    [Fact]
    public void Lire_l_etat_ne_leve_jamais_et_une_tache_absente_n_a_pas_de_cible()
    {
        StartupTaskInfo info = StartupTask.Read();

        Assert.NotNull(info);
        if (!info.IsEnabled) Assert.Null(info.Target);
    }

    [Fact]
    public void Sans_droits_administrateur_modifier_le_reglage_est_refuse_avec_une_raison()
    {
        if (ElevationHelper.IsAdministrator()) return; // Élevé, la modification serait réellement possible : on n'y touche pas.

        Assert.False(StartupTask.TryEnable(out string? enableError));
        Assert.False(StartupTask.TryDisable(out string? disableError));
        Assert.Contains("administrateur", enableError);
        Assert.Contains("administrateur", disableError);
        Assert.False(StartupTask.RepairIfTargetMissing());
    }

    [Fact]
    public void L_argument_de_demarrage_est_stable()
    {
        // Écrit dans les tâches déjà créées chez les utilisateurs : le renommer les casserait en silence.
        Assert.Equal("--demarrage-windows", StartupTask.LaunchArgument);
    }
}
