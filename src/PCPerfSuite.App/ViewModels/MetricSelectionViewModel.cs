using CommunityToolkit.Mvvm.ComponentModel;
using PCPerfSuite.App.Metrics;

namespace PCPerfSuite.App.ViewModels;

public sealed partial class MetricOptionViewModel : ObservableObject
{
    private readonly Action _onChanged;

    public MetricDefinition Definition { get; }
    public string Label => Definition.Label;

    [ObservableProperty] private bool isSelected;

    /// <summary>Vrai quand ce PC ne fournit pas la valeur : l'option reste cochable mais est signalée, pour qu'on
    /// sache avant de l'afficher qu'elle vaudra "N/D".</summary>
    [ObservableProperty] private bool isUnavailable;

    public string UnavailableHint => Definition.UnavailableHint;

    public MetricOptionViewModel(MetricDefinition definition, bool selected, Action onChanged)
    {
        Definition = definition;
        isSelected = selected;
        _onChanged = onChanged;
    }

    partial void OnIsSelectedChanged(bool value) => _onChanged();
}

public sealed record MetricGroupViewModel(string Name, IReadOnlyList<MetricOptionViewModel> Items);

/// <summary>État du MetricPicker : métriques cochées, groupées par catégorie. L'overlay et le Monitoring en ont
/// chacun leur instance ; le Monitoring y ajoute au fil des relevés les capteurs propres à la machine.</summary>
public sealed partial class MetricSelectionViewModel : ObservableObject
{
    private readonly List<MetricOptionViewModel> _options = new();
    private readonly HashSet<string> _knownIds = new();

    /// <summary>Identifiants cochés dont la métrique n'est pas encore proposée (capteur qui n'apparaît qu'au
    /// premier relevé) : gardés pour ne pas perdre la sélection enregistrée.</summary>
    private readonly HashSet<string> _pendingIds;

    [ObservableProperty] private IReadOnlyList<MetricGroupViewModel> groups = Array.Empty<MetricGroupViewModel>();

    /// <summary>Toutes les métriques proposées, dans l'ordre d'affichage.</summary>
    public IReadOnlyList<MetricDefinition> Definitions { get; private set; } = Array.Empty<MetricDefinition>();

    /// <summary>Métriques cochées, dans l'ordre d'affichage.</summary>
    public IReadOnlyList<MetricDefinition> Selected { get; private set; } = Array.Empty<MetricDefinition>();

    public List<string> SelectedIds => Selected.Select(m => m.Id).Concat(_pendingIds).ToList();

    public event Action? SelectionChanged;

    public MetricSelectionViewModel(IEnumerable<string> selectedIds)
        : this(selectedIds, MetricCatalog.All)
    {
    }

    public MetricSelectionViewModel(IEnumerable<string> selectedIds, IEnumerable<MetricDefinition> definitions)
    {
        _pendingIds = new HashSet<string>(selectedIds);
        AddDefinitions(definitions);
    }

    /// <summary>Propose les métriques pas encore connues, les autres sont ignorées. Lève SelectionChanged si
    /// l'une d'elles faisait partie de la sélection enregistrée.</summary>
    public void AddDefinitions(IEnumerable<MetricDefinition> definitions)
    {
        bool added = false;
        bool selectionGrew = false;
        foreach (MetricDefinition definition in definitions)
        {
            if (!_knownIds.Add(definition.Id)) continue;

            bool selected = _pendingIds.Remove(definition.Id);
            _options.Add(new MetricOptionViewModel(definition, selected, OnOptionChanged));
            added = true;
            selectionGrew |= selected;
        }

        if (!added) return;

        Definitions = _options.Select(o => o.Definition).ToList();
        Groups = _options
            .GroupBy(o => o.Definition.Category)
            .Select(g => new MetricGroupViewModel(g.Key.Name, g.ToList()))
            .ToList();
        Selected = CollectSelected();
        if (selectionGrew) SelectionChanged?.Invoke();
    }

    /// <summary>Signale les métriques que ce PC ne fournit pas, d'après le dernier relevé.</summary>
    public void UpdateAvailability(MetricSample sample)
    {
        foreach (MetricOptionViewModel option in _options)
        {
            option.IsUnavailable = option.Definition.Read(sample).IsUnavailable;
        }
    }

    private void OnOptionChanged()
    {
        Selected = CollectSelected();
        SelectionChanged?.Invoke();
    }

    private List<MetricDefinition> CollectSelected()
        => _options.Where(o => o.IsSelected).Select(o => o.Definition).ToList();
}
