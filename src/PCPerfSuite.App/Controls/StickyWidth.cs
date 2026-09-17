using System.Windows;
using System.Windows.Media;

namespace PCPerfSuite.App.Controls;

/// <summary>
/// Largeur qui s'élargit avec le contenu mais ne rétrécit jamais : une valeur qui change à chaque relevé
/// ("9,8 Mo/s" puis "12,4 Mo/s") garde sa place, et ce qui la suit ne se décale plus.
///
/// Avec <see cref="GroupProperty"/>, tous les éléments du même groupe dans une même portée
/// (<see cref="IsScopeProperty"/>) prennent la largeur du plus large : les colonnes s'alignent d'une ligne à
/// l'autre. Les largeurs repartent de zéro quand <see cref="ResetKeyProperty"/> change sur la portée (police,
/// taille, métriques affichées) ou sur l'élément seul.
/// </summary>
public static class StickyWidth
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(StickyWidth), new PropertyMetadata(false, OnIsEnabledChanged));

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    /// <summary>Nom de colonne partagé avec les autres éléments de la portée. Active le comportement.</summary>
    public static readonly DependencyProperty GroupProperty = DependencyProperty.RegisterAttached(
        "Group", typeof(string), typeof(StickyWidth), new PropertyMetadata(null, OnGroupChanged));

    public static void SetGroup(DependencyObject element, string? value) => element.SetValue(GroupProperty, value);

    public static string? GetGroup(DependencyObject element) => (string?)element.GetValue(GroupProperty);

    /// <summary>Marque l'élément qui regroupe les colonnes partagées (la fenêtre d'overlay, l'aperçu).</summary>
    public static readonly DependencyProperty IsScopeProperty = DependencyProperty.RegisterAttached(
        "IsScope", typeof(bool), typeof(StickyWidth), new PropertyMetadata(false));

    public static void SetIsScope(DependencyObject element, bool value) => element.SetValue(IsScopeProperty, value);

    public static bool GetIsScope(DependencyObject element) => (bool)element.GetValue(IsScopeProperty);

    /// <summary>Valeur quelconque dont le changement remet les largeurs à zéro (sur une portée : toutes ses colonnes).</summary>
    public static readonly DependencyProperty ResetKeyProperty = DependencyProperty.RegisterAttached(
        "ResetKey", typeof(object), typeof(StickyWidth), new PropertyMetadata(null, OnResetKeyChanged));

    public static void SetResetKey(DependencyObject element, object? value) => element.SetValue(ResetKeyProperty, value);

    public static object? GetResetKey(DependencyObject element) => element.GetValue(ResetKeyProperty);

    private static readonly DependencyProperty ScopeStateProperty = DependencyProperty.RegisterAttached(
        "ScopeState", typeof(ScopeState), typeof(StickyWidth), new PropertyMetadata(null));

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => Attach(d, e.NewValue is true || GetGroup(d) is not null);

    private static void OnGroupChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => Attach(d, e.NewValue is not null || GetIsEnabled(d));

    private static void Attach(DependencyObject d, bool enabled)
    {
        if (d is not FrameworkElement element) return;

        element.SizeChanged -= OnSizeChanged;
        element.Unloaded -= OnUnloaded;
        if (enabled)
        {
            element.SizeChanged += OnSizeChanged;
            element.Unloaded += OnUnloaded;
        }
        else
        {
            element.ClearValue(FrameworkElement.MinWidthProperty);
        }
    }

    private static void OnResetKeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;

        if (element.GetValue(ScopeStateProperty) is ScopeState state)
        {
            state.Reset();
        }
        else if (GetIsEnabled(element) || GetGroup(element) is not null)
        {
            element.ClearValue(FrameworkElement.MinWidthProperty);
        }
    }

    private static void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged || sender is not FrameworkElement element) return;

        double width = Math.Ceiling(e.NewSize.Width);

        if (GetGroup(element) is { } group && FindScope(element) is { } scope)
        {
            scope.Grow(group, element, width);
        }
        else if (width > element.MinWidth)
        {
            element.MinWidth = width;
        }
    }

    private static void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && GetGroup(element) is { } group && FindScope(element) is { } scope)
        {
            scope.Remove(group, element);
        }
    }

    private static ScopeState? FindScope(DependencyObject element)
    {
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (!GetIsScope(current)) continue;

            if (current.GetValue(ScopeStateProperty) is not ScopeState state)
            {
                state = new ScopeState();
                current.SetValue(ScopeStateProperty, state);
            }
            return state;
        }
        return null;
    }

    private sealed class ScopeState
    {
        private readonly Dictionary<string, Column> _columns = new();

        public void Grow(string group, FrameworkElement element, double width)
        {
            if (!_columns.TryGetValue(group, out Column? column))
            {
                column = new Column();
                _columns[group] = column;
            }

            column.Members.Add(element);

            if (width > column.Width)
            {
                column.Width = width;
                foreach (FrameworkElement member in column.Members) member.MinWidth = width;
            }
            else if (element.MinWidth < column.Width)
            {
                element.MinWidth = column.Width;
            }
        }

        public void Remove(string group, FrameworkElement element)
        {
            if (_columns.TryGetValue(group, out Column? column)) column.Members.Remove(element);
        }

        public void Reset()
        {
            foreach ((string group, Column column) in _columns)
            {
                column.Width = 0;
                foreach (FrameworkElement member in column.Members.ToArray())
                {
                    member.ClearValue(FrameworkElement.MinWidthProperty);

                    // Un élément dont la largeur naturelle égalait déjà la colonne ne signale aucun changement :
                    // on le remesure une fois la mise en page refaite.
                    member.Dispatcher.InvokeAsync(
                        () =>
                        {
                            if (member.IsLoaded) Grow(group, member, Math.Ceiling(member.ActualWidth));
                        },
                        System.Windows.Threading.DispatcherPriority.Loaded);
                }
            }
        }
    }

    private sealed class Column
    {
        public HashSet<FrameworkElement> Members { get; } = new();
        public double Width { get; set; }
    }
}
