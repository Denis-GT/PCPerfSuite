using System.Windows;
using System.Windows.Controls;

namespace PCPerfSuite.App.Controls;

/// <summary>
/// En-tête commun à toutes les pages : titre, sous-titre, et à droite le contenu du contrôle
/// (les actions propres à la page). Son apparence est définie dans Styles/Theme.xaml.
/// </summary>
public sealed class PageHeader : ContentControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(PageHeader), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty SubtitleProperty =
        DependencyProperty.Register(nameof(Subtitle), typeof(string), typeof(PageHeader), new PropertyMetadata(null));

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Subtitle
    {
        get => (string?)GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }
}
