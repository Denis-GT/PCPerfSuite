namespace PCPerfSuite.Core.PowerSettings.Animations;

/// <summary>Valeurs d'origine des réglages d'animation que l'app a modifiés, clé = <see cref="AnimationSetting.Key"/>.
/// Isolé pour être simulé dans les tests.</summary>
public interface IAnimationOriginStore
{
    IReadOnlyDictionary<string, bool> Load();

    /// <summary>Retient <paramref name="original"/> pour cette clé si aucune valeur n'y est déjà : une deuxième
    /// écriture ne doit pas retenir ce que l'app vient elle-même de poser. Faux si l'enregistrement a échoué : l'app
    /// ne doit alors pas toucher au réglage, faute de pouvoir le rendre.</summary>
    bool TryRemember(string key, bool original);

    /// <summary>Oublie ces clés : leur réglage est revenu à l'origine.</summary>
    void Forget(IReadOnlyCollection<string> keys);
}

/// <summary>Valeurs d'origine dans settings.json (<see cref="AppSettings.OriginalAnimationValues"/>), par
/// <see cref="AppSettingsStore.Update"/>.</summary>
public sealed class AppSettingsAnimationOriginStore : IAnimationOriginStore
{
    public IReadOnlyDictionary<string, bool> Load()
        => new Dictionary<string, bool>(AppSettingsStore.Load().OriginalAnimationValues, StringComparer.OrdinalIgnoreCase);

    public bool TryRemember(string key, bool original)
    {
        bool added = false;
        AppSettingsStore.Update(settings => added = settings.OriginalAnimationValues.TryAdd(key, original));

        // Save() ne lève pas : il note son échec dans LastError. Une clé déjà là était enregistrée avant.
        return !added || AppSettingsStore.LastError is null;
    }

    public void Forget(IReadOnlyCollection<string> keys)
    {
        if (keys.Count == 0) return;
        AppSettingsStore.Update(settings =>
        {
            foreach (string key in keys) settings.OriginalAnimationValues.Remove(key);
        });
    }
}
