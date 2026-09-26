using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCPerfSuite.Core.Hardware;
using PCPerfSuite.Core.Hardware.Fans;
using PCPerfSuite.Core.PowerSettings;
using PCPerfSuite.Core.SystemInfo;

namespace PCPerfSuite.App.ViewModels;

/// <summary>
/// Un ventilateur pilotable — de la carte mère ou du GPU, l'onglet les traite pareil. Le ViewModel
/// écrit directement dans la configuration persistée (FanCurveConfig) : c'est elle que lit le
/// régulateur, donc un réglage modifié prend effet au relevé suivant sans recopie intermédiaire.
/// </summary>
public sealed partial class FanControlItemViewModel : ObservableObject
{
    private readonly IFanController _controller;
    private readonly FanCurveConfig _config;
    private readonly FanCurveRegulator _regulator = new();
    private readonly Action _persist;
    private readonly Action<FanControlItemViewModel> _copyToAll;
    private readonly Action<FanControlItemViewModel> _autoRestored;
    private readonly Action<FanControlItemViewModel> _identityChanged;

    /// <summary>Dernière consigne effectivement envoyée au ventilateur, pour ne pas la repousser
    /// identique à chaque relevé. Null : rien n'est posé, le ventilateur est au firmware.</summary>
    private float? _lastSentPercent;

    /// <summary>Identifiant stable du ventilateur (voir <see cref="FanIdentityOverride.FanId"/>) : ce à quoi se rattachent
    /// sa courbe, son nom et sa catégorie enregistrés.</summary>
    public string FanId { get; }

    /// <summary>Nom trouvé par la détection (« CPU Fan », « Ventilateur 6 »), avant toute correction de l'utilisateur.</summary>
    public string DetectedName { get; }

    /// <summary>Catégorie trouvée par la détection, avant toute correction de l'utilisateur.</summary>
    public FanCategory DetectedCategory { get; }

    /// <summary>D'où vient ce ventilateur : le nom lu et la puce qui le porte (« Nom lu : CPU Fan · Nuvoton NCT6798D »),
    /// ou le canal quand la carte mère n'a donné aucun nom. C'est ce qui permet de retrouver le ventilateur dans un
    /// signalement, ou dans le BIOS.</summary>
    public string Subtitle { get; }

    /// <summary>Nom choisi par l'utilisateur, vide pour garder le nom détecté.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName), nameof(HasCustomIdentity))]
    private string customName;

    /// <summary>Ce que refroidit ce ventilateur : la catégorie détectée (voir <see cref="FanIdentification"/>), ou celle
    /// que l'utilisateur a choisie.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPump), nameof(IsGpu), nameof(CanStopWhenCool), nameof(HasCustomIdentity))]
    private FanCategory category;

    /// <summary>Le panneau de correction du nom et de la catégorie est ouvert.</summary>
    [ObservableProperty] private bool isEditingIdentity;

    public string DisplayName => string.IsNullOrWhiteSpace(CustomName) ? DetectedName : CustomName.Trim();

    /// <summary>Le nom ou la catégorie ont été corrigés à la main.</summary>
    public bool HasCustomIdentity => !string.IsNullOrWhiteSpace(CustomName) || Category != DetectedCategory;

    /// <summary>Le cooler d'une carte graphique est un cooler de carte graphique : on peut le renommer, pas le ranger ailleurs.</summary>
    public bool CanChangeCategory => !FanId.StartsWith(GpuControlService.FanIdPrefix, StringComparison.Ordinal);

    public IReadOnlyList<FanCategoryChoice> CategoryChoices => FanCategoryChoice.All;

    public bool IsPump => Category == FanCategory.Pump;

    /// <summary>L'arrêt complet à froid n'est pas proposé pour une pompe : sans circulation, le liquide ne refroidit plus
    /// rien et le processeur chauffe en quelques secondes.</summary>
    public bool CanStopWhenCool => !IsPump;

    /// <summary>Explication affichée sous le mode d'une pompe, jamais pour un ventilateur.</summary>
    public string PumpNote => $"Pompe : PCPerfSuite ne l'arrête jamais et ne la descend pas sous {PumpMinPercent:0} %.";

    /// <summary>Ventilateur du GPU, piloté par l'API du constructeur ou par la bibliothèque de capteurs : il a la
    /// protection thermique du GPU et la température du GPU comme source par défaut.</summary>
    public bool IsGpu => Category == FanCategory.Gpu;

    public ObservableCollection<FanCurvePoint> Points { get; }

    /// <summary>Null tant qu'aucune vitesse n'a été lue pour ce ventilateur : affiché « -- », jamais « 0 RPM ».</summary>
    [ObservableProperty] private double? rpm;

    /// <summary>0 tr/min alors que la carte alimente le connecteur : probablement rien de branché (voir
    /// <see cref="EmptyHeaderDetector"/>). Le ventilateur reste pilotable, il est seulement rangé à part.</summary>
    [ObservableProperty] private bool isEmptyHeader;
    [ObservableProperty] private double? currentPercent;
    [ObservableProperty] private double? targetPercent;
    [ObservableProperty] private double? sourceTempC;
    [ObservableProperty] private FanControlMode mode;
    [ObservableProperty] private double manualPercent;
    [ObservableProperty] private FanTempSource source;
    [ObservableProperty] private double hysteresisC;
    [ObservableProperty] private double minPercent;
    [ObservableProperty] private double maxPercent;
    [ObservableProperty] private bool stopWhenCool;
    [ObservableProperty] private double stopBelowTempC;

    /// <param name="identity">Nom et catégorie déjà corrigés par l'utilisateur, null si rien n'a été touché.</param>
    /// <param name="identityChanged">Appelé quand l'utilisateur corrige le nom ou la catégorie : à enregistrer, et à ranger.</param>
    public FanControlItemViewModel(
        FanCurveConfig config,
        string detectedName,
        string subtitle,
        FanCategory detectedCategory,
        FanIdentityOverride? identity,
        IFanController controller,
        Action persist,
        Action<FanControlItemViewModel> copyToAll,
        Action<FanControlItemViewModel> autoRestored,
        Action<FanControlItemViewModel> identityChanged)
    {
        _config = config;
        _controller = controller;
        _persist = persist;
        _copyToAll = copyToAll;
        _autoRestored = autoRestored;
        _identityChanged = identityChanged;

        FanId = config.ControlSensorId;
        DetectedName = detectedName;
        DetectedCategory = detectedCategory;
        Subtitle = subtitle;

        customName = identity?.Name?.Trim() ?? "";

        // Une clé inconnue (fichier édité à la main, version ultérieure) garde la catégorie détectée. Le cooler d'une
        // carte graphique n'en change jamais, même si le fichier le prétend.
        category = CanChangeCategory && FanCategoryInfo.TryParseKey(identity?.Category, out FanCategory chosen)
            ? chosen
            : detectedCategory;

        Points = new ObservableCollection<FanCurvePoint>(config.Points);
        mode = config.Mode;
        manualPercent = config.ManualPercent;
        source = config.Source;
        hysteresisC = config.HysteresisC;
        minPercent = config.MinPercent;
        maxPercent = config.MaxPercent;
        stopWhenCool = config.StopBelowTempC is not null;
        stopBelowTempC = config.StopBelowTempC ?? 40;

        EnforcePumpRules();
    }

    /// <summary>Une pompe ne s'arrête jamais. Un arrêt à froid réglé avant qu'elle soit reconnue comme telle (quand
    /// c'était encore « Ventilateur 6 »), ou avant que l'utilisateur la range en pompe, ne doit plus agir : le réglage
    /// n'est plus proposé à l'écran, il ne doit pas continuer à s'appliquer sans qu'on puisse le voir.</summary>
    private void EnforcePumpRules()
    {
        if (!IsPump || !StopWhenCool) return;

        // Passe par la propriété : elle efface le seuil de la configuration, remet le régulateur à zéro et enregistre.
        StopWhenCool = false;
    }

    partial void OnCustomNameChanged(string value) => _identityChanged(this);

    partial void OnCategoryChanged(FanCategory value)
    {
        EnforcePumpRules();
        _identityChanged(this);
    }

    [RelayCommand]
    private void ToggleIdentityEditing() => IsEditingIdentity = !IsEditingIdentity;

    /// <summary>Revient au nom et à la catégorie détectés.</summary>
    [RelayCommand]
    private void ResetIdentity()
    {
        CustomName = "";
        Category = DetectedCategory;
    }

    /// <summary>Une pompe ne descend jamais sous cette vitesse en mode Manuel ou Courbe. Beaucoup de pompes calent
    /// sous 20-30 %, et une consigne à 0 % par mégarde arrête la circulation du liquide.</summary>
    public const float PumpMinPercent = 30f;

    /// <summary>Au-delà de cette température, un ventilateur GPU piloté par l'app passe à 100% quel que
    /// soit le mode choisi — garde-fou indépendant des réglages utilisateur. En mode Auto, c'est la
    /// protection du VBIOS qui joue ce rôle.</summary>
    private const float GpuCriticalTempC = 88f;

    /// <summary>Durée pendant laquelle « Repérer » fait tourner un ventilateur à fond.</summary>
    public static readonly TimeSpan LocateDuration = TimeSpan.FromSeconds(5);

    private DispatcherTimer? _locateTimer;

    /// <summary>Le ventilateur tourne à 100 % pour qu'on le retrouve : la régulation est suspendue.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LocateLabel))]
    private bool isLocating;

    public string LocateLabel => IsLocating ? "Repérage…" : "Repérer";

    /// <summary>Jamais sur un portable : le contrôleur embarqué n'est pas écrit par l'app (règle de compatibilité 5). Un
    /// cooler de carte graphique d'un portable passe par le pilote graphique, mais le repérage n'y est pas proposé non
    /// plus : il n'a d'intérêt que pour retrouver un ventilateur dans un boîtier de PC de bureau.</summary>
    public bool CanLocate => !MachineInfo.Current.IsLaptop;

    /// <summary>Fait tourner ce ventilateur à 100 % pendant <see cref="LocateDuration"/> pour le retrouver dans le
    /// boîtier — ou, sur un hub, voir quels ventilateurs suivent ce connecteur — puis le rend à son mode. Sans danger,
    /// pompe comprise : c'est la vitesse maximale, jamais un arrêt.</summary>
    [RelayCommand]
    private void Locate()
    {
        if (IsLocating || !CanLocate) return;
        if (!_controller.TrySetPercent(FanId, 100)) return;

        IsLocating = true;
        TargetPercent = 100;
        _lastSentPercent = null;

        _locateTimer = new DispatcherTimer { Interval = LocateDuration };
        _locateTimer.Tick += (_, _) => EndLocate();
        _locateTimer.Start();
    }

    /// <summary>Fin du repérage. Il a pris la main sur le ventilateur quel que soit son mode : on le rend au firmware, et
    /// le relevé suivant repose la consigne du mode (Manuel, Courbe) s'il y en a une. Rendre la main plutôt que de
    /// « reposer l'ancienne valeur » : en mode Courbe sans température lue, il n'y a pas d'ancienne valeur, et le
    /// ventilateur serait resté à 100 %.</summary>
    private void EndLocate()
    {
        if (!IsLocating) return;

        CancelLocate();
        _controller.TrySetAuto(FanId);
        _autoRestored(this);
        _lastSentPercent = null;
        TargetPercent = null;
    }

    /// <summary>Arrête le minuteur sans toucher au ventilateur : l'appelant s'en charge.</summary>
    private void CancelLocate()
    {
        _locateTimer?.Stop();
        _locateTimer = null;
        IsLocating = false;
    }

    /// <summary>Applique le mode courant au ventilateur pour ce relevé.</summary>
    public void Apply(float? tempC)
    {
        SourceTempC = tempC;

        // Pendant le repérage, la régulation est suspendue : elle repartirait sur la consigne du mode avant la fin.
        if (IsLocating)
        {
            TargetPercent = 100;
            return;
        }

        float? target = Mode switch
        {
            FanControlMode.Manual => (float)ManualPercent,
            FanControlMode.Curve when tempC is { } t => _regulator.Evaluate(Points, t, _config),
            _ => null,
        };

        if (target is not null && IsGpu && tempC is { } temp && temp >= GpuCriticalTempC) target = 100;
        if (target is { } requested && IsPump && requested < PumpMinPercent) target = PumpMinPercent;

        if (target is { } percent)
        {
            // La consigne n'est réécrite que si elle a bougé. La repousser à chaque relevé ferait
            // dialoguer avec la puce Super I/O (ou le pilote graphique) dix fois par seconde,
            // indéfiniment, pour lui redire ce qu'elle applique déjà.
            if (_lastSentPercent is not { } last || Math.Abs(last - percent) >= MinPercentChange)
            {
                if (_controller.TrySetPercent(FanId, percent)) _lastSentPercent = percent;
            }

            TargetPercent = percent;
        }
        else
        {
            TargetPercent = null;
            _lastSentPercent = null;
        }
    }

    /// <summary>En dessous de cet écart, la consigne est considérée comme inchangée : les ventilateurs
    /// se pilotent par paliers de quelques pour cent, un demi-point ne change rien à leur vitesse.</summary>
    private const float MinPercentChange = 0.5f;

    public void RestoreAuto()
    {
        // Un repérage en cours a posé 100 % même sur un ventilateur en Auto : il faut le rendre aussi.
        bool wasLocating = IsLocating;
        CancelLocate();

        if (Mode == FanControlMode.Auto && !wasLocating) return;
        _controller.TrySetAuto(FanId);
        _lastSentPercent = null;
    }

    /// <summary>Oublie la dernière consigne envoyée : la prochaine sera renvoyée même si elle vaut la même. Sert
    /// quand le matériel a perdu la consigne sans que ce ventilateur y soit pour rien.</summary>
    public void ForgetSentPercent() => _lastSentPercent = null;

    /// <summary>Recopie la courbe et les réglages de régulation d'un autre ventilateur.</summary>
    public void CopyFrom(FanControlItemViewModel other)
    {
        Points.Clear();
        foreach (FanCurvePoint p in other.Points) Points.Add(new FanCurvePoint { TempC = p.TempC, Percent = p.Percent });

        HysteresisC = other.HysteresisC;
        MinPercent = other.MinPercent;
        MaxPercent = other.MaxPercent;
        StopWhenCool = other.StopWhenCool;
        StopBelowTempC = other.StopBelowTempC;
        NotifyPointsEdited();
    }

    /// <summary>La configuration en place, copiée, pour l'enregistrer dans un profil.</summary>
    public FanCurveConfig Capture()
    {
        FanCurveConfig copy = _config.Clone();

        // La courbe affichée fait foi : la configuration ne la reçoit qu'à la fin d'un geste (NotifyPointsEdited).
        copy.Points = Points.Select(p => new FanCurvePoint { TempC = p.TempC, Percent = p.Percent }).ToList();
        return copy;
    }

    /// <summary>
    /// Pose la configuration d'un profil, déjà ramenée dans les limites de l'onglet (voir
    /// <see cref="FanProfileMatcher.Sanitize"/>). Tout passe par les propriétés, donc par le chemin d'un clic :
    /// la consigne suit au relevé suivant, avec la protection thermique du GPU et le plancher d'une pompe. Rien
    /// n'est écrit ici sur le matériel en dehors de ce que le mode choisi ferait de toute façon.
    /// </summary>
    /// <returns>Ce qui n'a pas pu être posé tel quel — vide quand le profil s'applique entièrement.</returns>
    public IReadOnlyList<string> ApplyFromProfile(FanCurveConfig entry, bool hasGpu)
    {
        var notes = new List<string>();

        FanTempSource tempSource = entry.Source;
        if (!hasGpu && tempSource is FanTempSource.GpuCore or FanTempSource.HottestOfCpuGpu)
        {
            tempSource = FanTempSource.CpuPackage;
            notes.Add("pas de GPU sur ce PC, température du CPU suivie à la place");
        }

        Points.Clear();
        foreach (FanCurvePoint p in entry.Points) Points.Add(new FanCurvePoint { TempC = p.TempC, Percent = p.Percent });

        // Le maximum d'abord au plus haut : chaque borne repousse l'autre si elle la dépasse, et poser le minimum
        // puis le maximum dans le mauvais ordre laisserait une valeur du profil écrasée par l'ancienne.
        MaxPercent = 100;
        MinPercent = entry.MinPercent;
        MaxPercent = entry.MaxPercent;

        HysteresisC = entry.HysteresisC;
        ManualPercent = entry.ManualPercent;
        Source = tempSource;

        if (entry.StopBelowTempC is { } stop && CanStopWhenCool)
        {
            StopBelowTempC = stop;
            StopWhenCool = true;
        }
        else
        {
            StopWhenCool = false;
            if (entry.StopBelowTempC is not null) notes.Add("arrêt à froid ignoré, jamais proposé pour une pompe");
        }

        NotifyPointsEdited();

        // Le mode en dernier : les consignes ci-dessus sont alors en place avant que la régulation reparte.
        Mode = entry.Mode;
        return notes;
    }

    partial void OnModeChanged(FanControlMode value)
    {
        // Choisir un mode reprend la main sur le repérage ; la suite (Auto rend le ventilateur, les autres modes
        // reposent leur consigne) fait le reste.
        CancelLocate();

        _config.Mode = value;
        if (value == FanControlMode.Auto)
        {
            _controller.TrySetAuto(FanId);
            TargetPercent = null;
            _autoRestored(this);
        }

        // Le firmware a repris la main (ou va la reprendre) : la prochaine consigne doit repartir,
        // même si elle vaut la dernière qu'on avait posée.
        _lastSentPercent = null;
        _regulator.Reset();
        _persist();
    }

    partial void OnManualPercentChanged(double value)
    {
        _config.ManualPercent = (float)value;
        _persist();
    }

    partial void OnSourceChanged(FanTempSource value)
    {
        _config.Source = value;
        _regulator.Reset();
        _persist();
    }

    partial void OnHysteresisCChanged(double value)
    {
        _config.HysteresisC = (float)value;
        _regulator.Reset();
        _persist();
    }

    partial void OnMinPercentChanged(double value)
    {
        _config.MinPercent = (float)value;
        if (value > MaxPercent) MaxPercent = value;
        _persist();
    }

    partial void OnMaxPercentChanged(double value)
    {
        _config.MaxPercent = (float)value;
        if (value < MinPercent) MinPercent = value;
        _persist();
    }

    partial void OnStopWhenCoolChanged(bool value) => ApplyStopThreshold();

    partial void OnStopBelowTempCChanged(double value) => ApplyStopThreshold();

    private void ApplyStopThreshold()
    {
        _config.StopBelowTempC = StopWhenCool ? (float)StopBelowTempC : null;
        _regulator.Reset();
        _persist();
    }

    [RelayCommand]
    private void ApplySilencieux() => ApplyPreset(FanCurveMath.SilencieuxPoints());

    [RelayCommand]
    private void ApplyEquilibre() => ApplyPreset(FanCurveMath.EquilibrePoints());

    [RelayCommand]
    private void ApplyPerf() => ApplyPreset(FanCurveMath.PerfPoints());

    [RelayCommand]
    private void CopyToAll() => _copyToAll(this);

    private void ApplyPreset(List<FanCurvePoint> points)
    {
        Points.Clear();
        foreach (FanCurvePoint p in points) Points.Add(p);
        NotifyPointsEdited();
    }

    public void NotifyPointsEdited()
    {
        _config.Points = Points.ToList();
        _regulator.Reset();
        _persist();
    }
}

/// <summary>Une catégorie proposée dans la liste déroulante du panneau de correction.</summary>
public sealed record FanCategoryChoice(FanCategory Category, string Title)
{
    /// <summary>Ordre d'affichage des sections de l'onglet, repris par la liste déroulante.</summary>
    public static readonly IReadOnlyList<FanCategoryChoice> All = new[]
    {
        FanCategory.Cpu, FanCategory.Pump, FanCategory.Gpu, FanCategory.Case, FanCategory.Other, FanCategory.Unidentified,
    }.Select(category => new FanCategoryChoice(category, FanCategoryInfo.Title(category))).ToList();
}

/// <summary>Un profil de ventilation enregistré, tel qu'affiché dans la liste des profils. Le résumé est calculé par
/// l'onglet, seul à savoir quels ventilateurs existent sur CETTE machine ; le renommage se fait sur place.</summary>
public sealed partial class FanProfileViewModel : ObservableObject
{
    private readonly Action<FanProfileViewModel> _commitRename;

    public FanProfile Model { get; }

    public string Name => Model.Name;

    [ObservableProperty] private string summary;

    /// <summary>Le nom est en cours de modification : la ligne montre alors une zone de texte.</summary>
    [ObservableProperty] private bool isRenaming;

    [ObservableProperty] private string editName = "";

    public FanProfileViewModel(FanProfile model, string summary, Action<FanProfileViewModel> commitRename)
    {
        Model = model;
        this.summary = summary;
        _commitRename = commitRename;
    }

    /// <summary>Le nom est validé par l'onglet, seul à connaître les autres profils : un nom vide ou déjà pris est refusé.</summary>
    public void Rename(string name)
    {
        Model.Name = name;
        OnPropertyChanged(nameof(Name));
    }

    [RelayCommand]
    private void StartRename()
    {
        EditName = Name;
        IsRenaming = true;
    }

    [RelayCommand]
    private void CommitRename() => _commitRename(this);

    [RelayCommand]
    private void CancelRename() => IsRenaming = false;
}

/// <summary>
/// Pilotage des ventilateurs (onglet "Ventilateurs") : tous les ventilateurs pilotables au même
/// endroit — ceux de la carte mère via LibreHardwareMonitor et ceux du GPU via NVAPI — avec pour
/// chacun un mode (Auto/Manuel/Courbe), une source de température, une hystérésis et des bornes.
/// S'appuie sur les instantanés déjà produits par MonitoringViewModel plutôt que de repoller le
/// matériel en double : la consigne est recalculée à chaque nouveau relevé de température.
/// </summary>
public sealed partial class FanCurvesViewModel : ObservableObject, IDisposable
{
    private readonly HardwareMonitorService _hardware;
    private readonly GpuControlService _gpu;
    private readonly MonitoringViewModel _monitoring;
    private readonly AppSettings _settings;
    private readonly EmptyHeaderDetector _emptyHeaders = new();

    /// <summary>Coolers GPU exposés par l'API du constructeur, résolus une seule fois : NVAPI ne les renumérote
    /// pas en cours de route, inutile de les relire à chaque relevé.</summary>
    private List<int>? _gpuCoolerIds;

    private GpuVendor? _gpuVendor;

    /// <summary>Libellé de chaque cooler GPU (« GPU 1 », « GPU 2 »...), par numéro de cooler.</summary>
    private Dictionary<int, string> _gpuLabels = new();

    private string? _gpuName;

    /// <summary>Tous les ventilateurs à plat : ce que parcourent la fermeture de l'app et le diagnostic. L'écran,
    /// lui, affiche <see cref="Groups"/>.</summary>
    public ObservableCollectionEx<FanControlItemViewModel> Fans { get; } = new();

    /// <summary>Sections de l'écran, dans l'ordre d'affichage : une par catégorie non vide, puis les connecteurs sans
    /// ventilateur détecté.</summary>
    public ObservableCollectionEx<FanGroupViewModel> Groups { get; } = new();

    private const string UnidentifiedNote =
        "La carte mère ne donne pas le nom de ces connecteurs, et PCPerfSuite ne peut pas deviner ce qui y est branché. " +
        "« Repérer » fait tourner un ventilateur à fond pendant 5 s pour le retrouver dans le boîtier ; le crayon permet " +
        "ensuite de le nommer et de le ranger (pompe, boîtier…).";

    private readonly Dictionary<FanCategory, FanGroupViewModel> _groups = new();

    private readonly FanGroupViewModel _emptyHeadersGroup = new(
        "Connecteurs sans ventilateur détecté",
        "0 tr/min alors que la carte mère les alimente : connecteur vide, ou ventilateur dont la vitesse n'est pas lue " +
        "(2 broches, ou branché sur un hub). Ils restent pilotables.",
        isCollapsible: true);

    private string _groupSignature = "";

    private GpuFanPairing _lastGpuPairing = GpuFanPairing.None;

    [ObservableProperty] private bool hasGpu;

    /// <summary>Profils enregistrés par l'utilisateur : toutes les courbes sous un nom.</summary>
    public ObservableCollection<FanProfileViewModel> Profiles { get; } = new();

    [ObservableProperty] private string newProfileName = "";

    /// <summary>Null tant qu'aucun profil n'a été touché : la ligne d'état ne s'affiche qu'ensuite.</summary>
    [ObservableProperty] private string? profileStatus;

    /// <summary>La carte des profils n'a rien à montrer sans ventilateur ni profil.</summary>
    public bool ShowProfiles => Fans.Count > 0 || Profiles.Count > 0;

    /// <summary>Le premier relevé est passé : on sait quels ventilateurs existent ici, et donc lesquels manquent à un
    /// profil. Avant, un profil s'afficherait « absent de ce PC » pour tous ses ventilateurs.</summary>
    private bool _hasSnapshot;

    /// <summary>Un profil est en cours d'application : les enregistrements sont regroupés en un seul, à la fin.</summary>
    private bool _applyingProfile;

    // Ce que le diagnostic « Compatibilité de ce PC » demande à cet onglet : c'est lui qui sait comment les ventilateurs
    // ont été identifiés, rapprochés et rangés.

    /// <summary>Coolers de la carte graphique exposés par l'API du constructeur.</summary>
    public int GpuCoolerCount => _gpuCoolerIds?.Count ?? 0;

    /// <summary>Nom de l'API du constructeur qui pilote la carte graphique, null si aucune n'a répondu.</summary>
    public string? GpuDriverName => _gpuVendor switch
    {
        GpuVendor.Nvidia => "NVIDIA (NVAPI)",
        GpuVendor.Amd => "AMD (ADLX)",
        GpuVendor.Intel => "Intel (IGCL)",
        _ => null,
    };

    /// <summary>Lectures de la bibliothèque de capteurs écartées de la liste parce qu'elles décrivent les mêmes coolers.</summary>
    public int GpuDuplicatesDiscarded => _lastGpuPairing.Replaced.Count;

    /// <summary>Connecteurs de la carte mère où aucun ventilateur n'a été détecté.</summary>
    public int EmptyHeaderCount => Fans.Count(f => f.IsEmptyHeader);

    /// <summary>Ventilateurs dont l'utilisateur a corrigé le nom ou la catégorie.</summary>
    public int CustomizedCount => Fans.Count(f => f.HasCustomIdentity);

    /// <summary>Profils de ventilation enregistrés.</summary>
    public int ProfileCount => Profiles.Count;

    /// <summary>Ce que le dernier profil chargé n'a pas pu appliquer (ventilateurs absents, réglages corrigés), null
    /// tant qu'aucun profil n'a été chargé depuis le lancement de l'app.</summary>
    public string? LastProfileReport { get; private set; }

    /// <summary>Cette lecture décrit un ventilateur que l'API du constructeur pilote déjà : elle n'est pas listée.</summary>
    public bool IsGpuDuplicate(FanReading reading) => _lastGpuPairing.Replaced.Any(r => r.SensorId == reading.SensorId);

    /// <summary>Numéro du cooler (« gpu:2 ») dont cette lecture est le doublon, null si elle n'est rapprochée d'aucun.</summary>
    public string? GpuCoolerIdFor(FanReading reading)
        => _lastGpuPairing.Pairs.FirstOrDefault(p => p.Reading?.SensorId == reading.SensorId) is { } pair
            ? GpuControlService.FanId(pair.CoolerId)
            : null;

    /// <summary>Le ventilateur listé dans l'onglet qui correspond à cette lecture, null si elle n'y figure pas (lecture
    /// seule, doublon écarté, portable).</summary>
    public FanControlItemViewModel? FindItem(FanReading reading)
        => reading.PercentControlSensorId is { } id ? Fans.FirstOrDefault(f => f.FanId == id) : null;

    /// <summary>Pourquoi aucun ventilateur n'est pilotable ici, adapté au PC : sans administrateur, portable (dont
    /// les ventilateurs appartiennent au firmware du constructeur) ou carte mère sans pilotage logiciel.</summary>
    public string NoFansMessage
    {
        get
        {
            if (!ElevationHelper.IsAdministrator())
                return "PCPerfSuite n'est pas lancé en administrateur : les ventilateurs ne peuvent être ni lus ni pilotés. Relance l'app en administrateur.";

            if (MachineInfo.Current.IsLaptop)
                return "Sur un portable, les ventilateurs sont pilotés par le firmware du constructeur (Armoury Crate, Legion Zone, " +
                       "Omen Gaming Hub, MSI Center, PredatorSense…). Par sécurité, PCPerfSuite ne les pilote pas : leur vitesse " +
                       "s'affiche dans le Monitoring quand la marque est prise en charge (voir Paramètres › Compatibilité de ce PC).";

            return "Aucun ventilateur pilotable détecté : la puce de gestion de la carte mère n'est pas reconnue ou n'accepte pas de " +
                   "pilotage logiciel (sur certaines cartes, il faut passer les ventilateurs en mode PWM/DC manuel dans le BIOS). " +
                   "Ce n'est pas un dysfonctionnement de PCPerfSuite.";
        }
    }

    public FanCurvesViewModel(HardwareMonitorService hardware, GpuControlService gpu, MonitoringViewModel monitoring)
    {
        _hardware = hardware;
        _gpu = gpu;
        _monitoring = monitoring;
        _settings = AppSettingsStore.Load();

        // Un fichier édité à la main peut contenir des profils vides ou sans liste : on les remet d'aplomb plutôt
        // que de planter au premier affichage.
        _settings.FanProfiles.RemoveAll(p => p is null);
        foreach (FanProfile profile in _settings.FanProfiles)
        {
            profile.Name = string.IsNullOrWhiteSpace(profile.Name) ? "Profil" : profile.Name;
            profile.Fans ??= new List<FanCurveConfig>();
            profile.FanNames ??= new Dictionary<string, string>();
            Profiles.Add(new FanProfileViewModel(profile, BuildProfileSummary(profile), CommitProfileRename));
        }

        Fans.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ShowProfiles));
        Profiles.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(ShowProfiles));
            OnPropertyChanged(nameof(ProfileCount));
        };

        _monitoring.SnapshotUpdated += OnSnapshotUpdated;
    }

    // ---- Profils ----

    /// <summary>Enregistre toutes les courbes en place sous un nom. Un profil du même nom est remplacé, à sa place
    /// dans la liste, pour pouvoir le mettre à jour sans le supprimer d'abord.</summary>
    [RelayCommand]
    private void SaveProfile()
    {
        if (Fans.Count == 0)
        {
            ProfileStatus = "Aucun ventilateur pilotable pour l'instant : rien à enregistrer.";
            return;
        }

        string name = NewProfileName.Trim();
        if (name.Length == 0) name = NextDefaultProfileName();

        var profile = new FanProfile { Name = name };
        foreach (FanControlItemViewModel item in Fans)
        {
            profile.Fans.Add(item.Capture());
            profile.FanNames[item.FanId] = item.DisplayName;
        }

        var created = new FanProfileViewModel(profile, BuildProfileSummary(profile), CommitProfileRename);
        FanProfileViewModel? existing = FindProfile(name);
        if (existing is null) Profiles.Add(created);
        else Profiles[Profiles.IndexOf(existing)] = created;

        NewProfileName = "";
        ProfileStatus = existing is null ? $"Profil « {name} » enregistré." : $"Profil « {name} » mis à jour.";
        PersistProfiles();
    }

    /// <summary>
    /// Applique un profil. Il a pu être écrit sur une autre machine, ou avant qu'on débranche un ventilateur : ce
    /// qui n'existe pas ici est ignoré, et le message dit quoi et pourquoi plutôt que de passer sous silence. Les
    /// ventilateurs que le profil ne mentionne pas restent tels quels. Rien de ce que le profil contient ne peut
    /// atteindre un ventilateur qui n'est pas listé dans l'onglet — donc jamais le contrôleur embarqué d'un portable.
    /// </summary>
    [RelayCommand]
    private void ApplyProfile(FanProfileViewModel? profile)
    {
        if (profile is null) return;

        if (Fans.Count == 0)
        {
            ProfileStatus = $"Profil « {profile.Name} » non appliqué : aucun ventilateur pilotable. {NoFansMessage}";
            LastProfileReport = ProfileStatus;
            return;
        }

        FanProfileMatch match = FanProfileMatcher.Match(profile.Model, Fans.Select(f => f.FanId).ToList());

        var notApplied = new List<string>();
        var adjusted = new List<string>();
        int applied = 0;

        // Un seul enregistrement du fichier de réglages pour tout le profil, pas un par propriété modifiée.
        _applyingProfile = true;
        try
        {
            foreach (FanCurveConfig entry in match.Applicable)
            {
                FanControlItemViewModel item = Fans.First(f => f.FanId == entry.ControlSensorId);
                SanitizedFanCurve sanitized = FanProfileMatcher.Sanitize(entry);

                if (sanitized.Config is not { } config)
                {
                    notApplied.Add($"« {item.DisplayName} » ({string.Join(", ", sanitized.Notes)})");
                    continue;
                }

                var notes = new List<string>(sanitized.Notes);
                notes.AddRange(item.ApplyFromProfile(config, HasGpu));
                applied++;

                if (notes.Count > 0) adjusted.Add($"« {item.DisplayName} » ({string.Join(", ", notes)})");
            }
        }
        finally
        {
            _applyingProfile = false;
            Persist();
        }

        foreach (FanCurveConfig entry in match.Missing)
        {
            notApplied.Add($"« {ProfileFanName(profile.Model, entry.ControlSensorId)} » ({AbsenceReason(entry.ControlSensorId)})");
        }

        var parts = new List<string>
        {
            applied == 0 ? "aucun ventilateur réglé" : Plural(applied, "ventilateur réglé", "ventilateurs réglés"),
        };

        if (notApplied.Count > 0) parts.Add($"non appliqué : {string.Join(", ", notApplied)}");
        if (adjusted.Count > 0) parts.Add($"réglages corrigés : {string.Join(", ", adjusted)}");

        if (match.NotInProfile.Count > 0)
        {
            IEnumerable<string> names = match.NotInProfile.Select(id => $"« {Fans.First(f => f.FanId == id).DisplayName} »");
            parts.Add($"pas dans ce profil, laissé(s) tel(s) quel(s) : {string.Join(", ", names)}");
        }

        ProfileStatus = $"Profil « {profile.Name} » : {string.Join(" · ", parts)}.";
        LastProfileReport = ProfileStatus;
    }

    [RelayCommand]
    private void DeleteProfile(FanProfileViewModel? profile)
    {
        if (profile is null) return;

        Profiles.Remove(profile);
        ProfileStatus = $"Profil « {profile.Name} » supprimé.";
        PersistProfiles();
    }

    /// <summary>Valide le nouveau nom d'un profil. Un nom vide ou déjà pris est refusé et la zone de texte reste
    /// ouverte, avec la raison, pour qu'on puisse le corriger sans tout retaper.</summary>
    private void CommitProfileRename(FanProfileViewModel profile)
    {
        if (!profile.IsRenaming) return;

        string name = profile.EditName.Trim();
        if (name.Length == 0)
        {
            ProfileStatus = "Le nom d'un profil ne peut pas être vide.";
            return;
        }

        if (string.Equals(name, profile.Name, StringComparison.Ordinal))
        {
            profile.IsRenaming = false;
            return;
        }

        FanProfileViewModel? clash = FindProfile(name);
        if (clash is not null && !ReferenceEquals(clash, profile))
        {
            ProfileStatus = $"Un profil « {clash.Name} » existe déjà : choisis un autre nom.";
            return;
        }

        string previous = profile.Name;
        profile.Rename(name);
        profile.IsRenaming = false;
        ProfileStatus = $"Profil « {previous} » renommé en « {name} ».";
        PersistProfiles();
    }

    private FanProfileViewModel? FindProfile(string name)
        => Profiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase));

    private string NextDefaultProfileName()
    {
        int number = Profiles.Count + 1;
        while (FindProfile($"Profil {number}") is not null) number++;
        return $"Profil {number}";
    }

    /// <summary>Le nom d'un ventilateur tel qu'il s'appelait à l'enregistrement du profil, faute de mieux son identifiant.</summary>
    private static string ProfileFanName(FanProfile profile, string fanId)
        => profile.FanNames.TryGetValue(fanId, out string? name) && !string.IsNullOrWhiteSpace(name) ? name : fanId;

    /// <summary>Pourquoi un ventilateur d'un profil n'existe pas sur ce PC, en distinguant ce que la règle de
    /// compatibilité demande : limite de la machine, app lancée sans administrateur, ou simplement absent.</summary>
    private static string AbsenceReason(string fanId)
    {
        if (IsGpuCooler(fanId)) return "cooler de carte graphique non détecté sur ce PC";
        if (MachineInfo.Current.IsLaptop) return "ventilateur de carte mère, non piloté sur un portable";
        if (!ElevationHelper.IsAdministrator()) return "PCPerfSuite n'est pas lancé en administrateur";
        return "absent de ce PC";
    }

    /// <summary>Résumé d'un profil dans les termes de CETTE machine : le nom actuel du ventilateur quand il existe ici,
    /// celui de l'enregistrement sinon, avec la mention « absent de ce PC » une fois les ventilateurs connus.</summary>
    private string BuildProfileSummary(FanProfile profile)
    {
        var parts = new List<string>();
        foreach (FanCurveConfig? entry in profile.Fans)
        {
            if (entry is null || string.IsNullOrEmpty(entry.ControlSensorId)) continue;

            FanControlItemViewModel? item = Fans.FirstOrDefault(f => f.FanId == entry.ControlSensorId);
            string name = item?.DisplayName ?? ProfileFanName(profile, entry.ControlSensorId);
            string absent = _hasSnapshot && item is null ? " (absent de ce PC)" : "";

            parts.Add($"{name} : {DescribeMode(entry)}{absent}");
        }

        return parts.Count == 0 ? "Ce profil ne contient aucun ventilateur." : string.Join("  •  ", parts);
    }

    private static string DescribeMode(FanCurveConfig entry) => entry.Mode switch
    {
        FanControlMode.Auto => "Auto",
        FanControlMode.Manual => $"Manuel {entry.ManualPercent:0} %",
        FanControlMode.Curve => $"Courbe ({entry.Points?.Count ?? 0} points)",
        _ => "mode inconnu",
    };

    private void RefreshProfileSummaries()
    {
        foreach (FanProfileViewModel profile in Profiles) profile.Summary = BuildProfileSummary(profile.Model);
    }

    private void PersistProfiles()
    {
        _settings.FanProfiles = Profiles.Select(p => p.Model).ToList();
        Persist();
    }

    private static string Plural(int count, string singular, string plural)
        => count > 1 ? $"{count} {plural}" : $"{count} {singular}";

    private void OnSnapshotUpdated(HardwareSnapshot snapshot)
    {
        HasGpu = snapshot.Gpu is not null;

        // Une carte graphique arrive par deux chemins (voir GpuFanPairing) : ses coolers sont pilotés par l'API du
        // constructeur, et les commandes que la bibliothèque de capteurs expose pour la même carte ne sont pas
        // listées une deuxième fois. Elles servent à lire la vitesse de chaque cooler.
        IReadOnlyList<int> coolerIds = ResolveGpuCoolerIds();
        GpuFanPairing gpuFans = _lastGpuPairing = GpuFanPairing.Pair(coolerIds, _gpuVendor, snapshot.Fans);

        // Règle de compatibilité 5 : sur un portable, le refroidissement appartient au contrôleur
        // embarqué du constructeur, donc on n'expose aucun ventilateur de carte mère ici — même quand
        // LibreHardwareMonitor voit une puce Super I/O pilotable (barebones Clevo/Tongfang, quelques
        // MSI). Sans ce filtre, l'onglet les afficherait comme pilotables et NoFansMessage promettrait
        // exactement le contraire de ce que l'app ferait. Le ventilateur du GPU, lui, passe par le
        // pilote graphique (NVAPI/ADLX/IGCL) et reste légitime sur un portable à carte dédiée.
        List<FanReading> motherboard = MachineInfo.Current.IsLaptop
            ? new List<FanReading>()
            : snapshot.Fans.Where(f => f.CanControl && !gpuFans.Replaced.Contains(f)).ToList();
        SyncFanList(motherboard, coolerIds);

        var readings = new Dictionary<string, FanReading>();
        foreach (FanReading fan in motherboard) readings[fan.PercentControlSensorId!] = fan;
        foreach (GpuFanPair pair in gpuFans.Pairs)
        {
            if (pair.Reading is { } reading) readings[GpuControlService.FanId(pair.CoolerId)] = reading;
        }

        foreach (FanControlItemViewModel item in Fans)
        {
            if (readings.TryGetValue(item.FanId, out FanReading? reading))
            {
                item.Rpm = reading.Rpm;
                item.CurrentPercent = reading.PercentControl;
                item.IsEmptyHeader = _emptyHeaders.Observe(item.FanId, item.Category, reading.Rpm, reading.PercentControl);
            }
            else if (IsGpuCooler(item.FanId) && coolerIds.Count == 1)
            {
                // Un seul cooler : la lecture du GPU dans son ensemble est forcément la sienne.
                item.Rpm = snapshot.Gpu?.FanRpm;
                item.CurrentPercent = snapshot.Gpu?.FanPercent;
            }
            else
            {
                // Plusieurs coolers sans lecture rapprochée : on ne sait pas lequel tourne à quelle vitesse.
                item.Rpm = null;
                item.CurrentPercent = null;
            }

            item.Apply(TempFor(item.Source, snapshot));
        }

        RebuildGroups();

        // Les résumés de profils disent quels ventilateurs manquent : ils ne le savent qu'à partir d'ici, même sur
        // un PC sans aucun ventilateur (où la composition ne change jamais, et RebuildGroups ne fait donc rien).
        if (!_hasSnapshot)
        {
            _hasSnapshot = true;
            RefreshProfileSummaries();
        }
    }

    /// <summary>Range les ventilateurs par catégorie, puis les connecteurs sans ventilateur détecté à part. Ne touche
    /// à rien tant que la composition n'a pas changé : le relevé tombe plusieurs fois par seconde, et reconstruire
    /// les cartes à chaque fois ferait perdre le focus et l'état d'un champ en cours de saisie.</summary>
    private void RebuildGroups()
    {
        string signature = string.Join("|", Fans.Select(f => $"{f.FanId}/{(int)f.Category}/{f.IsEmptyHeader}"));
        if (signature == _groupSignature) return;
        _groupSignature = signature;

        // Un ventilateur est apparu ou a disparu : les profils l'affichent (ou le marquent absent) en conséquence.
        if (_hasSnapshot) RefreshProfileSummaries();

        var wanted = new List<FanGroupViewModel>();

        foreach (FanCategory category in FanCategoryChoice.All.Select(choice => choice.Category))
        {
            List<FanControlItemViewModel> members = Fans.Where(f => f.Category == category && !f.IsEmptyHeader).ToList();
            if (members.Count == 0) continue;

            if (!_groups.TryGetValue(category, out FanGroupViewModel? group))
            {
                group = _groups[category] = new FanGroupViewModel(
                    FanCategoryInfo.Title(category), category == FanCategory.Unidentified ? UnidentifiedNote : null);
            }

            group.Fans.SyncTo(members);
            wanted.Add(group);
        }

        List<FanControlItemViewModel> empty = Fans.Where(f => f.IsEmptyHeader).ToList();
        if (empty.Count > 0)
        {
            _emptyHeadersGroup.Fans.SyncTo(empty);
            wanted.Add(_emptyHeadersGroup);
        }

        Groups.SyncTo(wanted);
    }

    private static bool IsGpuCooler(string fanId) => fanId.StartsWith(GpuControlService.FanIdPrefix, StringComparison.Ordinal);

    private void SyncFanList(List<FanReading> motherboard, IReadOnlyList<int> coolerIds)
    {
        List<string> expected = motherboard.Select(f => f.PercentControlSensorId!)
            .Concat(coolerIds.Select(GpuControlService.FanId))
            .ToList();

        for (int i = Fans.Count - 1; i >= 0; i--)
        {
            if (!expected.Contains(Fans[i].FanId)) Fans.RemoveAt(i);
        }

        bool added = false;

        foreach (FanReading fan in motherboard)
        {
            string id = fan.PercentControlSensorId!;
            if (Fans.Any(f => f.FanId == id)) continue;

            FanCurveConfig config = ConfigFor(id, DefaultSourceFor(fan.Category), ref added);
            Fans.Add(new FanControlItemViewModel(
                config, fan.Label, DescribeSource(fan), fan.Category, IdentityFor(id), _hardware,
                Persist, CopyCurveToAll, OnFanRestoredToAuto, OnFanIdentityChanged));
        }

        foreach (int coolerId in coolerIds)
        {
            string id = GpuControlService.FanId(coolerId);
            if (Fans.Any(f => f.FanId == id)) continue;

            FanCurveConfig config = ConfigFor(id, FanTempSource.GpuCore, ref added);
            string label = _gpuLabels.GetValueOrDefault(coolerId, "GPU");
            string subtitle = _gpuName is { Length: > 0 } gpuName ? $"{gpuName} · cooler {coolerId}" : $"Cooler {coolerId}";
            Fans.Add(new FanControlItemViewModel(
                config, label, subtitle, FanCategory.Gpu, IdentityFor(id), _gpu,
                Persist, CopyCurveToAll, OnFanRestoredToAuto, OnFanIdentityChanged));
        }

        if (added) Persist();
    }

    /// <summary>Température suivie par défaut d'un ventilateur qu'on n'a jamais réglé : le GPU pour ses ventilateurs, la
    /// plus chaude du CPU et du GPU pour le boîtier (il doit suivre celui des deux qui chauffe), le CPU sinon. Sans GPU,
    /// « le plus chaud » n'est pas proposé à l'écran : on reste sur le CPU.</summary>
    private FanTempSource DefaultSourceFor(FanCategory category) => category switch
    {
        FanCategory.Gpu => FanTempSource.GpuCore,
        FanCategory.Case when HasGpu => FanTempSource.HottestOfCpuGpu,
        _ => FanTempSource.CpuPackage,
    };

    /// <summary>Ce qu'on sait de l'origine d'un ventilateur : le nom lu, ou le canal quand la carte n'en a donné aucun.</summary>
    private static string DescribeSource(FanReading fan)
    {
        if (fan.Category == FanCategory.Gpu) return $"{fan.HardwareName} · {fan.SensorName}";

        if (fan.NameFromHardware) return $"Nom lu : {fan.SensorName} · {fan.HardwareName}";

        return fan.Channel is { } channel
            ? $"{fan.HardwareName} · canal {channel + 1} — nom non fourni par la carte mère"
            : $"{fan.HardwareName} — nom non fourni";
    }

    /// <summary>Configuration d'un ventilateur, créée au besoin. Pour le GPU, on reprend les réglages
    /// de l'ancien onglet GPU (où vivait la courbe GPU) plutôt que de repartir de zéro.</summary>
    private FanCurveConfig ConfigFor(string fanId, FanTempSource defaultSource, ref bool added)
    {
        FanCurveConfig? config = _settings.FanCurves.FirstOrDefault(c => c.ControlSensorId == fanId);
        if (config is not null) return config;

        bool isGpu = fanId.StartsWith(GpuControlService.FanIdPrefix, StringComparison.Ordinal);
        config = new FanCurveConfig
        {
            ControlSensorId = fanId,
            Source = defaultSource,
            Mode = isGpu ? _settings.Gpu.FanMode : FanControlMode.Auto,
            ManualPercent = isGpu ? _settings.Gpu.FanManualPercent : 50,
            Points = isGpu ? _settings.Gpu.FanPoints.ToList() : FanCurveMath.EquilibrePoints(),
        };

        _settings.FanCurves.Add(config);
        added = true;
        return config;
    }

    private IReadOnlyList<int> ResolveGpuCoolerIds()
    {
        if (_gpuCoolerIds is not null) return _gpuCoolerIds;

        _gpuCoolerIds = new List<int>();
        if (_gpu.TryInitialize() && _gpu.GetSnapshot() is { } snap)
        {
            _gpuVendor = _gpu.Vendor;
            _gpuName = snap.Name;
            _gpuCoolerIds.AddRange(snap.Fans.Select(f => f.CoolerId).Distinct().OrderBy(id => id));

            // « GPU 1 », « GPU 2 »... — ou « GPU » pour un cooler seul. Même règle que pour les autres ventilateurs.
            IReadOnlyList<string> labels = FanIdentification.AssignLabels(_gpuCoolerIds
                .Select(id => new FanLabelInput(FanCategory.Gpu, $"GPU Fan {id}", NameFromHardware: false, id, HardwareId: null))
                .ToList());
            _gpuLabels = _gpuCoolerIds.Zip(labels).ToDictionary(x => x.First, x => x.Second);
        }

        return _gpuCoolerIds;
    }

    private FanIdentityOverride? IdentityFor(string fanId) => _settings.FanIdentities.FirstOrDefault(i => i.FanId == fanId);

    /// <summary>L'utilisateur a corrigé le nom ou la catégorie d'un ventilateur : on l'enregistre (ou on efface l'entrée
    /// quand il revient à ce que la détection avait trouvé), puis on le range dans sa nouvelle section sans attendre
    /// le relevé suivant.</summary>
    private void OnFanIdentityChanged(FanControlItemViewModel item)
    {
        string? name = string.IsNullOrWhiteSpace(item.CustomName) ? null : item.CustomName.Trim();
        string? category = item.Category != item.DetectedCategory ? FanCategoryInfo.Key(item.Category) : null;

        FanIdentityOverride? record = IdentityFor(item.FanId);
        if (name is null && category is null)
        {
            if (record is not null) _settings.FanIdentities.Remove(record);
        }
        else
        {
            if (record is null)
            {
                record = new FanIdentityOverride { FanId = item.FanId };
                _settings.FanIdentities.Add(record);
            }

            record.Name = name;
            record.Category = category;
        }

        Persist();
        RebuildGroups();

        // Le nom d'un ventilateur ne change pas la composition des sections, mais il figure dans les résumés de profils.
        RefreshProfileSummaries();
    }

    /// <summary>Le pilote graphique rend TOUS les coolers d'un coup (NVAPI RestoreCoolerSettingsToDefault, IGCL
    /// ctlFanSetDefaultMode sur chaque ventilateur) : repasser un cooler en Auto libère aussi ceux qui étaient en
    /// Manuel ou en Courbe. Les autres oublient leur dernière consigne pour que le prochain relevé la renvoie ;
    /// sans ça, un cooler resté à « Manuel 60 % » à l'écran repartait sur la régulation du pilote sans un mot,
    /// puisque la consigne n'est renvoyée que si elle change. Les commandes de la carte mère, elles, se rendent
    /// une par une.</summary>
    private void OnFanRestoredToAuto(FanControlItemViewModel restored)
    {
        if (!IsGpuCooler(restored.FanId)) return;

        foreach (FanControlItemViewModel other in Fans)
        {
            if (!ReferenceEquals(other, restored) && IsGpuCooler(other.FanId)) other.ForgetSentPercent();
        }
    }

    private void CopyCurveToAll(FanControlItemViewModel source)
    {
        foreach (FanControlItemViewModel item in Fans)
        {
            // Une pompe n'a pas les besoins d'un ventilateur : une courbe pensée pour un ventilateur de boîtier,
            // qui ralentit à froid, ne doit jamais lui être imposée en un clic.
            if (item.IsPump) continue;

            if (!ReferenceEquals(item, source)) item.CopyFrom(source);
        }
    }

    private static float? TempFor(FanTempSource source, HardwareSnapshot s) => source switch
    {
        FanTempSource.CpuPackage => s.Cpu.PackageTempC,
        FanTempSource.GpuCore => s.Gpu?.CoreTempC,
        FanTempSource.MotherboardSystem => s.Motherboard.SystemTempC,
        FanTempSource.HottestOfCpuGpu => Hottest(s.Cpu.PackageTempC, s.Gpu?.CoreTempC),
        _ => null,
    };

    private static float? Hottest(float? a, float? b)
        => a is null ? b : b is null ? a : Math.Max(a.Value, b.Value);

    /// <summary>Relit le fichier avant d'écrire : cet onglet n'est propriétaire que des courbes de
    /// ventilation, le reste (GPU, overlay, monitoring) appartient aux autres ViewModels.</summary>
    private void Persist()
    {
        // Appliquer un profil modifie des dizaines de propriétés : l'enregistrement se fait une fois, à la fin.
        if (_applyingProfile) return;

        AppSettings settings = AppSettingsStore.Load();
        settings.FanCurves = _settings.FanCurves;
        settings.FanIdentities = _settings.FanIdentities;
        settings.FanProfiles = _settings.FanProfiles;
        AppSettingsStore.Save(settings);
    }

    /// <summary>Rend tous les ventilateurs actuellement pilotés au firmware — appelé à la fermeture de
    /// l'app pour ne jamais laisser un ventilateur bloqué à un % logiciel une fois PCPerfSuite fermé.</summary>
    public void Dispose()
    {
        _monitoring.SnapshotUpdated -= OnSnapshotUpdated;

        foreach (FanControlItemViewModel item in Fans)
        {
            item.RestoreAuto();
        }
    }
}
