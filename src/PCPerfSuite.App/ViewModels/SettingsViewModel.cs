using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCPerfSuite.Core.PowerSettings;
using PCPerfSuite.Core.SystemInfo;

namespace PCPerfSuite.App.ViewModels;

public sealed partial class TweakItemViewModel : ObservableObject
{
    public PerformanceTweak Tweak { get; }
    private bool _suppressApply;

    [ObservableProperty] private bool isOn;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool isUnknownState;
    [ObservableProperty] private string? errorMessage;

    public string Name => Tweak.Name;
    public string Description => Tweak.Description;
    public string Category => Tweak.Category;
    public bool RequiresRestart => Tweak.RequiresRestart;
    public bool IsRisky => Tweak.IsRisky;

    public TweakItemViewModel(PerformanceTweak tweak) => Tweak = tweak;

    public async Task RefreshStateAsync()
    {
        IsBusy = true;
        try
        {
            TweakState state = await Task.Run(() => Tweak.GetState());
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
        if (!ElevationHelper.IsAdministrator())
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

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly WindowsPerformanceSettingsService _service = new();

    public bool IsElevated { get; } = ElevationHelper.IsAdministrator();
    public ObservableCollectionEx<TweakItemViewModel> Tweaks { get; } = new();

    [ObservableProperty] private bool isLoading;

    public SettingsViewModel()
    {
        foreach (PerformanceTweak tweak in _service.GetTweaks())
        {
            Tweaks.Add(new TweakItemViewModel(tweak));
        }
        _ = LoadCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        IsLoading = true;
        foreach (TweakItemViewModel item in Tweaks)
        {
            await item.RefreshStateAsync();
        }
        IsLoading = false;
    }
}
