using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCPerfSuite.App.Interop;
using PCPerfSuite.App.Utils;
using PCPerfSuite.Core.Hardware;
using PCPerfSuite.Core.PowerSettings;
using PCPerfSuite.Core.Processes;
using PCPerfSuite.Core.Storage;

namespace PCPerfSuite.App.ViewModels;

public enum ProcessKindFilter
{
    All,
    Applications,
    Background,
    Windows,
}

public sealed record ProcessRefreshOption(int Ms, string Label);

public sealed record ProcessKindOption(ProcessKindFilter Value, string Label);

/// <summary>
/// Une colonne de la liste : sa largeur sert directement de largeur de ColumnDefinition, aussi bien pour
/// l'en-tête que pour chaque ligne. Une colonne masquée passe à une largeur nulle plutôt que de disparaître
/// de la grille, ce qui garde en-tête et lignes alignés sans le coût d'un SharedSizeGroup (qui ferait
/// participer chaque ligne réalisée à un calcul commun, et annulerait le bénéfice de la virtualisation).
/// </summary>
public sealed partial class ProcessColumnViewModel : ObservableObject
{
    private readonly double _pixels;
    private readonly bool _fills;

    public string Id { get; }
    public string Header { get; }

    /// <summary>Colonne qu'on ne peut pas masquer (le nom : sans lui la liste n'a plus de sens).</summary>
    public bool IsLocked { get; }

    /// <summary>Colonne numérique : alignée à droite, et triée par ordre décroissant au premier clic — on
    /// cherche les gros consommateurs, pas les zéros.</summary>
    public bool IsNumeric { get; }

    [ObservableProperty] private bool isVisible;

    /// <summary>Faux quand la machine n'a pas fourni la donnée : la colonne disparaît complètement au lieu
    /// d'afficher « -- » sur quatre cents lignes.</summary>
    [ObservableProperty] private bool isAvailable = true;

    [ObservableProperty] private string sortGlyph = "";

    /// <summary>Prévient le ViewModel de l'onglet quand l'utilisateur coche ou décoche la colonne (pour
    /// enregistrer le choix et vérifier que la colonne de tri est toujours visible).</summary>
    internal Action? VisibilityChanged;

    public ProcessColumnViewModel(string id, string header, double pixels, bool isNumeric,
        bool fills = false, bool isLocked = false)
    {
        Id = id;
        Header = header;
        _pixels = pixels;
        _fills = fills;
        IsNumeric = isNumeric;
        IsLocked = isLocked;
    }

    public GridLength Width => IsVisible && IsAvailable
        ? _fills ? new GridLength(1, GridUnitType.Star) : new GridLength(_pixels)
        : new GridLength(0);

    public Visibility CellVisibility => IsVisible && IsAvailable ? Visibility.Visible : Visibility.Collapsed;

    public string HeaderDisplay => SortGlyph.Length > 0 ? $"{Header} {SortGlyph}" : Header;

    public HorizontalAlignment ContentAlignment => IsNumeric ? HorizontalAlignment.Right : HorizontalAlignment.Left;

    partial void OnIsVisibleChanged(bool value)
    {
        NotifyLayoutChanged();
        VisibilityChanged?.Invoke();
    }

    partial void OnIsAvailableChanged(bool value) => NotifyLayoutChanged();

    partial void OnSortGlyphChanged(string value) => OnPropertyChanged(nameof(HeaderDisplay));

    private void NotifyLayoutChanged()
    {
        OnPropertyChanged(nameof(Width));
        OnPropertyChanged(nameof(CellVisibility));
    }
}

/// <summary>Les colonnes, nommées une par une : la vue s'y lie sans indexeur ni converter.</summary>
public sealed class ProcessColumnsViewModel
{
    public ProcessColumnViewModel Name { get; } = new("name", "Nom", 0, isNumeric: false, fills: true, isLocked: true);
    public ProcessColumnViewModel Pid { get; } = new("pid", "PID", 64, isNumeric: true);
    public ProcessColumnViewModel Kind { get; } = new("kind", "Type", 106, isNumeric: false);
    public ProcessColumnViewModel Cpu { get; } = new("cpu", "CPU", 76, isNumeric: true);
    public ProcessColumnViewModel Memory { get; } = new("memory", "Mémoire", 98, isNumeric: true);
    public ProcessColumnViewModel Io { get; } = new("io", "E/S", 94, isNumeric: true);
    public ProcessColumnViewModel Threads { get; } = new("threads", "Threads", 70, isNumeric: true);
    public ProcessColumnViewModel User { get; } = new("user", "Compte", 150, isNumeric: false);
    public ProcessColumnViewModel Publisher { get; } = new("publisher", "Éditeur", 170, isNumeric: false);

    public IReadOnlyList<ProcessColumnViewModel> All { get; }

    /// <summary>Colonnes cochées au premier lancement.</summary>
    private static readonly string[] DefaultVisible = { "name", "pid", "kind", "cpu", "memory", "io" };

    public ProcessColumnsViewModel()
    {
        All = new[] { Name, Pid, Kind, Cpu, Memory, Io, Threads, User, Publisher };
    }

    public void ApplyVisibility(IEnumerable<string>? visibleIds)
    {
        var wanted = new HashSet<string>(visibleIds ?? DefaultVisible, StringComparer.Ordinal);
        foreach (ProcessColumnViewModel column in All)
        {
            column.IsVisible = column.IsLocked || wanted.Contains(column.Id);
        }
    }

    public List<string> VisibleIds => All.Where(c => c.IsVisible).Select(c => c.Id).ToList();

    /// <summary>À brancher APRÈS <see cref="ApplyVisibility"/>, pour que la restauration des réglages ne
    /// déclenche pas une réécriture du fichier au démarrage.</summary>
    internal void SetVisibilityCallback(Action callback)
    {
        foreach (ProcessColumnViewModel column in All) column.VisibilityChanged = callback;
    }

    public ProcessColumnViewModel? ById(string id) => All.FirstOrDefault(c => c.Id == id);
}

/// <summary>
/// Une ligne de la liste. Mise à jour en place à chaque relevé (jamais recréée) : la sélection, le défilement
/// et l'historique des mini-courbes survivent, et rien ne clignote.
/// </summary>
public sealed partial class ProcessRowViewModel : ObservableObject
{
    public ProcessesViewModel Owner { get; }

    public ProcessIdentity Identity { get; private set; }
    public int Pid { get; }
    public string Name { get; }

    /// <summary>Vrai pour une ligne d'agrégat — « Google Chrome (32) » — qui ne correspond à aucun processus
    /// mais totalise ceux qui partagent son exécutable, comme l'onglet « Processus » du Gestionnaire des
    /// tâches. Elle n'a ni PID, ni heure de démarrage, et ne peut pas être terminée directement : c'est
    /// l'onglet qui répercute l'action sur ses membres.</summary>
    public bool IsAggregate { get; }

    /// <summary>Ce qui rassemble les instances d'une même application : son exécutable et le compte qui la
    /// fait tourner. Deux utilisateurs connectés n'ont pas « le même » navigateur.</summary>
    public string GroupKey { get; private set; } = "";

    /// <summary>L'agrégat auquel cette ligne appartient, null si elle est seule de son espèce.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMember))]
    private ProcessRowViewModel? parent;

    public bool IsMember => Parent is not null;

    /// <summary>Nombre de processus totalisés par un agrégat.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MemberCountDisplay))]
    private int memberCount;

    /// <summary>« (32) » derrière le nom d'un agrégat, chaîne vide ailleurs. L'espace est dans la chaîne :
    /// les deux Run sont volontairement collés dans le XAML, sans quoi WPF glisserait une espace même sur
    /// les lignes ordinaires, où il n'y a rien à séparer.</summary>
    public string MemberCountDisplay => MemberCount > 0 ? $" ({MemberCount})" : "";

    /// <summary>Groupe déplié. Replié, ses membres quittent la vue par le filtre — le seul chemin par lequel
    /// une ligne entre ou sort, pour que rien ne bouge sous le curseur. Replié par défaut : vingt lignes de
    /// « chrome.exe » sous chaque navigateur noient la liste, et le chevron est là pour aller y voir.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExpanderGlyph))]
    private bool isExpanded;

    /// <summary>Chevron des agrégats : vers le bas quand le groupe est déplié, vers la droite sinon.</summary>
    public string ExpanderGlyph => IsExpanded ? "" : "";

    /// <summary>La ligne qui porte le classement du groupe : l'agrégat pour un membre, la ligne elle-même
    /// sinon. C'est ce qui garde les membres accrochés sous leur parent quel que soit le tri.</summary>
    public ProcessRowViewModel SortSubject => Parent ?? this;

    /// <summary>Famille affichée. Un membre prend celle de son agrégat : un navigateur dont la fenêtre est
    /// une « Application » et les rendus des processus d'arrière-plan doit rester d'un seul tenant sous
    /// « Applications », comme dans le Gestionnaire des tâches.</summary>
    public ProcessKind EffectiveKind => Parent?.Kind ?? Kind;

    [ObservableProperty] private string displayName = "";
    [ObservableProperty] private string? executablePath;

    /// <summary>Icône du fichier, chargée en arrière-plan. Null tant qu'elle n'est pas arrivée, et null
    /// définitivement pour un processus protégé ou sans chemin lisible : la vue affiche alors un glyphe
    /// neutre à la place.</summary>
    [ObservableProperty] private ImageSource? icon;
    [ObservableProperty] private string? publisher;
    [ObservableProperty] private string? userName;
    [ObservableProperty] private string? windowTitle;
    [ObservableProperty] private ProcessKind kind;
    [ObservableProperty] private int parentPid;
    [ObservableProperty] private int threadCount;
    [ObservableProperty] private bool isAccessible = true;
    [ObservableProperty] private bool isCritical;

    [ObservableProperty] private double? cpuPercent;
    [ObservableProperty] private long? memoryBytes;
    [ObservableProperty] private double? ioBytesPerSecond;

    [ObservableProperty] private bool isSelected;

    /// <summary>La ligne appartient au filtre courant. C'est une propriété de la ligne, et non le résultat
    /// d'un prédicat évalué par la vue, parce que c'est le ViewModel — et lui seul — qui doit décider QUAND
    /// une ligne entre ou sort : jamais pendant que la liste est visée, sinon tout ce qui est en dessous
    /// remonte d'une hauteur de ligne et le clic tombe sur le processus voisin.</summary>
    [ObservableProperty] private bool matchesFilter;

    /// <summary>Le processus a disparu, mais sa ligne reste affichée en grisé tant que le pointeur survole la
    /// liste : la retirer aussitôt ferait remonter tout le reste d'un cran sous le curseur.</summary>
    [ObservableProperty] private bool isGone;

    public SampleHistory CpuHistory { get; } = new(90);
    public SampleHistory MemoryHistory { get; } = new(90);

    public string KindLabel => Kind switch
    {
        ProcessKind.Application => "Application",
        ProcessKind.Windows => "Windows",
        _ => "Arrière-plan",
    };

    /// <summary>En-tête du groupe où la ligne se range quand la liste est triée par nom. Au pluriel et plus
    /// explicite que <see cref="KindLabel"/>, qui tient dans une colonne étroite.</summary>
    public string GroupLabel => EffectiveKind switch
    {
        ProcessKind.Application => "Applications",
        ProcessKind.Windows => "Processus Windows",
        _ => "Processus en arrière-plan",
    };

    /// <summary>Rang de la famille, utilisé comme critère de tri principal au tri par nom. L'ordre des
    /// en-têtes, lui, est fixé ailleurs (ApplyGrouping) ; ce rang sert à ranger la SOURCE par famille, ce
    /// qui fait qu'un processus changeant de famille devient mal placé, se fait déplacer, et rejoint du
    /// même coup le bon groupe.</summary>
    public int GroupRank => EffectiveKind switch
    {
        ProcessKind.Application => 0,
        ProcessKind.Windows => 2,
        _ => 1,
    };

    public string CpuDisplay => CpuPercent is not { } value
        ? "--"
        : value < 10 ? value.ToString("0.0", CultureInfo.CurrentCulture) + " %"
                     : value.ToString("0", CultureInfo.CurrentCulture) + " %";

    // ----- Intensité du fond bleu -----
    //
    // Chaque valeur est posée sur une pastille bleue d'autant plus marquée qu'elle pèse dans la MACHINE, pas
    // dans la liste : une teinte donnée veut toujours dire la même chose, et ne change pas de sens quand un
    // autre processus prend la tête. Les échelles sont écrasées (racine, logarithme) parce qu'à l'échelle
    // linéaire tout sauf trois processus serait transparent : un navigateur à 1,5 Go sur 32 Go, c'est 5 % de la
    // RAM, et il doit se voir.

    /// <summary>RAM physique visible par Windows, lue une fois : elle ne change pas en cours de session.
    /// TotalAvailableMemoryBytes est la RAM physique totale de la machine (ou la limite d'un conteneur, qui n'a
    /// pas de sens ici) ; 0 si le runtime ne la connaît pas, auquel cas la mémoire n'est pas teintée.</summary>
    private static readonly long TotalMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;

    /// <summary>Un débit de 1 Ko/s est le pied de l'échelle, 100 Mo/s son sommet : cinq décades.</summary>
    private static readonly double IoDecades = Math.Log10(100d * 1024);

    /// <summary>Au-delà de mille threads, une ligne est déjà au maximum.</summary>
    private static readonly double ThreadDecades = Math.Log10(1000);

    /// <summary>De 0 à 1, null si la valeur est absente (la cellule reste sans fond).</summary>
    public double? CpuIntensity => CpuPercent is { } percent ? Math.Sqrt(Math.Clamp(percent, 0, 100) / 100) : null;

    public double? MemoryIntensity => MemoryBytes is { } bytes && TotalMemoryBytes > 0
        ? Math.Sqrt(Math.Clamp((double)bytes / TotalMemoryBytes, 0, 1))
        : null;

    public double? IoIntensity => IoBytesPerSecond is { } rate && rate >= 1
        ? Math.Clamp(Math.Log10(rate / 1024d) / IoDecades, 0, 1)
        : null;

    public double? ThreadIntensity => ThreadCount > 0
        ? Math.Clamp(Math.Log10(ThreadCount) / ThreadDecades, 0, 1)
        : null;

    public string MemoryDisplay => MemoryBytes is { } bytes ? ByteFormatter.Format(bytes) : "--";

    public string IoDisplay => IoBytesPerSecond is { } rate && rate >= 1 ? ByteFormatter.FormatRate(rate) : "--";

    public string ThreadsDisplay => ThreadCount > 0 ? ThreadCount.ToString(CultureInfo.CurrentCulture) : "--";

    /// <summary>Un agrégat n'a pas de PID : il en totalise plusieurs. « -- » comme toute valeur absente,
    /// plutôt qu'un zéro qui désignerait le processus Idle.</summary>
    public string PidDisplay => IsAggregate ? "--" : Pid.ToString(CultureInfo.CurrentCulture);

    public string StartTimeDisplay => Identity.StartTimeUtc is { } start
        ? start.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.CurrentCulture)
        : "--";

    public string PathDisplay => ExecutablePath ?? (IsAccessible ? "Chemin non lisible" : "Processus protégé par Windows");

    /// <summary>Met en forme les points des mini-courbes du panneau de détail, pour que le repère posé au
    /// clic affiche la valeur dans la même unité que la colonne correspondante — un pourcentage d'un côté,
    /// des octets de l'autre — et non un double brut.</summary>
    public Func<double, string> FormatCpu { get; } =
        value => value.ToString("0.0", CultureInfo.CurrentCulture) + " %";

    public Func<double, string> FormatMemory { get; } = value => ByteFormatter.Format((long)value);

    public bool CanOpenLocation => ExecutablePath is { Length: > 0 };

    public ProcessRowViewModel(ProcessesViewModel owner, ProcessInfo info)
    {
        Owner = owner;
        Identity = info.Identity;
        Pid = info.Pid;
        Name = info.Name;
        Apply(info);
    }

    /// <summary>Ligne d'agrégat. Elle n'a pas d'instantané à recopier : ses valeurs sont les totaux de ses
    /// membres, posés par <see cref="ApplyAggregate"/> à chaque relevé.</summary>
    private ProcessRowViewModel(ProcessesViewModel owner, string groupKey, string name)
    {
        Owner = owner;
        IsAggregate = true;
        GroupKey = groupKey;
        Pid = 0;
        Name = name;
        DisplayName = name;
    }

    public static ProcessRowViewModel CreateAggregate(ProcessesViewModel owner, string groupKey, string name)
        => new(owner, groupKey, name);

    public void Apply(ProcessInfo info)
    {
        Identity = info.Identity;
        DisplayName = string.IsNullOrWhiteSpace(info.Description) ? info.Name : info.Description!;
        ExecutablePath = info.ExecutablePath;
        Publisher = info.Publisher;
        UserName = info.UserName;
        WindowTitle = info.WindowTitle;
        Kind = info.Kind;
        ParentPid = info.ParentPid;
        ThreadCount = info.ThreadCount;
        IsAccessible = info.IsAccessible;
        IsCritical = info.IsCritical;

        CpuPercent = info.CpuPercent;
        MemoryBytes = info.PrivateWorkingSetBytes ?? info.WorkingSetBytes;
        IoBytesPerSecond = info.IoBytesPerSecond;

        CpuHistory.Push(info.CpuPercent);
        MemoryHistory.Push(MemoryBytes);

        // Recalculée à chaque relevé : le chemin de l'exécutable et le compte propriétaire ne sont pas
        // toujours lisibles à la première apparition d'un processus, et une ligne qui les obtient au relevé
        // suivant doit rejoindre son groupe.
        GroupKey = string.Concat(
            info.ExecutablePath?.ToLowerInvariant() ?? info.Name.ToLowerInvariant(), "\u0000", info.UserName ?? "");

        if (IsGone) IsGone = false;
    }

    /// <summary>
    /// Totalise les membres du groupe, comme le fait l'onglet « Processus » du Gestionnaire des tâches : une
    /// application dont les vingt processus consomment chacun 0,3 % affiche bien 6 %, et non vingt lignes qui
    /// semblent ne rien faire.
    ///
    /// Une mesure absente ne compte pas pour zéro : si AUCUN membre ne l'a fournie, le total reste null et la
    /// cellule affiche « -- ». C'est le cas au tout premier relevé, où aucun %CPU n'est encore calculable.
    /// </summary>
    public void ApplyAggregate(List<ProcessRowViewModel> members)
    {
        MemberCount = members.Count;
        // Le nom vient du membre le plus parlant : celui qui a une fenêtre, sinon le premier. Deux processus
        // du même exécutable portent la même description, mais seul celui qui a une fenêtre porte un titre.
        ProcessRowViewModel head = members.FirstOrDefault(m => m.Kind == ProcessKind.Application) ?? members[0];

        DisplayName = head.DisplayName;
        ExecutablePath = head.ExecutablePath;
        Publisher = head.Publisher;
        UserName = head.UserName;
        WindowTitle = head.WindowTitle;
        IsAccessible = members.Any(m => m.IsAccessible);
        IsCritical = members.Any(m => m.IsCritical);

        // Un groupe est une « Application » dès qu'un de ses processus a une fenêtre, et un composant de
        // Windows seulement si tous le sont : c'est ce qui range un navigateur entier sous « Applications ».
        Kind = members.Any(m => m.Kind == ProcessKind.Application) ? ProcessKind.Application
            : members.All(m => m.Kind == ProcessKind.Windows) ? ProcessKind.Windows
            : ProcessKind.Background;

        CpuPercent = SumOrNull(members, m => m.CpuPercent);
        MemoryBytes = SumOrNull(members, m => (double?)m.MemoryBytes) is { } bytes ? (long)bytes : null;
        IoBytesPerSecond = SumOrNull(members, m => m.IoBytesPerSecond);
        ThreadCount = members.Sum(m => m.ThreadCount);

        // La plus ancienne : c'est le démarrage de l'application, pas celui de son dernier processus enfant.
        DateTime? started = null;
        foreach (ProcessRowViewModel member in members)
        {
            if (member.Identity.StartTimeUtc is { } time && (started is null || time < started)) started = time;
        }
        Identity = new ProcessIdentity(0, started);
        OnPropertyChanged(nameof(StartTimeDisplay));

        CpuHistory.Push(CpuPercent);
        MemoryHistory.Push(MemoryBytes);
    }

    private static double? SumOrNull(List<ProcessRowViewModel> members, Func<ProcessRowViewModel, double?> get)
    {
        double? total = null;
        foreach (ProcessRowViewModel member in members)
        {
            if (get(member) is { } value) total = (total ?? 0) + value;
        }
        return total;
    }

    /// <summary>Instant (TickCount64) où le processus a été constaté disparu : le sursis de la ligne grisée
    /// se compte à partir de là.</summary>
    public long GoneSinceTick { get; private set; }

    /// <summary>Le processus vient de disparaître : la ligne reste, vidée de ses mesures.</summary>
    public void MarkGone(long tick)
    {
        if (IsGone) return;

        IsGone = true;
        GoneSinceTick = tick;
        CpuPercent = null;
        // Vidée comme les autres : sans cela, la ligne et le panneau de détail continuaient d'annoncer
        // « 1,4 Go » pour un processus qui n'existe plus, juste au-dessus d'une mini-courbe tombée à vide,
        // et un tri par mémoire classait ce fantôme parmi les plus gros consommateurs.
        MemoryBytes = null;
        IoBytesPerSecond = null;
        // Une ligne morte ne reste pas sélectionnée : le compteur et le bouton rouge « Terminer » resteraient
        // actifs alors que l'action ne ferait plus rien du tout, sans le moindre message.
        IsSelected = false;
        CpuHistory.Push(null);
        MemoryHistory.Push(null);
    }

    public ProcessInfo ToInfo() => new()
    {
        Identity = Identity,
        Name = Name,
        Description = DisplayName,
        ExecutablePath = ExecutablePath,
        Kind = Kind,
        IsCritical = IsCritical,
        IsAccessible = IsAccessible,
    };

    partial void OnKindChanged(ProcessKind value)
    {
        OnPropertyChanged(nameof(KindLabel));
        NotifyGroupingChanged();
    }

    partial void OnParentChanged(ProcessRowViewModel? value) => NotifyGroupingChanged();

    /// <summary>La famille affichée d'une ligne dépend aussi de son agrégat : quand l'un ou l'autre change,
    /// l'en-tête de groupe et le rang de tri doivent être relus. Appelé aussi depuis l'onglet, après qu'un
    /// agrégat a recalculé sa propre famille, pour ses membres.</summary>
    internal void NotifyGroupingChanged()
    {
        OnPropertyChanged(nameof(EffectiveKind));
        OnPropertyChanged(nameof(GroupLabel));
        OnPropertyChanged(nameof(GroupRank));
    }

    partial void OnCpuPercentChanged(double? value)
    {
        OnPropertyChanged(nameof(CpuDisplay));
        OnPropertyChanged(nameof(CpuIntensity));
    }

    partial void OnMemoryBytesChanged(long? value)
    {
        OnPropertyChanged(nameof(MemoryDisplay));
        OnPropertyChanged(nameof(MemoryIntensity));
    }

    partial void OnIoBytesPerSecondChanged(double? value)
    {
        OnPropertyChanged(nameof(IoDisplay));
        OnPropertyChanged(nameof(IoIntensity));
    }

    partial void OnThreadCountChanged(int value)
    {
        OnPropertyChanged(nameof(ThreadsDisplay));
        OnPropertyChanged(nameof(ThreadIntensity));
    }
    partial void OnIsSelectedChanged(bool value) => Owner.OnRowSelectionChanged();

    partial void OnExecutablePathChanged(string? value)
    {
        OnPropertyChanged(nameof(PathDisplay));
        OnPropertyChanged(nameof(CanOpenLocation));
        TerminateCommand.NotifyCanExecuteChanged();
        OpenLocationCommand.NotifyCanExecuteChanged();
        ShowPropertiesCommand.NotifyCanExecuteChanged();
        LoadIcon(value);
    }

    /// <summary>Va chercher l'icône du fichier. Le chemin n'est résolu qu'une fois par processus, donc
    /// cette méthode n'est appelée qu'une fois par ligne ; ShellIcons met de plus en cache par chemin, et
    /// les dizaines de processus qui partagent un même exécutable n'en coûtent qu'une lecture.
    /// « async void » assumé : c'est une réaction à un changement de propriété, il n'y a personne pour
    /// attendre le résultat, et ShellIcons ne lève pas.</summary>
    private async void LoadIcon(string? path)
    {
        if (path is not { Length: > 0 })
        {
            Icon = null;
            return;
        }

        ImageSource? loaded = await ShellIcons.GetAsync(path);

        // La ligne a pu être réaffectée à un autre processus pendant l'attente (les lignes sont réutilisées
        // en place) : on ne pose l'icône que si le chemin est toujours celui qu'on a demandé.
        if (ExecutablePath == path) Icon = loaded;
    }

    partial void OnIsExpandedChanged(bool value) => Owner.OnGroupExpansionChanged();

    [RelayCommand]
    private void ToggleExpand()
    {
        if (IsAggregate) IsExpanded = !IsExpanded;
    }

    [RelayCommand]
    private Task TerminateAsync() => Owner.TerminateAsync(this);

    [RelayCommand(CanExecute = nameof(CanOpenLocation))]
    private void OpenLocation() => Owner.OpenLocation(this);

    [RelayCommand(CanExecute = nameof(CanOpenLocation))]
    private void ShowProperties() => Owner.ShowProperties(this);

    [RelayCommand]
    private void CopyName() => Owner.CopyToClipboard(Name, "Nom copié");

    [RelayCommand]
    private void CopyPath() => Owner.CopyToClipboard(ExecutablePath ?? Name, "Chemin copié");

    [RelayCommand]
    private void CopyPid() => Owner.CopyToClipboard(Pid.ToString(CultureInfo.InvariantCulture), "PID copié");
}

/// <summary>
/// Les lignes de la liste, avec en plus un remplacement en bloc. Le réordonnancement normal se fait à coups
/// de Move, un par ligne déplacée, ce qui préserve sélection et défilement ; mais inverser le tri met toutes
/// les lignes hors de leur place, et quatre cents notifications traitées une par une par la vue filtrée puis
/// par le panneau virtualisé se voient à l'écran. <see cref="ReplaceAll"/> n'en émet qu'une seule.
/// </summary>
public sealed class ProcessRowCollection : ObservableCollection<ProcessRowViewModel>
{
    /// <summary>Remplace tout le contenu en une seule notification Reset. Le défilement repart du haut :
    /// c'est le prix d'un Reset, et il ne se paie que sur une action volontaire de l'utilisateur (clic sur
    /// un en-tête, dégel, changement de colonnes), jamais au fil des relevés.</summary>
    public void ReplaceAll(IReadOnlyList<ProcessRowViewModel> ordered)
    {
        Items.Clear();
        foreach (ProcessRowViewModel row in ordered) Items.Add(row);

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}

/// <summary>
/// Onglet « Processus » : la liste de ce qui tourne, en plus lisible que le Gestionnaire des tâches. Le
/// classement suit toujours les valeurs affichées, relevé après relevé : c'est ce que l'utilisateur lit, donc
/// c'est ce qui doit être trié. Seul le bouton « Figer » l'arrête. Ce qui protège encore le clic, c'est que
/// les lignes n'entrent et ne sortent pas de la liste tant que le pointeur est dessus (voir
/// <c>UpdateFilterMembership</c> et <c>RemoveVanishedRows</c>).
/// </summary>
public sealed partial class ProcessesViewModel : ObservableObject, IDisposable
{
    private const string DialogTitle = "PCPerfSuite";

    /// <summary>Cadence de repli quand aucun réglage n'a encore été restauré.</summary>
    private const int DefaultRefreshMs = 2000;

    /// <summary>Durée pendant laquelle la ligne d'un processus disparu reste affichée en grisé alors que la
    /// liste est visée. Le but est que rien ne bouge à l'instant du clic, pas de garder des lignes mortes :
    /// sur une session Windows ordinaire, des dizaines de processus se terminent chaque minute, et sans
    /// échéance la liste enflerait indéfiniment tant que le pointeur reste dessus.</summary>
    private const int PlaceholderLifetimeMs = 5000;

    /// <summary>Au-delà de ce nombre de lignes hors de leur place, un reclassement demandé explicitement
    /// remplace la collection en bloc plutôt que de la remettre en ordre ligne à ligne.</summary>
    private const int BulkReorderThreshold = 64;

    private readonly ProcessService _service = new();
    private readonly MonitoringViewModel _monitoring;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<ProcessIdentity, ProcessRowViewModel> _byIdentity = new();

    /// <summary>Les lignes d'agrégat, par clé de groupe. Elles ne sont jamais recréées tant que leur
    /// application tourne : leur historique de mini-courbes et leur sélection survivent aux relevés.</summary>
    private readonly Dictionary<string, ProcessRowViewModel> _aggregates = new(StringComparer.Ordinal);

    /// <summary>Tampon réutilisé par la reconstruction des agrégats : un dictionnaire neuf à chaque relevé
    /// coûterait quatre cents allocations toutes les deux secondes, pour rien.</summary>
    private readonly Dictionary<string, List<ProcessRowViewModel>> _byGroupKey = new(StringComparer.Ordinal);

    private readonly HashSet<ProcessIdentity> _seen = new();
    private readonly List<ProcessRowViewModel> _ordered = new();
    private readonly ListCollectionView _rowsView;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;

    private bool _orderQueued;
    private bool _orderQueuedForce;

    /// <summary>Tous les groupes sont en train d'être dépliés d'un coup : chaque ligne ne doit pas déclencher
    /// sa propre réévaluation du filtre, l'appelant s'en charge une seule fois à la fin.</summary>
    private bool _bulkExpanding;

    /// <summary>L'onglet est fermé : ce qui traîne encore dans la file du dispatcher ne doit plus toucher à
    /// la liste ni au service.</summary>
    private bool _disposed;

    private bool _isRefreshing;
    private bool _initialized;

    /// <summary>Le message affiché vient d'un relevé en échec, pas d'une action de l'utilisateur.</summary>
    private bool _readErrorShown;

    public ProcessRowCollection Rows { get; } = new();
    public ICollectionView RowsView => _rowsView;
    public ProcessColumnsViewModel Columns { get; } = new();

    /// <summary>Cadences proposées. 0,5 s sert à attraper une pointe courte : un relevé complet coûte
    /// déjà plusieurs dizaines de millisecondes (voir <see cref="ReadDurationHint"/>), ce n'est pas une
    /// cadence à laisser tourner en fond.</summary>
    public IReadOnlyList<ProcessRefreshOption> RefreshOptions { get; } = new[]
    {
        new ProcessRefreshOption(500, "0,5 s"),
        new ProcessRefreshOption(1000, "1 s"),
        new ProcessRefreshOption(2000, "2 s"),
        new ProcessRefreshOption(5000, "5 s"),
        new ProcessRefreshOption(10000, "10 s"),
    };

    public IReadOnlyList<ProcessKindOption> KindOptions { get; } = new[]
    {
        new ProcessKindOption(ProcessKindFilter.All, "Tout"),
        new ProcessKindOption(ProcessKindFilter.Applications, "Applications"),
        new ProcessKindOption(ProcessKindFilter.Background, "Arrière-plan"),
        new ProcessKindOption(ProcessKindFilter.Windows, "Windows"),
    };

    [ObservableProperty] private string searchText = "";
    [ObservableProperty] private ProcessRefreshOption? selectedRefresh;
    [ObservableProperty] private ProcessKindOption? selectedKind;
    [ObservableProperty] private bool isFrozen;
    [ObservableProperty] private bool isChoosingColumns;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDetailVisible))]
    private bool showDetails;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDetailVisible))]
    private ProcessRowViewModel? selectedRow;

    /// <summary>Le panneau de détail s'affiche quand il a quelque chose à montrer ET que l'utilisateur le
    /// veut. La visibilité est calculée ici, et surtout pas dans la vue : la carte portait sa liaison de
    /// visibilité sur l'élément où elle posait aussi son DataContext, si bien que WPF cherchait
    /// « SelectedRow » sur la ligne sélectionnée — où cette propriété n'existe pas.</summary>
    public bool IsDetailVisible => ShowDetails && SelectedRow is not null;

    /// <summary>Facteur d'échelle du %CPU et sa provenance, repris tels quels du dernier relevé pour la ligne
    /// « Mesure du %CPU par processus » du diagnostic.</summary>
    public double CpuScaleFactor { get; private set; } = 1.0;

    public CpuScaleSource CpuScaleSource { get; private set; }
    [ObservableProperty] private string? statusText;
    [ObservableProperty] private string headerSummary = "";
    [ObservableProperty] private string? frozenAtDisplay;
    [ObservableProperty] private int totalCount;
    [ObservableProperty] private int visibleCount;
    [ObservableProperty] private int selectionCount;
    [ObservableProperty] private bool isTerminating;

    private bool _isPointerOverList;
    private bool _isListFocused;

    /// <summary>Posé par la vue : tant que le pointeur est sur la liste, l'ordre ne bouge plus.</summary>
    public bool IsPointerOverList
    {
        get => _isPointerOverList;
        set
        {
            if (_isPointerOverList == value) return;

            _isPointerOverList = value;
            OnListBusyChanged();
        }
    }

    public bool IsListFocused
    {
        get => _isListFocused;
        set
        {
            if (_isListFocused == value) return;

            _isListFocused = value;
            OnListBusyChanged();
        }
    }

    /// <summary>Vrai quand la liste est visée, à la souris ou au clavier : rien ne doit alors changer de
    /// place sous le curseur. La cible d'un menu contextuel ouvert, elle, est protégée autrement — la vue la
    /// fige à l'ouverture — plutôt qu'en gelant toute la liste sur un drapeau qu'un clic droit hors d'une
    /// ligne laisserait levé pour le reste de la session.</summary>
    private bool IsListBusy => IsPointerOverList || IsListFocused;

    public string SortColumnId { get; private set; } = "cpu";
    public bool SortDescending { get; private set; } = true;

    public string TerminateSelectionHeader => SelectionCount > 1
        ? $"Terminer les {SelectionCount} processus sélectionnés…"
        : "Terminer la sélection…";

    public string ReadDurationHint { get; private set; } = "Durée du dernier relevé : --";

    /// <summary>Le relevé ne tourne que quand l'onglet est affiché. Contrairement au Monitoring, dont
    /// l'overlay a besoin en permanence, une liste de processus cachée ne sert à personne et son relevé
    /// coûte bien plus cher qu'une lecture de capteurs.</summary>
    private bool _isActive;

    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value) return;

            _isActive = value;
            if (value)
            {
                _ = ResumeAsync();
            }
            else
            {
                _timer.Stop();
            }
        }
    }

    public ProcessesViewModel(MonitoringViewModel monitoring)
    {
        _monitoring = monitoring;
        _monitoring.SnapshotUpdated += OnHardwareSnapshot;

        // Le timer est créé AVANT la restauration des réglages, et non après : restaurer SelectedRefresh
        // déclenche OnSelectedRefreshChanged, qui règle l'intervalle du timer. Construit après, le champ
        // valait encore null à cet instant et la fenêtre principale ne s'ouvrait jamais.
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(DefaultRefreshMs),
        };
        _timer.Tick += async (_, _) => await RefreshAsync();

        AppSettings settings = AppSettingsStore.Load();
        ProcessesSettings saved = settings.Processes;

        SortColumnId = Columns.ById(saved.SortColumnId) is null ? "cpu" : saved.SortColumnId;
        SortDescending = saved.SortDescending;
        Columns.ApplyVisibility(saved.VisibleColumnIds);
        Columns.SetVisibilityCallback(OnColumnVisibilityChanged);
        ShowDetails = saved.ShowDetails;
        SelectedKind = KindOptions.FirstOrDefault(k => KindKey(k.Value) == saved.KindFilter) ?? KindOptions[0];
        // Repli cherché par sa valeur et non par son indice : insérer une cadence dans la liste
        // décalerait un indice et changerait le défaut sans qu'on s'en aperçoive.
        SelectedRefresh = RefreshOptions.FirstOrDefault(r => r.Ms == saved.RefreshMs)
            ?? RefreshOptions.First(r => r.Ms == DefaultRefreshMs);
        UpdateSortGlyphs();

        _rowsView = (ListCollectionView)CollectionViewSource.GetDefaultView(Rows);
        // Pas de SortDescriptions : le tri est appliqué à la source par des Move, ce qui préserve la
        // sélection et le défilement, là où un tri de vue relance une reconstruction complète.
        //
        // Le filtre ne RECALCULE rien : il lit une appartenance que le ViewModel a décidée. C'est ce qui
        // permet de choisir le moment où une ligne entre ou sort — voir UpdateFilterMembership.
        _rowsView.Filter = o => o is ProcessRowViewModel row && row.MatchesFilter;

        // Mise en forme dynamique sur cette seule propriété : quand une ligne change d'appartenance, la vue
        // l'ajoute ou la retire par une notification ciblée, au lieu du Reset qu'imposerait un Refresh() —
        // Reset qui détruit les conteneurs et renvoie le défilement en haut. Vérifié à l'exécution, sur les
        // conteneurs réellement rendus : retrait et réintégration se font bien par Add/Remove, jamais par un
        // Reset, et le rattrapage d'un lot de changements accumulés est exact.
        _rowsView.LiveFilteringProperties.Add(nameof(ProcessRowViewModel.MatchesFilter));
        _rowsView.IsLiveFiltering = true;

        ApplyGrouping();

        _initialized = true;
    }

    // ----- Relevé -----

    /// <summary>Reprend les relevés après une absence (changement d'onglet) ou un gel. Les compteurs
    /// d'avant l'interruption sont abandonnés : un écart rapporté au temps écoulé n'aurait plus aucun sens,
    /// et le premier relevé affiche « -- ».
    ///
    /// L'abandon passe par le pool, jamais par le thread d'interface : il prend le verrou du service, donc
    /// il attend la fin d'un relevé en vol — quatre cents processus, une lecture de ressources de version
    /// par exécutable inconnu, et une traduction de SID qui peut interroger un contrôleur de domaine. Appelé
    /// directement, il figeait la fenêtre entière le temps que tout cela se termine.</summary>
    private async Task ResumeAsync()
    {
        await Task.Run(_service.ResetCounters);

        // L'utilisateur a pu repartir, figer, ou fermer la fenêtre pendant l'attente.
        if (_disposed || !IsActive || IsFrozen) return;

        _timer.Start();
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        // Même garde que le Monitoring : si le relevé précédent n'est pas terminé, on saute ce tick plutôt
        // que d'empiler des énumérations concurrentes que l'interface ne rattraperait jamais.
        if (_isRefreshing || _disposed) return;

        _isRefreshing = true;
        try
        {
            ProcessSnapshot snapshot = await Task.Run(_service.GetSnapshot);
            Apply(snapshot);

            // Seul le message d'échec d'un relevé précédent s'efface ici. Une terminaison se termine par un
            // rafraîchissement : effacer sans distinction faisait disparaître son compte rendu quelques
            // dizaines de millisecondes après l'avoir posé, avant que l'utilisateur ait pu le lire — et pour
            // un processus déjà mort, ce message est le seul retour qu'il reçoit.
            if (_readErrorShown)
            {
                StatusText = null;
                _readErrorShown = false;
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Lecture des processus impossible : {ex.Message}";
            _readErrorShown = true;
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private void Apply(ProcessSnapshot snapshot)
    {
        if (IsFrozen) return;

        Columns.Io.IsAvailable = snapshot.Capabilities.HasIoRate || snapshot.IsFirstSample;

        _seen.Clear();
        foreach (ProcessInfo info in snapshot.Processes)
        {
            _seen.Add(info.Identity);

            if (_byIdentity.TryGetValue(info.Identity, out ProcessRowViewModel? row))
            {
                row.Apply(info);
                continue;
            }

            row = new ProcessRowViewModel(this, info);
            // Décidée AVANT l'ajout : la vue applique le filtre au moment où la ligne entre dans la
            // collection, et une ligne hors filtre ne doit jamais apparaître, même le temps d'un tour.
            row.MatchesFilter = PassesFilter(row);
            _byIdentity[info.Identity] = row;

            // Insérer à son rang décale vers le bas tout ce qui suit. Sous le curseur, la ligne visée se
            // déroberait donc au moment même du clic — c'est précisément le défaut que cet onglet corrige.
            // Ajoutée en queue, elle ne pousse personne ; le prochain reclassement la remettra à sa place.
            if (IsListBusy) Rows.Add(row);
            else Rows.Insert(FindInsertIndex(row), row);
        }

        RemoveVanishedRows();
        RebuildAggregates();

        CpuScaleFactor = snapshot.CpuScaleFactor;
        CpuScaleSource = snapshot.CpuScaleSource;
        TotalCount = snapshot.Processes.Count;
        ReadDurationHint = $"Durée du dernier relevé : {snapshot.ReadDuration.TotalMilliseconds:0} ms"
                           + (snapshot.InaccessibleCount > 0
                               ? $" · {snapshot.InaccessibleCount} processus protégés par Windows (mesures vides)"
                               : "");
        OnPropertyChanged(nameof(ReadDurationHint));

        // Reclassement, sélection et compteurs sont remis en file : ils doivent tous passer APRÈS la remise
        // en forme de la vue, que les changements de propriétés ci-dessus viennent de déclencher.
        QueueOrder();
    }

    /// <summary>Retire les lignes dont le processus a disparu — mais pas sous le curseur : tant que le
    /// pointeur est sur la liste, elles restent en grisé pour que rien ne bouge sous la souris.</summary>
    private void RemoveVanishedRows()
    {
        bool keepPlaceholders = IsListBusy;
        long now = Environment.TickCount64;
        bool removedSelected = false;

        for (int i = Rows.Count - 1; i >= 0; i--)
        {
            ProcessRowViewModel row = Rows[i];
            // Un agrégat ne correspond à aucun processus : sa disparition se décide sur le nombre de ses
            // membres, dans RemoveUnusedAggregates, et surtout pas ici.
            if (row.IsAggregate) continue;
            if (_seen.Contains(row.Identity)) continue;

            if (keepPlaceholders)
            {
                row.MarkGone(now);
                // Le sursis est borné : passé le délai, la ligne part même si la liste est toujours visée.
                if (now - row.GoneSinceTick < PlaceholderLifetimeMs) continue;
            }

            _byIdentity.Remove(row.Identity);
            if (ReferenceEquals(SelectedRow, row)) SelectedRow = null;
            if (row.IsSelected) removedSelected = true;
            Rows.RemoveAt(i);
        }

        // Une ligne retirée emporte sa sélection sans que personne ne le signale : le compteur et le bouton
        // « Terminer » resteraient actifs pour une ligne que plus rien n'affiche.
        if (removedSelected) OnRowSelectionChanged();
    }

    // ----- Regroupement par application -----

    /// <summary>Un groupe ne vaut que s'il rassemble plusieurs processus : une application qui n'en a qu'un
    /// reste une ligne ordinaire, sans chevron ni ligne d'en-tête inutile.</summary>
    private const int MinimumGroupSize = 2;

    /// <summary>
    /// Refait le regroupement par application : quels processus partagent un exécutable et un compte, et
    /// quels totaux afficher pour chacun. Appelé à chaque relevé, après que toutes les lignes ont reçu leurs
    /// valeurs — les totaux n'auraient aucun sens calculés sur des mesures à moitié à jour.
    ///
    /// Les lignes d'agrégat entrent et sortent de la collection comme les autres : jamais à leur rang quand
    /// la liste est visée, pour ne pas décaler ce qui est sous le curseur.
    /// </summary>
    private void RebuildAggregates()
    {
        foreach (List<ProcessRowViewModel> members in _byGroupKey.Values) members.Clear();

        foreach (ProcessRowViewModel row in Rows)
        {
            // Un processus disparu ne compte plus dans les totaux : sa ligne grisée n'a plus de mesures,
            // et le faire peser dans la somme ferait mentir l'agrégat pendant tout son sursis.
            if (row.IsAggregate || row.IsGone) continue;

            if (!_byGroupKey.TryGetValue(row.GroupKey, out List<ProcessRowViewModel>? members))
            {
                members = new List<ProcessRowViewModel>();
                _byGroupKey[row.GroupKey] = members;
            }
            members.Add(row);
        }

        foreach ((string key, List<ProcessRowViewModel> members) in _byGroupKey)
        {
            if (members.Count < MinimumGroupSize)
            {
                foreach (ProcessRowViewModel member in members) member.Parent = null;
                continue;
            }

            bool isNew = !_aggregates.TryGetValue(key, out ProcessRowViewModel? aggregate);
            if (isNew)
            {
                aggregate = ProcessRowViewModel.CreateAggregate(this, key, members[0].Name);
                _aggregates[key] = aggregate;
            }

            aggregate!.ApplyAggregate(members);

            // Le rattachement précède l'insertion : c'est lui qui donne aux membres leur clé de classement,
            // et un agrégat inséré avant que ses membres le connaissent atterrirait loin d'eux, le temps que
            // le reclassement suivant — jusqu'à trois secondes plus tard — les réunisse.
            foreach (ProcessRowViewModel member in members)
            {
                member.Parent = aggregate;
                // La famille de l'agrégat vient d'être recalculée : celle de ses membres en découle.
                member.NotifyGroupingChanged();
            }

            if (!isNew) continue;

            // Décidée AVANT l'ajout, comme pour une ligne de processus : une ligne hors filtre ne doit
            // jamais apparaître, même le temps d'un tour.
            aggregate.MatchesFilter = PassesFilter(aggregate);

            if (IsListBusy) Rows.Add(aggregate);
            else Rows.Insert(FindInsertIndex(aggregate), aggregate);

            // Un groupe naît replié : ses membres, eux, ont été ajoutés avant qu'on sache qu'ils appartenaient à
            // un groupe, donc visibles. Sans cette ligne ils resteraient affichés jusqu'au reclassement suivant
            // — une image entière d'une liste dépliée, à l'ouverture de l'onglet. Pas quand la liste est visée :
            // rien ne doit alors sortir de sous le curseur, c'est au relâchement que tout se range.
            if (!IsListBusy)
            {
                foreach (ProcessRowViewModel member in members) member.MatchesFilter = PassesFilter(member);
            }
        }

        RemoveUnusedAggregates();
    }

    /// <summary>Retire les agrégats dont l'application ne tourne plus, ou qui sont retombés à un seul
    /// processus. Rien n'est retiré tant que la liste est visée : leurs lignes sont déjà hors du filtre, donc
    /// invisibles, mais l'appartenance au filtre n'est réévaluée qu'une fois le pointeur ressorti.</summary>
    private void RemoveUnusedAggregates()
    {
        if (IsListBusy) return;

        bool removedSelected = false;
        var removed = new HashSet<ProcessRowViewModel>();

        for (int i = Rows.Count - 1; i >= 0; i--)
        {
            ProcessRowViewModel row = Rows[i];
            if (!row.IsAggregate) continue;
            if (_byGroupKey.TryGetValue(row.GroupKey, out List<ProcessRowViewModel>? members)
                && members.Count >= MinimumGroupSize)
            {
                continue;
            }

            _aggregates.Remove(row.GroupKey);
            removed.Add(row);
            if (ReferenceEquals(SelectedRow, row)) SelectedRow = null;
            if (row.IsSelected) removedSelected = true;
            Rows.RemoveAt(i);
        }

        if (removed.Count > 0)
        {
            // Une ligne grisée n'est pas comptée parmi les membres : elle pointerait sinon sur un agrégat
            // que plus rien n'affiche, et en hériterait une famille et donc un en-tête de groupe fantômes.
            foreach (ProcessRowViewModel row in Rows)
            {
                if (row.Parent is { } parent && removed.Contains(parent)) row.Parent = null;
            }
        }

        if (removedSelected) OnRowSelectionChanged();

        // Les clés mortes s'accumuleraient sinon pendant toute la session : une entrée par exécutable
        // rencontré depuis l'ouverture de l'onglet, liste vide comprise.
        foreach (string key in _byGroupKey.Where(e => e.Value.Count == 0).Select(e => e.Key).ToList())
        {
            _byGroupKey.Remove(key);
        }
    }

    /// <summary>Un groupe vient d'être déplié ou replié : ses membres entrent ou sortent de la vue. Le
    /// changement est volontaire, donc immédiat — c'est l'utilisateur qui vient de cliquer le chevron.</summary>
    internal void OnGroupExpansionChanged()
    {
        if (!_initialized || _bulkExpanding) return;

        UpdateFilterMembership();

        // La mise en forme dynamique traite ces changements d'appartenance en priorité DataBind : réconcilier
        // tout de suite interrogerait une vue qui affiche encore les membres qu'on vient de replier, et la
        // sélection d'un membre replié y survivrait. D'où la même file que le reclassement.
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (!_disposed) ReconcileWithView();
        }));
    }

    // ----- Classement -----

    private Comparison<ProcessRowViewModel> BuildComparison()
    {
        string column = SortColumnId;
        int direction = SortDescending ? -1 : 1;

        return (a, b) =>
        {
            // Le classement porte d'abord sur les GROUPES : un membre est comparé par son agrégat, jamais
            // par lui-même. Sans cela, trier par mémoire éparpillerait les vingt processus d'un navigateur
            // aux quatre coins de la liste, loin de la ligne qui les totalise.
            ProcessRowViewModel aSubject = a.SortSubject;
            ProcessRowViewModel bSubject = b.SortSubject;

            if (!ReferenceEquals(aSubject, bSubject))
            {
                return CompareRows(aSubject, bSubject, column, direction);
            }

            // Même groupe : l'agrégat en tête, puis ses membres classés entre eux.
            if (a.IsAggregate != b.IsAggregate) return a.IsAggregate ? -1 : 1;

            return CompareRows(a, b, column, direction);
        };
    }

    private static int CompareRows(ProcessRowViewModel a, ProcessRowViewModel b, string column, int direction)
    {
        // Tri par nom : les familles d'abord, façon Gestionnaire des tâches. Le rang de famille est
        // comparé hors du sens du tri — inverser le tri doit retourner les noms DANS chaque famille,
        // pas remonter les processus Windows au-dessus des applications.
        if (column == "name" && a.GroupRank != b.GroupRank) return a.GroupRank.CompareTo(b.GroupRank);

        int result = column switch
        {
            "name" => string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase),
            "pid" => a.Pid.CompareTo(b.Pid),
            "kind" => string.Compare(a.KindLabel, b.KindLabel, StringComparison.CurrentCultureIgnoreCase),
            "memory" => CompareNullable(a.MemoryBytes, b.MemoryBytes, direction),
            "io" => CompareNullable(a.IoBytesPerSecond, b.IoBytesPerSecond, direction),
            "threads" => a.ThreadCount.CompareTo(b.ThreadCount),
            "user" => string.Compare(a.UserName, b.UserName, StringComparison.CurrentCultureIgnoreCase),
            "publisher" => string.Compare(a.Publisher, b.Publisher, StringComparison.CurrentCultureIgnoreCase),
            // Le tri suit la valeur affichée : ce que l'utilisateur lit doit être ce qui est trié.
            _ => CompareNullable(a.CpuPercent, b.CpuPercent, direction),
        };

        if (result != 0) return result * direction;

        // Ordre total obligatoire : sans départage, List.Sort permute les ex æquo d'un relevé à l'autre
        // et les lignes sauteraient malgré tout le reste. Vingt chrome.exe à 0 % sont un cas courant.
        // La clé de groupe fait partie du départage : deux agrégats homonymes — la même application sous
        // deux comptes — ont le même nom et le même PID nul, et l'ordre ne serait alors pas total, ce qui
        // laisserait leurs membres s'entremêler.
        result = string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
        if (result != 0) return result;

        result = string.CompareOrdinal(a.GroupKey, b.GroupKey);
        return result != 0 ? result : a.Pid.CompareTo(b.Pid);
    }

    /// <summary>Compare deux valeurs éventuellement absentes en gardant les absentes en dernier, quel que
    /// soit le sens du tri : sinon trier par E/S ferait remonter en tête les trois cents processus inactifs.</summary>
    private static int CompareNullable<T>(T? left, T? right, int direction) where T : struct, IComparable<T>
    {
        if (left is null && right is null) return 0;
        // Le sens est neutralisé pour que l'absence reste en bas dans les deux sens.
        if (left is null) return 1 * direction;
        if (right is null) return -1 * direction;
        return left.Value.CompareTo(right.Value);
    }

    private int FindInsertIndex(ProcessRowViewModel row)
    {
        Comparison<ProcessRowViewModel> comparison = BuildComparison();
        for (int i = 0; i < Rows.Count; i++)
        {
            if (comparison(row, Rows[i]) < 0) return i;
        }
        return Rows.Count;
    }

    /// <summary>Met un reclassement en file au lieu de l'appliquer sur-le-champ, et remet du même coup
    /// sélection et compteur d'aplomb.
    ///
    /// La mise en forme dynamique de la vue ne s'applique pas immédiatement : elle poste son travail sur le
    /// dispatcher, en priorité DataBind. Réordonner la collection par des Move alors qu'une réévaluation de
    /// filtre est encore en attente corrompt la vue — une ligne finit par y figurer DEUX FOIS, l'utilisateur
    /// voit le même processus sur deux lignes, et rien ne le répare jamais. Vérifié : reclassement en ligne,
    /// 36 tirages cassés sur 40 ; reclassement mis en file ici, 0 sur 40.
    ///
    /// La file est en priorité Background, donc après la remise en forme (DataBind) et avant le relevé
    /// suivant, dont le timer est lui aussi en Background.</summary>
    private void QueueOrder(bool force = false)
    {
        _orderQueuedForce |= force;
        if (_orderQueued) return;

        _orderQueued = true;
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            // Rendu avant toute chose, et quoi qu'il arrive ensuite : un drapeau resté levé sur une
            // exception empêcherait définitivement la liste d'être reclassée.
            _orderQueued = false;
            bool forced = _orderQueuedForce;
            _orderQueuedForce = false;

            // L'onglet a pu être fermé entre la mise en file et son exécution.
            if (_disposed) return;

            // L'ordre des trois étapes est celui qui a été validé au banc, et il n'est pas interchangeable.
            // 1. La vue reflète ici les appartenances déclarées au tour précédent : c'est donc le moment où
            //    l'on sait ce qu'elle affiche réellement, et quelles lignes ont cessé d'être visibles.
            ReconcileWithView();

            // 2. Reclasser maintenant, pendant qu'aucune réévaluation n'est en attente. Des Move appliqués
            //    alors qu'une ligne attend d'entrer ou de sortir corrompent la vue — elle finit par afficher
            //    le même processus deux fois, et rien ne le répare jamais.
            ApplyOrder(forced);

            // 3. Déclarer enfin les nouvelles appartenances. La vue les traitera avant le prochain relevé,
            //    puisque ce travail est posté à une priorité supérieure à celle de cette file.
            if (IsListBusy || !UpdateFilterMembership()) return;

            // 4. Remettre sélection et compteur d'aplomb dès que la vue a traité ces changements, sans
            //    attendre le relevé suivant : c'est justement le moment où l'utilisateur, qui vient de
            //    quitter la liste, va cliquer sur « Terminer ». Posté à la même priorité, donc après la
            //    remise en forme ; et sans Move ni changement d'appartenance, donc sans risque pour la vue.
            _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (!_disposed) ReconcileWithView();
            }));
        }));
    }

    /// <summary>Remet la collection dans l'ordre voulu à coups de Move : la liste déplace ses conteneurs au
    /// lieu de les détruire, donc la sélection, le focus et le défilement survivent — ce qu'un Clear suivi
    /// d'Add perdrait tous les trois.</summary>
    private void ApplyOrder(bool force = false)
    {
        // Appelé à chaque relevé, pointeur ou pas sur la liste : le classement doit correspondre aux valeurs
        // qu'on lit au même instant. Seul « Figer » l'arrête.
        if (!force && IsFrozen) return;

        _ordered.Clear();
        _ordered.AddRange(Rows);
        _ordered.Sort(BuildComparison());

        int misplaced = 0;
        for (int i = 0; i < _ordered.Count; i++)
        {
            if (!ReferenceEquals(Rows[i], _ordered[i])) misplaced++;
        }
        if (misplaced == 0) return;

        // Inverser le tri met quasiment toutes les lignes hors de leur place : les remettre une par une
        // émettrait autant de notifications que de lignes, chacune retraitée par la vue filtrée puis par le
        // panneau virtualisé — un à-coup visible sur quatre cents lignes, pour un simple clic d'en-tête. On
        // remplace alors la collection en bloc, en une notification. Réservé aux reclassements demandés :
        // le Reset renvoie le défilement en haut, ce qui se comprend après un clic de l'utilisateur mais
        // serait insupportable au fil des relevés.
        if (force && misplaced > BulkReorderThreshold)
        {
            ProcessRowViewModel? selected = SelectedRow;
            Rows.ReplaceAll(_ordered);

            // La sélection est reposée APRÈS que la vue a traité le Reset, pas juste ici : la vue filtrée
            // diffère ce traitement en priorité DataBind, et remettait donc SelectedItem — donc SelectedRow
            // — à null une fraction de seconde après cette ligne. Le panneau de détail disparaissait alors
            // au moindre clic d'en-tête, sans que rien ne l'explique.
            if (selected is not null)
            {
                _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    if (_disposed || !_rowsView.Contains(selected)) return;
                    SelectedRow = selected;
                }));
            }
            return;
        }

        for (int i = 0; i < _ordered.Count; i++)
        {
            if (ReferenceEquals(Rows[i], _ordered[i])) continue;

            // Les rangs déjà traités sont bons : la ligne cherchée est forcément plus bas.
            int from = IndexOfFrom(_ordered[i], i);
            if (from > i) Rows.Move(from, i);
        }
    }

    private int IndexOfFrom(ProcessRowViewModel row, int start)
    {
        for (int i = start; i < Rows.Count; i++)
        {
            if (ReferenceEquals(Rows[i], row)) return i;
        }
        return start;
    }

    [RelayCommand]
    private void SortBy(string? columnId)
    {
        if (columnId is null || Columns.ById(columnId) is not { } column) return;

        if (SortColumnId == columnId)
        {
            SortDescending = !SortDescending;
        }
        else
        {
            SortColumnId = columnId;
            // Une colonne de nombres part en décroissant : on cherche les gros consommateurs. Une colonne de
            // texte part en croissant, l'ordre alphabétique.
            SortDescending = column.IsNumeric;
        }

        UpdateSortGlyphs();
        ApplyGrouping();
        QueueOrder(force: true);
        Persist();
    }

    /// <summary>
    /// Groupe la liste en Applications / Processus en arrière-plan / Processus Windows, mais seulement au
    /// tri par nom : c'est le seul tri où le regroupement aide à retrouver quelque chose. Trier par CPU ou
    /// par mémoire sert justement à voir les plus gros consommateurs toutes familles confondues, des
    /// en-têtes y couperaient le classement.
    ///
    /// Pas de IsLiveGrouping : un Move rend déjà son groupe à la ligne déplacée, et le rang de groupe étant
    /// le critère de tri principal, un processus qui change de famille devient « mal placé » et se fait
    /// déplacer au réordonnancement suivant. Le regroupement dynamique, lui, ajouterait des notifications
    /// pendant que des Move sont en vol — exactement ce qui provoquait les lignes affichées en double
    /// (voir le commentaire de QueueOrder).
    /// </summary>
    private void ApplyGrouping()
    {
        bool grouped = SortColumnId == "name";
        bool alreadyGrouped = _rowsView.GroupDescriptions.Count > 0;
        if (grouped == alreadyGrouped) return;

        // Changer les groupes provoque un Reset de la vue : on ne le fait qu'au changement de tri, jamais
        // au fil des relevés.
        _rowsView.GroupDescriptions.Clear();
        if (!grouped) return;

        var description = new PropertyGroupDescription(nameof(ProcessRowViewModel.GroupLabel));

        // Les trois en-têtes sont déclarés d'avance, dans l'ordre voulu. Sans ça, une vue WPF crée ses
        // groupes au fil des éléments qu'elle rencontre : l'ordre dépendrait de l'état de la liste au moment
        // où le regroupement est posé, et « Processus en arrière-plan » pouvait passer devant
        // « Applications ». Un groupe resté vide est masqué par le gabarit (voir ProcessesView.xaml).
        foreach (string name in GroupNames) description.GroupNames.Add(name);

        _rowsView.GroupDescriptions.Add(description);
    }

    /// <summary>Les en-têtes de groupe, dans leur ordre d'affichage. Doit rester accordé à
    /// <see cref="ProcessRowViewModel.GroupLabel"/> et <see cref="ProcessRowViewModel.GroupRank"/>.</summary>
    private static readonly string[] GroupNames =
    [
        "Applications",
        "Processus en arrière-plan",
        "Processus Windows",
    ];

    private void UpdateSortGlyphs()
    {
        foreach (ProcessColumnViewModel column in Columns.All)
        {
            column.SortGlyph = column.Id == SortColumnId ? (SortDescending ? "▼" : "▲") : "";
        }
    }

    // ----- Filtre -----

    private bool PassesFilter(ProcessRowViewModel row)
    {
        // Un agrégat retombé sous deux membres quitte la liste par le filtre — le seul chemin qui ne décale
        // rien sous le curseur.
        if (row.IsAggregate && row.MemberCount < MinimumGroupSize) return false;

        ProcessKindFilter kind = SelectedKind?.Value ?? ProcessKindFilter.All;
        // La famille comparée est celle du groupe : filtrer sur « Applications » doit montrer un navigateur
        // entier, processus de rendu compris, et non l'amputer de tout ce qui n'a pas de fenêtre.
        bool kindOk = kind switch
        {
            ProcessKindFilter.Applications => row.EffectiveKind == ProcessKind.Application,
            ProcessKindFilter.Background => row.EffectiveKind == ProcessKind.Background,
            ProcessKindFilter.Windows => row.EffectiveKind == ProcessKind.Windows,
            _ => true,
        };
        if (!kindOk) return false;

        if (SearchText.Length == 0)
        {
            // Sans recherche, les membres d'un groupe replié sont masqués. Une recherche, elle, doit pouvoir
            // trouver un processus où qu'il soit : elle ouvre donc tous les groupes le temps de sa durée.
            return row.Parent is not { IsExpanded: false };
        }

        if (Matches(row)) return true;

        // Un membre trouvé par la recherche remonte avec son en-tête, et un groupe dont le nom correspond
        // montre tous ses processus : c'est ce que fait le Gestionnaire des tâches.
        if (row.Parent is { } parent && Matches(parent)) return true;
        if (row.IsAggregate
            && _byGroupKey.TryGetValue(row.GroupKey, out List<ProcessRowViewModel>? members)
            && members.Any(Matches))
        {
            return true;
        }

        return false;
    }

    private bool Matches(ProcessRowViewModel row)
        => TextSearch.Contains(row.DisplayName, SearchText)
           || TextSearch.Contains(row.Name, SearchText)
           || TextSearch.Contains(row.Publisher, SearchText)
           || TextSearch.Contains(row.ExecutablePath, SearchText)
           || TextSearch.Contains(row.WindowTitle, SearchText)
           || (!row.IsAggregate
               && row.Pid.ToString(CultureInfo.InvariantCulture).Contains(SearchText, StringComparison.Ordinal));

    /// <summary>Groupes qui étaient dépliés avant que la recherche ne déplie tout (clés de groupe). Null hors
    /// recherche. Sert à remettre la liste comme l'utilisateur l'avait laissée quand il efface le champ.</summary>
    private HashSet<string>? _expandedBeforeSearch;

    partial void OnSearchTextChanged(string value)
    {
        // Une recherche doit pouvoir trouver un processus où qu'il soit, y compris dans un groupe replié :
        // elle déplie donc tout, comme le fait le Gestionnaire des tâches. Le chevron annonce ainsi « déplié »
        // au-dessus de membres bien visibles. L'état d'avant est gardé, et rendu à l'effacement du champ :
        // sans cela, une recherche laisserait tous les groupes dépliés pour de bon.
        if (value.Length > 0 && _aggregates.Count > 0)
        {
            _expandedBeforeSearch ??= _aggregates.Values.Where(a => a.IsExpanded).Select(a => a.GroupKey).ToHashSet();
            SetGroupsExpanded(expandedKeys: null);
        }
        else if (value.Length == 0 && _expandedBeforeSearch is { } before)
        {
            _expandedBeforeSearch = null;
            SetGroupsExpanded(before);
        }

        RefreshFilter();
    }

    /// <summary>Règle l'état de tous les groupes d'un coup : chacun ne doit pas déclencher sa propre
    /// réévaluation du filtre, l'appelant s'en charge une seule fois à la fin. <paramref name="expandedKeys"/>
    /// liste les groupes à déplier, les autres sont repliés ; null les déplie tous.</summary>
    private void SetGroupsExpanded(HashSet<string>? expandedKeys)
    {
        _bulkExpanding = true;
        try
        {
            foreach (ProcessRowViewModel aggregate in _aggregates.Values)
            {
                aggregate.IsExpanded = expandedKeys is null || expandedKeys.Contains(aggregate.GroupKey);
            }
        }
        finally
        {
            _bulkExpanding = false;
        }
    }

    partial void OnSelectedKindChanged(ProcessKindOption? value)
    {
        RefreshFilter();
        Persist();
    }

    /// <summary>Refresh() lève un Reset : la liste reconstruit ses conteneurs et le défilement repart en
    /// haut. Réservé aux changements de critère — recherche, filtre de type — qui ne peuvent pas passer par
    /// la mise en forme dynamique, puisqu'ils ne sont pas des propriétés des lignes.</summary>
    private void RefreshFilter()
    {
        if (!_initialized) return;

        // Changer de critère est une action volontaire : elle s'applique tout de suite, même si le pointeur
        // est sur la liste — c'est l'utilisateur qui vient de la demander. Refresh() est synchrone, donc la
        // vue est à jour dès la ligne suivante et tout se réconcilie ici même, sans passer par la file.
        UpdateFilterMembership();
        _rowsView.Refresh();
        ReconcileWithView();
    }

    /// <summary>Aucune ligne absente de la vue ne reste sélectionnée. Une ligne qui quitte la vue perd son
    /// conteneur, et WPF n'a alors plus par où lui réécrire IsSelected : la sélection resterait vraie sur un
    /// processus que plus rien n'affiche — l'entête annoncerait « 3 sélectionnés » avec deux lignes
    /// surlignées, et « Terminer » tuerait le troisième avec les autres.
    ///
    /// La question posée est « la vue l'affiche-t-elle ? », pas « passe-t-elle le filtre ? » : tant que la
    /// liste est visée, une ligne qui ne passe plus le filtre reste volontairement affichée, et elle doit
    /// donc rester sélectionnable.</summary>
    private void SyncSelectionToView()
    {
        foreach (ProcessRowViewModel row in Rows)
        {
            // IsSelected d'abord : la vue n'est interrogée que pour les rares lignes sélectionnées.
            if (row.IsSelected && !_rowsView.Contains(row)) row.IsSelected = false;
        }

        if (SelectedRow is { } selected && !_rowsView.Contains(selected)) SelectedRow = null;
    }

    /// <summary>Déclare quelles lignes appartiennent au filtre. C'est le seul endroit qui fait entrer ou
    /// sortir une ligne de la liste, et il n'est appelé que quand plus rien n'est visé : tant que le
    /// pointeur ou le clavier est sur la liste, une application réduite dans la zone de notification change
    /// bien de type dans sa colonne, mais sa ligne reste en place. Sans cette retenue, tout ce qui est en
    /// dessous remonterait d'une hauteur de ligne au moment du clic — le défaut même que cet onglet corrige,
    /// et celui contre lequel l'insertion, le retrait et le reclassement sont déjà protégés.</summary>
    /// <returns>Vrai si au moins une ligne a changé d'appartenance.</returns>
    private bool UpdateFilterMembership()
    {
        bool changed = false;
        foreach (ProcessRowViewModel row in Rows)
        {
            bool matches = PassesFilter(row);
            if (row.MatchesFilter == matches) continue;

            row.MatchesFilter = matches;
            changed = true;
        }

        return changed;
    }

    /// <summary>Aligne sélection, compteur et entête sur ce que la vue affiche réellement.</summary>
    private void ReconcileWithView()
    {
        SyncSelectionToView();
        // Compté sur la vue et non sur la source : le compteur annonce ce qui est réellement affiché.
        VisibleCount = _rowsView.Count;
        UpdateHeaderSummary();
    }

    /// <summary>La liste vient d'être visée, ou relâchée. Au relâchement, tout ce qui a été retenu pendant
    /// ce temps peut enfin être appliqué — sans attendre le relevé suivant, qui peut être à deux secondes de
    /// là.</summary>
    private void OnListBusyChanged()
    {
        if (!_initialized || IsListBusy) return;

        QueueOrder();
    }

    [RelayCommand]
    private void ClearSearch() => SearchText = "";

    [RelayCommand]
    private void ToggleColumns() => IsChoosingColumns = !IsChoosingColumns;

    partial void OnIsTerminatingChanged(bool value) => TerminateSelectionCommand.NotifyCanExecuteChanged();

    partial void OnSelectedRefreshChanged(ProcessRefreshOption? value)
    {
        if (value is null) return;

        var interval = TimeSpan.FromMilliseconds(value.Ms);
        // Réaffecter Interval relance le décompte du timer : seulement quand la valeur change vraiment.
        if (_timer.Interval != interval) _timer.Interval = interval;
        Persist();
    }

    partial void OnIsFrozenChanged(bool value)
    {
        FrozenAtDisplay = value
            ? $"Affichage figé — valeurs de {DateTime.Now:HH:mm:ss}"
            : null;

        // Figer, c'est arrêter les relevés, pas seulement jeter leur résultat. Sans cela le tick continuait à
        // relever les quelque quatre cents processus — un instantané Toolhelp32, un EnumWindows et trois
        // appels natifs par processus — pour que Apply() mette tout à la poubelle : l'état censé être le
        // moins coûteux de l'onglet coûtait exactement autant que l'état actif.
        if (value)
        {
            _timer.Stop();
            return;
        }

        QueueOrder(force: true);

        // Les compteurs datent d'avant le gel : comme au retour sur l'onglet, on repart de zéro.
        if (IsActive) _ = ResumeAsync();
    }

    partial void OnShowDetailsChanged(bool value) => Persist();

    internal void OnRowSelectionChanged()
    {
        SelectionCount = Rows.Count(r => r.IsSelected);
        OnPropertyChanged(nameof(TerminateSelectionHeader));
        TerminateSelectionCommand.NotifyCanExecuteChanged();
    }

    internal void OnColumnVisibilityChanged()
    {
        // Trier sur une colonne qu'on vient de masquer n'aurait plus de sens visible : on retombe sur le CPU
        // — sauf quand c'est justement le CPU qu'on vient de masquer, auquel cas on se replie sur le nom, la
        // seule colonne qui ne peut pas être masquée. Annoncer le CPU comme repli laissait la liste triée sur
        // une colonne invisible, sans glyphe nulle part, et le message revenait à chaque coche suivante.
        if (Columns.ById(SortColumnId) is { IsVisible: false })
        {
            bool cpuVisible = Columns.Cpu.IsVisible;
            SortColumnId = cpuVisible ? "cpu" : Columns.Name.Id;
            SortDescending = cpuVisible;
            UpdateSortGlyphs();
            QueueOrder(force: true);
            StatusText = cpuVisible
                ? "La colonne de tri a été masquée : le classement est revenu sur le CPU."
                : "La colonne de tri a été masquée : le classement est revenu sur le nom.";
        }

        Persist();
    }

    // ----- Actions -----

    private bool CanTerminateSelection() => SelectionCount > 0 && !IsTerminating;

    [RelayCommand(CanExecute = nameof(CanTerminateSelection))]
    private async Task TerminateSelectionAsync()
    {
        // Seules les lignes affichées : une sélection peut survivre un instant à la sortie de sa ligne du
        // filtre, et l'on ne termine jamais un processus que l'utilisateur ne voit pas.
        List<ProcessRowViewModel> targets = Rows
            .Where(r => r.IsSelected && !r.IsGone && _rowsView.Contains(r))
            .SelectMany(ExpandAggregate)
            .Distinct()
            .ToList();
        if (targets.Count == 0) return;
        if (targets.Count == 1)
        {
            await TerminateAsync(targets[0]);
            return;
        }

        await TerminateManyAsync(targets);
    }

    /// <summary>Termine un lot de processus, garde-fous compris. Partagé par la sélection multiple et par
    /// l'action sur une ligne d'agrégat : le second cas ne doit surtout pas contourner les vérifications du
    /// premier.</summary>
    private async Task TerminateManyAsync(List<ProcessRowViewModel> targets)
    {
        if (IsTerminating) return;

        var refused = new List<string>();
        var allowed = new List<ProcessRowViewModel>();
        foreach (ProcessRowViewModel row in targets)
        {
            if (ProcessTerminationGuard.GetRefusalReason(row.ToInfo()) is { } reason) refused.Add(reason);
            else allowed.Add(row);
        }

        if (allowed.Count == 0)
        {
            ShowMessage("Aucun des processus visés ne peut être terminé :\n\n"
                        + string.Join("\n\n", refused), MessageBoxImage.Information);
            return;
        }

        string names = string.Join("\n", allowed.Take(10).Select(r => $"• {r.DisplayName} (PID {r.Pid})"));
        if (allowed.Count > 10) names += $"\n• … et {allowed.Count - 10} autres";

        // Les avertissements valent aussi pour la sélection multiple. Sans eux, un Ctrl+A suivi de Suppr
        // emportait explorer.exe au milieu du lot — barre des tâches et bureau compris — alors que le même
        // processus, terminé seul par le menu contextuel, l'annonçait noir sur blanc.
        var warnings = allowed
            .Select(r => ProcessTerminationGuard.GetExtraWarning(r.ToInfo()))
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();

        MessageBoxResult answer = ShowMessage(
            $"Terminer {allowed.Count} processus ?\n\n{names}\n\n"
            + "Ils sont arrêtés net : tout travail non enregistré est perdu, et c'est irréversible.\n"
            + (warnings.Count > 0 ? "\n" + string.Join("\n", warnings) + "\n" : "")
            + (refused.Count > 0 ? $"\n{refused.Count} processus seront ignorés (protégés par Windows).\n" : ""),
            MessageBoxImage.Warning, MessageBoxButton.YesNo, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        IsTerminating = true;
        try
        {
            int done = 0, denied = 0, already = 0, other = 0;
            foreach (ProcessRowViewModel row in allowed)
            {
                TerminateResult result = await Task.Run(() => _service.Terminate(row.Identity));
                switch (result.Failure)
                {
                    case TerminateFailure.None: done++; break;
                    case TerminateFailure.AccessDenied: denied++; break;
                    case TerminateFailure.AlreadyExited: already++; break;
                    default: other++; break;
                }
            }

            var parts = new List<string> { $"{done} terminé(s)" };
            if (denied > 0) parts.Add($"{denied} refusé(s) par Windows");
            if (already > 0) parts.Add($"{already} déjà terminé(s)");
            if (other > 0) parts.Add($"{other} en échec");
            if (refused.Count > 0) parts.Add($"{refused.Count} ignoré(s)");
            StatusText = string.Join(", ", parts) + ".";
        }
        finally
        {
            IsTerminating = false;
        }

        await RefreshAsync();
    }

    /// <summary>Les processus réellement visés par une action sur cette ligne : elle-même pour un processus,
    /// tous ses membres pour un agrégat — qui n'est pas un processus et ne peut donc pas être terminé.
    /// Un agrégat replié cache ses membres, mais l'action porte bien sur tous, comme dans le Gestionnaire
    /// des tâches : le nombre est annoncé dans la demande de confirmation.</summary>
    private IEnumerable<ProcessRowViewModel> ExpandAggregate(ProcessRowViewModel row)
    {
        if (!row.IsAggregate) return new[] { row };

        return _byGroupKey.TryGetValue(row.GroupKey, out List<ProcessRowViewModel>? members)
            ? members.Where(m => !m.IsGone).ToList()
            : Array.Empty<ProcessRowViewModel>();
    }

    internal async Task TerminateAsync(ProcessRowViewModel row)
    {
        if (IsTerminating) return;

        // Un agrégat n'a pas de PID à terminer : l'action se répercute sur ses membres, par le même chemin
        // que la sélection multiple — garde-fous, avertissements et revérification d'identité compris.
        if (row.IsAggregate)
        {
            List<ProcessRowViewModel> members = ExpandAggregate(row).ToList();
            if (members.Count == 0) return;
            if (members.Count == 1)
            {
                await TerminateAsync(members[0]);
                return;
            }

            await TerminateManyAsync(members);
            return;
        }

        ProcessInfo info = row.ToInfo();
        if (ProcessTerminationGuard.GetRefusalReason(info) is { } refusal)
        {
            ShowMessage(refusal, MessageBoxImage.Information);
            return;
        }

        string warning = ProcessTerminationGuard.GetExtraWarning(info) is { } extra ? $"\n{extra}\n" : "";

        MessageBoxResult answer = ShowMessage(
            $"Terminer « {row.DisplayName} » (PID {row.Pid}) ?\n\n"
            + $"Fichier : {row.PathDisplay}\n"
            + $"Mémoire : {row.MemoryDisplay}\n"
            + $"Démarré : {row.StartTimeDisplay}\n\n"
            + "Le processus est arrêté net : tout travail non enregistré est perdu, et les processus enfants "
            + "qu'il a lancés peuvent rester en place. Quand c'est possible, ferme plutôt l'application par sa "
            + "propre fenêtre.\n"
            + warning,
            MessageBoxImage.Warning, MessageBoxButton.YesNo, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        IsTerminating = true;
        try
        {
            TerminateResult result = await Task.Run(() => _service.Terminate(row.Identity));
            switch (result.Failure)
            {
                case TerminateFailure.None:
                    StatusText = $"« {row.DisplayName} » (PID {row.Pid}) terminé.";
                    break;

                case TerminateFailure.AlreadyExited:
                    StatusText = $"« {row.DisplayName} » (PID {row.Pid}) s'était déjà terminé ; la liste est à jour.";
                    break;

                case TerminateFailure.AccessDenied:
                    ShowMessage($"Impossible de terminer « {row.Name} » (PID {row.Pid}) : accès refusé.\n\n"
                                + "Windows protège ce processus (processus protégé, antivirus, ou compte plus "
                                + "privilégié que l'app). Même en administrateur, il ne peut pas être arrêté d'ici.",
                        MessageBoxImage.Warning);
                    break;

                case TerminateFailure.IdentityMismatch:
                    ShowMessage($"Le PID {row.Pid} appartient maintenant à un autre processus : rien n'a été terminé.\n\n"
                                + "Windows réattribue les numéros de processus. La liste va être remise à jour.",
                        MessageBoxImage.Information);
                    break;

                case TerminateFailure.StillRunning:
                    ShowMessage($"« {row.Name} » (PID {row.Pid}) n'a pas disparu dans les secondes qui ont suivi la "
                                + "demande. Il est peut-être bloqué dans un pilote ; réessaie dans un instant.",
                        MessageBoxImage.Warning);
                    break;

                default:
                    ShowMessage($"Impossible de terminer « {row.Name} » (PID {row.Pid}).\n{result.Message}",
                        MessageBoxImage.Warning);
                    break;
            }
        }
        finally
        {
            IsTerminating = false;
        }

        await RefreshAsync();
    }

    internal void OpenLocation(ProcessRowViewModel row)
    {
        if (row.ExecutablePath is not { Length: > 0 } path) return;

        // Toujours passer par l'Explorateur. PCPerfSuite tourne en administrateur : démarrer directement un
        // exécutable lui ferait hériter de ce jeton élevé, sans la moindre invite UAC. explorer.exe, lui,
        // tourne déjà sous le compte normal.
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            ShowMessage($"Impossible d'ouvrir l'Explorateur sur « {path} » :\n{ex.Message}", MessageBoxImage.Warning);
        }
    }

    internal void ShowProperties(ProcessRowViewModel row)
    {
        if (row.ExecutablePath is not { Length: > 0 } path) return;

        try
        {
            ShellFileOperations.ShowProperties(path, OwnerHandle());
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            ShowMessage($"Impossible d'afficher les propriétés de « {path} » :\n{ex.Message}", MessageBoxImage.Warning);
        }
    }

    internal void CopyToClipboard(string value, string successMessage)
    {
        // Le presse-papiers est partagé : s'il est tenu ouvert ailleurs, l'écriture échoue. Clipboard.SetText
        // réessaie déjà en interne, inutile d'empiler notre propre boucle qui ne ferait que figer la fenêtre.
        try
        {
            Clipboard.SetText(value);
            StatusText = $"{successMessage} : {value}";
        }
        catch (ExternalException ex)
        {
            ShowMessage($"Le presse-papiers est occupé par une autre application, réessaie dans un instant.\n\n{ex.Message}",
                MessageBoxImage.Warning);
        }
    }

    [RelayCommand]
    private async Task RefreshNowAsync() => await RefreshAsync();

    // ----- Entête et réglages -----

    private void OnHardwareSnapshot(HardwareSnapshot snapshot) => UpdateHeaderSummary(snapshot);

    private HardwareSnapshot? _lastHardware;

    private void UpdateHeaderSummary(HardwareSnapshot? snapshot = null)
    {
        _lastHardware = snapshot ?? _lastHardware;

        var parts = new List<string>();
        if (_lastHardware?.Cpu.LoadPercent is { } cpu) parts.Add($"CPU {cpu:0} %");
        if (_lastHardware?.Memory is { UsedGb: { } used, TotalGb: { } total } && total > 0)
        {
            parts.Add($"RAM {used:0.0} / {total:0.0} Go");
        }

        parts.Add($"{TotalCount} processus");
        if (VisibleCount != TotalCount) parts.Add($"{VisibleCount} affichés");
        if (SelectionCount > 0) parts.Add($"{SelectionCount} sélectionnés");

        HeaderSummary = string.Join("  ·  ", parts);
    }

    private static string KindKey(ProcessKindFilter filter) => filter switch
    {
        ProcessKindFilter.Applications => "apps",
        ProcessKindFilter.Background => "background",
        ProcessKindFilter.Windows => "windows",
        _ => "all",
    };

    /// <summary>Relit le fichier avant d'écrire : cet onglet n'est propriétaire que de son propre bloc, le
    /// reste appartient aux autres ViewModels et serait écrasé par une sauvegarde de l'instance en mémoire.</summary>
    private void Persist()
    {
        if (!_initialized) return;

        AppSettings settings = AppSettingsStore.Load();
        settings.Processes.RefreshMs = SelectedRefresh?.Ms ?? DefaultRefreshMs;
        settings.Processes.SortColumnId = SortColumnId;
        settings.Processes.SortDescending = SortDescending;
        settings.Processes.VisibleColumnIds = Columns.VisibleIds;
        settings.Processes.KindFilter = KindKey(SelectedKind?.Value ?? ProcessKindFilter.All);
        settings.Processes.ShowDetails = ShowDetails;
        AppSettingsStore.Save(settings);
    }

    private static MessageBoxResult ShowMessage(string text, MessageBoxImage image,
        MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxResult defaultResult = MessageBoxResult.OK)
    {
        Window? owner = Application.Current?.MainWindow;
        return owner is null
            ? MessageBox.Show(text, DialogTitle, buttons, image, defaultResult)
            : MessageBox.Show(owner, text, DialogTitle, buttons, image, defaultResult);
    }

    private static IntPtr OwnerHandle()
        => Application.Current?.MainWindow is { } window ? new WindowInteropHelper(window).Handle : IntPtr.Zero;

    public void Dispose()
    {
        _disposed = true;
        _timer.Stop();
        _monitoring.SnapshotUpdated -= OnHardwareSnapshot;

        // Sur le pool, pas ici : Dispose prend le verrou du service et attend donc la fin d'un relevé en vol.
        // Appelé sur le thread d'interface à la fermeture de la fenêtre, il laissait l'application en vie
        // sans fenêtre, l'air plantée, le temps que le relevé se termine.
        //
        // On lui laisse un court délai, puis on passe : la fermeture de la fenêtre enchaîne sur l'arrêt de
        // l'application, et un nettoyage lancé sans être attendu n'avait quasiment aucune chance de
        // s'exécuter. Au pire, c'est Windows qui ferme les handles restants — rien n'est perdu, mais autant
        // que ce soit le cas rare plutôt que le cas courant.
        Task cleanup = Task.Run(_service.Dispose);
        cleanup.Wait(TimeSpan.FromMilliseconds(500));
    }
}
