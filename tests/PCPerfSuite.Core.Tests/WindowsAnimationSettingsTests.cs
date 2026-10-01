using PCPerfSuite.Core.PowerSettings.Animations;
using PCPerfSuite.Core.SystemChanges;

namespace PCPerfSuite.Core.Tests;

/// <summary>La carte « Animations et effets », sur un accès SPI simulé : préréglage en un passage, origine retenue une
/// seule fois, restauration à l'identique, et carte grisée sous un autre compte.</summary>
public class WindowsAnimationSettingsTests
{
    /// <summary>Windows simulé : une valeur par réglage, toutes activées au départ. <see cref="MaskByUiEffects"/>
    /// reproduit un Windows qui rend « désactivé » pour un effet dépendant quand l'interrupteur général est coupé.</summary>
    private sealed class FakeAccess : IAnimationSettingsAccess
    {
        public Dictionary<string, bool?> Values { get; } =
            WindowsAnimationCatalog.All.ToDictionary(s => s.Key, _ => (bool?)true);

        public List<(string Key, bool Value)> Writes { get; } = new();
        public HashSet<string> Refused { get; } = new();

        /// <summary>Écritures acceptées mais sans effet (Windows garde la valeur).</summary>
        public HashSet<string> Ignored { get; } = new();

        public bool MaskByUiEffects { get; set; }

        public bool? Read(AnimationSetting setting)
        {
            bool? value = Values[setting.Key];
            if (MaskByUiEffects && setting.DependsOnUiEffects && Values[WindowsAnimationCatalog.UiEffectsKey] == false) return false;
            return value;
        }

        public void Write(AnimationSetting setting, bool enabled)
        {
            if (Refused.Contains(setting.Key)) throw new InvalidOperationException($"Refusé : {setting.Key}");
            Writes.Add((setting.Key, enabled));
            if (!Ignored.Contains(setting.Key)) Values[setting.Key] = enabled;
        }
    }

    private sealed class MemoryOrigins : IAnimationOriginStore
    {
        public Dictionary<string, bool> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool FailSaves { get; set; }

        public IReadOnlyDictionary<string, bool> Load() => new Dictionary<string, bool>(Values, StringComparer.OrdinalIgnoreCase);

        public bool TryRemember(string key, bool original)
        {
            if (Values.ContainsKey(key)) return true;
            if (FailSaves) return false;
            Values[key] = original;
            return true;
        }

        public void Forget(IReadOnlyCollection<string> keys)
        {
            foreach (string key in keys) Values.Remove(key);
        }
    }

    private const string Menu = "menu-animation";
    private const string Transparency = "transparency";

    private static (WindowsAnimationSettings Settings, FakeAccess Access, MemoryOrigins Origins) Create(string? unavailable = null)
    {
        var access = new FakeAccess();
        var origins = new MemoryOrigins();
        return (new WindowsAnimationSettings(access, origins, () => unavailable), access, origins);
    }

    private static Dictionary<string, bool> Target(string key, bool value) => new() { [key] = value };

    [Fact]
    public void Le_preset_reactif_coupe_tout_sauf_l_interrupteur_general_en_un_passage()
    {
        (WindowsAnimationSettings settings, FakeAccess access, MemoryOrigins origins) = Create();

        AnimationApplyResult result = settings.ApplyResponsivePreset();

        Assert.Empty(result.Failures);
        // Une seule écriture par réglage, et aucune pour l'interrupteur général.
        string[] expected = WindowsAnimationCatalog.All.Where(s => s.Key != WindowsAnimationCatalog.UiEffectsKey).Select(s => s.Key).ToArray();
        Assert.Equal(expected, access.Writes.Select(w => w.Key));
        Assert.All(access.Writes, w => Assert.False(w.Value));
        Assert.True(access.Values[WindowsAnimationCatalog.UiEffectsKey]);
        // Chaque origine est retenue, et l'état relu le dit.
        Assert.Equal(expected.Length, origins.Values.Count);
        Assert.All(origins.Values.Values, Assert.True);
        Assert.Equal(expected.Length, result.After.ChangedCount);
    }

    [Fact]
    public void Un_reglage_deja_en_place_n_est_pas_reecrit()
    {
        (WindowsAnimationSettings settings, FakeAccess access, _) = Create();
        settings.ApplyResponsivePreset();
        access.Writes.Clear();

        AnimationApplyResult again = settings.ApplyResponsivePreset();

        Assert.Empty(again.Failures);
        Assert.Empty(access.Writes);
    }

    [Fact]
    public void L_origine_est_retenue_au_premier_changement_seulement_puis_oubliee_au_retour()
    {
        (WindowsAnimationSettings settings, FakeAccess access, MemoryOrigins origins) = Create();

        settings.Apply(Target(Menu, false));
        Assert.True(origins.Values[Menu]);

        // Une deuxième demande ne retient pas la valeur que l'app vient elle-même de poser.
        settings.ApplyResponsivePreset();
        Assert.True(origins.Values[Menu]);

        // Revenu à l'origine : plus rien à rendre.
        AnimationApplyResult back = settings.Apply(Target(Menu, true));
        Assert.Empty(back.Failures);
        Assert.False(origins.Values.ContainsKey(Menu));
        Assert.False(back.After.Find(Menu)!.IsChanged);
    }

    [Fact]
    public void Retablir_rend_l_etat_exact_d_avant()
    {
        (WindowsAnimationSettings settings, FakeAccess access, MemoryOrigins origins) = Create();
        access.Values["drop-shadow"] = false;
        access.Values[Transparency] = false;
        Dictionary<string, bool?> before = new(access.Values);

        settings.ApplyResponsivePreset();
        settings.Apply(Target(Transparency, true));
        Assert.True(settings.HasChanges);

        SystemRestoreResult result = settings.RestoreAll();

        Assert.Equal(SystemRestoreStatus.Restored, result.Status);
        Assert.Equal(before, access.Values);
        Assert.Empty(origins.Values);
        Assert.False(settings.HasChanges);
        Assert.Empty(settings.Describe());
    }

    [Fact]
    public void Retablir_sans_modification_ne_touche_a_rien()
    {
        (WindowsAnimationSettings settings, FakeAccess access, _) = Create();

        Assert.Equal(SystemRestoreStatus.NothingToRestore, settings.RestoreAll().Status);
        Assert.Empty(access.Writes);
    }

    [Fact]
    public void Un_refus_pendant_la_restauration_donne_un_retablissement_partiel()
    {
        (WindowsAnimationSettings settings, FakeAccess access, MemoryOrigins origins) = Create();
        settings.Apply(new Dictionary<string, bool> { [Menu] = false, [Transparency] = false });
        access.Refused.Add(Transparency);

        SystemRestoreResult result = settings.RestoreAll();

        Assert.Equal(SystemRestoreStatus.Partial, result.Status);
        SystemChange left = Assert.Single(result.NotRestored);
        Assert.Equal(WindowsAnimationCatalog.Find(Transparency)!.Name, left.Title);
        Assert.Contains("Refusé", result.Message);
        Assert.True(access.Values[Menu]);
        // L'origine non rendue reste retenue pour un prochain essai.
        Assert.Equal(new[] { Transparency }, origins.Values.Keys);
    }

    [Fact]
    public void Une_ecriture_que_Windows_ignore_est_signalee()
    {
        (WindowsAnimationSettings settings, FakeAccess access, MemoryOrigins origins) = Create();
        access.Ignored.Add(Menu);

        AnimationApplyResult result = settings.Apply(Target(Menu, false));

        AnimationWriteOutcome failure = Assert.Single(result.Failures);
        Assert.Equal(true, failure.Actual);
        Assert.Contains("activé", failure.Error);
        // Rien n'a changé : l'origine est oubliée aussitôt.
        Assert.Empty(origins.Values);
    }

    [Fact]
    public void Un_etat_illisible_n_est_pas_ecrit_faute_de_pouvoir_le_retablir()
    {
        (WindowsAnimationSettings settings, FakeAccess access, MemoryOrigins origins) = Create();
        access.Values[Menu] = null;

        AnimationApplyResult result = settings.Apply(Target(Menu, false));

        Assert.NotNull(Assert.Single(result.Failures).Error);
        Assert.Empty(access.Writes);
        Assert.Empty(origins.Values);
    }

    [Fact]
    public void Une_origine_qui_ne_s_enregistre_pas_bloque_l_ecriture()
    {
        (WindowsAnimationSettings settings, FakeAccess access, MemoryOrigins origins) = Create();
        origins.FailSaves = true;

        AnimationApplyResult result = settings.Apply(Target(Menu, false));

        Assert.Single(result.Failures);
        Assert.Empty(access.Writes);
    }

    [Fact]
    public void L_interrupteur_general_coupe_est_relu_pour_toutes_les_lignes()
    {
        (WindowsAnimationSettings settings, FakeAccess access, _) = Create();
        access.MaskByUiEffects = true;

        AnimationApplyResult result = settings.Apply(Target(WindowsAnimationCatalog.UiEffectsKey, false));

        Assert.Empty(result.Failures);
        Assert.True(result.After.UiEffectsOff);
        Assert.Equal(false, result.After.Find(Menu)!.Current);
        // Seul l'interrupteur général a été modifié par l'app.
        Assert.Equal(1, result.After.ChangedCount);
        Assert.Single(access.Writes);
    }

    [Fact]
    public void Le_retablissement_rend_l_interrupteur_general_en_premier()
    {
        (WindowsAnimationSettings settings, FakeAccess access, _) = Create();
        access.MaskByUiEffects = true;
        settings.Apply(Target(Menu, false));
        settings.Apply(Target(WindowsAnimationCatalog.UiEffectsKey, false));
        access.Writes.Clear();

        SystemRestoreResult result = settings.RestoreAll();

        Assert.Equal(SystemRestoreStatus.Restored, result.Status);
        Assert.Equal(WindowsAnimationCatalog.UiEffectsKey, access.Writes[0].Key);
        Assert.True(access.Values[Menu]);
    }

    [Fact]
    public void Describe_donne_chaque_reglage_change_avec_sa_valeur_d_origine()
    {
        (WindowsAnimationSettings settings, _, _) = Create();
        settings.Apply(Target(Menu, false));

        SystemChange change = Assert.Single(settings.Describe());

        Assert.Equal(WindowsAnimationCatalog.Find(Menu)!.Name, change.Title);
        Assert.Contains("origine : activé", change.Detail);
        Assert.Contains("actuelle : désactivé", change.Detail);
        Assert.True(change.CanRestore);
    }

    [Fact]
    public void Sous_un_autre_compte_rien_n_est_ecrit_et_la_raison_est_donnee()
    {
        const string reason = "Autre compte.";
        (WindowsAnimationSettings settings, FakeAccess access, MemoryOrigins origins) = Create(reason);
        origins.Values[Menu] = true;
        access.Values[Menu] = false;

        AnimationApplyResult preset = settings.ApplyResponsivePreset();
        SystemRestoreResult restore = settings.RestoreAll();

        Assert.Empty(access.Writes);
        Assert.All(preset.Failures, f => Assert.Equal(reason, f.Error));
        Assert.Equal(reason, preset.After.UnavailableReason);
        Assert.Equal(SystemRestoreStatus.Failed, restore.Status);
        Assert.False(Assert.Single(restore.NotRestored).CanRestore);
    }

    [Fact]
    public void Une_cle_inconnue_du_catalogue_est_oubliee()
    {
        (WindowsAnimationSettings settings, _, MemoryOrigins origins) = Create();
        origins.Values["reglage-retire"] = true;

        Assert.Equal(SystemRestoreStatus.NothingToRestore, settings.RestoreAll().Status);
        Assert.Empty(origins.Values);
    }

    [Fact]
    public void Le_catalogue_a_des_cles_uniques_et_les_codes_de_winuser()
    {
        IReadOnlyList<AnimationSetting> all = WindowsAnimationCatalog.All;

        Assert.Equal(all.Count, all.Select(s => s.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal((0x1042u, 0x1043u), Codes(WindowsAnimationCatalog.ClientAreaAnimationKey));
        Assert.Equal((0x103Eu, 0x103Fu), Codes(WindowsAnimationCatalog.UiEffectsKey));
        Assert.Equal((0x0048u, 0x0049u), Codes("window-minimize-animation"));
        // DRAGFULLWINDOWS : le SET (0x25) précède le GET (0x26).
        Assert.Equal((0x0026u, 0x0025u), Codes("drag-full-windows"));
        // Les codes SPI de la plage 0x10xx vont par paire GET pair, SET = GET + 1.
        Assert.All(all.Where(s => s.Kind == AnimationSettingKind.SpiBool),
            s => Assert.Equal(s.GetAction + 1, s.SetAction));
        Assert.All(all.Where(s => s.Kind == AnimationSettingKind.UserRegistry),
            s => Assert.False(string.IsNullOrEmpty(s.RegistrySubKey) || string.IsNullOrEmpty(s.RegistryValue)));
        // Seul l'interrupteur général échappe au préréglage ; rien n'est encore vérifié sur une vraie machine.
        Assert.Equal(new[] { WindowsAnimationCatalog.UiEffectsKey }, all.Where(s => !s.InResponsivePreset).Select(s => s.Key));
        Assert.All(all, s => Assert.False(s.IsVerified));
    }

    private static (uint Get, uint Set) Codes(string key)
    {
        AnimationSetting setting = WindowsAnimationCatalog.Find(key)!;
        return (setting.GetAction, setting.SetAction);
    }
}
