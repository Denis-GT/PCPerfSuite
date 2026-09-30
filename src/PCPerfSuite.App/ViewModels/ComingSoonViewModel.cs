namespace PCPerfSuite.App.ViewModels;

/// <summary>Page ou onglet pas encore livré (« Bientôt disponible ») : les pages à venir restent visibles dans la
/// navigation, avec ce qu'elles feront (voir <see cref="ComingSoonPages"/>, et l'onglet Paramètres › Thèmes).</summary>
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
