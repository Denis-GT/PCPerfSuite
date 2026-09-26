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

public static class ObservableCollectionSync
{
    /// <summary>Amène <paramref name="target"/> à contenir exactement <paramref name="wanted"/>, dans cet ordre,
    /// en ne retirant, ajoutant ou déplaçant que ce qui diffère. Contrairement à <c>ReplaceAll</c>, un élément déjà
    /// bien placé n'est pas touché : son visuel n'est pas recréé et l'UI ne clignote pas.</summary>
    public static void SyncTo<T>(this ObservableCollection<T> target, IReadOnlyList<T> wanted) where T : class
    {
        for (int i = target.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(target[i])) target.RemoveAt(i);
        }

        for (int i = 0; i < wanted.Count; i++)
        {
            if (i < target.Count && ReferenceEquals(target[i], wanted[i])) continue;

            int existing = target.IndexOf(wanted[i]);
            if (existing >= 0) target.Move(existing, i);
            else target.Insert(i, wanted[i]);
        }
    }
}
