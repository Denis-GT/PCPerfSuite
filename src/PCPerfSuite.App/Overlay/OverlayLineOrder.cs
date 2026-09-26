namespace PCPerfSuite.App.Overlay;

/// <summary>
/// Ordre des lignes de l'overlay : de simples listes de clés (clé de catégorie en mode « une ligne par
/// catégorie », identifiant de métrique en mode « une ligne par métrique »), enregistrées telles quelles dans
/// settings.json. Logique pure, sans WPF, pour qu'on puisse la tester.
///
/// Une liste enregistrée ne connaît que ce qui existait à ce moment-là. Deux cas la rendent incomplète : une
/// métrique découverte plus tard (batterie, conso totale) et une catégorie ajoutée par une version ultérieure de
/// l'app. <see cref="Merge"/> les y range à leur place du catalogue plutôt qu'au bout, ce qui les garde près de
/// leurs voisines.
/// </summary>
public static class OverlayLineOrder
{
    /// <summary>L'ordre enregistré, complété par les clés du catalogue qu'il ne connaît pas encore : chacune
    /// se place juste après sa voisine du catalogue. Une clé enregistrée que le catalogue ne propose pas (pas
    /// encore) est conservée à sa place : elle reprendra sa position quand elle apparaîtra.</summary>
    public static List<string> Merge(IReadOnlyList<string>? saved, IReadOnlyList<string> catalog)
    {
        var order = new List<string>();
        var known = new HashSet<string>();

        if (saved is not null)
        {
            foreach (string key in saved)
            {
                if (known.Add(key)) order.Add(key);
            }
        }

        for (int i = 0; i < catalog.Count; i++)
        {
            if (!known.Add(catalog[i])) continue;

            // La voisine précédente du catalogue est déjà dans la liste : soit elle y était, soit on vient de l'y ranger.
            order.Insert(i == 0 ? 0 : order.IndexOf(catalog[i - 1]) + 1, catalog[i]);
        }

        return order;
    }

    /// <summary>Fait passer <paramref name="key"/> de l'autre côté de sa voisine <em>affichée</em> (vers le haut
    /// pour un <paramref name="direction"/> négatif). Les clés masquées ne bougent pas : une catégorie décochée
    /// puis recochée retrouve la place qu'on lui avait donnée. Sans voisine affichée dans ce sens, rien ne change.</summary>
    public static List<string> Move(IReadOnlyList<string> order, IEnumerable<string> visible, string key, int direction)
    {
        var result = order.ToList();
        int from = result.IndexOf(key);
        if (from < 0 || direction == 0) return result;

        var shown = visible.ToHashSet();
        int step = Math.Sign(direction);

        for (int to = from + step; to >= 0 && to < result.Count; to += step)
        {
            if (!shown.Contains(result[to])) continue;

            (result[from], result[to]) = (result[to], result[from]);
            break;
        }

        return result;
    }

    /// <summary>Range les éléments selon <paramref name="order"/> ; ceux dont la clé n'y figure pas passent à la fin,
    /// dans leur ordre d'origine (tri stable).</summary>
    public static List<T> Sort<T>(IEnumerable<T> items, Func<T, string> keyOf, IReadOnlyList<string> order)
    {
        var position = new Dictionary<string, int>();
        for (int i = 0; i < order.Count; i++) position.TryAdd(order[i], i);

        return items.OrderBy(item => position.TryGetValue(keyOf(item), out int index) ? index : int.MaxValue).ToList();
    }

    /// <summary>Vrai quand l'ordre est celui du catalogue. Les clés que le catalogue ne connaît pas (pas encore) ne comptent pas.</summary>
    public static bool IsDefault(IReadOnlyList<string> order, IReadOnlyList<string> catalog)
    {
        var known = catalog.ToHashSet();
        return order.Where(known.Contains).SequenceEqual(catalog);
    }
}
