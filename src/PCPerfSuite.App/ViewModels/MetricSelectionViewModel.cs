using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

/// <summary>
/// Une catégorie du MetricPicker, affichée en boîte repliable : la liste complète des métriques et des
/// capteurs d'une machine bien équipée fait plusieurs dizaines de cases, et un mur de cases ne se lit pas.
/// La boîte porte donc le compte de ce qui est coché et de quoi tout cocher d'un geste.
/// </summary>
public sealed partial class MetricGroupViewModel : ObservableObject
{
    private readonly Action<MetricGroupViewModel, bool> _setAll;

    public MetricCategory Category { get; }
    public string Name => Category.Name;

    /// <summary>Pastille de couleur de l'en-tête : la même teinte que les courbes du Monitoring et que
    /// la catégorie dans l'overlay, pour qu'on relie les trois d'un coup d'œil.</summary>
    public Brush Accent { get; }

    [ObservableProperty] private IReadOnlyList<MetricOptionViewModel> items = Array.Empty<MetricOptionViewModel>();

    /// <summary>Déplié par défaut : on ne cache pas d'emblée ce que l'utilisateur vient d'ouvrir.</summary>
    [ObservableProperty] private bool isExpanded = true;

    [ObservableProperty] private string countDisplay = "";

    [ObservableProperty] private string toggleAllLabel = "Tout cocher";

    public IRelayCommand ToggleAllCommand { get; }

    public MetricGroupViewModel(MetricCategory category, Action<MetricGroupViewModel, bool> setAll)
    {
        Category = category;
        _setAll = setAll;
        Accent = BuildAccent(category.DefaultColor);
        ToggleAllCommand = new RelayCommand(ToggleAll);
    }

    /// <summary>Recalcule le compteur. Appelé par le ViewModel parent, qui sait quand une case a bougé :
    /// s'abonner à chaque option ferait le même travail avec un abonnement par métrique à entretenir.</summary>
    public void RefreshCount()
    {
        int selected = Items.Count(o => o.IsSelected);
        CountDisplay = $"{selected}/{Items.Count}";
        ToggleAllLabel = selected == Items.Count && Items.Count > 0 ? "Tout décocher" : "Tout cocher";
    }

    private void ToggleAll() => _setAll(this, Items.Count == 0 || Items.Any(o => !o.IsSelected));

    private static Brush BuildAccent(string hex)
    {
        // Une couleur de catégorie illisible ne doit pas empêcher d'afficher la boîte.
        Color color;
        try
        {
            color = (Color)ColorConverter.ConvertFromString(hex);
        }
        catch (FormatException)
        {
            color = Colors.Gray;
        }

        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

/// <summary>État du MetricPicker : métriques cochées, groupées par catégorie. L'overlay et le Monitoring en ont
/// chacun leur instance ; le Monitoring y ajoute au fil des relevés les capteurs propres à la machine.</summary>
public sealed partial class MetricSelectionViewModel : ObservableObject
{
    private readonly List<MetricOptionViewModel> _options = new();
    private readonly HashSet<string> _knownIds = new();

    /// <summary>Les boîtes déjà construites, réutilisées quand un capteur apparaît en cours de route : une
    /// boîte recréée repartirait dépliée et perdrait le repli que l'utilisateur venait de faire.</summary>
    private readonly Dictionary<MetricCategory, MetricGroupViewModel> _groupsByCategory = new();

    /// <summary>Identifiants cochés dont la métrique n'est pas encore proposée (capteur qui n'apparaît qu'au
    /// premier relevé) : gardés pour ne pas perdre la sélection enregistrée.</summary>
    private readonly HashSet<string> _pendingIds;

    /// <summary>Vrai le temps de cocher toute une catégorie : sans ça, « tout cocher » préviendrait le
    /// Monitoring une fois par case, qui reconstruirait ses tuiles autant de fois.</summary>
    private bool _isBulkChanging;

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
        RebuildGroups();
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

    private void RebuildGroups()
    {
        var rebuilt = new List<MetricGroupViewModel>();
        foreach (IGrouping<MetricCategory, MetricOptionViewModel> grouping in _options.GroupBy(o => o.Definition.Category))
        {
            if (!_groupsByCategory.TryGetValue(grouping.Key, out MetricGroupViewModel? group))
            {
                group = new MetricGroupViewModel(grouping.Key, SetGroupSelection);
                _groupsByCategory[grouping.Key] = group;
            }

            group.Items = grouping.ToList();
            group.RefreshCount();
            rebuilt.Add(group);
        }

        Groups = rebuilt;
    }

    /// <summary>« Tout cocher » / « tout décocher » d'une catégorie, en une seule notification.</summary>
    private void SetGroupSelection(MetricGroupViewModel group, bool selected)
    {
        _isBulkChanging = true;
        try
        {
            foreach (MetricOptionViewModel option in group.Items) option.IsSelected = selected;
        }
        finally
        {
            _isBulkChanging = false;
        }

        OnOptionChanged();
    }

    private void OnOptionChanged()
    {
        if (_isBulkChanging) return;

        foreach (MetricGroupViewModel group in Groups) group.RefreshCount();
        Selected = CollectSelected();
        SelectionChanged?.Invoke();
    }

    private List<MetricDefinition> CollectSelected()
        => _options.Where(o => o.IsSelected).Select(o => o.Definition).ToList();
}
