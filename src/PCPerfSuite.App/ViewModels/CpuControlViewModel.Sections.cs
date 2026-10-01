using CommunityToolkit.Mvvm.ComponentModel;

namespace PCPerfSuite.App.ViewModels;

/// <summary>Sous-onglets de la page Processeur : « Réglages » (alimentation, limites en watts, profils) et « Cœurs »
/// (visuel par cœur et parking, <see cref="CoreParkingViewModel"/>).</summary>
public sealed partial class CpuControlViewModel : IPageLifecycle
{
    public const string SettingsSectionKey = "settings";
    public const string CoresSectionKey = "cores";

    public IReadOnlyList<AppSettingsSection> Sections { get; } =
    [
        new AppSettingsSection(SettingsSectionKey, "Réglages"),
        new AppSettingsSection(CoresSectionKey, "Cœurs"),
    ];

    /// <summary>Peut devenir null : Ctrl+clic sur la pastille active la désélectionne.</summary>
    [ObservableProperty] private AppSettingsSection? selectedSection;

    /// <summary>Posé par MainViewModel (voir <see cref="IPageLifecycle"/>).</summary>
    [ObservableProperty] private bool isPageShown;

    /// <summary>Visuel par cœur et parking des cœurs.</summary>
    public CoreParkingViewModel Cores { get; }

    partial void OnIsPageShownChanged(bool value) => UpdateSectionShown();

    partial void OnSelectedSectionChanged(AppSettingsSection? value) => UpdateSectionShown();

    /// <summary>Le relevé par cœur ne tourne que page affichée ET sous-onglet « Cœurs » choisi.</summary>
    private void UpdateSectionShown() => Cores.IsPageShown = IsPageShown && SelectedSection?.Key == CoresSectionKey;
}
