using PCPerfSuite.App.ViewModels;
using PCPerfSuite.Core.PowerSettings.Animations;

namespace PCPerfSuite.App.Tests;

/// <summary>Le sous-onglet Animations : afficher l'état n'écrit rien, un interrupteur écrit son seul réglage, le
/// préréglage fait un seul passage, et la carte grisée n'écrit pas.</summary>
public class WindowsAnimationsViewModelTests
{
    private sealed class FakeAccess : IAnimationSettingsAccess
    {
        public Dictionary<string, bool> Values { get; } = WindowsAnimationCatalog.All.ToDictionary(s => s.Key, _ => true);
        public List<(string Key, bool Value)> Writes { get; } = new();

        public bool? Read(AnimationSetting setting) => Values[setting.Key];

        public void Write(AnimationSetting setting, bool enabled)
        {
            lock (Writes) Writes.Add((setting.Key, enabled));
            Values[setting.Key] = enabled;
        }
    }

    private sealed class MemoryOrigins : IAnimationOriginStore
    {
        private readonly Dictionary<string, bool> _values = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyDictionary<string, bool> Load() => new Dictionary<string, bool>(_values);
        public bool TryRemember(string key, bool original) => _values.TryAdd(key, original) || true;

        public void Forget(IReadOnlyCollection<string> keys)
        {
            foreach (string key in keys) _values.Remove(key);
        }
    }

    private const string Menu = "menu-animation";

    private static async Task<(WindowsAnimationsViewModel Vm, FakeAccess Access)> CreateShownAsync(string? unavailable = null)
    {
        var access = new FakeAccess();
        var vm = new WindowsAnimationsViewModel(new WindowsAnimationSettings(access, new MemoryOrigins(), () => unavailable));
        vm.IsPageShown = true;
        await IdleAsync(vm);
        return (vm, access);
    }

    /// <summary>Chaque action tourne hors du thread d'interface : on attend que la carte soit libérée.</summary>
    private static async Task IdleAsync(WindowsAnimationsViewModel vm)
    {
        for (int i = 0; i < 500 && vm.IsBusy; i++) await Task.Delay(10);
        Assert.False(vm.IsBusy);
    }

    private static AnimationItemViewModel Item(WindowsAnimationsViewModel vm, string key) => vm.Items.Single(i => i.Setting.Key == key);

    [Fact]
    public async Task Afficher_l_etat_n_ecrit_rien()
    {
        (WindowsAnimationsViewModel vm, FakeAccess access) = await CreateShownAsync();

        Assert.Empty(access.Writes);
        Assert.All(vm.Items, i => Assert.True(i.IsOn));
        Assert.True(vm.CanEdit);
        Assert.False(vm.HasChanges);
    }

    [Fact]
    public async Task Un_interrupteur_ecrit_son_seul_reglage()
    {
        (WindowsAnimationsViewModel vm, FakeAccess access) = await CreateShownAsync();

        Item(vm, Menu).IsOn = false;
        await IdleAsync(vm);

        Assert.Equal(new[] { (Menu, false) }, access.Writes);
        Assert.NotNull(Item(vm, Menu).ChangedText);
        Assert.True(vm.HasChanges);
    }

    [Fact]
    public async Task Le_preset_ecrit_chaque_reglage_une_fois_puis_retablir_rend_tout()
    {
        (WindowsAnimationsViewModel vm, FakeAccess access) = await CreateShownAsync();

        await vm.ApplyResponsivePresetCommand.ExecuteAsync(null);
        await IdleAsync(vm);

        int presetCount = WindowsAnimationCatalog.All.Count(s => s.InResponsivePreset);
        Assert.Equal(presetCount, access.Writes.Count);
        Assert.Equal(presetCount, access.Writes.Select(w => w.Key).Distinct().Count());
        Assert.All(vm.Items.Where(i => i.Setting.InResponsivePreset), i => Assert.False(i.IsOn));
        Assert.False(vm.StatusIsError);

        await vm.RestoreOriginalCommand.ExecuteAsync(null);
        await IdleAsync(vm);

        Assert.All(access.Values.Values, Assert.True);
        Assert.False(vm.HasChanges);
        Assert.All(vm.Items, i => Assert.True(i.IsOn));
    }

    [Fact]
    public async Task Sous_un_autre_compte_l_interrupteur_revient_sans_ecrire()
    {
        (WindowsAnimationsViewModel vm, FakeAccess access) = await CreateShownAsync("Autre compte.");

        Item(vm, Menu).IsOn = false;
        await IdleAsync(vm);

        Assert.False(vm.CanEdit);
        Assert.True(vm.IsUnavailable);
        Assert.True(Item(vm, Menu).IsOn);
        Assert.Empty(access.Writes);
    }
}
