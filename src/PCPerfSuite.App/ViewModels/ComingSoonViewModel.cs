namespace PCPerfSuite.App.ViewModels;

/// <summary>Vue de remplacement pour les fonctionnalités prévues en phase 2/3 (ventilateurs, GPU, overlay) —
/// garde la navigation complète visible dès la 1ère version, avec le détail de ce qui arrive.</summary>
public sealed class ComingSoonViewModel
{
    public string Title { get; }
    public string Subtitle { get; }
    public IReadOnlyList<string> Bullets { get; }

    public ComingSoonViewModel(string title, string subtitle, IReadOnlyList<string> bullets)
    {
        Title = title;
        Subtitle = subtitle;
        Bullets = bullets;
    }
}
