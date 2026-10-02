using PCPerfSuite.Core.Profiles;

namespace PCPerfSuite.App.ViewModels;

/// <summary>
/// Ce que la page Profils offre à la bascule automatique (#9), à part pour ne pas grossir la page. La page reste seule à
/// écrire le bloc ProfileGroups de settings.json : la bascule y passe pour appliquer un groupe (état actif), pour
/// enregistrer les groupes générés et pour lever une suspension confirmée. Tout s'appelle sur le fil d'interface.
/// </summary>
public sealed partial class ProfileGroupsViewModel
{
    /// <summary>Un groupe est en cours de réglage dans un onglet (« Régler dans l'onglet ») : la bascule attend.</summary>
    public bool IsGroupTuning => _tuning is not null;

    /// <summary>Applique un groupe pour la bascule : demandeur « bascule-auto » (le bail est pris le temps de
    /// l'application), pas un clic de l'utilisateur (aucune hausse après une sécurité thermique), sans en faire l'état de
    /// démarrage (D7 : rendu à la fermeture). Null si une erreur inattendue l'a empêché.</summary>
    public async Task<ProfileGroupApplyResult?> ApplyForAutoSwitchAsync(ProfileGroup group)
    {
        ProfileGroupApplyResult? result = await RunApplyAsync(group,
            new ProfileGroupApplyOptions(AutoSwitchRequester.Id, AutoSwitchRequester.Label, IsManual: false, MakeStartupState: false));
        if (_loaded) Refresh();
        return result;
    }

    /// <summary>Enregistre les groupes générés : un groupe de même identifiant est remplacé, les autres ajoutés.</summary>
    public void UpsertGenerated(IEnumerable<ProfileGroup> groups)
    {
        foreach (ProfileGroup group in groups)
        {
            ProfileGroup copy = group.Clone();
            int index = _store.Groups.FindIndex(g => string.Equals(g.Id, copy.Id, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) _store.Groups[index] = copy;
            else _store.Groups.Add(copy);
        }

        Persist();
        if (_loaded) Refresh();
    }

    /// <summary>Lève la suspension d'un groupe, après confirmation dans la page (D6). Vrai si elle existait.</summary>
    public bool LiftSuspension(string groupId)
    {
        if (!_store.Suspensions.Remove(groupId)) return false;

        Persist();
        if (_loaded) Refresh();
        return true;
    }

    /// <summary>« Modifier » depuis le sous-onglet Automatique : ouvre l'éditeur de #8 sur ce groupe, dans « Groupes ».</summary>
    public void OpenEditor(string groupId)
    {
        SelectedSection = Sections[0];
        _loaded = true;
        Refresh();
        if (Groups.FirstOrDefault(g => string.Equals(g.Id, groupId, StringComparison.OrdinalIgnoreCase)) is { } item) Edit(item);
    }
}
