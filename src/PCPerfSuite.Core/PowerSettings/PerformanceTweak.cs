namespace PCPerfSuite.Core.PowerSettings;

public enum TweakState
{
    Enabled,
    Disabled,
    Unknown,
}

public sealed class PerformanceTweak
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string Category { get; init; }

    /// <summary>Vrai si ce réglage n'a d'effet complet qu'après redémarrage/déconnexion.</summary>
    public bool RequiresRestart { get; init; }

    /// <summary>Vrai si activer ce réglage dégrade la sécurité ou la stabilité — affiché avec un avertissement.</summary>
    public bool IsRisky { get; init; }

    public required Func<TweakState> GetState { get; init; }
    public required Action<bool> Apply { get; init; }
}
