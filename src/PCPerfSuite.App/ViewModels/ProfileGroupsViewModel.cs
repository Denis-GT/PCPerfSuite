using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCPerfSuite.Core.Hardware;
using PCPerfSuite.Core.Hardware.Cpu;
using PCPerfSuite.Core.Hardware.Fans;
using PCPerfSuite.Core.Hardware.Gpu;
using PCPerfSuite.Core.PowerSettings;
using PCPerfSuite.Core.Profiles;
using PCPerfSuite.Core.Safety;

namespace PCPerfSuite.App.ViewModels;

/// <summary>Un sous-onglet de la page Profils.</summary>
public sealed record ProfileGroupsSection(string Key, string Title);

/// <summary>
/// Page « Profils » (Régler) : les groupes de profils CPU + GPU + ventilation (#8), appliqués d'un clic, modifiés à la
/// main ; le sous-onglet « Automatique » montre la bascule selon l'usage (#9, <see cref="AutoProfilesViewModel"/>).
///
/// Seule cette page écrit le bloc ProfileGroups de settings.json (hors suspensions posées au lancement par
/// <see cref="ProfileGroupRecoveryHandler"/>). Les onglets Processeur, GPU et Ventilateurs restent propriétaires de
/// leur partie : un groupe passe toujours par eux (<see cref="ProfileGroupApplier"/>). La prudence au démarrage (période
/// probatoire de 30 min, TDR, sécurité thermique) vit dans ProfileGroupsViewModel.Safety.cs.
/// </summary>
public sealed partial class ProfileGroupsViewModel : ObservableObject, IPageLifecycle, IDisposable
{
    public const string RequesterLabel = "la page Profils";

    private readonly ProfileGroupApplier _applier;
    private readonly CpuControlViewModel _cpu;
    private readonly GpuControlViewModel _gpu;
    private readonly FanCurvesViewModel _fans;
    private readonly TuningLease _lease;
    private readonly StartupRecoveryReport _recovery;
    private readonly Action<string> _navigate;
    private readonly TimeProvider _time = TimeProvider.System;

    /// <summary>Copie en mémoire du bloc ProfileGroups, réécrite en entier à chaque modification.</summary>
    private ProfileGroupsSettings _store;

    private bool _loaded;

    /// <summary>Réglage d'un groupe en cours dans un onglet (« Régler dans l'onglet »).</summary>
    private (string GroupId, ProfileDimension Dimension)? _tuning;

    public IReadOnlyList<ProfileGroupsSection> Sections { get; } =
    [
        new ProfileGroupsSection("groups", "Groupes"),
        new ProfileGroupsSection("auto", "Automatique"),
    ];

    [ObservableProperty] private ProfileGroupsSection selectedSection;

    /// <summary>Sous-onglet « Automatique » : la bascule selon l'usage (#9), posée par MainViewModel une fois la bascule
    /// construite (elle dépend de cette page).</summary>
    public AutoProfilesViewModel? Automatic { get; private set; }

    public void AttachAutomatic(AutoProfilesViewModel automatic)
    {
        Automatic = automatic;
        OnPropertyChanged(nameof(Automatic));
        UpdateAutomaticShown();
    }

    /// <summary>Le sous-onglet ne relève en direct que page affichée et sous-onglet choisi.</summary>
    private void UpdateAutomaticShown()
    {
        if (Automatic is { } automatic) automatic.IsPageShown = IsPageShown && SelectedSection?.Key == "auto";
    }

    partial void OnSelectedSectionChanged(ProfileGroupsSection value) => UpdateAutomaticShown();

    public TuningStatusViewModel Tuning { get; }

    public ObservableCollection<ProfileGroupItemViewModel> Groups { get; } = new();

    [ObservableProperty] private bool isPageShown;

    // ---- Enregistrer l'état actuel ----

    [ObservableProperty] private string newGroupName = "";
    [ObservableProperty] private UsageChoice newGroupUsage = UsageChoice.All[0];
    [ObservableProperty] private bool includeCpu = true;
    [ObservableProperty] private bool includeGpu;
    [ObservableProperty] private bool includeFans;
    [ObservableProperty] private bool canIncludeGpu;
    [ObservableProperty] private bool canIncludeFans;

    public IReadOnlyList<UsageChoice> UsageChoices => UsageChoice.All;

    // ---- État ----

    /// <summary>Ce que la page dit de la dernière action (enregistrement, suppression…).</summary>
    [ObservableProperty] private string? status;

    /// <summary>Le dernier rapport d'application, une phrase par dimension.</summary>
    [ObservableProperty] private string? lastReport;

    [ObservableProperty] private bool isApplying;

    /// <summary>« Groupe actif : « Jeu », appliqué à 14:05. »</summary>
    [ObservableProperty] private string? activeText;

    /// <summary>« Processeur : conforme · Carte graphique : modifié (cœur +0 MHz au lieu de +150 MHz). »</summary>
    [ObservableProperty] private string? conformityText;

    private DateTimeOffset? _conformityCheckedUtc;

    /// <summary>Ce qui sera réappliqué au prochain lancement, et ce qui est rendu à la fermeture (D7).</summary>
    [ObservableProperty] private string startupText = "";

    /// <summary>Réglage d'un groupe en cours dans un onglet, null sinon.</summary>
    [ObservableProperty] private string? groupTuningText;

    public ObservableCollection<ProfileGroupWarning> Warnings { get; } = new();

    public ProfileGroupsViewModel(
        ProfileGroupApplier applier,
        CpuControlViewModel cpu,
        GpuControlViewModel gpu,
        FanCurvesViewModel fans,
        TuningLease lease,
        TuningStatusViewModel tuning,
        StartupRecoveryReport recovery,
        Action<string> navigate,
        CpuControlService cpuService,
        GpuControlService gpuService)
    {
        _applier = applier;
        _cpu = cpu;
        _gpu = gpu;
        _fans = fans;
        _lease = lease;
        Tuning = tuning;
        _recovery = recovery;
        _navigate = navigate;
        selectedSection = Sections[0];

        _store = LoadStore();

        _cpu.HardwareResynced += OnHardwareResynced;
        _gpu.HardwareResynced += OnHardwareResynced;
        InitializeSafety(cpuService, gpuService);
    }

    /// <summary>Le bloc tel qu'en mémoire, pour le diagnostic.</summary>
    public ProfileGroupsSettings Store => _store;

    /// <summary>Ce que le diagnostic reprend de la page.</summary>
    public ProfileGroupsStatus DiagnosticStatus => new(ConformityText, _conformityCheckedUtc, LastReport);

    private static ProfileGroupsSettings LoadStore()
    {
        ProfileGroupsSettings store = AppSettingsStore.Load().ProfileGroups ?? new ProfileGroupsSettings();
        if (store.Normalize())
        {
            ProfileGroupsSettings copy = ProfileGroupJson.Clone(store);
            AppSettingsStore.Update(settings => settings.ProfileGroups = copy);
        }

        return store;
    }

    private void Persist()
    {
        ProfileGroupsSettings copy = ProfileGroupJson.Clone(_store);
        AppSettingsStore.Update(settings => settings.ProfileGroups = copy);
    }

    partial void OnIsPageShownChanged(bool value)
    {
        UpdateAutomaticShown();
        if (!value) return;

        // La première ouverture construit la liste ; chaque ouverture remet les résumés et la conformité à jour (un
        // onglet a pu changer entre-temps).
        _loaded = true;
        Refresh();
        CheckConformity();
    }

    /// <summary>Les résumés dans les termes de ce PC, les badges, l'annonce du démarrage et les avertissements.</summary>
    private void Refresh()
    {
        CanIncludeGpu = _gpu.IsAvailable;
        CanIncludeFans = _fans.WhenReady.IsCompleted && _fans.Fans.Count > 0;
        if (!CanIncludeGpu) IncludeGpu = false;
        if (!CanIncludeFans) IncludeFans = false;

        var existing = Groups.ToDictionary(g => g.Id, StringComparer.OrdinalIgnoreCase);
        var wanted = new List<ProfileGroupItemViewModel>();
        foreach (ProfileGroup group in _store.Groups)
        {
            if (!existing.TryGetValue(group.Id, out ProfileGroupItemViewModel? item)) item = new ProfileGroupItemViewModel(group);
            item.Update(group);
            Describe(item);
            wanted.Add(item);
        }

        for (int i = Groups.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(Groups[i])) Groups.RemoveAt(i);
        }

        for (int i = 0; i < wanted.Count; i++)
        {
            int at = Groups.IndexOf(wanted[i]);
            if (at < 0) Groups.Insert(i, wanted[i]);
            else if (at != i) Groups.Move(at, i);
        }

        StartupText = DescribeStartup();
        RefreshWarnings();
        RefreshActive();
    }

    private void Describe(ProfileGroupItemViewModel item)
    {
        ProfileGroup group = item.Model;
        item.Name = group.Name;
        item.UsageLabel = ProfileGroupUsage.Label(group.Usage);
        item.IsGenerated = group.IsGenerated;
        item.IsEditedByUser = group.IsGenerated && group.EditedByUser;
        item.IsActive = string.Equals(_store.Active?.GroupId, group.Id, StringComparison.OrdinalIgnoreCase);
        item.SuspensionText = _store.Suspensions.TryGetValue(group.Id, out ProfileGroupSuspension? suspension)
            ? $"Suspendu depuis le {suspension.SinceUtc.ToLocalTime():dd/MM à HH:mm} : {suspension.Cause}"
            : null;

        item.CpuSummary = DescribeCpu(group.Cpu);
        item.GpuSummary = DescribeGpu(group.Gpu);
        item.FanSummary = DescribeFans(group.Fans);
        item.IsPermanent = group.Cpu is { } cpu
                           && (cpu.ParsedKind == ProfilePartKind.Origin || cpu.Values?.PowerSettings?.Count > 0);
    }

    private string DescribeCpu(ProfileGroupCpuPart? part) => part?.ParsedKind switch
    {
        null => "non touché",
        ProfilePartKind.Origin => "remis d'origine (plan d'alimentation rendu, limites d'usine)",
        ProfilePartKind.Unknown => "réglage d'une version plus récente de PCPerfSuite, ignoré ici",
        ProfilePartKind.Empty => "vide",
        _ => _cpu.Describe(part.Values!)
             + (part.Values!.SustainedWatts is not null && !CpuIdentity.Matches(part.CapturedOn, _cpu.Identity)
                 ? $" — watts relevés sur {part.CapturedOn?.Describe() ?? "un processeur non identifié"}, non posés ici"
                 : ""),
    };

    private string DescribeGpu(ProfileGroupGpuPart? part) => part?.ParsedKind switch
    {
        null => "non touchée",
        ProfilePartKind.Origin => "remise d'origine (overclock retiré)",
        ProfilePartKind.Unknown => "réglage d'une version plus récente de PCPerfSuite, ignoré ici",
        ProfilePartKind.Empty => "vide",
        _ => _gpu.Describe(part.Values!)
             + (!GpuIdentity.IsSameCard(part.CapturedOn, _gpu.Identity)
                 ? $" — relevé sur {part.CapturedOn?.Describe() ?? "une carte non identifiée"}, non posé ici"
                 : ""),
    };

    private string DescribeFans(ProfileGroupFansPart? part) => part?.ParsedKind switch
    {
        null => "non touchée",
        ProfilePartKind.Origin => "rendue au BIOS (courbes gardées)",
        ProfilePartKind.Unknown => "réglage d'une version plus récente de PCPerfSuite, ignoré ici",
        ProfilePartKind.Empty => "vide",
        _ => _fans.Describe(part.Values!),
    };

    /// <summary>Ce que les cases « Appliquer au démarrage » des onglets reposeront au prochain lancement, et ce qui est
    /// rendu à la fermeture (D7).</summary>
    private static string DescribeStartup()
    {
        AppSettings settings = AppSettingsStore.Load();
        string cpu = settings.Cpu.ApplyAtStartup && settings.Cpu.SustainedWatts is { } watts
            ? $"limites de {watts:0} W reposées"
            : "limites d'origine";
        string gpu = settings.Gpu.ApplyOverclockAtStartup ? "overclock enregistré reposé (même carte seulement)" : "réglages d'origine";
        return $"Au prochain lancement : processeur, {cpu} ; carte graphique, {gpu} ; ventilateurs, courbes enregistrées dès le premier relevé. "
               + "À la fermeture : les watts et l'overclock sont rendus d'origine sauf « Appliquer au démarrage » coché, les ventilateurs "
               + "reviennent au BIOS, et les réglages du plan d'alimentation restent (permanents, « Tout rétablir » les rend).";
    }

    // ---- Enregistrer l'état actuel ----

    [RelayCommand]
    private void SaveCurrent()
    {
        if (!IncludeCpu && !IncludeGpu && !IncludeFans)
        {
            Status = "Coche au moins une dimension à enregistrer.";
            return;
        }

        DateTimeOffset now = _time.GetUtcNow();
        string name = ProfileGroupEditor.UniqueName(
            ProfileGroupEditor.CleanName(NewGroupName) ?? $"Groupe {_store.Groups.Count + 1}", _store.Groups.Select(g => g.Name));
        ProfileGroup group = ProfileGroupEditor.Create(name, NewGroupUsage.Value, now);
        if (IncludeCpu) group.Cpu = CaptureCpu(name);
        if (IncludeGpu && CaptureGpu(name) is { } gpu) group.Gpu = gpu;
        if (IncludeFans) group.Fans = CaptureFans(name);

        _store.Groups.Add(group);
        Persist();
        NewGroupName = "";
        Status = $"Groupe « {name} » enregistré.";
        Refresh();
    }

    private ProfileGroupCpuPart CaptureCpu(string name)
    {
        CpuTargetState state = _cpu.ReadState();
        return ProfileGroupEditor.CpuValues(state.ToProfile(name), state.Identity);
    }

    private ProfileGroupGpuPart? CaptureGpu(string name)
    {
        GpuTargetState state = _gpu.ReadState();
        return state.IsAvailable ? ProfileGroupEditor.GpuValues(state.ToProfile(name), state.Identity) : null;
    }

    private ProfileGroupFansPart CaptureFans(string name)
    {
        FanProfile current = _fans.ReadState().Current;
        current.Name = name;
        return ProfileGroupEditor.FanValues(current);
    }

    // ---- Appliquer ----

    [RelayCommand]
    private Task Apply(ProfileGroupItemViewModel? item) => ApplyAsync(item, makeStartupState: true);

    /// <summary>Outil de vérification : appliquer sans en faire l'état de démarrage, comme le fait la bascule
    /// automatique (#9).</summary>
    [RelayCommand]
    private Task ApplyTransient(ProfileGroupItemViewModel? item) => ApplyAsync(item, makeStartupState: false);

    [RelayCommand]
    private void CancelApply(ProfileGroupItemViewModel? item)
    {
        if (item is not null) item.IsConfirmingApply = false;
    }

    private async Task ApplyAsync(ProfileGroupItemViewModel? item, bool makeStartupState)
    {
        if (item is null || IsApplying) return;

        // Groupe suspendu après un incident : une confirmation dans la page, « Non » par défaut (D6).
        if (item.IsSuspended && !item.IsConfirmingApply)
        {
            item.IsConfirmingApply = true;
            return;
        }

        bool liftSuspension = item.IsConfirmingApply;
        item.IsConfirmingApply = false;
        ProfileGroupApplyResult? result = await RunApplyAsync(item.Model, ManualOptions(makeStartupState));
        if (result is null) return;

        // Enregistré tout de suite : RunApplyAsync a déjà écrit le bloc, et une suspension restée dans settings.json
        // ferait écarter le groupe par la bascule et ôterait sa période « demarrage » au prochain lancement.
        if (liftSuspension && result.Active is not null && _store.Suspensions.Remove(item.Id)) Persist();
        Refresh();
    }

    private static ProfileGroupApplyOptions ManualOptions(bool makeStartupState)
        => new(ProfileGroupRequesters.Manual, RequesterLabel, IsManual: true, MakeStartupState: makeStartupState);

    /// <summary>Applique par l'orchestrateur, puis mémorise l'état retenu comme groupe actif (et comme état de démarrage
    /// s'il l'est devenu). Un clic de l'utilisateur est un réglage à la main : la bascule automatique se met en
    /// pause.</summary>
    private async Task<ProfileGroupApplyResult?> RunApplyAsync(ProfileGroup group, ProfileGroupApplyOptions options)
    {
        IsApplying = true;
        try
        {
            if (options.IsManual) Tuning.NoteManualWrite($"groupe « {group.Name} » appliqué à la main");
            ProfileGroupApplyResult result = await _applier.ApplyAsync(group, options);

            LastReport = result.Report.Describe();
            Status = result.Report.Title;
            if (result.Active is { } active)
            {
                _store.Remember(active);
                Persist();
            }

            if (options.IsManual || IsPageShown)
            {
                CheckConformity();
            }
            else
            {
                // Bascule automatique, page cachée : pas de relecture du matériel pour un affichage que personne ne voit ;
                // la conformité sera vérifiée à la prochaine ouverture de la page.
                RefreshActive();
                ConformityText = null;
                _conformityCheckedUtc = null;
            }

            return result;
        }
        catch (Exception ex)
        {
            // Règle 2 : rien ne doit faire tomber la page ; la raison s'affiche.
            Status = $"Groupe « {group.Name} » non appliqué : erreur inattendue ({ex.GetType().Name}).";
            return null;
        }
        finally
        {
            IsApplying = false;
        }
    }

    // ---- Renommer, dupliquer, supprimer ----

    [RelayCommand]
    private void StartRename(ProfileGroupItemViewModel? item)
    {
        if (item is null) return;
        item.EditName = item.Name;
        item.IsRenaming = true;
    }

    [RelayCommand]
    private void CancelRename(ProfileGroupItemViewModel? item)
    {
        if (item is not null) item.IsRenaming = false;
    }

    [RelayCommand]
    private void CommitRename(ProfileGroupItemViewModel? item)
    {
        if (item is null || !item.IsRenaming) return;

        if (ProfileGroupEditor.CleanName(item.EditName) is not { } name)
        {
            Status = "Le nom d'un groupe ne peut pas être vide.";
            return;
        }

        if (_store.Groups.Any(g => !ReferenceEquals(g, item.Model) && string.Equals(g.Name, name, StringComparison.CurrentCultureIgnoreCase)))
        {
            Status = $"Un groupe « {name} » existe déjà : choisis un autre nom.";
            return;
        }

        string previous = item.Model.Name;
        ProfileGroupEditor.Rename(item.Model, name, _time.GetUtcNow());
        item.IsRenaming = false;
        Persist();
        Status = previous == name ? null : $"Groupe « {previous} » renommé en « {name} ».";
        Refresh();
    }

    [RelayCommand]
    private void Duplicate(ProfileGroupItemViewModel? item)
    {
        if (item is null) return;

        ProfileGroup copy = ProfileGroupEditor.Duplicate(item.Model, _store.Groups.Select(g => g.Name), _time.GetUtcNow());
        _store.Groups.Insert(_store.Groups.IndexOf(item.Model) + 1, copy);
        Persist();
        Status = $"Groupe « {copy.Name} » créé.";
        Refresh();
    }

    [RelayCommand]
    private void AskDelete(ProfileGroupItemViewModel? item)
    {
        if (item is not null) item.IsConfirmingDelete = true;
    }

    [RelayCommand]
    private void CancelDelete(ProfileGroupItemViewModel? item)
    {
        if (item is not null) item.IsConfirmingDelete = false;
    }

    [RelayCommand]
    private void ConfirmDelete(ProfileGroupItemViewModel? item)
    {
        if (item is null) return;

        if (_tuning?.GroupId == item.Id) EndGroupTuning();
        ProfileGroupEditor.Delete(_store, item.Id);
        Persist();
        Status = $"Groupe « {item.Name} » supprimé.";
        Refresh();
    }

    // ---- Modifier ----

    [RelayCommand]
    private void Edit(ProfileGroupItemViewModel? item)
    {
        if (item is null) return;
        foreach (ProfileGroupItemViewModel other in Groups) other.IsEditing = false;

        ProfileGroup group = item.Model;
        item.BeginEdit(
            Choices(DescribeCpu(group.Cpu), "Processeur", _cpu.Profiles.Select(p => p.Name)),
            Choices(DescribeGpu(group.Gpu), "GPU", _gpu.IsAvailable ? _gpu.Profiles.Select(p => p.Name) : []),
            Choices(DescribeFans(group.Fans), "Ventilateurs", _fans.Profiles.Select(p => p.Name)));
    }

    private static IReadOnlyList<PartChoice> Choices(string currentSummary, string tab, IEnumerable<string> profiles)
    {
        var choices = new List<PartChoice>
        {
            new(PartChoiceKind.Keep, $"Inchangé : {currentSummary}"),
            new(PartChoiceKind.None, "Ne pas toucher"),
            new(PartChoiceKind.Origin, "Remettre d'origine"),
            new(PartChoiceKind.Current, $"État actuel de l'onglet {tab}"),
        };
        choices.AddRange(profiles.Select(name => new PartChoice(PartChoiceKind.TabProfile, $"Profil « {name} » de l'onglet {tab}", name)));
        return choices;
    }

    [RelayCommand]
    private void CancelEdit(ProfileGroupItemViewModel? item)
    {
        if (item is not null) item.IsEditing = false;
    }

    [RelayCommand]
    private void SaveEdit(ProfileGroupItemViewModel? item)
    {
        if (item is null) return;

        ProfileGroup group = item.Model;
        DateTimeOffset now = _time.GetUtcNow();
        var notes = new List<string>();

        if (item.CpuChoice is { Kind: not PartChoiceKind.Keep } cpu)
        {
            ProfileGroupEditor.SetCpu(group, cpu.Kind switch
            {
                PartChoiceKind.None => null,
                PartChoiceKind.Origin => ProfileGroupEditor.CpuOrigin(),
                PartChoiceKind.Current => CaptureCpu(group.Name),
                _ => ImportCpu(cpu.ProfileName, notes),
            }, now);
        }

        if (item.GpuChoice is { Kind: not PartChoiceKind.Keep } gpu)
        {
            ProfileGroupGpuPart? part = gpu.Kind switch
            {
                PartChoiceKind.None => null,
                PartChoiceKind.Origin => ProfileGroupEditor.GpuOrigin(),
                PartChoiceKind.Current => CaptureGpu(group.Name),
                _ => ImportGpu(gpu.ProfileName, notes),
            };
            if (gpu.Kind == PartChoiceKind.Current && part is null) notes.Add("Carte graphique non relue : partie GPU laissée telle quelle.");
            else ProfileGroupEditor.SetGpu(group, part, now);
        }

        if (item.FanChoice is { Kind: not PartChoiceKind.Keep } fans)
        {
            ProfileGroupEditor.SetFans(group, fans.Kind switch
            {
                PartChoiceKind.None => null,
                PartChoiceKind.Origin => ProfileGroupEditor.FanOrigin(),
                PartChoiceKind.Current => CaptureFans(group.Name),
                _ => ImportFans(fans.ProfileName),
            }, now);
        }

        if (item.EditUsage is { } usage) ProfileGroupEditor.SetUsage(group, usage.Value, now);

        item.IsEditing = false;
        Persist();
        Status = notes.Count == 0 ? $"Groupe « {group.Name} » modifié." : $"Groupe « {group.Name} » modifié. {string.Join(" ", notes)}";
        Refresh();
    }

    private ProfileGroupCpuPart? ImportCpu(string? name, List<string> notes)
    {
        if (_cpu.Profiles.FirstOrDefault(p => p.Name == name)?.Model is not { } profile) return null;
        if (ProfileGroupEditor.ImportNote(ProfileDimension.Cpu, profile.SustainedWatts is not null) is { } note) notes.Add(note);
        return ProfileGroupEditor.CpuValues(profile, _cpu.Identity);
    }

    private ProfileGroupGpuPart? ImportGpu(string? name, List<string> notes)
    {
        if (_gpu.Profiles.FirstOrDefault(p => p.Name == name)?.Model is not { } profile) return null;
        if (ProfileGroupEditor.ImportNote(ProfileDimension.Gpu, true) is { } note) notes.Add(note);
        return ProfileGroupEditor.GpuValues(profile, _gpu.Identity);
    }

    private ProfileGroupFansPart? ImportFans(string? name)
        => _fans.Profiles.FirstOrDefault(p => p.Name == name)?.Model is { } profile ? ProfileGroupEditor.FanValues(profile) : null;

    // ---- Régler dans l'onglet ----

    [RelayCommand]
    private Task TuneCpu(ProfileGroupItemViewModel? item) => TuneInTabAsync(item, ProfileDimension.Cpu);

    [RelayCommand]
    private Task TuneGpu(ProfileGroupItemViewModel? item) => TuneInTabAsync(item, ProfileDimension.Gpu);

    [RelayCommand]
    private Task TuneFans(ProfileGroupItemViewModel? item) => TuneInTabAsync(item, ProfileDimension.Fans);

    /// <summary>Applique le groupe, ouvre l'onglet, et met « Mettre à jour le groupe » dans sa bannière.</summary>
    private async Task TuneInTabAsync(ProfileGroupItemViewModel? item, ProfileDimension dimension)
    {
        if (item is null || IsApplying) return;

        item.IsEditing = false;
        if (item.IsSuspended)
        {
            // D6 : un groupe suspendu ne se réapplique qu'après confirmation, par « Appliquer », qui lève la suspension.
            item.IsConfirmingApply = true;
            Status = $"Groupe « {item.Name} » suspendu après un incident : confirme son application avant de le régler dans un onglet.";
            return;
        }

        ProfileGroupApplyResult? result = await RunApplyAsync(item.Model, ManualOptions(makeStartupState: true));
        if (result is null || result.Report.WasRefused) return;

        _tuning = (item.Id, dimension);
        string tab = TabTitle(dimension);
        GroupTuningText = $"Réglage du groupe « {item.Name} » dans l'onglet {tab} : ajuste, puis « Mettre à jour le groupe ».";
        Tuning.GroupTuningText = GroupTuningText;
        Tuning.UpdateGroupCommand = UpdateTunedGroupCommand;
        Refresh();
        _navigate(dimension switch
        {
            ProfileDimension.Cpu => PageKeys.Cpu,
            ProfileDimension.Gpu => PageKeys.Gpu,
            _ => PageKeys.Fans,
        });
    }

    private static string TabTitle(ProfileDimension dimension) => dimension switch
    {
        ProfileDimension.Cpu => "Processeur",
        ProfileDimension.Gpu => "GPU",
        _ => "Ventilateurs",
    };

    /// <summary>« Mettre à jour le groupe » : capture la dimension réglée dans l'onglet.</summary>
    [RelayCommand]
    private void UpdateTunedGroup()
    {
        if (_tuning is not { } tuning || _store.Find(tuning.GroupId) is not { } group)
        {
            EndGroupTuning();
            return;
        }

        DateTimeOffset now = _time.GetUtcNow();
        switch (tuning.Dimension)
        {
            case ProfileDimension.Cpu:
                ProfileGroupEditor.SetCpu(group, CaptureCpu(group.Name), now);
                break;
            case ProfileDimension.Gpu when CaptureGpu(group.Name) is { } gpu:
                ProfileGroupEditor.SetGpu(group, gpu, now);
                break;
            case ProfileDimension.Gpu:
                Status = "Carte graphique non relue : le groupe n'a pas été mis à jour.";
                return;
            default:
                ProfileGroupEditor.SetFans(group, CaptureFans(group.Name), now);
                break;
        }

        Persist();
        string tab = TabTitle(tuning.Dimension);
        EndGroupTuning();
        Status = $"Groupe « {group.Name} » mis à jour depuis l'onglet {tab}.";
        Tuning.GroupTuningText = null;
        Refresh();
    }

    [RelayCommand]
    private void StopGroupTuning() => EndGroupTuning();

    private void EndGroupTuning()
    {
        _tuning = null;
        GroupTuningText = null;
        Tuning.GroupTuningText = null;
        Tuning.UpdateGroupCommand = null;
    }

    // ---- Groupe actif ----

    private void RefreshActive()
    {
        if (_store.Active is not { } active || _store.Find(active.GroupId) is not { } group)
        {
            ActiveText = null;
            ConformityText = null;
            return;
        }

        string transient = active.MadeStartupState ? "" : " sans en faire l'état de démarrage";
        string changed = group.Revision != active.Revision ? " Le groupe a été modifié depuis : réapplique-le pour poser sa nouvelle version." : "";
        ActiveText = $"Dernier groupe appliqué : « {group.Name} », le {active.SinceUtc.ToLocalTime():dd/MM à HH:mm}{transient}.{changed}";
    }

    /// <summary>Compare l'état relu à l'état retenu à l'application, dimension par dimension (jamais à la demande).</summary>
    private void CheckConformity()
    {
        RefreshActive();
        if (_store.Active is not { } active || ActiveText is null) return;

        try
        {
            var parts = new List<DimensionConformity>();
            if (active.Cpu is { } cpu && (cpu.PowerSettings.Count > 0 || cpu.SustainedWatts is not null))
                parts.Add(ProfileGroupConformity.CompareCpu(cpu, _cpu.ReadState()));
            if (active.Gpu is { } gpu) parts.Add(ProfileGroupConformity.CompareGpu(gpu, _gpu.ReadState()));
            if (active.Fans is { Fans.Count: > 0 } fans) parts.Add(ProfileGroupConformity.CompareFans(fans, _fans.ReadState()));

            ConformityText = parts.Count == 0
                ? "Rien à vérifier : le groupe n'a rien posé de relisible."
                : parts.All(p => p.State == ConformityState.Conform)
                    ? "Conforme : l'état relu est celui que le groupe a laissé."
                    : string.Join(" · ", parts.Select(p => p.Describe()));
            _conformityCheckedUtc = _time.GetUtcNow();
        }
        catch (Exception ex)
        {
            ConformityText = $"Vérification impossible ({ex.GetType().Name}).";
        }
    }

    private void OnHardwareResynced()
    {
        if (_loaded) CheckConformity();
    }

    // ---- Avertissements ----

    private void RefreshWarnings()
    {
        Warnings.Clear();
        foreach ((string id, ProfileGroupSuspension suspension) in _store.Suspensions)
        {
            if (suspension.Acknowledged || _store.Find(id) is not { } group) continue;

            var unchecked_ = new List<string>();
            if (suspension.CpuStartupUnchecked) unchecked_.Add("Processeur");
            if (suspension.GpuStartupUnchecked) unchecked_.Add("GPU");
            string startup = unchecked_.Count == 0 ? "" : $" « Appliquer au démarrage » a été décoché : {string.Join(" et ", unchecked_)}.";
            Warnings.Add(new ProfileGroupWarning(id,
                $"Le groupe « {group.Name} » n'a pas été réappliqué : {suspension.Cause}.{startup} L'appliquer à nouveau demandera une confirmation.",
                OffersGpuRestore: false));
        }

        if (_sessionWarning is { } session) Warnings.Add(session);
    }

    [RelayCommand]
    private void Acknowledge(ProfileGroupWarning? warning)
    {
        if (warning is null) return;

        if (warning.GroupId is { } id && _store.Suspensions.TryGetValue(id, out ProfileGroupSuspension? suspension))
        {
            suspension.Acknowledged = true;
            Persist();
        }

        if (ReferenceEquals(warning, _sessionWarning)) _sessionWarning = null;
        RefreshWarnings();
    }

    /// <summary>Après un TDR pendant la période probatoire : rendre la carte d'origine, par le même chemin qu'un groupe.</summary>
    [RelayCommand]
    private async Task RestoreGpuOrigin()
    {
        var origin = new ProfileGroup { Name = "GPU d'origine", Gpu = ProfileGroupEditor.GpuOrigin() };
        IsApplying = true;
        try
        {
            Tuning.NoteManualWrite("carte graphique rendue d'origine à la main");
            ProfileGroupApplyResult result = await _applier.ApplyAsync(origin,
                new ProfileGroupApplyOptions(ProfileGroupRequesters.Manual, RequesterLabel, IsManual: true));
            Status = result.Report.WasRefused ? result.Report.Title : $"Carte graphique : {result.Report.Find(ProfileDimension.Gpu)?.Body ?? "rien à changer"}.";
        }
        catch (Exception ex)
        {
            Status = $"Retour d'origine impossible ({ex.GetType().Name}).";
        }
        finally
        {
            IsApplying = false;
        }

        CheckConformity();
    }

    // ---- Outils de vérification ----

    private TuningLeaseHandle? _testLease;

    [ObservableProperty] private ProfileGroupItemViewModel? toolGroup;

    /// <summary>Le panneau des outils de vérification est déplié.</summary>
    [ObservableProperty] private bool showTools;

    /// <summary>Tient le bail 5 minutes, comme le ferait le bench ou une recherche d'OC : les onglets se grisent, et une
    /// application de groupe est refusée avec la raison.</summary>
    [RelayCommand]
    private void HoldTestLease()
    {
        if (_testLease is { IsReleased: false })
        {
            Status = "Le bail de test est déjà tenu.";
            return;
        }

        DateTimeOffset until = _time.GetUtcNow() + TimeSpan.FromMinutes(5);
        TuningLeaseResult result = _lease.TryAcquire("test", "l'outil de test de la page Profils", "vérification du bail, 5 min",
            () => _time.GetUtcNow() < until);
        _testLease = result.Handle;
        Status = result.Acquired ? "Bail de test tenu pendant 5 minutes." : $"Bail de test refusé : {result.Holder?.Describe(_lease.UtcNow, _lease.LocalTimeZone)}";
    }

    [RelayCommand]
    private void ReleaseTestLease()
    {
        _testLease?.Dispose();
        _testLease = null;
        Status = "Bail de test libéré.";
    }

    [RelayCommand]
    private Task ApplyToolGroupTransient() => ApplyTransient(ToolGroup);

    public void Dispose()
    {
        _cpu.HardwareResynced -= OnHardwareResynced;
        _gpu.HardwareResynced -= OnHardwareResynced;
        _testLease?.Dispose();
        Automatic?.Dispose();
        DisposeSafety();
    }
}

/// <summary>Un avertissement de la page : groupe suspendu après un incident, ou TDR pendant la période probatoire.</summary>
public sealed record ProfileGroupWarning(string? GroupId, string Text, bool OffersGpuRestore);
