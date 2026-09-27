using PCPerfSuite.Core.PowerSettings;

namespace PCPerfSuite.Core.Hardware.Cpu;

/// <summary>
/// Retrouve les vraies limites d'usine (firmware) du processeur plutôt que celles lues au lancement de
/// l'app : si PCPerfSuite a déjà relevé les limites lors d'une session Windows précédente et que
/// « Appliquer au démarrage » les a reposées, la valeur lue à ce lancement est déjà celle de
/// PCPerfSuite, pas celle du BIOS/UEFI (voir M2 du rapport de revue).
///
/// La session Windows est identifiée par son heure de démarrage approximative (dérivée
/// d'<see cref="Environment.TickCount64"/>), qui ne change qu'au redémarrage : tant qu'elle correspond
/// à la dernière capture, celle-ci reste la référence ; sinon (redémarrage détecté), la valeur qui
/// vient d'être lue sur le matériel est la nouvelle référence.
/// </summary>
public static class CpuFirmwareDefaultsStore
{
    /// <summary>Tolérance sur l'heure de démarrage : deux lectures d'Environment.TickCount64 à quelques
    /// secondes d'intervalle ne tombent pas exactement sur la même valeur.</summary>
    private static readonly TimeSpan BootTimeTolerance = TimeSpan.FromSeconds(5);

    /// <summary>Renvoie les limites à afficher comme « valeurs d'origine », en mémorisant la lecture
    /// courante si elle correspond à un nouveau démarrage de Windows.</summary>
    public static (float Sustained, float Burst) Resolve(float freshlyReadSustained, float freshlyReadBurst)
    {
        long currentBoot = CurrentBootTimestampTicks();
        float resultSustained = freshlyReadSustained;
        float resultBurst = freshlyReadBurst;

        // Update() plutôt que Load()/Save() séparés : le backend Intel et le backend AMD ne s'exécutent
        // jamais ensemble, mais un appel concurrent depuis l'interface (relecture manuelle) ne doit pas
        // pouvoir s'intercaler et perdre la capture.
        AppSettingsStore.Update(settings =>
        {
            CpuControlSettings cpu = settings.Cpu;

            if (cpu.OriginalBootTimestampTicks is { } stored
                && Math.Abs(stored - currentBoot) <= BootTimeTolerance.Ticks
                && cpu.OriginalSustainedWatts is { } sustained
                && cpu.OriginalBurstWatts is { } burst)
            {
                resultSustained = sustained;
                resultBurst = burst;
                return;
            }

            cpu.OriginalSustainedWatts = freshlyReadSustained;
            cpu.OriginalBurstWatts = freshlyReadBurst;
            cpu.OriginalBootTimestampTicks = currentBoot;
        });

        return (resultSustained, resultBurst);
    }

    private static long CurrentBootTimestampTicks()
        => (DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64)).Ticks;
}
