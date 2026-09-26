using System.Security.Principal;
using System.Xml.Linq;

namespace PCPerfSuite.Core.SystemInfo;

/// <summary>État de la tâche de démarrage. <paramref name="Target"/> est l'exécutable qu'elle lance, tel que Windows
/// l'a enregistré. <paramref name="UnavailableReason"/> est non nul quand ce PC, ce compte ou cette façon de lancer
/// l'app ne permet pas de créer ou supprimer la tâche : la raison est prête à afficher.</summary>
public sealed record StartupTaskInfo(bool IsEnabled, string? Target, string? UnavailableReason);

/// <summary>
/// Lancement de PCPerfSuite à l'ouverture de session Windows, sans invite d'autorisation (UAC).
///
/// PCPerfSuite exige les droits administrateur (app.manifest). Deux méthodes courantes ne conviennent donc pas :
/// la clé de registre « Run » et le dossier Démarrage lancent l'app avec le jeton normal de l'utilisateur, et Windows
/// bloque au démarrage les programmes qui demandent l'élévation (ou bien il faudrait accepter une invite UAC à chaque
/// ouverture de session). Un service, lui, tourne en session 0 : ni fenêtre ni icône de notification.
///
/// La méthode retenue est celle des outils de monitoring comme LibreHardwareMonitor ou HWiNFO : une tâche planifiée
/// « à l'ouverture de session, avec les autorisations maximales ». Elle est créée UNE fois, par l'app, qui tourne déjà
/// en administrateur (c'est la seule fois où l'élévation compte) ; ensuite le Planificateur de tâches lance l'app
/// directement avec le jeton d'administrateur de la session, sans invite.
///
/// Passe par l'API COM du Planificateur (« Schedule.Service »), celle qu'utilise schtasks : pas de processus externe,
/// pas de fichier XML temporaire, et les accents des chemins passent tels quels. Toute méthode est « best-effort » :
/// elle renvoie un état ou un échec expliqué, jamais une exception.
/// </summary>
public static class StartupTask
{
    /// <summary>Argument passé par la tâche : l'app sait ainsi qu'elle est lancée par Windows, et démarre dans la zone
    /// de notification au lieu d'ouvrir sa fenêtre.</summary>
    public const string LaunchArgument = "--demarrage-windows";

    private const string TaskNamespace = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    private const int TaskCreateOrUpdate = 6;
    private const int TaskLogonInteractiveToken = 3;

    private const int ErrorFileNotFound = unchecked((int)0x80070002);
    private const int ErrorAccessDenied = unchecked((int)0x80070005);

    /// <summary>Une tâche par compte Windows, dans la racine du Planificateur. Sans antislash ni séparateur : Windows
    /// lirait « DOMAINE\compte » comme un dossier.</summary>
    public static string TaskName { get; } = BuildTaskName();

    /// <summary>Lit l'état de la tâche, et dit si elle peut être créée ou supprimée d'ici. Ne lève jamais.</summary>
    public static StartupTaskInfo Read()
    {
        string? reason = CheckAvailability();

        try
        {
            dynamic service = Connect();
            dynamic folder = service.GetFolder("\\");

            try
            {
                dynamic task = folder.GetTask(TaskName);
                string? target = task.Definition.Actions.Item(1).Path as string;
                return new StartupTaskInfo((bool)task.Enabled, target, reason);
            }
            catch (Exception ex) when (ex.HResult == ErrorFileNotFound)
            {
                return new StartupTaskInfo(false, null, reason);
            }
        }
        catch (Exception ex)
        {
            return new StartupTaskInfo(false, null, reason ?? $"Le Planificateur de tâches de Windows est injoignable ({ex.Message}).");
        }
    }

    /// <summary>Crée (ou remplace) la tâche pour l'exécutable en cours. Ne lève jamais.</summary>
    public static bool TryEnable(out string? error)
    {
        error = CheckAvailability();
        if (error is not null) return false;

        try
        {
            string executable = Environment.ProcessPath!;
            dynamic service = Connect();
            dynamic folder = service.GetFolder("\\");
            folder.RegisterTask(TaskName, BuildDefinition(executable), TaskCreateOrUpdate, null, null, TaskLogonInteractiveToken, null);
            return true;
        }
        catch (Exception ex)
        {
            error = Describe("La tâche de démarrage n'a pas pu être créée", ex);
            return false;
        }
    }

    /// <summary>Supprime la tâche. Une tâche déjà absente est un succès : c'est bien l'état demandé.</summary>
    public static bool TryDisable(out string? error)
    {
        error = CheckAvailability();
        if (error is not null) return false;

        try
        {
            dynamic service = Connect();
            dynamic folder = service.GetFolder("\\");
            folder.DeleteTask(TaskName, 0);
            return true;
        }
        catch (Exception ex) when (ex.HResult == ErrorFileNotFound)
        {
            return true;
        }
        catch (Exception ex)
        {
            error = Describe("La tâche de démarrage n'a pas pu être supprimée", ex);
            return false;
        }
    }

    /// <summary>Répare une tâche dont l'exécutable n'existe plus, typiquement après le déplacement du dossier de l'app :
    /// elle est réenregistrée pour la copie en cours. Une tâche qui vise une AUTRE copie encore présente n'est jamais
    /// touchée : un développeur qui lance une version de test ne doit pas détourner le démarrage de la version installée.
    /// Renvoie vrai si elle a été réparée.</summary>
    public static bool RepairIfTargetMissing()
    {
        StartupTaskInfo info = Read();
        if (!info.IsEnabled || info.UnavailableReason is not null) return false;
        if (string.IsNullOrEmpty(info.Target) || File.Exists(info.Target)) return false;

        return TryEnable(out _);
    }

    /// <summary>Vérifications rapides, sans toucher au Planificateur : droits, compte, façon de lancer l'app.</summary>
    private static string? CheckAvailability()
    {
        if (!ElevationHelper.IsAdministrator())
        {
            return "Relance PCPerfSuite en administrateur pour modifier ce réglage : seule une session administrateur peut " +
                   "créer la tâche qui lance l'app sans demande d'autorisation.";
        }

        if (SessionUser.OtherProfileMessage is not null)
        {
            return $"PCPerfSuite tourne sous le compte {SessionUser.ProcessAccount} et non sous {SessionUser.InteractiveAccount} : " +
                   $"la tâche démarrerait à la connexion de {SessionUser.ProcessAccount}. Lance PCPerfSuite depuis " +
                   $"{SessionUser.InteractiveAccount} pour activer ce réglage.";
        }

        // « dotnet PCPerfSuite.dll » : le processus est dotnet.exe, et la tâche lancerait dotnet.exe sans l'app.
        string? processPath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(processPath)
            || Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return "PCPerfSuite n'est pas lancé depuis son propre exécutable : impossible d'indiquer à Windows quoi démarrer.";
        }

        return null;
    }

    private static dynamic Connect()
    {
        Type type = Type.GetTypeFromProgID("Schedule.Service")
                    ?? throw new InvalidOperationException("le Planificateur de tâches n'est pas disponible sur ce PC");
        dynamic service = Activator.CreateInstance(type)!;
        service.Connect();
        return service;
    }

    /// <summary>Définition XML de la tâche (schéma 1.2 du Planificateur). Construite avec XDocument : les chemins
    /// contenant « &amp; » ou des apostrophes sont échappés correctement.</summary>
    private static string BuildDefinition(string executable)
    {
        XNamespace ns = TaskNamespace;
        string account = WindowsIdentity.GetCurrent().Name;

        var task = new XElement(ns + "Task",
            new XAttribute("version", "1.2"),
            new XElement(ns + "RegistrationInfo",
                new XElement(ns + "Description",
                    "Lance PCPerfSuite à l'ouverture de session, avec les droits administrateur et sans demande d'autorisation.")),
            new XElement(ns + "Triggers",
                new XElement(ns + "LogonTrigger",
                    new XElement(ns + "Enabled", "true"),
                    new XElement(ns + "UserId", account))),
            new XElement(ns + "Principals",
                new XElement(ns + "Principal", new XAttribute("id", "Author"),
                    new XElement(ns + "UserId", account),
                    new XElement(ns + "LogonType", "InteractiveToken"),
                    new XElement(ns + "RunLevel", "HighestAvailable"))),
            new XElement(ns + "Settings",
                new XElement(ns + "MultipleInstancesPolicy", "IgnoreNew"),
                // Par défaut le Planificateur ne démarre pas une tâche sur batterie : sur un portable, l'app ne
                // se lancerait alors jamais sans le chargeur.
                new XElement(ns + "DisallowStartIfOnBatteries", "false"),
                new XElement(ns + "StopIfGoingOnBatteries", "false"),
                new XElement(ns + "AllowStartOnDemand", "true"),
                new XElement(ns + "Enabled", "true"),
                new XElement(ns + "ExecutionTimeLimit", "PT0S"),
                // 5 = priorité normale. Le défaut des tâches (7) lancerait l'app en dessous de la normale, pour le
                // processeur comme pour le disque : un moniteur qui échantillonne des capteurs en pâtirait.
                new XElement(ns + "Priority", "5")),
            new XElement(ns + "Actions", new XAttribute("Context", "Author"),
                new XElement(ns + "Exec",
                    new XElement(ns + "Command", executable),
                    new XElement(ns + "Arguments", LaunchArgument),
                    new XElement(ns + "WorkingDirectory", Path.GetDirectoryName(executable) ?? ""))));

        return new XDocument(task).ToString();
    }

    private static string Describe(string what, Exception ex) => ex.HResult == ErrorAccessDenied
        ? $"{what} : Windows a refusé l'accès. Relance PCPerfSuite en administrateur."
        : $"{what} ({ex.Message.Trim()}).";

    private static string BuildTaskName()
    {
        string user = Environment.UserName;
        foreach (char invalid in Path.GetInvalidFileNameChars()) user = user.Replace(invalid, '_');
        return $"PCPerfSuite ({user})";
    }
}
