using System.Collections.ObjectModel;

namespace PCPerfSuite.App.ViewModels;

/// <summary>ObservableCollection avec un remplacement en bloc qui ne fait clignoter l'UI qu'une fois.</summary>
public sealed class ObservableCollectionEx<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        Clear();
        foreach (T item in items) Add(item);
    }
}
