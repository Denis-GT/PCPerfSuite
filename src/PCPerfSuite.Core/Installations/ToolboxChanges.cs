using PCPerfSuite.Core.PowerSettings;
using PCPerfSuite.Core.SystemChanges;

namespace PCPerfSuite.Core.Installations;

/// <summary>
/// Ce que la Boîte à outils a laissé sur le PC, pour le registre des modifications (« Tout rétablir » du mode
/// technicien) : les outils portables déposés dans %ProgramData%\PCPerfSuite\Tools, que <see cref="RestoreAll"/>
/// supprime, et les outils posés par leur propre installeur, que PCPerfSuite ne désinstalle pas d'office
/// (<see cref="SystemChange.CanRestore"/> faux) : un installeur tiers sait seul ce qu'il a mis où.
///
/// Les fichiers remis dans Téléchargements appartiennent à l'utilisateur : ils ne sont pas listés ici.
/// </summary>
public sealed class ToolboxChanges : ISystemChangeOwner
{
    private readonly IReadOnlyList<ToolDefinition> _tools;
    private readonly Func<ToolDefinition, ToolInstallState> _detect;
    private readonly Func<IReadOnlyList<ToolInstallRecord>> _installedByApp;
    private readonly Func<ToolDefinition, ToolActionOutcome> _removePortable;

    public ToolboxChanges()
        : this(ToolCatalog.All, ToolDetection.Detect, () => AppSettingsStore.Load().Toolbox.InstalledByApp, ToolboxActions.RemovePortable)
    {
    }

    /// <summary>Pour les tests : détection, outils installés et suppression donnés.</summary>
    internal ToolboxChanges(IReadOnlyList<ToolDefinition> tools, Func<ToolDefinition, ToolInstallState> detect,
        Func<IReadOnlyList<ToolInstallRecord>> installedByApp, Func<ToolDefinition, ToolActionOutcome> removePortable)
    {
        _tools = tools;
        _detect = detect;
        _installedByApp = installedByApp;
        _removePortable = removePortable;
    }

    public string Id => "boite-a-outils";
    public string Title => "Boîte à outils";

    /// <summary>S'arrête au premier outil portable trouvé, ou à la première installation inscrite encore présente.</summary>
    public bool HasChanges
        => _tools.Where(t => t.IsPortable).Any(t => SafeDetect(t) is { IsPresent: true, IsPortable: true })
           || SafeInstalledByApp().Any(record => _tools.FirstOrDefault(t => t.Id == record.Id) is { IsInstaller: true } tool && SafeDetect(tool).IsPresent);

    public IReadOnlyList<SystemChange> Describe()
    {
        var changes = new List<SystemChange>();

        foreach ((ToolDefinition tool, ToolInstallState state) in PresentPortables()) changes.Add(PortableChange(tool, state));

        foreach (ToolInstallRecord record in SafeInstalledByApp())
        {
            if (_tools.FirstOrDefault(t => t.Id == record.Id) is not { } tool || !tool.IsInstaller) continue;

            ToolInstallState state = SafeDetect(tool);
            if (!state.IsPresent) continue;

            string version = state.Version ?? record.Version ?? "";
            changes.Add(new SystemChange($"{tool.Name} {version}".TrimEnd() + ", installé par son installeur",
                "PCPerfSuite ne le désinstalle pas lui-même : son installeur sait seul ce qu'il a posé. Désinstalle-le depuis " +
                "Paramètres Windows › Applications › Applications installées.", CanRestore: false));
        }

        return changes;
    }

    /// <summary>Supprime les outils portables ; les outils installés restent, et le résultat le dit.</summary>
    public SystemRestoreResult RestoreAll()
    {
        IReadOnlyList<SystemChange> before = Describe();
        if (before.Count == 0) return SystemRestoreResult.Nothing;

        var notRestored = new List<SystemChange>(before.Where(change => !change.CanRestore));
        var failures = new List<string>();
        int removed = 0;

        foreach ((ToolDefinition tool, ToolInstallState state) in PresentPortables())
        {
            ToolActionOutcome outcome = SafeRemove(tool);
            if (outcome.Succeeded)
            {
                removed++;
            }
            else
            {
                notRestored.Add(PortableChange(tool, state));
                failures.Add(outcome.Message);
            }
        }

        SystemRestoreStatus status = notRestored.Count == 0
            ? SystemRestoreStatus.Restored
            : removed == 0 && failures.Count > 0 ? SystemRestoreStatus.Failed : SystemRestoreStatus.Partial;

        string? message = failures.Count > 0 ? string.Join(" ", failures) : null;
        return new SystemRestoreResult(status, notRestored, message);
    }

    private IEnumerable<(ToolDefinition Tool, ToolInstallState State)> PresentPortables()
        => _tools.Where(t => t.IsPortable)
            .Select(t => (Tool: t, State: SafeDetect(t)))
            .Where(pair => pair.State is { IsPresent: true, IsPortable: true })
            .ToList();

    private static SystemChange PortableChange(ToolDefinition tool, ToolInstallState state)
        => new($"{tool.Name} {state.Version} (portable)".Replace("  ", " "),
            $"Dossier {state.Location ?? ProgramDataFolder.RootPath} : « Tout rétablir » le supprime.", CanRestore: true);

    private ToolInstallState SafeDetect(ToolDefinition tool)
    {
        try { return _detect(tool); }
        catch { return ToolInstallState.Absent; }
    }

    private IReadOnlyList<ToolInstallRecord> SafeInstalledByApp()
    {
        try { return _installedByApp() ?? Array.Empty<ToolInstallRecord>(); }
        catch { return Array.Empty<ToolInstallRecord>(); }
    }

    private ToolActionOutcome SafeRemove(ToolDefinition tool)
    {
        try { return _removePortable(tool); }
        catch (Exception ex) { return new ToolActionOutcome(false, $"{tool.Name} n'a pas pu être supprimé ({ex.Message})."); }
    }
}
