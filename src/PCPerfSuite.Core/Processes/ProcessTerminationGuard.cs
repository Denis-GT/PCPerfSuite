namespace PCPerfSuite.Core.Processes;

/// <summary>
/// Refuse de terminer ce qui ne doit pas l'être, sur le modèle de <see cref="Storage.DeletionGuard"/>.
/// Deux familles de règles volontairement redondantes : le drapeau « critique » que Windows lui-même pose
/// sur un processus, et une liste de noms connus. La liste seule ne suffirait pas (un nom de fichier
/// s'usurpe en trois secondes), le drapeau seul non plus (il n'est lisible que si le handle a pu être
/// ouvert, et il ne couvre pas explorer.exe ni dwm.exe, qui ne mettent pas Windows par terre mais ruinent
/// la session en cours).
/// </summary>
public static class ProcessTerminationGuard
{
    /// <summary>Processus dont l'arrêt casse Windows ou la session. Comparés sans extension ni casse.
    /// Cette liste est un second rideau derrière <see cref="ProcessInfo.IsCritical"/>, jamais l'unique filet.</summary>
    private static readonly HashSet<string> ProtectedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "idle", "registry", "memory compression", "memcompression", "secure system",
        "smss", "csrss", "wininit", "winlogon", "services", "lsass", "lsaiso", "fontdrvhost",
    };

    /// <summary>Hôtes de services : on n'arrête pas l'hôte, on arrête le service.</summary>
    private const string ServiceHostName = "svchost";

    /// <summary>Processus qu'on peut techniquement terminer, mais qui emportent l'interface de la session.
    /// Ils ne sont pas refusés — Windows relance le shell — seulement signalés avant confirmation.</summary>
    private static readonly HashSet<string> SessionCriticalNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "dwm",
    };

    /// <summary>Phrase prête à afficher expliquant pourquoi ce processus ne sera pas terminé, ou null si
    /// l'action est permise.</summary>
    public static string? GetRefusalReason(ProcessInfo process)
    {
        try
        {
            if (process.Pid == System.Environment.ProcessId)
            {
                return "PCPerfSuite ne se termine pas lui-même : ferme sa fenêtre pour quitter.";
            }

            if (process.Pid is 0 or 4)
            {
                return $"« {process.Name} » est un processus du noyau Windows : il n'est pas arrêtable, "
                       + "même en administrateur.";
            }

            string bare = BareName(process.Name);

            if (process.IsCritical)
            {
                return $"Windows a marqué « {process.Name} » comme processus critique : l'arrêter provoquerait "
                       + "un arrêt brutal du système. PCPerfSuite refuse de s'y attaquer.";
            }

            if (ProtectedNames.Contains(bare))
            {
                return $"« {process.Name} » est un composant indispensable de Windows : l'arrêter provoquerait "
                       + "un arrêt brutal du système. PCPerfSuite refuse de s'y attaquer.";
            }

            if (string.Equals(bare, ServiceHostName, StringComparison.OrdinalIgnoreCase))
            {
                return $"« {process.Name} » héberge des services Windows, souvent plusieurs à la fois. Arrête le "
                       + "service concerné depuis la console Services de Windows plutôt que le processus qui l'héberge.";
            }

            return null;
        }
        catch (Exception ex)
        {
            // On échoue du côté sûr : un garde-fou qui n'a pas pu se prononcer refuse. Sans ce filet,
            // l'exception remonterait jusqu'à la commande asynchrone du menu, qui la relaie sur le thread UI.
            return $"Impossible de vérifier que « {process.Name} » peut être terminé sans risque "
                   + $"({ex.Message}). Terminaison refusée.";
        }
    }

    /// <summary>Avertissement supplémentaire à faire figurer dans la confirmation, pour un processus qu'on
    /// accepte de terminer mais dont l'arrêt se verra immédiatement à l'écran. Null s'il n'y a rien à dire.</summary>
    public static string? GetExtraWarning(ProcessInfo process)
    {
        string bare = BareName(process.Name);

        if (string.Equals(bare, "explorer", StringComparison.OrdinalIgnoreCase))
        {
            return "C'est le shell de Windows : la barre des tâches et le bureau vont disparaître. Windows le "
                   + "relance normalement tout seul ; sinon, relance-le depuis le Gestionnaire des tâches.";
        }

        if (string.Equals(bare, "dwm", StringComparison.OrdinalIgnoreCase))
        {
            return "C'est le compositeur de bureau : l'affichage va clignoter le temps que Windows le relance.";
        }

        if (process.Kind == ProcessKind.Windows)
        {
            return "Ce processus appartient à Windows : son arrêt peut désactiver une fonction du système "
                   + "jusqu'à la prochaine ouverture de session.";
        }

        return null;
    }

    private static string BareName(string fileName)
    {
        int dot = fileName.LastIndexOf('.');
        return dot > 0 ? fileName[..dot] : fileName;
    }
}
