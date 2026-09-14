using CommunityToolkit.Mvvm.ComponentModel;
using PCPerfSuite.App.Metrics;

namespace PCPerfSuite.App.ViewModels;

public sealed partial class MetricOptionViewModel : ObservableObject
{
    private readonly Action _onChanged;

    public MetricDefinition Definition { get; }
    public string Label => Definition.Label;

    [ObservableProperty] private bool isSelected;

    public MetricOptionViewModel(MetricDefinition definition, bool selected, Action onChanged)
    {
        Definition = definition;
        isSelected = selected;
        _onChanged = onChanged;
    }

    partial void OnIsSelectedChanged(bool value) => _onChanged();
}

public sealed record MetricGroupViewModel(string Name, IReadOnlyList<MetricOptionViewModel> Items);

/// <summary>État du MetricPicker : métriques du catalogue cochées, groupées par catégorie. L'overlay et
/// "Mes métriques" en ont chacun leur instance.</summary>
public sealed class MetricSelectionViewModel
{
    private readonly List<MetricOptionViewModel> _options;

    public IReadOnlyList<MetricGroupViewModel> Groups { get; }

    /// <summary>Métriques cochées, toujours dans l'ordre du catalogue.</summary>
    public IReadOnlyList<MetricDefinition> Selected { get; private set; }

    public List<string> SelectedIds => Selected.Select(m => m.Id).ToList();

    public event Action? SelectionChanged;

    public MetricSelectionViewModel(IEnumerable<string> selectedIds)
    {
        var initial = new HashSet<string>(selectedIds);
        _options = MetricCatalog.All
            .Select(m => new MetricOptionViewModel(m, initial.Contains(m.Id), OnOptionChanged))
            .ToList();
        Groups = _options
            .GroupBy(o => o.Definition.Category)
            .Select(g => new MetricGroupViewModel(g.Key.Name, g.ToList()))
            .ToList();
        Selected = CollectSelected();
    }

    private void OnOptionChanged()
    {
        Selected = CollectSelected();
        SelectionChanged?.Invoke();
    }

    private List<MetricDefinition> CollectSelected()
        => _options.Where(o => o.IsSelected).Select(o => o.Definition).ToList();
}
