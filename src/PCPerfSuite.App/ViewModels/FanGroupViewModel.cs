using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace PCPerfSuite.App.ViewModels;

/// <summary>
/// Une section de l'onglet Ventilateurs : un titre (« Processeur », « Boîtier »...) et les ventilateurs qui y sont
/// rangés. L'instance d'une section survit aux relevés : seul son contenu change, si bien qu'une section dépliée
/// par l'utilisateur le reste quand un ventilateur en rejoint ou en quitte une autre.
/// </summary>
public sealed partial class FanGroupViewModel : ObservableObject
{
    private readonly string _title;

    public FanGroupViewModel(string title, string? note = null, bool isCollapsible = false)
    {
        _title = title;
        Note = note;
        IsCollapsible = isCollapsible;

        // Une section repliable démarre repliée : elle ne contient que ce qui n'a pas de ventilateur détecté.
        isExpanded = !isCollapsible;

        Fans.CollectionChanged += (_, _) => OnPropertyChanged(nameof(Heading));
    }

    public ObservableCollection<FanControlItemViewModel> Fans { get; } = new();

    /// <summary>Titre affiché ; une section repliable y ajoute son effectif, visible même repliée.</summary>
    public string Heading => IsCollapsible ? $"{_title} ({Fans.Count})" : _title;

    /// <summary>Phrase d'explication sous le titre, quand la section a besoin d'être expliquée.</summary>
    public string? Note { get; }

    public bool IsCollapsible { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExpanderGlyph))]
    private bool isExpanded;

    /// <summary>Chevron : vers le bas quand la section est dépliée, vers la droite sinon.</summary>
    public string ExpanderGlyph => IsExpanded ? "" : "";

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;
}
