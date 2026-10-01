using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCPerfSuite.Core.PowerSettings.Animations;
using PCPerfSuite.Core.SystemChanges;

namespace PCPerfSuite.App.ViewModels;

/// <summary>Une ligne de la carte « Animations et effets ». Basculer l'interrupteur demande l'écriture à la carte,
/// qui relit ensuite toutes les lignes : un préréglage les met à jour sans repasser par ici.</summary>
public sealed partial class AnimationItemViewModel : ObservableObject
{
    private readonly WindowsAnimationsViewModel _owner;
    private bool _suppressApply;
    private bool _shownOn;

    [ObservableProperty] private bool isOn;
    [ObservableProperty] private bool isUnknownState;

    /// <summary>« Modifié — origine : activé », tant que l'app l'a changé et qu'il n'y est pas revenu.</summary>
    [ObservableProperty] private string? changedText;

    /// <summary>Sans effet tant que l'interrupteur général est coupé.</summary>
    [ObservableProperty] private string? blockedReason;

    [ObservableProperty] private string? errorMessage;

    public AnimationSetting Setting { get; }
    public string Name => Setting.Name;
    public string Description => Setting.Description;
    public string? ApplyNote => Setting.ApplyNote;

    public AnimationItemViewModel(AnimationSetting setting, WindowsAnimationsViewModel owner)
    {
        Setting = setting;
        _owner = owner;
    }

    /// <summary>Montre l'état relu, sans écrire.</summary>
    internal void Show(AnimationReading reading, bool uiEffectsOff, string? error)
    {
        _shownOn = reading.Current == true;
        RevertToShown();

        IsUnknownState = reading.Current is null;
        ChangedText = reading.IsChanged ? $"Modifié — origine : {WindowsAnimationSettings.OnOff(reading.Original)}" : null;
        BlockedReason = uiEffectsOff && Setting.DependsOnUiEffects
            ? "Sans effet pour l'instant : « Tous les effets d'interface » est coupé."
            : null;
        ErrorMessage = error;
    }

    /// <summary>Remet l'interrupteur sur le dernier état lu, sans écrire.</summary>
    internal void RevertToShown()
    {
        _suppressApply = true;
        IsOn = _shownOn;
        _suppressApply = false;
    }

    partial void OnIsOnChanged(bool value)
    {
        if (_suppressApply) return;
        _ = _owner.ApplyOneAsync(this, value);
    }
}

/// <summary>
/// Sous-onglet « Animations » d'Optimisation Windows : un interrupteur par effet, le préréglage « Réactif » et
/// « Rétablir mes réglages d'origine ». Chaque action est un seul passage hors du thread d'interface, suivi d'une
/// relecture de toutes les lignes ; pendant ce temps, la carte est grisée.
/// </summary>
public sealed partial class WindowsAnimationsViewModel : ObservableObject, IPageLifecycle
{
    private readonly WindowsAnimationSettings _settings;
    private bool _loadRequested;

    public ObservableCollection<AnimationItemViewModel> Items { get; } = new();

    /// <summary>Vrai tant que l'état n'a pas été lu, et pendant chaque écriture.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanEdit))]
    [NotifyCanExecuteChangedFor(nameof(ApplyResponsivePresetCommand), nameof(RestoreOriginalCommand))]
    private bool isBusy = true;

    /// <summary>Pourquoi la carte est grisée sur ce PC (app élevée sous un autre compte), ou null.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanEdit), nameof(IsUnavailable))]
    [NotifyCanExecuteChangedFor(nameof(ApplyResponsivePresetCommand), nameof(RestoreOriginalCommand))]
    private string? unavailableReason;

    /// <summary>Au moins un réglage modifié par l'app et pas encore revenu à l'origine.</summary>
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(RestoreOriginalCommand))]
    private bool hasChanges;

    /// <summary>Ce qu'a donné la dernière action de la carte.</summary>
    [ObservableProperty] private string? statusMessage;
    [ObservableProperty] private bool statusIsError;

    /// <summary>Posé par OptimizationViewModel : page affichée ET sous-onglet choisi.</summary>
    [ObservableProperty] private bool isPageShown;

    public bool CanEdit => !IsBusy && UnavailableReason is null;
    public bool IsUnavailable => UnavailableReason is not null;

    public WindowsAnimationsViewModel(WindowsAnimationSettings settings)
    {
        _settings = settings;
        foreach (AnimationSetting setting in settings.Settings) Items.Add(new AnimationItemViewModel(setting, this));
    }

    partial void OnIsPageShownChanged(bool value)
    {
        if (!value || _loadRequested) return;

        _loadRequested = true;
        _ = RunAsync(() => _settings.Read(), snapshot => Show(snapshot, null), null);
    }

    internal async Task ApplyOneAsync(AnimationItemViewModel item, bool value)
    {
        if (!CanEdit)
        {
            // Basculé pendant une écriture ou sur une carte grisée : l'interrupteur revient au dernier état lu.
            item.RevertToShown();
            return;
        }

        var targets = new Dictionary<string, bool> { [item.Setting.Key] = value };
        await RunAsync(() => _settings.Apply(targets), result =>
        {
            Show(result.After, result.Failures);
            StatusMessage = null;
        }, null);
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task ApplyResponsivePresetAsync()
        => RunAsync(() => _settings.ApplyResponsivePreset(), result =>
        {
            Show(result.After, result.Failures);
            SetStatus(result.Failures.Count == 0
                ? "Préréglage « Réactif » appliqué. Le gain porte sur la réactivité du bureau, pas sur les FPS des jeux."
                : $"Préréglage appliqué en partie : {string.Join(", ", result.Failures.Select(f => f.Setting.Name))} "
                  + "n'ont pas changé (voir chaque ligne).",
                isError: result.Failures.Count > 0);
        }, "Préréglage « Réactif »");

    private bool CanRestore() => CanEdit && HasChanges;

    /// <summary>Même chemin que « Tout rétablir » du registre des modifications.</summary>
    [RelayCommand(CanExecute = nameof(CanRestore))]
    private Task RestoreOriginalAsync()
        => RunAsync(() => (Result: _settings.RestoreAll(), After: _settings.Read()), done =>
        {
            Show(done.After, null);
            SetStatus(done.Result.Status switch
            {
                SystemRestoreStatus.Restored or SystemRestoreStatus.NothingToRestore => "Réglages d'origine rétablis.",
                _ => $"Rétablissement incomplet : {done.Result.Message}",
            }, isError: done.Result.Status is SystemRestoreStatus.Partial or SystemRestoreStatus.Failed);
        }, "Rétablissement");

    /// <summary>Lance un passage hors du thread d'interface, carte grisée, puis montre son résultat. Une exception
    /// (ne devrait pas arriver : le service est best-effort) se dit dans la carte au lieu de casser la page.</summary>
    private async Task RunAsync<T>(Func<T> work, Action<T> show, string? actionName)
    {
        IsBusy = true;
        try
        {
            T result = await Task.Run(work);
            show(result);
        }
        catch (Exception ex)
        {
            SetStatus($"{actionName ?? "Lecture des effets visuels"} impossible : {ex.Message}", isError: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Show(AnimationSnapshot snapshot, IReadOnlyList<AnimationWriteOutcome>? failures)
    {
        UnavailableReason = snapshot.UnavailableReason;
        HasChanges = snapshot.ChangedCount > 0;

        foreach (AnimationItemViewModel item in Items)
        {
            if (snapshot.Find(item.Setting.Key) is not { } reading) continue;
            string? error = failures?.FirstOrDefault(f => f.Setting.Key == item.Setting.Key)?.Error;
            // Sous un autre compte, la raison est déjà dite en tête de carte : pas besoin de la répéter par ligne.
            item.Show(reading, snapshot.UiEffectsOff, snapshot.UnavailableReason is null ? error : null);
        }
    }

    private void SetStatus(string message, bool isError)
    {
        StatusMessage = message;
        StatusIsError = isError;
    }
}
