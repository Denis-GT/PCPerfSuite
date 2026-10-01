using CommunityToolkit.Mvvm.ComponentModel;
using PCPerfSuite.Core.Profiles;

namespace PCPerfSuite.App.ViewModels;

/// <summary>Un usage proposé dans la liste déroulante (« Aucun » = null).</summary>
public sealed record UsageChoice(string? Value, string Label)
{
    public static IReadOnlyList<UsageChoice> All { get; } =
        [new UsageChoice(null, "Aucun usage"), .. ProfileGroupUsage.All.Select(u => new UsageChoice(u, ProfileGroupUsage.Label(u)!))];

    public static UsageChoice For(string? usage) => All.FirstOrDefault(c => c.Value == usage) ?? new UsageChoice(usage, ProfileGroupUsage.Label(usage) ?? "");
}

/// <summary>Ce qu'une dimension deviendra à l'enregistrement de « Modifier ».</summary>
public enum PartChoiceKind
{
    /// <summary>Garder la partie du groupe telle quelle.</summary>
    Keep,

    /// <summary>Ne pas toucher à cette dimension (partie null).</summary>
    None,

    /// <summary>Remettre d'origine.</summary>
    Origin,

    /// <summary>L'état actuel de l'onglet.</summary>
    Current,

    /// <summary>Un profil enregistré dans l'onglet.</summary>
    TabProfile,
}

/// <summary>Un choix de la liste « Modifier » pour une dimension.</summary>
public sealed record PartChoice(PartChoiceKind Kind, string Label, string? ProfileName = null);

/// <summary>
/// Un groupe de la page Profils, tel qu'affiché : nom, usage, résumé de chaque dimension dans les termes de ce PC,
/// badges, et l'état de ses confirmations en place (renommage, suppression, application d'un groupe suspendu).
/// </summary>
public sealed partial class ProfileGroupItemViewModel : ObservableObject
{
    public ProfileGroupItemViewModel(ProfileGroup model) => Model = model;

    public ProfileGroup Model { get; private set; }

    public string Id => Model.Id;

    [ObservableProperty] private string name = "";
    [ObservableProperty] private string? usageLabel;
    [ObservableProperty] private string cpuSummary = "";
    [ObservableProperty] private string gpuSummary = "";
    [ObservableProperty] private string fanSummary = "";

    /// <summary>La partie processeur écrit des réglages du plan d'alimentation : ils restent après la fermeture (D7).</summary>
    [ObservableProperty] private bool isPermanent;

    [ObservableProperty] private bool isGenerated;
    [ObservableProperty] private bool isEditedByUser;
    [ObservableProperty] private bool isActive;
    [ObservableProperty] private string? suspensionText;

    public bool IsSuspended => SuspensionText is not null;

    partial void OnSuspensionTextChanged(string? value) => OnPropertyChanged(nameof(IsSuspended));

    // ---- Confirmations en place ----

    [ObservableProperty] private bool isRenaming;
    [ObservableProperty] private string editName = "";
    [ObservableProperty] private bool isConfirmingDelete;

    /// <summary>Groupe suspendu après un incident : l'appliquer demande une confirmation, « Non » par défaut (D6).</summary>
    [ObservableProperty] private bool isConfirmingApply;

    /// <summary>Le formulaire « Modifier » est ouvert sur ce groupe.</summary>
    [ObservableProperty] private bool isEditing;

    public IReadOnlyList<PartChoice> CpuChoices { get; private set; } = [];
    public IReadOnlyList<PartChoice> GpuChoices { get; private set; } = [];
    public IReadOnlyList<PartChoice> FanChoices { get; private set; } = [];

    [ObservableProperty] private PartChoice? cpuChoice;
    [ObservableProperty] private PartChoice? gpuChoice;
    [ObservableProperty] private PartChoice? fanChoice;
    [ObservableProperty] private UsageChoice? editUsage;

    public void Update(ProfileGroup model) => Model = model;

    /// <summary>Ouvre « Modifier » : chaque dimension part sur « inchangé ».</summary>
    public void BeginEdit(IReadOnlyList<PartChoice> cpu, IReadOnlyList<PartChoice> gpu, IReadOnlyList<PartChoice> fans)
    {
        CpuChoices = cpu;
        GpuChoices = gpu;
        FanChoices = fans;
        OnPropertyChanged(nameof(CpuChoices));
        OnPropertyChanged(nameof(GpuChoices));
        OnPropertyChanged(nameof(FanChoices));
        CpuChoice = cpu[0];
        GpuChoice = gpu[0];
        FanChoice = fans[0];
        EditUsage = UsageChoice.For(Model.Usage);
        IsEditing = true;
    }
}
