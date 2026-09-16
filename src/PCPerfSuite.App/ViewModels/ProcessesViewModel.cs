using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    /// <summary>Poids d'un nouveau relevé dans la clé de tri. Le %CPU instantané d'un processus saute de 0 à
    /// 8 % d'un relevé à l'autre : trier là-dessus fait danser la liste, et c'est précisément ce qu'on
    /// reproche au Gestionnaire des tâches. La colonne affiche l'instantané, le tri suit la moyenne.</summary>
    private const double CpuSmoothing = 0.35;

    public ProcessesViewModel Owner { get; }

    public ProcessIdentity Identity { get; private set; }
    public int Pid { get; }
    public string Name { get; }

    [ObservableProperty] private string displayName = "";
    [ObservableProperty] private string? executablePath;
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

    /// <summary>Le processus a disparu, mais sa ligne reste affichée en grisé tant que le pointeur survole la
    /// liste : la retirer aussitôt ferait remonter tout le reste d'un cran sous le curseur.</summary>
    [ObservableProperty] private bool isGone;

    public SampleHistory CpuHistory { get; } = new(90);
    public SampleHistory MemoryHistory { get; } = new(90);

    public double CpuSortKey { get; private set; }

    public string KindLabel => Kind switch
    {
        ProcessKind.Application => "Application",
        ProcessKind.Windows => "Windows",
        _ => "Arrière-plan",
    };

    public string CpuDisplay => CpuPercent is not { } value
        ? "--"
        : value < 10 ? value.ToString("0.0", CultureInfo.CurrentCulture) + " %"
                     : value.ToString("0", CultureInfo.CurrentCulture) + " %";

    public string MemoryDisplay => MemoryBytes is { } bytes ? ByteFormatter.Format(bytes) : "--";

    public string IoDisplay => IoBytesPerSecond is { } rate && rate >= 1 ? ByteFormatter.FormatRate(rate) : "--";

    public string ThreadsDisplay => ThreadCount > 0 ? ThreadCount.ToString(CultureInfo.CurrentCulture) : "--";

    public string StartTimeDisplay => Identity.StartTimeUtc is { } start
        ? start.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.CurrentCulture)
        : "--";

    public string PathDisplay => ExecutablePath ?? (IsAccessible ? "Chemin non lisible" : "Processus protégé par Windows");

    public bool CanOpenLocation => ExecutablePath is { Length: > 0 };

    public ProcessRowViewModel(ProcessesViewModel owner, ProcessInfo info)
    {
        Owner = owner;
        Identity = info.Identity;
        Pid = info.Pid;
        Name = info.Name;
        CpuSortKey = 0;
        Apply(info);
    }

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

        CpuSortKey += CpuSmoothing * ((info.CpuPercent ?? 0) - CpuSortKey);

        CpuHistory.Push(info.CpuPercent);
        MemoryHistory.Push(MemoryBytes);

        if (IsGone) IsGone = false;
    }

    /// <summary>Le processus vient de disparaître : la ligne reste, vidée de ses mesures.</summary>
    public void MarkGone()
    {
        if (IsGone) return;

        IsGone = true;
        CpuPercent = null;
        IoBytesPerSecond = null;
        CpuSortKey = 0;
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

    partial void OnKindChanged(ProcessKind value) => OnPropertyChanged(nameof(KindLabel));
    partial void OnCpuPercentChanged(double? value) => OnPropertyChanged(nameof(CpuDisplay));
    partial void OnMemoryBytesChanged(long? value) => OnPropertyChanged(nameof(MemoryDisplay));
    partial void OnIoBytesPerSecondChanged(double? value) => OnPropertyChanged(nameof(IoDisplay));
    partial void OnThreadCountChanged(int value) => OnPropertyChanged(nameof(ThreadsDisplay));
    partial void OnIsSelectedChanged(bool value) => Owner.OnRowSelectionChanged();

    partial void OnExecutablePathChanged(string? value)
    {
        OnPropertyChanged(nameof(PathDisplay));
        OnPropertyChanged(nameof(CanOpenLocation));
        TerminateCommand.NotifyCanExecuteChanged();
        OpenLocationCommand.NotifyCanExecuteChanged();
        ShowPropertiesCommand.NotifyCanExecuteChanged();
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
/// Onglet « Processus » : la liste de ce qui tourne, en plus lisible que le Gestionnaire des tâches. Le vrai
/// sujet n'est pas d'afficher des chiffres, c'est que la liste reste cliquable — d'où trois mécanismes
/// cumulés contre les lignes qui sautent : une clé de tri lissée, un reclassement bien plus lent que les
/// relevés, et le gel complet du classement dès que le pointeur entre dans la liste.
/// </summary>
public sealed partial class ProcessesViewModel : ObservableObject, IDisposable
{
    private const string DialogTitle = "PCPerfSuite";

    /// <summary>Le classement n'est recalculé qu'à ce rythme, bien plus lentement que les valeurs : un
    /// reclassement à chaque relevé rendrait le lissage inutile.</summary>
    private const int ReorderIntervalMs = 3000;

    private readonly ProcessService _service = new();
    private readonly MonitoringViewModel _monitoring;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<ProcessIdentity, ProcessRowViewModel> _byIdentity = new();
    private readonly HashSet<ProcessIdentity> _seen = new();
    private readonly List<ProcessRowViewModel> _ordered = new();

    private bool _isRefreshing;
    private long _lastReorderTick;
    private bool _initialized;

    public ObservableCollection<ProcessRowViewModel> Rows { get; } = new();
    public ICollectionView RowsView { get; }
    public ProcessColumnsViewModel Columns { get; } = new();

    public IReadOnlyList<ProcessRefreshOption> RefreshOptions { get; } = new[]
    {
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
    [ObservableProperty] private bool showDetails;
    [ObservableProperty] private ProcessRowViewModel? selectedRow;
    [ObservableProperty] private string? statusText;
    [ObservableProperty] private string headerSummary = "";
    [ObservableProperty] private string? frozenAtDisplay;
    [ObservableProperty] private int totalCount;
    [ObservableProperty] private int visibleCount;
    [ObservableProperty] private int selectionCount;
    [ObservableProperty] private bool isTerminating;

    /// <summary>Posé par la vue : tant que le pointeur est sur la liste, l'ordre ne bouge plus.</summary>
    public bool IsPointerOverList { get; set; }

    public bool IsListFocused { get; set; }

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
                // Les compteurs précédents datent d'avant l'absence : un écart rapporté au temps écoulé
                // n'aurait plus aucun sens, donc on repart de zéro et le premier relevé affiche « -- ».
                _service.ResetCounters();
                _timer.Start();
                _ = RefreshAsync();
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

        AppSettings settings = AppSettingsStore.Load();
        ProcessesSettings saved = settings.Processes;

        SortColumnId = Columns.ById(saved.SortColumnId) is null ? "cpu" : saved.SortColumnId;
        SortDescending = saved.SortDescending;
        Columns.ApplyVisibility(saved.VisibleColumnIds);
        Columns.SetVisibilityCallback(OnColumnVisibilityChanged);
        ShowDetails = saved.ShowDetails;
        SelectedKind = KindOptions.FirstOrDefault(k => KindKey(k.Value) == saved.KindFilter) ?? KindOptions[0];
        SelectedRefresh = RefreshOptions.FirstOrDefault(r => r.Ms == saved.RefreshMs) ?? RefreshOptions[1];
        UpdateSortGlyphs();

        RowsView = CollectionViewSource.GetDefaultView(Rows);
        // Pas de SortDescriptions : le tri est appliqué à la source par des Move, ce qui préserve la
        // sélection et le défilement, là où un tri de vue relance une reconstruction complète.
        RowsView.Filter = o => o is ProcessRowViewModel row && PassesFilter(row);

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(SelectedRefresh?.Ms ?? 2000),
        };
        _timer.Tick += async (_, _) => await RefreshAsync();

        _initialized = true;
    }

    // ----- Relevé -----

    private async Task RefreshAsync()
    {
        // Même garde que le Monitoring : si le relevé précédent n'est pas terminé, on saute ce tick plutôt
        // que d'empiler des énumérations concurrentes que l'interface ne rattraperait jamais.
        if (_isRefreshing) return;

        _isRefreshing = true;
        try
        {
            ProcessSnapshot snapshot = await Task.Run(_service.GetSnapshot);
            Apply(snapshot);
            StatusText = null;
        }
        catch (Exception ex)
        {
            StatusText = $"Lecture des processus impossible : {ex.Message}";
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
            _byIdentity[info.Identity] = row;
            Rows.Insert(FindInsertIndex(row), row);
        }

        RemoveVanishedRows();

        TotalCount = snapshot.Processes.Count;
        VisibleCount = Rows.Count(PassesFilter);
        ReadDurationHint = $"Durée du dernier relevé : {snapshot.ReadDuration.TotalMilliseconds:0} ms"
                           + (snapshot.InaccessibleCount > 0
                               ? $" · {snapshot.InaccessibleCount} processus protégés par Windows (mesures vides)"
                               : "");
        OnPropertyChanged(nameof(ReadDurationHint));

        ApplyOrder();
        UpdateHeaderSummary();
    }

    /// <summary>Retire les lignes dont le processus a disparu — mais pas sous le curseur : tant que le
    /// pointeur est sur la liste, elles restent en grisé pour que rien ne bouge sous la souris.</summary>
    private void RemoveVanishedRows()
    {
        bool keepPlaceholders = IsPointerOverList || IsListFocused;

        for (int i = Rows.Count - 1; i >= 0; i--)
        {
            ProcessRowViewModel row = Rows[i];
            if (_seen.Contains(row.Identity)) continue;

            if (keepPlaceholders)
            {
                row.MarkGone();
                continue;
            }

            _byIdentity.Remove(row.Identity);
            if (ReferenceEquals(SelectedRow, row)) SelectedRow = null;
            Rows.RemoveAt(i);
        }
    }

    // ----- Classement -----

    private Comparison<ProcessRowViewModel> BuildComparison()
    {
        string column = SortColumnId;
        int direction = SortDescending ? -1 : 1;

        return (a, b) =>
        {
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
                // Le tri suit la moyenne lissée, jamais la valeur instantanée affichée.
                _ => a.CpuSortKey.CompareTo(b.CpuSortKey),
            };

            if (result != 0) return result * direction;

            // Ordre total obligatoire : sans départage, List.Sort permute les ex æquo d'un relevé à l'autre
            // et les lignes sauteraient malgré tout le reste. Vingt chrome.exe à 0 % sont un cas courant.
            result = string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
            return result != 0 ? result : a.Pid.CompareTo(b.Pid);
        };
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

    /// <summary>Remet la collection dans l'ordre voulu à coups de Move : la liste déplace ses conteneurs au
    /// lieu de les détruire, donc la sélection, le focus et le défilement survivent — ce qu'un Clear suivi
    /// d'Add perdrait tous les trois.</summary>
    private void ApplyOrder(bool force = false)
    {
        if (!force)
        {
            if (IsFrozen || IsPointerOverList || IsListFocused) return;
            if (Environment.TickCount64 - _lastReorderTick < ReorderIntervalMs) return;
        }

        _lastReorderTick = Environment.TickCount64;

        _ordered.Clear();
        _ordered.AddRange(Rows);
        _ordered.Sort(BuildComparison());

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
        ApplyOrder(force: true);
        Persist();
    }

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
        ProcessKindFilter kind = SelectedKind?.Value ?? ProcessKindFilter.All;
        bool kindOk = kind switch
        {
            ProcessKindFilter.Applications => row.Kind == ProcessKind.Application,
            ProcessKindFilter.Background => row.Kind == ProcessKind.Background,
            ProcessKindFilter.Windows => row.Kind == ProcessKind.Windows,
            _ => true,
        };
        if (!kindOk) return false;

        if (SearchText.Length == 0) return true;

        return TextSearch.Contains(row.DisplayName, SearchText)
               || TextSearch.Contains(row.Name, SearchText)
               || TextSearch.Contains(row.Publisher, SearchText)
               || TextSearch.Contains(row.ExecutablePath, SearchText)
               || TextSearch.Contains(row.WindowTitle, SearchText)
               || row.Pid.ToString(CultureInfo.InvariantCulture).Contains(SearchText, StringComparison.Ordinal);
    }

    partial void OnSearchTextChanged(string value) => RefreshFilter();

    partial void OnSelectedKindChanged(ProcessKindOption? value)
    {
        RefreshFilter();
        Persist();
    }

    /// <summary>Refresh() lève un Reset : la liste reconstruit ses conteneurs et le défilement repart en
    /// haut. C'est acceptable sur une action volontaire (recherche, filtre), jamais dans un relevé.</summary>
    private void RefreshFilter()
    {
        if (!_initialized) return;

        RowsView.Refresh();
        VisibleCount = Rows.Count(PassesFilter);
        if (SelectedRow is { } selected && !PassesFilter(selected)) SelectedRow = null;
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

        if (!value) ApplyOrder(force: true);
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
        // Trier sur une colonne qu'on vient de masquer n'aurait plus de sens visible : on retombe sur le CPU.
        if (Columns.ById(SortColumnId) is { IsVisible: false })
        {
            SortColumnId = "cpu";
            SortDescending = true;
            UpdateSortGlyphs();
            ApplyOrder(force: true);
            StatusText = "La colonne de tri a été masquée : le classement est revenu sur le CPU.";
        }

        Persist();
    }

    // ----- Actions -----

    private bool CanTerminateSelection() => SelectionCount > 0 && !IsTerminating;

    [RelayCommand(CanExecute = nameof(CanTerminateSelection))]
    private async Task TerminateSelectionAsync()
    {
        List<ProcessRowViewModel> targets = Rows.Where(r => r.IsSelected && !r.IsGone).ToList();
        if (targets.Count == 0) return;
        if (targets.Count == 1)
        {
            await TerminateAsync(targets[0]);
            return;
        }

        var refused = new List<string>();
        var allowed = new List<ProcessRowViewModel>();
        foreach (ProcessRowViewModel row in targets)
        {
            if (ProcessTerminationGuard.GetRefusalReason(row.ToInfo()) is { } reason) refused.Add(reason);
            else allowed.Add(row);
        }

        if (allowed.Count == 0)
        {
            ShowMessage("Aucun des processus sélectionnés ne peut être terminé :\n\n"
                        + string.Join("\n\n", refused), MessageBoxImage.Information);
            return;
        }

        string names = string.Join("\n", allowed.Take(10).Select(r => $"• {r.DisplayName} (PID {r.Pid})"));
        if (allowed.Count > 10) names += $"\n• … et {allowed.Count - 10} autres";

        MessageBoxResult answer = ShowMessage(
            $"Terminer {allowed.Count} processus ?\n\n{names}\n\n"
            + "Ils sont arrêtés net : tout travail non enregistré est perdu, et c'est irréversible.\n"
            + (refused.Count > 0 ? $"\n{refused.Count} processus de la sélection seront ignorés (protégés).\n" : ""),
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

    internal async Task TerminateAsync(ProcessRowViewModel row)
    {
        if (IsTerminating) return;

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
        settings.Processes.RefreshMs = SelectedRefresh?.Ms ?? 2000;
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
        _timer.Stop();
        _monitoring.SnapshotUpdated -= OnHardwareSnapshot;
        _service.Dispose();
    }
}
