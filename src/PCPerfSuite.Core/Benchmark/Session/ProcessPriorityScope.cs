using System.Diagnostics;

namespace PCPerfSuite.Core.Benchmark.Session;

/// <summary>
/// Relève la priorité du processus de l'app le temps d'un test (parade au worker en High, qui sinon affame le relevé des
/// capteurs et l'interface), puis la rend. Best-effort : un refus laisse la priorité telle quelle et le dit.
/// </summary>
public sealed class ProcessPriorityScope : IDisposable
{
    private readonly ProcessPriorityClass? _previous;

    private ProcessPriorityScope(ProcessPriorityClass? previous, string note)
    {
        _previous = previous;
        Note = note;
    }

    public string Note { get; }

    public static ProcessPriorityScope Raise(ProcessPriorityClass target = ProcessPriorityClass.High)
    {
        try
        {
            using Process current = Process.GetCurrentProcess();
            ProcessPriorityClass previous = current.PriorityClass;
            if (previous == target) return new ProcessPriorityScope(null, $"priorité de l'app déjà {target}");
            current.PriorityClass = target;
            return new ProcessPriorityScope(previous, $"priorité de l'app {target} pendant le test");
        }
        catch (Exception ex)
        {
            return new ProcessPriorityScope(null, $"priorité de l'app inchangée ({ex.GetType().Name})");
        }
    }

    public void Dispose()
    {
        if (_previous is not { } previous) return;
        try
        {
            using Process current = Process.GetCurrentProcess();
            current.PriorityClass = previous;
        }
        catch (Exception)
        {
            // Rien de mieux à faire : la priorité retombera avec le processus.
        }
    }
}
