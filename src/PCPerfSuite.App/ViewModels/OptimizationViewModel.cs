using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCPerfSuite.Core.PowerSettings;
using PCPerfSuite.Core.PowerSettings.Animations;
using PCPerfSuite.Core.SystemInfo;

namespace PCPerfSuite.App.ViewModels;

public sealed partial class TweakItemViewModel : ObservableObject
{
    public PerformanceTweak Tweak { get; }
    private bool _suppressApply;

    [ObservableProperty] private bool isOn;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanToggle))] private bool isBusy;
    [ObservableProperty] private bool isUnknownState;
    [ObservableProperty] private string? errorMessage;

    /// <summary>Pourquoi l'interrupteur est grisé sur ce PC (réglage du profil, app sous un autre compte), ou null.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanToggle))] private string? unavailableReason;

    public string Name => Tweak.Name;
    public string Description => Tweak.Description;
    public string Category => Tweak.Category;
    public bool RequiresRestart => Tweak.RequiresRestart;
    public bool IsRisky => Tweak.IsRisky;
    public bool CanToggle => !IsBusy && UnavailableReason is null;

    public TweakItemViewModel(PerformanceTweak tweak) => Tweak = tweak;

    public async Task RefreshStateAsync()
    {
        IsBusy = true;
        try
        {
            // Le compte de la session se lit en parcourant les processus : hors du thread d'interface, lui aussi.
            (TweakState state, string? reason) = await Task.Run(() =>
                (Tweak.GetState(), Tweak.TargetsUserProfile ? SessionUser.OtherProfileSettingMessage : null));
            UnavailableReason = reason;
            SetOnSilently(state == TweakState.Enabled);
            IsUnknownState = state == TweakState.Unknown;
            ErrorMessage = null;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            IsUnknownState = true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void SetOnSilently(bool value)
    {
        _suppressApply = true;
        IsOn = value;
        _suppressApply = false;
    }

    partial void OnIsOnChanged(bool value)
    {
        if (_suppressApply) return;
        _ = ApplyAsync(value);
    }

    private async Task ApplyAsync(bool value)
    {
        if (UnavailableReason is not null)
        {
            SetOnSilently(!value);
            return;
        }

        // Seuls les réglages qui écrivent dans HKLM ou dans le plan d'alimentation ont besoin de
        // l'élévation : ceux qui ne touchent qu'au profil de l'utilisateur marchent très bien sans.
        if (Tweak.RequiresElevation && !ElevationHelper.IsAdministrator())
        {
            ErrorMessage = "Relance PCPerfSuite en administrateur pour modifier ce réglage.";
            SetOnSilently(!value);
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        try
        {
            await Task.Run(() => Tweak.Apply(value));

            // Réglage en lecture seule : l'app vient d'ouvrir la page Windows, elle n'a rien changé.
            // L'interrupteur ne doit pas prétendre le contraire en restant dans sa nouvelle position.
            if (Tweak.IsReadOnly) SetOnSilently(!value);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            SetOnSilently(!value);
        }
        finally
        {
            IsBusy = false;
        }
    }
}

/// <summary>Onglet "Optimisation Windows" : réglages de performance de Windows, y compris ceux masqués dans les
/// menus standards, et sous-onglet « Animations » (<see cref="WindowsAnimationsViewModel"/>). Les réglages de l'app
/// elle-même sont dans <see cref="AppSettingsViewModel"/>.</summary>
public sealed partial class OptimizationViewModel : ObservableObject, IPageLifecycle
{
    public const string TweaksSectionKey = "tweaks";
    public const string AnimationsSectionKey = "animations";

    private readonly WindowsPerformanceSettingsService _service = new();

    public bool IsElevated { get; } = ElevationHelper.IsAdministrator();
    public ObservableCollectionEx<TweakItemViewModel> Tweaks { get; } = new();

    /// <summary>Sous-onglet « Animations » : carte « Animations et effets ».</summary>
    public WindowsAnimationsViewModel Animations { get; }

    public IReadOnlyList<AppSettingsSection> Sections { get; } = new[]
    {
        new AppSettingsSection(TweaksSectionKey, "Réglages"),
        new AppSettingsSection(AnimationsSectionKey, "Animations"),
    };

    [ObservableProperty] private AppSettingsSection? selectedSection;

    [ObservableProperty] private bool isLoading;

    /// <summary>Posé par MainViewModel (voir <see cref="IPageLifecycle"/>).</summary>
    [ObservableProperty] private bool isPageShown;

    private bool _loadRequested;

    public OptimizationViewModel(WindowsAnimationSettings animations)
    {
        Animations = new WindowsAnimationsViewModel(animations);
        selectedSection = Sections[0];

        foreach (PerformanceTweak tweak in _service.GetTweaks())
        {
            Tweaks.Add(new TweakItemViewModel(tweak));
        }
    }

    /// <summary>L'état des réglages se lit à la première ouverture de l'onglet, plus au démarrage : une dizaine
    /// d'appels à powercfg pour une page que l'utilisateur n'ouvre peut-être pas, y compris quand l'app démarre dans
    /// la zone de notification.</summary>
    partial void OnIsPageShownChanged(bool value)
    {
        UpdateSectionShown();
        if (!value || _loadRequested) return;

        _loadRequested = true;
        _ = LoadCommand.ExecuteAsync(null);
    }

    partial void OnSelectedSectionChanged(AppSettingsSection? value) => UpdateSectionShown();

    /// <summary>Le sous-onglet Animations est affiché quand la page l'est ET qu'il est choisi. SelectedSection peut être
    /// null : Ctrl+clic sur la pastille active la désélectionne.</summary>
    private void UpdateSectionShown()
        => Animations.IsPageShown = IsPageShown && SelectedSection?.Key == AnimationsSectionKey;

    [RelayCommand]
    private async Task LoadAsync()
    {
        IsLoading = true;

        // Les réglages se lisent un par un : ceux qui attendent leur tour restent grisés, plutôt que d'afficher un
        // « désactivé » qui n'a pas été lu et de se laisser basculer sur cette base.
        foreach (TweakItemViewModel item in Tweaks) item.IsBusy = true;

        foreach (TweakItemViewModel item in Tweaks)
        {
            await item.RefreshStateAsync();
        }
        IsLoading = false;
    }
}
