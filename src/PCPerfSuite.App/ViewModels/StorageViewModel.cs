using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCPerfSuite.App.Utils;
using PCPerfSuite.Core.Storage;

namespace PCPerfSuite.App.ViewModels;

public sealed partial class DriveOption : ObservableObject
{
    public required string RootPath { get; init; }
    public required string Letter { get; init; }
    public required string VolumeLabel { get; init; }
    public required string UsageText { get; init; }
    public required string FreeText { get; init; }
    public required double UsedPercent { get; init; }
    public required bool IsRemovable { get; init; }

    public string UsedPercentText => $"{UsedPercent:0} %";
    public bool IsAlmostFull => UsedPercent >= 90;

    /// <summary>Glyphe Segoe Fluent Icons / MDL2 : clé USB ou disque dur.</summary>
    public string Glyph => IsRemovable ? "" : "";

    [ObservableProperty] private bool isSelected;
}

public sealed partial class StorageViewModel : ObservableObject
{
    private readonly DiskSpaceScanner _scanner = new();
    private CancellationTokenSource? _scanCts;

    public ObservableCollectionEx<DriveOption> Drives { get; } = new();
    public ObservableCollectionEx<FolderNode> Breadcrumb { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    private bool isScanning;

    [ObservableProperty] private string? statusText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    private FolderNode? rootNode;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NavigateUpCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenCurrentInExplorerCommand))]
    private FolderNode? currentNode;

    [ObservableProperty] private FolderNode? hoveredNode;
    [ObservableProperty] private string? hoveredDetails;
    [ObservableProperty] private string selectionSummary = "";

    public bool HasResult => RootNode is not null;
    public bool ShowEmptyState => RootNode is null && !IsScanning;

    public StorageViewModel()
    {
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            DriveOption? option = TryCreateOption(drive);
            if (option is null) continue;

            option.PropertyChanged += OnDrivePropertyChanged;
            Drives.Add(option);
        }

        if (Drives.Count > 0) Drives[0].IsSelected = true;
        UpdateSelectionSummary();
    }

    private static DriveOption? TryCreateOption(DriveInfo drive)
    {
        try
        {
            if (!drive.IsReady || drive.DriveType is not (DriveType.Fixed or DriveType.Removable)) return null;

            long total = drive.TotalSize;
            long free = drive.AvailableFreeSpace;
            long used = total - free;
            bool removable = drive.DriveType == DriveType.Removable;

            return new DriveOption
            {
                RootPath = drive.RootDirectory.FullName,
                Letter = drive.Name.TrimEnd('\\'),
                VolumeLabel = string.IsNullOrWhiteSpace(drive.VolumeLabel)
                    ? (removable ? "Disque amovible" : "Disque local")
                    : drive.VolumeLabel,
                UsageText = $"{ByteFormatter.Format(used)} / {ByteFormatter.Format(total)}",
                FreeText = $"{ByteFormatter.Format(free)} libres",
                UsedPercent = total > 0 ? used * 100.0 / total : 0,
                IsRemovable = removable,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void OnDrivePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DriveOption.IsSelected)) UpdateSelectionSummary();
    }

    private void UpdateSelectionSummary()
    {
        int count = Drives.Count(d => d.IsSelected);
        SelectionSummary = count switch
        {
            0 => "Aucun disque sélectionné",
            1 => "1 disque sélectionné",
            _ => $"{count} disques sélectionnés",
        };
    }

    partial void OnHoveredNodeChanged(FolderNode? value)
    {
        if (value is null || CurrentNode is not { SizeBytes: > 0 } current)
        {
            HoveredDetails = null;
            return;
        }

        string kind = value.IsFile ? "fichier" : value.IsAggregate ? "regroupement" : "dossier";
        double share = value.SizeBytes * 100.0 / current.SizeBytes;
        HoveredDetails = $"{ByteFormatter.Format(value.SizeBytes)} · {share:0.#} % de {current.Name} · {kind}";
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        List<DriveOption> selected = Drives.Where(d => d.IsSelected).ToList();
        if (selected.Count == 0)
        {
            StatusText = "Sélectionne au moins un disque.";
            return;
        }

        _scanCts?.Cancel();
        var cts = new CancellationTokenSource();
        _scanCts = cts;

        IsScanning = true;
        HoveredNode = null;
        CurrentNode = null;
        RootNode = null;
        Breadcrumb.Clear();

        var progress = new Progress<ScanProgress>(p =>
            StatusText = $"{ByteFormatter.Format(p.BytesScanned)} analysés — {Truncate(p.CurrentPath, 70)}");

        try
        {
            FolderNode root;
            if (selected.Count == 1)
            {
                root = await _scanner.ScanAsync(selected[0].RootPath, selected[0].Letter, progress, cts.Token);
            }
            else
            {
                var drives = new List<FolderNode>();
                foreach (DriveOption drive in selected)
                {
                    drives.Add(await _scanner.ScanAsync(drive.RootPath, drive.Letter, progress, cts.Token));
                }

                root = new FolderNode { Name = "Disques sélectionnés", SizeBytes = drives.Sum(d => d.SizeBytes) };
                foreach (FolderNode drive in drives)
                {
                    drive.Parent = root;
                    root.Children.Add(drive);
                }
            }

            RootNode = root;
            SetCurrent(root);
            StatusText = $"Terminé — {ByteFormatter.Format(root.SizeBytes)} analysés.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Analyse annulée.";
        }
        catch (Exception ex)
        {
            StatusText = $"Échec de l'analyse : {ex.Message}";
        }
        finally
        {
            IsScanning = false;
        }
    }

    [RelayCommand]
    private void CancelScan() => _scanCts?.Cancel();

    [RelayCommand]
    private void DrillInto(FolderNode? node)
    {
        if (node is { CanDrillInto: true } && !ReferenceEquals(node, CurrentNode)) SetCurrent(node);
    }

    private bool CanNavigateUp() => CurrentNode?.Parent is not null;

    [RelayCommand(CanExecute = nameof(CanNavigateUp))]
    private void NavigateUp()
    {
        if (CurrentNode?.Parent is { } parent) SetCurrent(parent);
    }

    [RelayCommand]
    private void NavigateToBreadcrumb(FolderNode? node)
    {
        if (node is not null && !ReferenceEquals(node, CurrentNode)) SetCurrent(node);
    }

    private bool CanOpenCurrentInExplorer() => !string.IsNullOrEmpty(CurrentNode?.FullPath);

    [RelayCommand(CanExecute = nameof(CanOpenCurrentInExplorer))]
    private void OpenCurrentInExplorer()
    {
        string? path = CurrentNode?.FullPath;
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{path}\"",
            UseShellExecute = true,
        });
    }

    private void SetCurrent(FolderNode node)
    {
        CurrentNode = node;

        var chain = new List<FolderNode>();
        for (FolderNode? n = node; n is not null; n = n.Parent) chain.Add(n);
        chain.Reverse();
        Breadcrumb.ReplaceAll(chain);
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : "…" + value[^maxLength..];
}
