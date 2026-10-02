using System.Text.RegularExpressions;
using PCPerfSuite.Core.Benchmark.Worker;
using PCPerfSuite.Core.SystemInfo;

namespace PCPerfSuite.App;

public enum SecondaryModeKind
{
    /// <summary>L'app, avec sa fenêtre (ou dans la zone de notification si Windows l'a lancée).</summary>
    Normal,

    /// <summary>Le worker de charge du bench : aucune fenêtre, aucun réglage lu, juste le tube.</summary>
    BenchWorker,

    /// <summary>Argument inconnu ou incohérent : l'app quitte sans fenêtre, code <see cref="SecondaryModes.RefusedExitCode"/>.</summary>
    Refused,
}

public sealed record SecondaryMode(SecondaryModeKind Kind, bool LaunchedByWindows, string? PipeName, string? Problem);

/// <summary>
/// Modes secondaires de l'exe (décision D2 : un seul binaire à signer, donc le worker de bench et demain le chien de
/// garde sont des modes du même exe). Liste fermée, lue en tête d'<c>App.OnStartup</c>, avant le mutex d'instance
/// unique et avant toute lecture du dossier de données : un argument inconnu est refusé, sans fenêtre.
/// <c>--demarrage-windows</c> (tâche de démarrage) reste tel quel. #15 ajoutera <c>--watchdog</c> et <c>--reprendre-oc</c>.
/// </summary>
public static partial class SecondaryModes
{
    public const string BenchWorkerArgument = BenchWorkerLauncher.Argument;
    public const int RefusedExitCode = 2;

    public static SecondaryMode Parse(IReadOnlyList<string> args)
    {
        bool launchedByWindows = false;
        string? pipeName = null;

        for (int i = 0; i < args.Count; i++)
        {
            string argument = args[i];
            if (string.Equals(argument, StartupTask.LaunchArgument, StringComparison.OrdinalIgnoreCase))
            {
                launchedByWindows = true;
            }
            else if (string.Equals(argument, BenchWorkerArgument, StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Count) return Refused($"{BenchWorkerArgument} sans nom de tube");
                string candidate = args[++i];
                if (!PipeNamePattern().IsMatch(candidate)) return Refused("nom de tube invalide");
                if (pipeName is not null) return Refused($"{BenchWorkerArgument} répété");
                pipeName = candidate;
            }
            else
            {
                return Refused($"argument inconnu « {argument} »");
            }
        }

        if (pipeName is not null && launchedByWindows) return Refused("worker de bench et démarrage par Windows incompatibles");

        return pipeName is not null
            ? new SecondaryMode(SecondaryModeKind.BenchWorker, false, pipeName, null)
            : new SecondaryMode(SecondaryModeKind.Normal, launchedByWindows, null, null);
    }

    private static SecondaryMode Refused(string problem) => new(SecondaryModeKind.Refused, false, null, problem);

    [GeneratedRegex("^[A-Za-z0-9._-]{1,128}$")]
    private static partial Regex PipeNamePattern();
}
