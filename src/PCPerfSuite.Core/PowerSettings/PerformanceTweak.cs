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

    /// <summary>Vrai quand appliquer ce réglage écrit dans HKLM ou dans le plan d'alimentation, ce qui
    /// demande les droits administrateur. Faux pour ceux qui n'écrivent que dans le profil de l'utilisateur
    /// (HKCU) ou qui se contentent d'ouvrir une page de réglages Windows : les bloquer sans élévation
    /// priverait l'utilisateur de réglages qui marcheraient parfaitement.</summary>
    public bool RequiresElevation { get; init; } = true;

    /// <summary>Vrai quand le réglage ne se modifie pas depuis l'app : <see cref="Apply"/> se contente
    /// d'ouvrir la page Windows correspondante, et l'interrupteur doit revenir à l'état réel plutôt que
    /// de rester dans une position qui ne correspond à rien.</summary>
    public bool IsReadOnly { get; init; }

    public required Func<TweakState> GetState { get; init; }
    public required Action<bool> Apply { get; init; }
}
